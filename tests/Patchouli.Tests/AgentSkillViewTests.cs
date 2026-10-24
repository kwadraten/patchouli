using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Agents;
using LucideIcon = Patchouli.Lucide.Avalonia.Lucide;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class AgentSkillViewTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Each_agent_row_has_one_icon_button_whose_text_and_icon_switch_in_place()
    {
        await headless.Session.Dispatch<bool>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            using AgentTestDirectory isolated = new();
            string home = isolated.Combine("home");
            string state = isolated.Combine("state");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(state);
            AgentSkillService skills = new(new AgentSkillLocations(home, state));
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            using AgentIntegrationSettingsViewModel section = new(main, skills);
            section.Enabled = false;
            SettingsPage page = new();
            Window window = new() { Width = 850, Height = 900, Content = page };
            window.Show();
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                RetainedContentHost host = page.GetVisualDescendants().OfType<RetainedContentHost>().First();
                Control body = host.DataTemplates.Single(template => template.Match(section)).Build(section)!;
                body.DataContext = section;
                window.Content = body;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                foreach (AgentIntegrationRowViewModel row in section.Agents)
                {
                    Button button = body.GetVisualDescendants().OfType<Button>()
                        .Single(candidate => ReferenceEquals(candidate.Command, row.ToggleSkillCommand));
                    body.GetVisualDescendants().OfType<Button>()
                        .Count(candidate => ReferenceEquals(candidate.Command, row.ToggleSkillCommand))
                        .Should().Be(1);
                    row.ApplySkillStatus(
                        Result<AgentSkillStatus>.Success(new AgentSkillStatus("test", false, "native")));
                    Dispatcher.UIThread.RunJobs();
                    button.IsEnabled.Should().BeTrue();
                    button.IsEffectivelyEnabled.Should().BeTrue();
                    row.SkillToolTip.Should().Contain("native");
                    body.GetVisualDescendants().OfType<RadioButton>()
                        .Single(candidate => ReferenceEquals(candidate.DataContext, row)).IsEnabled.Should().BeFalse();
                    body.GetVisualDescendants().OfType<Button>()
                        .First(candidate => ReferenceEquals(candidate.Command, row.TogglePathEditorCommand))
                        .IsEnabled.Should().BeFalse();
                    button.GetVisualDescendants().OfType<TextBlock>().Should().Contain(block => block.Text == "注入");
                    button.GetVisualDescendants().OfType<LucideIcon>().Single().Icon.Should().Be("Download");

                    row.ApplySkillStatus(
                        Result<AgentSkillStatus>.Success(new AgentSkillStatus("test", true, "native")));
                    Dispatcher.UIThread.RunJobs();
                    button.GetVisualDescendants().OfType<TextBlock>().Should().Contain(block => block.Text == "清除已有");
                    button.GetVisualDescendants().OfType<LucideIcon>().Single().Icon.Should().Be("Trash2");
                    button.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(block => block.Text == "注入");
                }

                await section.DiscardAsync();
                Dispatcher.UIThread.RunJobs();
                body.GetVisualDescendants().OfType<RadioButton>().Should()
                    .HaveCount(section.Agents.Count).And.OnlyContain(button => button.IsEnabled,
                        "discard restores the saved enabled state and notifies cross-object row bindings");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }
}
