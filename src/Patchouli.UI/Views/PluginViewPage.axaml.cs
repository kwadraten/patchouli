using Avalonia;
using Avalonia.Controls;
using Patchouli.UI.Controls;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

/// <summary>
/// Workspace-tab surface for a plugin-declared HTML view. Loads the session once on first
/// activation/attach and releases the native web view when the tab closes (ADR 0041: the page's
/// visual tree is retained by <see cref="WorkspaceTabContentHost"/>, the webview is not).
/// </summary>
public sealed partial class PluginViewPage : UserControl, IWorkspaceTabPage
{
    private PluginViewViewModel? _viewModel;
    private bool _isClosed;

    public PluginViewPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel = _isClosed ? null : DataContext as PluginViewViewModel;
        EnsureLoaded();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (_isClosed || _viewModel is null || VisualRoot is null)
        {
            return;
        }

        _viewModel.EnsureLoadedAsync().Observe(nameof(PluginViewPage), "ensure-loaded");
    }

    /// <inheritdoc/>
    public void OnTabActivated()
    {
        EnsureLoaded();
    }

    /// <inheritdoc/>
    public void OnTabDeactivated()
    {
    }

    /// <inheritdoc/>
    public void OnTabClosed()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        WebHost.Release();
        _viewModel?.OnClosed();
        _viewModel = null;
    }
}
