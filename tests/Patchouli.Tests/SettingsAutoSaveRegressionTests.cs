using FluentAssertions;
using Microsoft.Reactive.Testing;
using System.Reactive.Concurrency;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Settings;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class SettingsAutoSaveRegressionTests
{
    [Fact]
    public async Task Auto_save_drains_edits_made_while_the_previous_write_is_running()
    {
        using TemporaryAppSettingsFile file = new();
        PausedSettingsStore store = new(PatchouliAppSettings.Load(file.Path));
        TestScheduler scheduler = new();
        await using MainWindowViewModel main = new(settingsPath: file.Path, settingsStore: store,
            timingScheduler: scheduler, uiScheduler: ImmediateScheduler.Instance);
        main.Settings.EnableAutoSave();
        main.Settings.ImportSettings.MaxFailedPageRatioPercent = 12;
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        await store.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        main.Settings.ImportSettings.MaxFailedPageRatioPercent = 25;
        TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ImportSettingsViewModel importSettings = main.Settings.ImportSettings;
        importSettings.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ISettingsSection.IsDirty) && !importSettings.IsDirty)
            {
                settled.TrySetResult();
            }
        };
        store.ReleaseFirstWrite.SetResult();
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.Written.Select(settings => settings.Import.MaxFailedPageRatio).Should().Equal(0.12, 0.25);
        main.Settings.ImportSettings.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Appearance_edits_during_a_write_remain_dirty_until_the_new_values_are_saved()
    {
        using TemporaryAppSettingsFile file = new();
        PausedSettingsStore store = new(PatchouliAppSettings.Load(file.Path));
        await using MainWindowViewModel main = new(settingsPath: file.Path, settingsStore: store);
        AppearanceSettingsViewModel section = main.Settings.AppearanceSettings;
        section.ReadingFontSize = 18;
        Task save = section.SaveAsync();
        await store.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        section.ReadingFontSize = 24;
        store.ReleaseFirstWrite.SetResult();
        await save;
        section.IsDirty.Should().BeTrue();
        await section.SaveAsync();
        section.IsDirty.Should().BeFalse();
        store.Written.Select(settings => settings.Ui.ReadingFontSize).Should().Equal(18, 24);
    }

    [Theory]
    [InlineData("csl-styles", "cite", false)]
    [InlineData("texts", "put", false)]
    [InlineData("translations", "send", false)]
    [InlineData("runs", "send", true)]
    [InlineData("items", "cite", true)]
    public void Unsupported_permission_cells_remain_off_and_cannot_modify_settings(string domain, string verb,
        bool supported)
    {
        using TemporaryAppSettingsFile file = new();
        using MainWindowViewModel main = new(settingsPath: file.Path);
        McpDomainPermissionCellViewModel cell = new(main.Settings.McpSettings, domain, verb, true);
        cell.IsSupported.Should().Be(supported);
        if (!supported)
        {
            cell.Enabled.Should().BeFalse();
            cell.Enabled = true;
            cell.Enabled.Should().BeFalse();
            main.Settings.McpSettings.IsDirty.Should().BeFalse();
        }

        McpPermissionPolicy.Supports(domain, verb).Should().Be(supported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_library_sync_and_search_forms_are_restored_as_saved_drafts(bool emptySyncRoot)
    {
        using TemporaryAppSettingsFile file = new();
        await using (MainWindowViewModel main = new(settingsPath: file.Path))
        {
            main.Settings.LibrarySettings.ExclusionPatternsText = "[";
            main.Settings.SyncSettings.DeviceName = "未完成的同步设置";
            if (emptySyncRoot)
            {
                main.Settings.SyncSettings.SyncRoot = "";
            }

            main.Settings.OcrProviderSettings.SelectedDocumentEngine = "";
            await main.Settings.SearchRewriteSettings.LoadAsync();
            main.Settings.SearchRewriteSettings.AddRule();
            main.Settings.SearchRewriteSettings.Rules.Single().Pattern = "draft";
            main.Settings.ImportSettings.MaxFailedPageRatioPercent = 37;
            (await main.Settings.SaveAllDirtySectionsAsync()).Should().BeTrue(main.Settings.GlobalStatus);
            main.Settings.HasDirtySections.Should().BeFalse();
            main.Settings.GlobalStatus.Should().Contain("补全");
            main.AppOptions.FileScanning.ExclusionPatterns.Should().NotContain("[");
            main.AppOptions.Import.MaxFailedPageRatio.Should().Be(0.37);
        }

        await using MainWindowViewModel reopened = new(settingsPath: file.Path);
        await reopened.Settings.LibrarySettings.LoadAsync();
        await reopened.Settings.SyncSettings.LoadAsync();
        await reopened.Settings.SearchRewriteSettings.LoadAsync();
        await reopened.Settings.OcrProviderSettings.LoadAsync();
        reopened.Settings.LibrarySettings.ExclusionPatternsText.Should().Be("[");
        reopened.Settings.SyncSettings.DeviceName.Should().Be("未完成的同步设置");
        reopened.Settings.OcrProviderSettings.SelectedDocumentEngine.Should().BeEmpty();
        reopened.Settings.SearchRewriteSettings.Rules.Should().ContainSingle().Which.Pattern.Should().Be("draft");
        reopened.Settings.HasDirtySections.Should().BeFalse();
        reopened.Settings.SearchRewriteSettings.Rules.Single().Replacement = "complete";
        await reopened.Settings.SearchRewriteSettings.SaveAsync();
        reopened.Settings.SearchRewriteSettings.IsDirty.Should().BeFalse();
        reopened.Settings.SearchRewriteSettings.ValidationState.Should().Be(SettingsValidationState.Invalid);
        await reopened.Settings.SearchRewriteSettings.LoadAsync();
        reopened.Settings.SearchRewriteSettings.Rules.Single().Replacement.Should().Be("complete");
        HostServices services = await reopened.ServicesAsync();
        (await services.Library.CreateLibraryAsync("草稿恢复书库")).IsSuccess.Should().BeTrue();
        reopened.Settings.SearchRewriteSettings.Rules.Single().Replacement = "ready";
        await reopened.Settings.SearchRewriteSettings.SaveAsync();
        reopened.Settings.SearchRewriteSettings.ValidationState.Should().Be(SettingsValidationState.Valid);
        await reopened.Settings.SearchRewriteSettings.LoadAsync();
        reopened.Settings.SearchRewriteSettings.Rules.Should().ContainSingle().Which.Replacement.Should()
            .Be("ready");
    }

    [Fact]
    public async Task A_failed_section_does_not_prevent_later_sections_from_saving()
    {
        using TemporaryAppSettingsFile file = new();
        await using MainWindowViewModel main = new(settingsPath: file.Path);
        // A stale MCP revision creates a real persistence failure before import is visited.
        await main.Settings.McpSettings.LoadAsync();
        HostServices services = await main.ServicesAsync();
        Result<McpServerSettings> current = await services.McpSettings.GetSettingsAsync();
        (await services.McpSettings.SaveSettingsAsync(current.Value)).IsSuccess.Should().BeTrue();
        main.Settings.McpSettings.Port += 1;
        main.Settings.ImportSettings.MaxFailedPageRatioPercent = 43;
        (await main.Settings.SaveAllDirtySectionsAsync()).Should().BeFalse();
        main.Settings.McpSettings.IsDirty.Should().BeTrue();
        main.Settings.ImportSettings.IsDirty.Should().BeFalse();
        main.AppOptions.Import.MaxFailedPageRatio.Should().Be(0.43);
        main.Settings.GlobalStatus.Should().Contain("保存失败");
        main.Status.Should().Contain("保存失败");
        main.StatusIsError.Should().BeTrue();
    }

    private sealed class PausedSettingsStore(PatchouliAppSettings initial) : IAppSettingsStore
    {
        public PatchouliAppSettings Current { get; private set; } = initial;
        public bool IsDirty => false;
        public List<PatchouliAppSettings> Written { get; } = [];

        public TaskCompletionSource FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Update(Func<PatchouliAppSettings, PatchouliAppSettings> updater, string? fieldCategory = null)
        {
            Current = updater(Current);
        }

        public async Task<SettingsSaveResult> SaveImmediatelyAsync(CancellationToken cancellationToken = default)
        {
            PatchouliAppSettings snapshot = Current;
            if (Written.Count == 0)
            {
                FirstWriteStarted.TrySetResult();
                await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
            }

            Written.Add(snapshot);
            return SettingsSaveResult.Success;
        }

        public Task<SettingsSaveResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SettingsSaveResult.Success);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
