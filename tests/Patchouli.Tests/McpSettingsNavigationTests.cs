using System.Net;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Patchouli.Core.Mcp;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <remarks>
/// R14 shared-state audit (no case moved): the Fixture constructs <see cref="MainWindowViewModel"/>
/// on the xunit worker thread with <c>uiScheduler: ImmediateScheduler.Instance</c> — deterministic,
/// but not UI-free. The VM ctor calls <c>ThemePaletteApplier.Apply</c>, which writes process-global
/// theme statics and mutates <c>Application.Current</c> resources/theme variant whenever an
/// Application is installed by another test's dispatch. CreateAsync additionally opens a real
/// SQLite database (OpenDatabaseCommand/Library.CreateCommand via ServicesAsync/HostServices) and
/// binds real loopback <see cref="TcpListener"/> ports — machine-scoped resources. DisposeAsync
/// disposes the VM and the temp settings file; SQLite pools are not purged here. Keep the class in
/// a DisableParallelization collection.
/// </remarks>
[Collection("Avalonia")]
public sealed class McpSettingsNavigationTests
{
    [Theory]
    [InlineData(ConfirmDialogResult.Cancel)]
    [InlineData(null)]
    public async Task Dismissing_the_dialog_keeps_the_subpage_and_draft(ConfirmDialogResult? choice)
    {
        await using Fixture fixture = await Fixture.CreateAsync(choice);
        SettingsViewModel settings = fixture.Main.Settings;
        settings.McpSettings.ExposeLibraryTags = false;

        await settings.SelectSectionAsync("llm");

        settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("mcp");
        settings.McpSettings.IsDirty.Should().BeTrue();
        (await fixture.ReadSettingsAsync()).ExposeLibraryTags.Should().BeTrue();
        fixture.Dialogs.Shown.Should().ContainSingle().Which.ConfirmText.Should().Be("保存并重启");
    }

