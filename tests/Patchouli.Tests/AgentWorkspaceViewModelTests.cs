using System.Reflection;
using FluentAssertions;
using Iciclecreek.Terminal;
using Patchouli.Core.Agents;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Mcp;
using Patchouli.Terminal;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Agents;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D3: the Agent workspace page. Preset prompts paste into a running terminal without submission; the
/// item picker filters one client-side snapshot and the empty states explain why no agent can start.
/// The view model has no service-injection seam, so the database-backed cases open a real isolated
/// runtime database (the same pattern as the library UI tests). The shared session avoids resetting
/// the Avalonia dispatcher between the many cases in this class.
/// </summary>
[Collection("RetainedTabUI")]
public sealed class AgentWorkspaceViewModelTests
{
    private readonly AvaloniaSharedSessionFixture _headless;

    public AgentWorkspaceViewModelTests(AvaloniaSharedSessionFixture headless)
    {
        _headless = headless;
    }

    [Fact]
    public async Task Closing_a_page_before_launch_releases_its_uninitialized_terminal()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.OnPageAttached();
            workspace.Terminal.Should().NotBeNull();

            workspace.OnPageClosed();

            workspace.Terminal.Should().BeNull();
            workspace.HasActiveSession.Should().BeFalse();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Applying_a_preset_without_a_running_terminal_reports_an_error_and_never_launches()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;

            workspace.Presets.Should().HaveCount(4);
            workspace.Presets.Select(preset => preset.Key)
                .Should().Equal("organize", "investigate", "outline", "socratic");
            workspace.Presets.Select(preset => preset.Title)
                .Should().OnlyContain(title => !string.IsNullOrWhiteSpace(title));
            workspace.Presets.Select(preset => preset.Template)
                .Should().OnlyContain(template => !string.IsNullOrWhiteSpace(template));

            await workspace.ApplyPresetAsync("organize");

            workspace.Terminal.Should().BeNull();
            workspace.HasLaunched.Should().BeFalse();
            workspace.IsRunning.Should().BeFalse();
            main.Status.Should().Contain("尚未运行");
            main.StatusIsError.Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-preset")]
    public async Task An_unknown_or_blank_preset_is_ignored(string? key)
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            string previousStatus = main.Status;

            await workspace.ApplyPresetAsync(key);

            main.Status.Should().Be(previousStatus);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Without_an_open_database_the_page_reports_the_unavailable_state()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;

            await workspace.RefreshAsync(false);

            main.HasOpenRuntimeDatabase.Should().BeFalse();
            workspace.IsIntegrationDisabled.Should().BeFalse();
            workspace.IsAgentMissing.Should().BeFalse();
            workspace.EmptyStateTitle.Should().Be("尚未打开书库");
            workspace.EmptyStateMessage.Should().Contain("请先打开书库");
            workspace.CanStart.Should().BeFalse();
            workspace.ShowSettingsShortcut.Should().BeFalse(
                "the settings shortcut needs a host service, which does not exist yet");
            workspace.ShowRetryShortcut.Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_item_picker_filters_one_snapshot_and_commits_the_selection_without_sending()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("agent-picker")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            main.HasOpenRuntimeDatabase.Should().BeTrue();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            Result<ItemMetadata> first = await services.Items.CreateItemAsync("document", "机器学习基础");
            Result<ItemMetadata> second = await services.Items.CreateItemAsync(
                "article-journal", "深度学习综述");
            first.IsSuccess.Should().BeTrue(first.ErrorMessage);
            second.IsSuccess.Should().BeTrue(second.ErrorMessage);

            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            await workspace.OpenItemPickerCommand.ExecuteAsync();

            workspace.IsItemPickerOpen.Should().BeTrue();
            workspace.FilteredItems.Should().HaveCount(2);
            workspace.ItemPickerStatusText.Should().Be("共 2 条题录");
            workspace.SelectedItem.Should().NotBeNull();

