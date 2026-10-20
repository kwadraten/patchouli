using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Mcp;
using Patchouli.Infrastructure.Mcp;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class RunSendPermissionsTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task Checkbox_clicks_select_all_clear_all_and_promote_partial_to_all()
    {
        await headless.Session.Dispatch(() =>
        {
            Dictionary<string, bool> saved = new();
            McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow);
            RunSendPermissionsViewModel group = new(McpPermissionPolicy.RunSendDomains, settings,
                (domain, enabled) => saved[domain] = enabled);
            ClickableCheckBox parent = new()
                { IsThreeState = true, Command = group.ToggleCommand, DataContext = group };
            parent.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(group.Checked)) { Mode = BindingMode.OneWay });
            parent.IsChecked.Should().BeFalse();
            parent.InvokeClick();
            Dispatcher.UIThread.RunJobs();
            group.Checked.Should().BeTrue();
            saved.Values.Should().OnlyContain(value => value);
            parent.InvokeClick();
            Dispatcher.UIThread.RunJobs();
            group.Checked.Should().BeFalse();
            group.Children[0].Enabled = true;
            Dispatcher.UIThread.RunJobs();
            parent.IsChecked.Should().BeNull();
            parent.InvokeClick();
            Dispatcher.UIThread.RunJobs();
            group.Checked.Should().BeTrue();
            parent.IsChecked.Should().BeTrue();
            Expander expander = new() { Content = parent, IsExpanded = true };
            expander.IsExpanded = false;
            group.Checked.Should().BeTrue();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Newly_discovered_child_is_off_and_changes_a_former_all_selection_to_partial()
    {
        await headless.Session.Dispatch(() =>
        {
            McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
            {
                DomainPermissions = McpPermissionPolicy.RunSendDomains
                    .Select(domain => new McpDomainPermission(domain, "send", true)).ToArray()
            };
            RunSendPermissionsViewModel group = new(McpPermissionPolicy.RunSendDomains.Append("runs/future"), settings,
                (_, _) => { });
            group.Checked.Should().BeNull();
            group.Children.Single(value => value.Domain == "runs/future").Enabled.Should().BeFalse();
        }, CancellationToken.None);
    }

    private sealed class ClickableCheckBox : CheckBox
    {
        public void InvokeClick()
        {
            OnClick();
        }
    }
}
