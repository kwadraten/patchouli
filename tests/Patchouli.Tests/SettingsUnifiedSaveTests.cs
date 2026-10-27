using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Results;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>The settings header 保存/放弃更改 acts on every dirty section in one action.</summary>
public sealed class SettingsUnifiedSaveTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Header_save_commits_all_dirty_sections_at_once()
    {
        string path = TempDbPath("save-all");
        try
        {
            MainWindowViewModel vm = await OpenMainAsync(path);
            await vm.Settings.LibrarySettings.LoadAsync();
            await vm.Settings.McpSettings.LoadAsync();

            vm.Settings.LibrarySettings.RememberLastDatabase =
                !vm.Settings.LibrarySettings.RememberLastDatabase;
            vm.Settings.McpSettings.Port = 4599;
            vm.Settings.LibrarySettings.IsDirty.Should().BeTrue();
            vm.Settings.McpSettings.IsDirty.Should().BeTrue();
            vm.Settings.HasDirtySections.Should().BeTrue();
            vm.Settings.CanSaveAll.Should().BeTrue();

            await vm.Settings.SaveCommand.ExecuteAsync();

            vm.Settings.HasDirtySections.Should().BeFalse();
            vm.Settings.LibrarySettings.IsDirty.Should().BeFalse();
            vm.Settings.McpSettings.IsDirty.Should().BeFalse();
            vm.Settings.McpSettings.SaveState.Should().Be(SettingsSaveState.Saved);
            vm.Settings.GlobalStatus.Should().Contain("已保存");

            await vm.Settings.McpSettings.LoadAsync();
            vm.Settings.McpSettings.Port.Should().Be(4599);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Header_discard_reverts_all_dirty_sections_at_once()
    {
        string path = TempDbPath("discard-all");
        try
        {
            MainWindowViewModel vm = await OpenMainAsync(path);
            await vm.Settings.LibrarySettings.LoadAsync();
            await vm.Settings.McpSettings.LoadAsync();
            bool originalRemember = vm.Settings.LibrarySettings.RememberLastDatabase;
            int originalPort = vm.Settings.McpSettings.Port;

            vm.Settings.LibrarySettings.RememberLastDatabase = !originalRemember;
            vm.Settings.McpSettings.Port = originalPort + 7;
            vm.Settings.HasDirtySections.Should().BeTrue();
            vm.Settings.CanDiscardAll.Should().BeTrue();

            await vm.Settings.DiscardCommand.ExecuteAsync();

            vm.Settings.HasDirtySections.Should().BeFalse();
            vm.Settings.LibrarySettings.RememberLastDatabase.Should().Be(originalRemember);
            vm.Settings.McpSettings.Port.Should().Be(originalPort);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Section_switch_is_not_blocked_by_unsaved_changes()
    {
        string path = TempDbPath("switch");
        try
        {
            MainWindowViewModel vm = await OpenMainAsync(path);
            await vm.Settings.LibrarySettings.LoadAsync();
            NavCategoryViewModel libraryCategory = vm.Settings.ActiveCategory;
            NavCategoryViewModel syncCategory =
                vm.Settings.Categories.Single(category => category.IconName == "Cloud");

            vm.Settings.LibrarySettings.RememberLastDatabase =
                !vm.Settings.LibrarySettings.RememberLastDatabase;
            vm.Settings.ActiveCategory = syncCategory;

            vm.Settings.ActiveCategory.Should().BeSameAs(syncCategory);
            vm.Settings.HasDirtySections.Should().BeTrue();

            // Switching back shows the in-memory draft, still dirty.
            vm.Settings.ActiveCategory = libraryCategory;
            vm.Settings.LibrarySettings.IsDirty.Should().BeTrue();
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Ocr_preset_selection_save_persists_the_scope_choice()
    {
        string path = TempDbPath("ocr-presets");
        try
        {
            MainWindowViewModel vm = await OpenMainAsync(path);
            HostServices services = await vm.ServicesAsync();
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset A", null,
                OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
            await vm.Settings.OcrProviderSettings.LoadAsync();

            vm.Settings.OcrProviderSettings.DocumentPresetPicker.SelectedOption =
                vm.Settings.OcrProviderSettings.DocumentPresetPicker.Options.Single(option =>
                    option.PresetId == preset.PresetId.ToString());
            vm.Settings.OcrProviderSettings.IsDirty.Should().BeTrue();

            await vm.Settings.SaveCommand.ExecuteAsync();

            vm.Settings.OcrProviderSettings.SaveState.Should()
                .Be(SettingsSaveState.Saved, $"status: {vm.Settings.GlobalStatus}");
            vm.Settings.OcrProviderSettings.IsDirty.Should().BeFalse();
            (await StoredSelectionsAsync(services)).DocumentPresetId.Should()
                .Be(preset.PresetId.ToString());
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Fact]
    public async Task Ocr_preset_selection_save_keeps_persisted_mineru_settings_untouched()
    {
        string path = TempDbPath("ocr-presets-token");
        try
        {
            MainWindowViewModel vm = await OpenMainAsync(path);
            (await vm.SaveMinerUSettingsAsync("persisted-token", "vlm", 300)).Should().BeTrue();
            vm.Shell.MinerUToken.Should().Be("persisted-token");

            HostServices services = await vm.ServicesAsync();
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset A", null,
                OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
            await vm.Settings.OcrProviderSettings.LoadAsync();

            vm.Settings.OcrProviderSettings.DocumentPresetPicker.SelectedOption =
                vm.Settings.OcrProviderSettings.DocumentPresetPicker.Options.Single(option =>
                    option.PresetId == preset.PresetId.ToString());
            vm.Settings.OcrProviderSettings.IsDirty.Should().BeTrue();

            await vm.Settings.SaveCommand.ExecuteAsync();

            vm.Settings.OcrProviderSettings.SaveState.Should()
                .Be(SettingsSaveState.Saved, $"status: {vm.Settings.GlobalStatus}");
            vm.Settings.OcrProviderSettings.IsDirty.Should().BeFalse();
            (await StoredSelectionsAsync(services)).DocumentPresetId.Should()
                .Be(preset.PresetId.ToString());

            // The OCR scope save must not touch the persisted MinerU component state.
            vm.Shell.MinerUToken.Should().Be("persisted-token");
            PatchouliAppSettings loaded = PatchouliAppSettings.Load(_settings.Path);
            loaded.MinerU.ModelVersion.Should().Be("vlm");
            loaded.MinerU.PollingTimeoutSeconds.Should().Be(300);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    private async Task<MainWindowViewModel> OpenMainAsync(string path)
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path) { RuntimeDatabasePath = path };
        await vm.OpenDatabaseCommand.ExecuteAsync();
        await vm.Library.CreateCommand.ExecuteAsync();
        return vm;
    }

    private async Task<OcrPresetSelections> StoredSelectionsAsync(HostServices services)
    {
        OcrPresetSelectionService selectionService = new(
            new OcrPresetSelectionStore(_settings.Path), services.OcrPresets, services.Library);
        Result<OcrPresetSelections> stored = await selectionService.ReadStoredSelectionsAsync();
        stored.IsSuccess.Should().BeTrue(stored.ErrorMessage);
        return stored.Value;
    }

    private static string TempDbPath(string tag)
    {
        return Path.Combine(Path.GetTempPath(), $"settings-unified-{tag}-{Guid.NewGuid():N}.sqlite");
    }

    private static void CleanupDb(string path)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
