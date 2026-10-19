using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels.Agents;

namespace Patchouli.UI.Views;

/// <summary>
/// View side of the Agent integration page: it anchors and focuses the @-reference popup, forwards
/// the picker's keyboard contract, and detaches the terminal control from the visual tree when the
/// retained tab page is released so the cached session can be reattached on the next open.
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
        // The view model keeps the EmbeddedTerminal (and its PTY) alive when the user chose to leave
        // the session in the background. Clearing Content detaches the control from this page's
        // visual/logical tree so a future page instance can attach it again.
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
                _viewModel.CommitSelectedItem();
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
        _viewModel?.CommitSelectedItem();
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
            _viewModel?.ApplyPreset(key);
        }
    }

    private void FocusInput()
    {
        Dispatcher.UIThread.Post(() => InputBox.Focus(), DispatcherPriority.Input);
    }
}