            workspace.ItemSearchText = "机器";
            workspace.FilteredItems.Should().ContainSingle()
                .Which.Title.Should().Be("机器学习基础");
            workspace.ItemPickerStatusText.Should().Be("1 / 2 条匹配");

            workspace.ItemSearchText = "ARTICLE-JOURNAL";
            workspace.FilteredItems.Should().ContainSingle()
                .Which.Title.Should().Be("深度学习综述");

            workspace.ItemSearchText = first.Value.ItemId.ToString();
            workspace.FilteredItems.Should().ContainSingle()
                .Which.ItemId.Should().Be(first.Value.ItemId);

            workspace.ItemSearchText = "没有这个题录";
            workspace.FilteredItems.Should().BeEmpty();
            workspace.ItemPickerStatusText.Should().Be("0 / 2 条匹配");
            workspace.SelectedItem.Should().BeNull();
            workspace.MoveItemSelection(1);

            workspace.ItemSearchText = "";
            workspace.FilteredItems.Should().HaveCount(2);
            AgentItemReferenceViewModel selected =
                workspace.FilteredItems.Single(item => item.Title == "深度学习综述");
            workspace.SelectedItem = selected;
            await workspace.CommitSelectedItemAsync();

            selected.ReferenceText.Should().Be($"深度学习综述 (规范 URI: {selected.CanonicalUri})");
            workspace.IsItemPickerOpen.Should().BeTrue("a failed paste must preserve the selection");
            workspace.HasLaunched.Should().BeFalse();
            main.Status.Should().Contain("尚未运行");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Disabled_settings_render_the_disabled_empty_state()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            WriteAgentsSettings(settings, new AgentIntegrationAppSettings(false, "opencode"));
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("agent-disabled")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;

            await workspace.RefreshAsync(false);

            workspace.IsIntegrationDisabled.Should().BeTrue();
            workspace.IsAgentMissing.Should().BeFalse();
            workspace.EmptyStateTitle.Should().Be("Agent 集成已停用");
            workspace.EmptyStateMessage.Should().Contain("不会启动任何进程");
            workspace.AgentVersionText.Should().Be("未启用");
            workspace.ConnectionStatusText.Should().Be("已停用");
            workspace.CanStart.Should().BeFalse();
            workspace.ShowSettingsShortcut.Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_missing_custom_executable_renders_the_not_installed_empty_state()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            string missing = Path.Combine(Path.GetTempPath(),
                $"agent-missing-{Guid.NewGuid():N}.exe");
            WriteAgentsSettings(settings, AgentIntegrationAppSettings.Default()
                .WithCustomPath("opencode", missing));
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("agent-missing")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;

            await workspace.RefreshAsync(false);

