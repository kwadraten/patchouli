using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Patchouli.UI.Controls;
using Patchouli.Core.Agents;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels.Agents;

namespace Patchouli.UI.Views;

/// <summary>
/// View side of the Agent integration page: it anchors and focuses the @-reference popup, forwards
/// the picker's keyboard contract, and releases the terminal when its tab closes.
/// </summary>
public sealed partial class AgentWorkspacePage : UserControl, IWorkspaceTabPage
{
    private AgentWorkspaceViewModel? _viewModel;
    private bool _isClosed;

    public AgentWorkspacePage()
    {
        InitializeComponent();
        ItemPickerPopup.PlacementTarget = ItemPickerButton;
        ItemPickerPopup.Opened += OnItemPickerOpened;
        // Tunnel so the search TextBox and ListBox cannot consume Up/Down/Enter/Esc before the
        // picker's keyboard contract sees them.
        ItemPickerPanel.AddHandler(KeyDownEvent, OnItemPickerKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as AgentWorkspaceViewModel;
        if (_viewModel is not null && !_isClosed)
        {
            _viewModel.OnPageAttached();
        }
    }

    public void OnTabActivated()
    {
        if (_isClosed)
        {
            return;
        }

        _viewModel?.OnPageActivated();
    }

    public void OnTabDeactivated()
    {
    }

    public void OnTabClosed()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _viewModel?.OnPageClosed();
        // Closing a tab ends its terminal session; the next page receives a fresh terminal.
        TerminalHost.Content = null;
    }

    private void OnItemPickerOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                ItemSearchBox.Focus();
                ItemSearchBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void OnItemPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.CloseItemPicker();
                FocusInput();
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.CommitSelectedItemAsync().Observe("agent-workspace", "paste-reference");
                FocusInput();
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveItemSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                _viewModel.MoveItemSelection(1);
                e.Handled = true;
                break;
        }
    }

    private void OnItemPickerDoubleTapped(object? sender, TappedEventArgs e)
    {
        _viewModel?.CommitSelectedItemAsync().Observe("agent-workspace", "paste-reference");
        FocusInput();
    }

    private void OnItemPickerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ItemPickerList.SelectedItem is { } item)
        {
            ItemPickerList.ScrollIntoView(item);
        }
    }

    private void OnPresetMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string key })
        {
            _viewModel?.ApplyPresetAsync(key).Observe("agent-workspace", "paste-preset");
        }
    }

    private void OnAgentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is { } workspace && AgentSelector.SelectedItem is AgentCatalogEntry entry &&
            !string.Equals(workspace.SelectedAgent?.Id, entry.Id, StringComparison.Ordinal))
        {
            SwitchAgentAsync(workspace, entry.Id).Observe("agent-workspace", "switch-agent");
        }
    }

    private async Task SwitchAgentAsync(AgentWorkspaceViewModel workspace, string agentId)
    {
        try
        {
            await workspace.SwitchAgentAsync(agentId);
        }
        finally
        {
            AgentSelector.SetCurrentValue(ComboBox.SelectedItemProperty, workspace.SelectedAgent);
        }
    }

    private void FocusInput()
    {
        Dispatcher.UIThread.Post(() => _viewModel?.Terminal?.TerminalControl.Focus(), DispatcherPriority.Input);
    }
}
