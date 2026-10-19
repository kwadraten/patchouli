using FluentAssertions;
using Patchouli.Core.Agents;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D3: the Agent workspace page. Preset prompts are editable text only and never send; the
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
    public async Task Applying_a_preset_only_fills_the_composer_and_never_launches_an_agent()
    {
        await _headless.Session.Dispatch(async () =>
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

            workspace.ApplyPreset("organize");

            workspace.InputText.Should().Be(
                workspace.Presets.Single(preset => preset.Key == "organize").Template);
            workspace.CanCopyInput.Should().BeTrue();
            workspace.CanInjectInput.Should().BeFalse();
            workspace.Terminal.Should().BeNull();
            workspace.HasLaunched.Should().BeFalse();
            workspace.IsRunning.Should().BeFalse();
            main.Status.Should().Contain("已填入预置提示词").And.Contain("自行发送");
            main.StatusIsError.Should().BeFalse();
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-preset")]
    public async Task An_unknown_or_blank_preset_is_ignored(string? key)
    {
        await _headless.Session.Dispatch(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            AgentWorkspaceViewModel workspace = main.AgentWorkspace;
            workspace.InputText = "hand-written prompt";

            workspace.ApplyPreset(key);

            workspace.InputText.Should().Be("hand-written prompt");
            main.Status.Should().NotContain("已填入预置提示词");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Without_an_open_database_the_page_reports_the_unavailable_state()
    {
        await _headless.Session.Dispatch(async () =>
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
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_item_picker_filters_one_snapshot_and_commits_the_selection_without_sending()
    {
        await _headless.Session.Dispatch(async () =>
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
            Result<ItemMetadata> first = await services.Items.CreateItemAsync("book", "机器学习基础");
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
            workspace.CommitSelectedItem();

            workspace.InputText.Should().Contain(selected.Title)
                .And.Contain(selected.ItemIdText)
                .And.Contain(selected.CanonicalUri)
                .And.Contain("Item ID:");
            workspace.IsItemPickerOpen.Should().BeFalse();
            workspace.CanInjectInput.Should().BeFalse("committing a reference must not send it");
            workspace.HasLaunched.Should().BeFalse();
            main.Status.Should().Contain("尚未发送");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Disabled_settings_render_the_disabled_empty_state()
    {
        await _headless.Session.Dispatch(async () =>
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
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_missing_custom_executable_renders_the_not_installed_empty_state()
    {
        await _headless.Session.Dispatch(async () =>
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
        }, CancellationToken.None);
    }

    private static void WriteAgentsSettings(TemporaryAppSettingsFile settings,
        AgentIntegrationAppSettings agents)
    {
        PatchouliAppSettings current = PatchouliAppSettings.Load(settings.Path);
        (current with { Agents = agents }).Save(settings.Path).IsSuccess.Should().BeTrue();
    }
}
