using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public partial class ChapterEditorPanel : UserControl
{
    private ChapterSidebarViewModel? _observedSidebar;
    private ChapterScanRowViewModel? _dragSource;
    private ChapterSidebarViewModel? _dragOwner;
    private Point _dragStart;
    private Border? _dropTarget;
    private bool _isDragging;

    private void ClearDropIndicator()
    {
        _dropTarget?.Classes.Remove("before");
        _dropTarget?.Classes.Remove("after");
        _dropTarget?.Classes.Remove("inside");
        _dropTarget = null;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ChapterScanRowViewModel row } ||
            DataContext is not ChapterSidebarViewModel { IsBusy: false, IsFormOpen: false } owner ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragSource = row;
        _dragOwner = owner;
        _dragStart = e.GetPosition(this);
        _isDragging = false;
        e.Pointer.Capture(sender as Control);
        e.Handled = true;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Vector delta = e.GetPosition(this) - _dragStart;
        _isDragging |= Math.Abs(delta.X) + Math.Abs(delta.Y) > 8;
        if (!_isDragging)
        {
            return;
        }

        ClearDropIndicator();
        Border? card = this.GetVisualsAt(e.GetPosition(this)).OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("PreviewChapter"));
        if (card?.DataContext is not ChapterScanRowViewModel target || target == _dragSource)
        {
            return;
        }

        _dropTarget = card;
        bool inside = delta.X >= 20 || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        card.Classes.Add(inside ? "inside" : e.GetPosition(card).Y > card.Bounds.Height / 2 ? "after" : "before");
        ScrollViewer? scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null)
        {
            double y = e.GetPosition(scroll).Y;
            if (y < 24 || y > scroll.Bounds.Height - 24)
            {
                scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + (y < 24 ? -12 : 12));
            }
        }

        e.Handled = true;
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ChapterScanRowViewModel? source = _dragSource;
        ChapterSidebarViewModel? owner = _dragOwner;
        Border? card = _dropTarget;
        bool dragging = _isDragging;
        Vector delta = e.GetPosition(this) - _dragStart;
        bool after = card is not null && e.GetPosition(card).Y > card.Bounds.Height / 2;
        e.Pointer.Capture(null);
        ClearDropIndicator();
        _dragSource = null;
        _dragOwner = null;
        _isDragging = false;
        if (dragging && source is not null && card?.DataContext is ChapterScanRowViewModel target &&
            ReferenceEquals(owner, DataContext))
        {
            owner?.DropPreview(source, target, after,
                delta.X >= 20 || e.KeyModifiers.HasFlag(KeyModifiers.Control), delta.X <= -20);
        }

        e.Handled = true;
    }

    private void OnPreviewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ClearDropIndicator();
        _dragSource = null;
        _dragOwner = null;
        _isDragging = false;
    }

    public ChapterEditorPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveSidebar();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopObservingSidebar();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        StopObservingSidebar();
        if (VisualRoot is not null)
        {
            ObserveSidebar();
        }
    }

    private void ObserveSidebar()
    {
        if (ReferenceEquals(_observedSidebar, DataContext))
        {
            return;
        }

        StopObservingSidebar();
        _observedSidebar = DataContext as ChapterSidebarViewModel;
        if (_observedSidebar is null)
        {
            return;
        }

        _observedSidebar.PropertyChanged += OnSidebarPropertyChanged;
        RevealActiveEditor();
    }

    private void StopObservingSidebar()
    {
        ClearDropIndicator();
        _dragSource = null;
        _dragOwner = null;
        if (_observedSidebar is not null)
        {
            _observedSidebar.PropertyChanged -= OnSidebarPropertyChanged;
        }

        _observedSidebar = null;
    }

    private void OnSidebarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChapterSidebarViewModel.IsFormOpen)
            or nameof(ChapterSidebarViewModel.IsDeletePending))
        {
            RevealActiveEditor();
        }
    }

    private void RevealActiveEditor()
    {
        ChapterSidebarViewModel? sidebar = _observedSidebar;
        if (sidebar is null || (!sidebar.IsFormOpen && !sidebar.IsDeletePending))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (VisualRoot is null || !ReferenceEquals(sidebar, _observedSidebar))
            {
                return;
            }

            UpdateLayout();
            if (sidebar.IsFormOpen)
            {
                ChapterForm.BringIntoView(new Rect(0, 0, ChapterForm.Bounds.Width,
                    Math.Min(160, ChapterForm.Bounds.Height)));
                ChapterTitleInput.Focus();
            }
            else if (sidebar.IsDeletePending)
            {
                ChapterDeleteConfirmation.BringIntoView();
                ChapterConfirmDelete.Focus();
            }
        }, DispatcherPriority.Loaded);
    }
}
