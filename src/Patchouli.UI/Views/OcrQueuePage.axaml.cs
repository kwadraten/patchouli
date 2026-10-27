using Avalonia;
using Avalonia.Controls;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

/// <summary>
/// Queue page lifecycle: the view model only pumps snapshots while the page is effectively
/// usable (attached, not closed, and — inside the retained tab host — the active tab). While
/// hidden it marks refresh state dirty and performs no host work at all.
/// </summary>
public sealed partial class OcrQueuePage : UserControl, IWorkspaceTabPage
{
    private OcrQueueViewModel? _observed;
    private bool _isClosed;
    private bool _observing;

    public OcrQueuePage()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _observing = true;
        SyncObserving();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _observing = false;
        SyncObserving();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        SyncObserving();
    }

    public void OnTabActivated()
    {
        _observing = true;
        SyncObserving();
    }

    public void OnTabDeactivated()
    {
        _observing = false;
        SyncObserving();
    }

    public void OnTabClosed()
    {
        _isClosed = true;
        _observing = false;
        SyncObserving();
    }

    private void SyncObserving()
    {
        OcrQueueViewModel? target = _observing && !_isClosed ? DataContext as OcrQueueViewModel : null;
        if (ReferenceEquals(_observed, target))
        {
            return;
        }

        _observed?.StopObserving();
        _observed = target;
        _observed?.StartObserving();
    }
}