            workspace.IsIntegrationDisabled.Should().BeFalse();
            workspace.IsAgentMissing.Should().BeTrue();
            workspace.EmptyStateTitle.Should().Be("OpenCode 未安装");
            workspace.EmptyStateMessage.Should().Contain("可执行文件不存在");
            workspace.AgentVersionText.Should().Be("未安装");
            workspace.ConnectionStatusText.Should().Be("未安装");
            workspace.CanStart.Should().BeFalse();
            workspace.ShowSettingsShortcut.Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_patchouli_link_activated_in_the_terminal_navigates_in_app_without_a_confirmation()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs)
            {
                RuntimeDatabasePath = settings.CreateDatabasePath("agent-link-internal")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync("document", "终端链接目标");
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            string target = McpResourceUris.ItemUri(created.Value.ItemId);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.OnPageAttached();
            workspace.Terminal.Should().NotBeNull();

            RaiseUrlClicked(workspace.Terminal!, target, true);

            (await WaitUntilAsync(() => main.Status.Contains("已打开题录", StringComparison.Ordinal)))
                .Should().BeTrue("a patchouli:// link routes to the in-app navigation entry point");
            main.Layout.Tabs.Should().Contain(tab => tab.Kind == WorkspaceTabKind.ItemEditor);
            dialogs.Shown.Should().BeEmpty("in-app navigation must not prompt");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_external_link_asks_for_confirmation_with_the_actual_target_and_a_cancel_never_launches()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.OnPageAttached();
            workspace.Terminal.Should().NotBeNull();
            const string target = "https://example.org/real-target?doi=10.1000/test";

            RaiseUrlClicked(workspace.Terminal!, target, true);

            (await WaitUntilAsync(() => dialogs.Shown.Count == 1)).Should().BeTrue();
            ConfirmDialogViewModel prompt = dialogs.Shown.Should().ContainSingle()
                .Which.Should().BeOfType<ConfirmDialogViewModel>().Which;
            prompt.Title.Should().Be("打开外部链接");
            prompt.Message.Should().Contain(target,
                "the confirmation shows the actual target URL, never the OSC 8 display text");
            await Task.Delay(200);
            dialogs.Shown.Should().HaveCount(1,
                "declining the confirmation must not launch anything or report a failure");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_confirmed_external_link_surfaces_a_failed_launch_with_the_actual_target()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new() { Choice = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.OnPageAttached();
            workspace.Terminal.Should().NotBeNull();
            const string target = "https://example.org/real-target?doi=10.1000/test";

            RaiseUrlClicked(workspace.Terminal!, target, true);

            (await WaitUntilAsync(() => dialogs.Shown.Count == 2)).Should().BeTrue(
                "the headless session has no desktop launcher, so a confirmed open fails visibly");
            dialogs.Shown[0].Should().BeOfType<ConfirmDialogViewModel>();
            InfoDialogViewModel failure = dialogs.Shown[1].Should().BeOfType<InfoDialogViewModel>().Which;
            failure.Message.Should().Contain(target);
            main.Status.Should().Contain("无法打开链接");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_replaced_terminal_stops_activating_links_and_the_new_one_activates_once_per_click()
    {
        await _headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            LinkDialogs dialogs = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.OnPageAttached();
            EmbeddedTerminal first = workspace.Terminal!;
            workspace.OnPageClosed();
            workspace.Terminal.Should().BeNull();
            workspace.OnPageAttached();
            EmbeddedTerminal second = workspace.Terminal!;
            second.Should().NotBeSameAs(first);

            RaiseUrlClicked(first, "https://example.org/stale-terminal");
            await Task.Delay(200);
            dialogs.Shown.Should().BeEmpty("a replaced terminal is unsubscribed and must not navigate");

            RaiseUrlClicked(second, "https://example.org/live-terminal");
            (await WaitUntilAsync(() => dialogs.Shown.Count == 1)).Should().BeTrue();

            // A second, sequential activation is a new gesture and prompts again, exactly once.
            RaiseUrlClicked(second, "https://example.org/live-terminal");
            (await WaitUntilAsync(() => dialogs.Shown.Count == 2)).Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    private static void WriteAgentsSettings(TemporaryAppSettingsFile settings,
        AgentIntegrationAppSettings agents)
    {
        PatchouliAppSettings current = PatchouliAppSettings.Load(settings.Path);
        (current with { Agents = agents }).Save(settings.Path).IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// Simulates a Ctrl+click the way the terminal control reports it: UrlClicked is a field-like
    /// event on <see cref="TerminalControl"/>, so a test raises it through the compiler-generated
    /// backing delegate. A missing delegate means nobody is subscribed, which is itself observable.
    /// </summary>
    private static void RaiseUrlClicked(EmbeddedTerminal terminal, string url, bool fromSequence = false)
    {
        EventHandler<UrlClickedEventArgs>? handlers = typeof(TerminalControl)
            .GetField("UrlClicked", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(terminal.TerminalControl) as EventHandler<UrlClickedEventArgs>;
        handlers?.Invoke(terminal.TerminalControl, new UrlClickedEventArgs(url, fromSequence));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>Records every dialog the link flow would show and answers with a fixed choice.</summary>
    private sealed class LinkDialogs : IDialogService
    {
        public ConfirmDialogResult Choice { get; set; } = ConfirmDialogResult.Cancel;
        public List<object> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.FromResult(Choice is TResult result ? result : default);
        }
    }
}