    [Theory]
    [InlineData("subpage")]
    [InlineData("category")]
    [InlineData("workspace")]
    [InlineData("close")]
    public async Task Discard_leaves_without_persisting_the_permissions(string route)
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Discard);
        fixture.Main.Settings.McpSettings.ExposeLibraryTags = false;

        await fixture.LeaveAsync(route);

        fixture.Dialogs.Shown.Should().ContainSingle();
        fixture.Main.Settings.McpSettings.IsDirty.Should().BeFalse();
        fixture.Main.Settings.McpSettings.ExposeLibraryTags.Should().BeTrue();
        fixture.Main.McpServerRunning.Should().BeFalse();
        (await fixture.ReadSettingsAsync()).ExposeLibraryTags.Should().BeTrue();
        fixture.AssertLeft(route);
    }

    [Theory]
    [InlineData("subpage")]
    [InlineData("category")]
    [InlineData("workspace")]
    [InlineData("close")]
    public async Task Save_restarts_the_listener_with_the_new_permissions_before_leaving(string route)
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Confirm);
        await fixture.Main.StartMcpServerAsync();
        fixture.Main.McpServerRunning.Should().BeTrue();
        fixture.Main.Settings.McpSettings.ExposeLibraryTags = false;

        await fixture.LeaveAsync(route);

        fixture.Dialogs.Shown.Should().ContainSingle();
        fixture.Main.McpServerRunning.Should().BeTrue();
        fixture.Main.Settings.McpSettings.HasPendingLeaveDecision.Should().BeFalse();
        fixture.Main.McpRunningSettingsRevision.Should().Be((await fixture.ReadSettingsAsync()).Revision);
        fixture.Main.McpRunningSettings!.ExposeLibraryTags.Should().BeFalse();
        fixture.AssertLeft(route);
    }

    [Fact]
    public async Task Turning_auth_off_with_a_stored_token_stays_off_after_save_and_restart()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        McpSettingsViewModel mcp = fixture.Main.Settings.McpSettings;
        IMcpServerSettingsService service = (await fixture.Main.ServicesAsync()).McpSettings;
        McpServerSettings current = await fixture.ReadSettingsAsync();
        (await service.SaveSettingsAsync(current with { AuthRequired = true, Token = "kept-token" },
            current.Revision)).IsSuccess.Should().BeTrue();
        await fixture.Main.StartMcpServerAsync();
        await mcp.LoadAsync();
        mcp.AuthRequired.Should().BeTrue();

        mcp.AuthRequired = false;
        mcp.NeedsSaveAndRestart.Should().BeTrue();
        await mcp.SaveAndRestartCommand.ExecuteAsync();

        fixture.Main.McpServerRunning.Should().BeTrue();
        mcp.AuthRequired.Should().BeFalse();
        mcp.RequiresReload.Should().BeFalse();
        mcp.NeedsSaveAndRestart.Should().BeFalse();
        fixture.Main.McpRunningSettings!.AuthRequired.Should().BeFalse();
        fixture.Main.McpRunningSettings.Token.Should().Be("kept-token");
        (await fixture.ReadSettingsAsync()).AuthRequired.Should().BeFalse();
        await mcp.LoadAsync();
        mcp.AuthRequired.Should().BeFalse();
        mcp.NeedsSaveAndRestart.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsaved_edits_show_the_save_and_restart_banner_until_applied(bool running)
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        McpSettingsViewModel mcp = fixture.Main.Settings.McpSettings;
        if (running)
        {
            await fixture.Main.StartMcpServerAsync();
            await mcp.LoadAsync();
        }

        mcp.NeedsSaveAndRestart.Should().BeFalse();
        List<string?> raised = [];
        mcp.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        mcp.ExposeLibraryTags = false;

        raised.Should().Contain(nameof(McpSettingsViewModel.NeedsSaveAndRestart));
        mcp.NeedsSaveAndRestart.Should().BeTrue();
        mcp.SaveAndRestartBannerText.Should().Be("有未保存的更改，保存并重启服务后生效");

        await mcp.SaveAndRestartCommand.ExecuteAsync();

        mcp.LastError.Should().BeNull();
        fixture.Main.McpServerRunning.Should().BeTrue();
        fixture.Main.McpRunningSettings!.Port.Should().Be(fixture.InitialPort);
        fixture.Main.McpRunningSettings.ExposeLibraryTags.Should().BeFalse();
        mcp.IsDirty.Should().BeFalse();
        mcp.RequiresReload.Should().BeFalse();
        mcp.NeedsSaveAndRestart.Should().BeFalse();
        mcp.HasPendingLeaveDecision.Should().BeFalse();
    }

    [Fact]
    public async Task Saved_but_unapplied_settings_show_the_restart_text()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        McpSettingsViewModel mcp = fixture.Main.Settings.McpSettings;
        await fixture.Main.StartMcpServerAsync();
        await mcp.LoadAsync();
        mcp.ExposeLibraryTags = false;

        await mcp.SaveAsync();

        mcp.IsDirty.Should().BeFalse();
        mcp.RequiresReload.Should().BeTrue();
        mcp.NeedsSaveAndRestart.Should().BeTrue();
        mcp.SaveAndRestartBannerText.Should().Be("设置已保存，需重启服务后生效");
    }

    [Fact]
    public async Task Banner_discard_reverts_the_draft_without_persisting()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        McpSettingsViewModel mcp = fixture.Main.Settings.McpSettings;
        mcp.ExposeLibraryTags = false;

        await mcp.DiscardChangesCommand.ExecuteAsync();

        mcp.ExposeLibraryTags.Should().BeTrue();
        mcp.IsDirty.Should().BeFalse();
        mcp.NeedsSaveAndRestart.Should().BeFalse();
        (await fixture.ReadSettingsAsync()).ExposeLibraryTags.Should().BeTrue();
        fixture.Dialogs.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_failure_reports_the_actual_reason_and_keeps_the_banner()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        McpSettingsViewModel mcp = fixture.Main.Settings.McpSettings;
        using TcpListener occupied = new(IPAddress.Loopback, 0);
        occupied.Start();
        mcp.Port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        await mcp.SaveAndRestartCommand.ExecuteAsync();

        fixture.Main.McpServerRunning.Should().BeFalse();
        mcp.IsDirty.Should().BeFalse();
        mcp.NeedsSaveAndRestart.Should().BeTrue();
        mcp.LastError.Should().NotBeNullOrWhiteSpace();
        mcp.LastError.Should().NotContain("请检查状态栏");
        mcp.Status.Should().StartWith("MCP 启动失败");
    }

    [Fact]
    public async Task Invalid_settings_keep_the_page_and_do_not_write_the_draft()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Confirm);
        fixture.Main.Settings.McpSettings.Port = 0;

        await fixture.Main.Settings.SelectSectionAsync("appearance");

        fixture.Main.Settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("mcp");
        fixture.Main.Settings.McpSettings.IsDirty.Should().BeTrue();
        fixture.Main.Settings.McpSettings.LastError.Should().NotBeNullOrWhiteSpace();
        (await fixture.ReadSettingsAsync()).Port.Should().Be(fixture.InitialPort);
    }

    [Fact]
    public async Task Restart_failure_keeps_the_page_and_discard_restores_the_previous_configuration()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Confirm);
        using TcpListener occupied = new(IPAddress.Loopback, 0);
        occupied.Start();
        fixture.Main.Settings.McpSettings.Port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        fixture.Main.Settings.McpSettings.ExposeLibraryTags = false;

        await fixture.Main.Settings.SelectSectionAsync("appearance");

        fixture.Main.Settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("mcp");
        fixture.Main.Settings.McpSettings.HasPendingLeaveDecision.Should().BeTrue();
        fixture.Main.McpServerRunning.Should().BeFalse();
        await fixture.Main.Settings.SelectSectionAsync("mcp");
        fixture.Main.Settings.McpSettings.HasPendingLeaveDecision.Should().BeTrue();
        fixture.Dialogs.Choice = ConfirmDialogResult.Discard;
        await fixture.Main.Settings.SelectSectionAsync("appearance");

        fixture.Main.Settings.ActiveCategory.IconName.Should().Be("Palette");
        fixture.Main.Settings.McpSettings.HasPendingLeaveDecision.Should().BeFalse();
        (await fixture.ReadSettingsAsync()).Port.Should().Be(fixture.InitialPort);
        (await fixture.ReadSettingsAsync()).ExposeLibraryTags.Should().BeTrue();
    }

    [Fact]
    public async Task Automatic_saves_preserve_mcp_as_a_draft_and_save_other_settings()
    {
        TestScheduler scheduler = new();
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        using SettingsViewModel settings = new(fixture.Main, scheduler, ImmediateScheduler.Instance);
        await settings.McpSettings.LoadAsync();
        settings.EnableAutoSave();
        settings.McpSettings.ExposeLibraryTags = false;
        settings.AppearanceSettings.ReadingFontSize = 23;
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        await settings.WaitForAutoSaveAsync();

        settings.McpSettings.IsDirty.Should().BeTrue();
        (await fixture.ReadSettingsAsync()).ExposeLibraryTags.Should().BeTrue();
        PatchouliAppSettings.Load(fixture.SettingsFile.Path).Ui.ReadingFontSize.Should().Be(23);
    }

    [Theory]
    [InlineData(ConfirmDialogResult.Confirm, true)]
    [InlineData(ConfirmDialogResult.Discard, true)]
    [InlineData(ConfirmDialogResult.Cancel, false)]
    public async Task Exiting_also_requires_an_explicit_decision(ConfirmDialogResult choice, bool allowed)
    {
        await using Fixture fixture = await Fixture.CreateAsync(choice);
        fixture.Main.Settings.McpSettings.ExposeLibraryTags = false;

        (await fixture.Main.ConfirmMcpSettingsBeforeExitAsync()).Should().Be(allowed);

        fixture.Dialogs.Shown.Should().ContainSingle();
        (await fixture.ReadSettingsAsync()).ExposeLibraryTags.Should().Be(choice != ConfirmDialogResult.Confirm);
    }

    [Fact]
    public async Task Workspace_navigation_updates_visibility_bindings()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Discard);
        fixture.Main.IsSettingsVisible.Should().BeTrue();
        fixture.Main.Settings.McpSettings.ExposeLibraryTags = false;

        await fixture.LeaveAsync("workspace");

        fixture.Main.IsSettingsVisible.Should().BeFalse();
        fixture.Main.Layout.IsLibraryActive.Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_navigation_shows_one_dialog_and_cancel_keeps_the_current_tab()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        TaskCompletionSource<ConfirmDialogResult?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dialogs.PendingAnswer = answer.Task;
        fixture.Main.Settings.McpSettings.ExposeLibraryTags = false;
        WorkspaceTabViewModel current = fixture.Main.ActiveTab!;
        fixture.Main.ActiveTab = fixture.Main.Workspace.FindKind(WorkspaceTabKind.Library);
        Task firstNavigation = fixture.Main.Layout.WaitForNavigationAsync();
        await fixture.Main.Settings.SelectSectionAsync("llm");
        fixture.Dialogs.Shown.Should().ContainSingle();
        fixture.Main.ActiveTab.Should().BeSameAs(current);
        answer.SetResult(ConfirmDialogResult.Cancel);
        await firstNavigation;
        fixture.Main.ActiveTab.Should().BeSameAs(current);
        fixture.Main.Settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("mcp");
    }

    [Fact]
    public async Task Unchanged_mcp_settings_leave_without_a_dialog()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        await fixture.Main.Settings.SelectSectionAsync("llm");
        fixture.Main.Settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("llm");
        fixture.Dialogs.Shown.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_port_fallback_does_not_create_pending_edits_or_change_the_discard_baseline(bool edit)
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Discard);
        using TcpListener occupied = new(IPAddress.Loopback, fixture.InitialPort);
        occupied.Start();
        await fixture.Main.StartMcpServerAsync();
        fixture.Main.McpRunningSettings!.Port.Should().NotBe(fixture.InitialPort);
        await fixture.Main.Settings.McpSettings.LoadAsync();
        fixture.Main.Settings.McpSettings.HasPendingLeaveDecision.Should().BeFalse();
        if (edit)
        {
            fixture.Main.Settings.McpSettings.ExposeLibraryTags = false;
        }

        await fixture.LeaveAsync("subpage");

        fixture.AssertLeft("subpage");
        fixture.Dialogs.Shown.Should().HaveCount(edit ? 1 : 0);
        fixture.Main.Settings.McpSettings.HasPendingLeaveDecision.Should().BeFalse();
        (await fixture.ReadSettingsAsync()).Port.Should().Be(fixture.InitialPort);
        (await fixture.ReadSettingsAsync()).ExposeLibraryTags.Should().BeTrue();
    }

    [Fact]
    public async Task Starting_mcp_before_loading_its_settings_does_not_prompt_on_exit()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Discard, openMcp: false);
        _ = fixture.Main.Settings.McpSettings;

        await fixture.Main.StartMcpServerAsync();

        fixture.Main.HasPendingMcpSettings.Should().BeFalse();
        (await fixture.Main.ConfirmMcpSettingsBeforeExitAsync()).Should().BeTrue();
        fixture.Dialogs.Shown.Should().BeEmpty();
        (await fixture.ReadSettingsAsync()).Port.Should().Be(fixture.InitialPort);
    }

    [Fact]
    public async Task Run_child_selections_round_trip_without_a_parent_grant_or_enabling_global_send()
    {
        await using Fixture fixture = await Fixture.CreateAsync(ConfirmDialogResult.Cancel);
        McpSettingsViewModel mcp = fixture.Main.Settings.McpSettings;
        mcp.ToolOverrides.Single(value => value.ToolName == "patchouli.send").Enabled = false;
        mcp.RunSendPermissions!.ToggleCommand.Execute(null);
        mcp.RunSendPermissions.Checked.Should().BeTrue();
        mcp.ToolOverrides.Single(value => value.ToolName == "patchouli.send").Enabled.Should().BeFalse();
        mcp.RunSendPermissions.Children.Single(value => value.Domain == "runs/translation").Enabled = false;
        mcp.RunSendPermissions.Checked.Should().BeNull();
        await mcp.SaveAsync();
        McpServerSettings persisted = await fixture.ReadSettingsAsync();
        persisted.DomainPermissions.Should().Contain(new McpDomainPermission("runs/ocr", "send", true));
        persisted.DomainPermissions.Should().Contain(new McpDomainPermission("runs/translation", "send", false));
        persisted.DomainPermissions.Should().NotContain(value => value.Domain == "runs" && value.Verb == "send");
        persisted.ToolOverrides.Should().Contain(value => value.ToolName == "patchouli.send" && !value.Enabled);
        await mcp.LoadAsync();
        mcp.RunSendPermissions!.Checked.Should().BeNull();
        mcp.RunSendPermissions.Children.Single(value => value.Domain == "runs/ocr").Enabled.Should().BeTrue();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TemporaryAppSettingsFile SettingsFile { get; } = new();
        public MainWindowViewModel Main { get; }
        public ChoiceDialogs Dialogs { get; } = new();
        public int InitialPort { get; private set; }

        private Fixture(ConfirmDialogResult? choice, TestScheduler? scheduler)
        {
            Dialogs.Choice = choice;
            Main = new MainWindowViewModel(settingsPath: SettingsFile.Path, dialogs: Dialogs,
                timingScheduler: scheduler, uiScheduler: ImmediateScheduler.Instance);
        }

        public static async Task<Fixture> CreateAsync(ConfirmDialogResult? choice, TestScheduler? scheduler = null,
            bool openMcp = true)
        {
            Fixture fixture = new(choice, scheduler);
            try
            {
                await fixture.Main.OpenDatabaseCommand.ExecuteAsync();
                await fixture.Main.Library.CreateCommand.ExecuteAsync();
                using TcpListener listener = new(IPAddress.Loopback, 0);
                listener.Start();
                fixture.InitialPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                IMcpServerSettingsService service = (await fixture.Main.ServicesAsync()).McpSettings;
                McpServerSettings baseline = (await service.GetSettingsAsync()).Value;
                (await service.SaveSettingsAsync(baseline with { Port = fixture.InitialPort })).IsSuccess.Should()
                    .BeTrue();
                if (openMcp)
                {
                    await fixture.Main.OpenSettingsAsync("mcp");
                }

                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<McpServerSettings> ReadSettingsAsync()
        {
            return (await (await Main.ServicesAsync()).McpSettings.GetSettingsAsync()).Value;
        }

        public async Task LeaveAsync(string route)
        {
            switch (route)
            {
                case "subpage":
                    Main.Settings.ActiveTabbedGroup!.SelectedTab = Main.Settings.ActiveTabbedGroup.Entries[0];
                    await Main.Settings.WaitForActiveSectionLoadAsync();
                    break;
                case "category":
                    Main.Settings.ActiveCategory = Main.Settings.Categories[0];
                    await Main.Settings.WaitForActiveSectionLoadAsync();
                    break;
                case "workspace":
                    Main.ActiveTab = Main.Workspace.FindKind(WorkspaceTabKind.Library);
                    await Main.Layout.WaitForNavigationAsync();
                    break;
                case "close":
                    (await Main.Workspace.CloseAsync("Settings")).Should().BeTrue();
                    break;
            }
        }

        public void AssertLeft(string route)
        {
            if (route == "subpage")
            {
                Main.Settings.ActiveTabbedGroup!.ActiveEntry.Id.Should().Be("llm");
            }
            else if (route == "category")
            {
                Main.Settings.ActiveCategory.IconName.Should().Be("Palette");
            }
            else
            {
                Main.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Library);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Main.DisposeAsync();
            SettingsFile.Dispose();
        }
    }

    private sealed class ChoiceDialogs : IDialogService
    {
        public ConfirmDialogResult? Choice { get; set; }
        public Task<ConfirmDialogResult?>? PendingAnswer { get; set; }
        public List<ConfirmDialogViewModel> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is not ConfirmDialogViewModel dialog)
            {
                return default;
            }

            Shown.Add(dialog);
            ConfirmDialogResult? result = PendingAnswer is { } pending ? await pending : Choice;
            return result is TResult typed ? typed : default;
        }
    }
}
