using FluentAssertions;
using Patchouli.Host.Agents;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class AgentSkillSettingsTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task User_skill_button_works_without_a_library_and_while_integration_is_disabled()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            using AgentTestDirectory isolated = new();
            SkillDialogs dialogs = new();
            AgentSkillLocations locations = CreateLocations(isolated);
            AgentSkillService skills = new(locations);
            string managedSkill = locations.GetManagedDirectory("opencode", isolated.Combine("workspace"));
            Directory.CreateDirectory(managedSkill);
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            using AgentIntegrationSettingsViewModel section = new(main, skills);
            // This is a user-level filesystem workflow, not a real-machine executable-discovery
            // integration test. LoadAsync starts that unrelated pipeline and initializes a Host.
            await section.RefreshSkillsAsync();
            section.Enabled = false;
            section.HasActiveOperation.Should()
                .BeFalse("refreshing user-level skills does not start executable discovery");
            main.HasOpenRuntimeDatabase.Should().BeFalse();
            AgentIntegrationRowViewModel row = section.Agents.Single(agent => agent.Entry.Id == "opencode");

            row.SkillActionText.Should().Be("注入");
            row.CanManageSkill.Should().BeTrue();
            bool dirtyBeforeSkillActions = section.IsDirty;

            await row.ToggleSkillCommand.ExecuteAsync();
            row.HasSkill.Should().BeFalse("a cancelled confirmation makes no filesystem changes");
            dialogs.Choice = ConfirmDialogResult.Confirm;
            await row.ToggleSkillCommand.ExecuteAsync();
            row.HasSkill.Should().BeTrue();
            row.SkillActionText.Should().Be("清除已有");
            row.SkillActionIcon.Should().Be("Trash2");
            Directory.Exists(managedSkill).Should().BeTrue("the user button must not manage workspace copies");
            section.IsDirty.Should().Be(dirtyBeforeSkillActions, "skill files are not AppSettings");

            string userSkill = skills.GetStatus("opencode").Value.Directory;
            string entry = Path.Combine(userSkill, "SKILL.md");
            string siblingSkill = Path.Combine(Path.GetDirectoryName(userSkill)!, "other-skill");
            Directory.CreateDirectory(siblingSkill);
            await File.WriteAllTextAsync(Path.Combine(siblingSkill, "SKILL.md"), "unrelated skill");
            await File.WriteAllTextAsync(entry, "custom content");
            await row.ToggleSkillCommand.ExecuteAsync();
            dialogs.Shown.Last().ConfirmDanger.Should().BeTrue();
            dialogs.Shown.Last().Message.Should().Contain("用户级").And.Contain("所有项目")
                .And.Contain("自定义修改").And.Contain("MCP 配置").And.Contain("token")
                .And.Contain("可能需重启");
            row.HasSkill.Should().BeFalse();
            await row.ToggleSkillCommand.ExecuteAsync();
            File.ReadAllText(entry).Should().Contain("name: patchouli");
            File.ReadAllText(Path.Combine(siblingSkill, "SKILL.md")).Should().Be("unrelated skill");
            Directory.Delete(userSkill, true);
            await section.RefreshSkillsAsync();
            row.SkillActionText.Should().Be("注入", "external removal is reflected by a disk refresh");
            section.IsDirty.Should().Be(dirtyBeforeSkillActions);
            main.HasOpenRuntimeDatabase.Should().BeFalse("user-level skill actions never require a Library Host");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Existing_user_folder_is_clearable_even_when_entry_is_missing_and_cancel_does_not_write()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            using AgentTestDirectory isolated = new();
            SkillDialogs dialogs = new();
            AgentSkillService skills = new(CreateLocations(isolated));
            string target = skills.GetStatus("opencode").Value.Directory;
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "custom.txt"), "keep until confirmed");
            await using MainWindowViewModel main = new(settingsPath: settings.Path, dialogs: dialogs);
            using AgentIntegrationSettingsViewModel section = new(main, skills);
            await section.RefreshSkillsAsync();
            AgentIntegrationRowViewModel row = section.Agents.Single(agent => agent.Entry.Id == "opencode");
            row.HasSkill.Should().BeTrue("directory presence, not SKILL.md validation, defines installed state");
            await row.ToggleSkillCommand.ExecuteAsync();
            File.Exists(Path.Combine(target, "custom.txt")).Should().BeTrue();
            dialogs.Choice = ConfirmDialogResult.Confirm;
            await row.ToggleSkillCommand.ExecuteAsync();
            Directory.Exists(target).Should().BeFalse();
            section.IsDirty.Should().BeFalse();
            return true;
        }, CancellationToken.None);
    }

    private static AgentSkillLocations CreateLocations(AgentTestDirectory isolated)
    {
        string home = isolated.Combine("home");
        string state = isolated.Combine("state");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(state);
        return new AgentSkillLocations(home, state);
    }

    private sealed class SkillDialogs : IDialogService
    {
        public ConfirmDialogResult Choice { get; set; } = ConfirmDialogResult.Cancel;
        public List<ConfirmDialogViewModel> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (viewModel is ConfirmDialogViewModel confirm)
            {
                Shown.Add(confirm);
            }

            return Task.FromResult(Choice is TResult result ? result : default);
        }
    }
}
