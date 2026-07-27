using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class PdfWorkspacePage : UserControl
{
    private PdfWorkspaceViewModel? _workspace;
    private PdfBBoxViewModel? _draggedBBox;
    private Point _dragStart;
    private double _dragLeft;
    private double _dragTop;
    private double _dragWidth;
    private double _dragHeight;
    private BBoxDragZone _dragZone;
    private DateTimeOffset _lastTreeClick;
    private PdfBBoxViewModel? _lastTreeBox;
    private PdfBBoxViewModel? _draggedTreeBox;
    private PdfBBoxViewModel? _treePendingSelectBox;
    private Point _treeDragStart;
    private bool _isTreeDragging;
    private Border? _dropTargetRow;
    private bool _dropAbove;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;

    public PdfWorkspacePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Tunnel so Ctrl+wheel is handled (and swallowed) before ScrollViewer's own bubble-phase scrolling.
        PdfScrollViewer.AddHandler(PointerWheelChangedEvent, OnScrollPointerWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnBBoxContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_workspace is not { IsEditMode: true })
        {
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _workspace = DataContext as PdfWorkspaceViewModel;
    }

    private void OnBBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PdfBBoxViewModel bbox ||
            DataContext is not PdfWorkspaceViewModel pdf)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(control).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            // Let the press bubble to the ScrollViewer for middle-drag panning.
            return;
        }

        bool additive = !properties.IsRightButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool wasSelected = bbox.IsSelected;
        if (!properties.IsRightButtonPressed || !wasSelected)
        {
            pdf.SelectBox(bbox, additive);
        }

        e.Handled = true;
        if (!pdf.IsEditMode || additive || !wasSelected ||
            !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed ||
            this.FindControl<ItemsControl>("BBoxItems") is not { } boxes)
        {
            return;
        }

        _dragZone = HitZone(e.GetPosition(control), bbox.Width, bbox.Height);
        if (_dragZone == BBoxDragZone.None)
        {
            return;
        }

        _draggedBBox = bbox;
        _dragStart = e.GetPosition(boxes);
        _dragLeft = bbox.Left;
        _dragTop = bbox.Top;
        _dragWidth = bbox.Width;
        _dragHeight = bbox.Height;
        e.Pointer.Capture(control);
    }

    private static BBoxDragZone HitZone(Point point, double width, double height)
    {
        const double cornerSize = 14;
        bool left = point.X <= cornerSize;
        bool right = point.X >= width - cornerSize;
        bool top = point.Y <= cornerSize;
        bool bottom = point.Y >= height - cornerSize;
        if (left && top)
        {
            return BBoxDragZone.TopLeft;
        }

        if (right && top)
        {
            return BBoxDragZone.TopRight;
        }

        if (left && bottom)
        {
            return BBoxDragZone.BottomLeft;
        }

        if (right && bottom)
        {
            return BBoxDragZone.BottomRight;
        }

        return Math.Abs(point.X - width / 2) <= 10 && Math.Abs(point.Y - height / 2) <= 10
            ? BBoxDragZone.Move
            : BBoxDragZone.None;
    }

    private void OnBBoxPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedBBox is null || this.FindControl<ItemsControl>("BBoxItems") is not { } boxes)
        {
            return;
        }

        Point current = e.GetPosition(boxes);
        if (_dragZone == BBoxDragZone.Move)
        {
            double dx = current.X - _dragStart.X;
            double dy = current.Y - _dragStart.Y;
            _draggedBBox.SetCanvasBBox(_dragLeft + dx, _dragTop + dy, _dragWidth, _dragHeight);
            return;
        }

        double fixedX = _dragZone is BBoxDragZone.TopLeft or BBoxDragZone.BottomLeft
            ? _dragLeft + _dragWidth
            : _dragLeft;
        double fixedY = _dragZone is BBoxDragZone.TopLeft or BBoxDragZone.TopRight
            ? _dragTop + _dragHeight
            : _dragTop;
        _draggedBBox.SetCanvasBBox(
            Math.Min(fixedX, current.X),
            Math.Min(fixedY, current.Y),
            Math.Max(5, Math.Abs(current.X - fixedX)),
            Math.Max(5, Math.Abs(current.Y - fixedY)));
    }

    private void OnBBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedBBox is null)
        {
            return;
        }

        PdfBBoxViewModel bbox = _draggedBBox;
        _draggedBBox = null;
        e.Pointer.Capture(null);
        _ = bbox.SaveBBoxAsync();
        e.Handled = true;
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf && sender is Control control)
        {
            Point p = e.GetPosition(control);
            pdf.OnPointerPressed(p.X, p.Y);
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf && sender is Control control)
        {
            Point p = e.GetPosition(control);
            pdf.OnPointerMoved(p.X, p.Y);
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf)
        {
            pdf.OnPointerReleased(e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }
    }

    private void OnCanvasBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        _workspace?.ClearSelection();
    }

    private void OnScrollPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !e.GetCurrentPoint(scroll).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        _panStart = e.GetPosition(scroll);
        _panStartOffset = scroll.Offset;
        _isPanning = true;
        e.Pointer.Capture(scroll);
        e.Handled = true;
    }

    private void OnScrollPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || sender is not ScrollViewer scroll)
        {
            return;
        }

        Point position = e.GetPosition(scroll);
        scroll.Offset = new Vector(
            _panStartOffset.X - (position.X - _panStart.X),
            _panStartOffset.Y - (position.Y - _panStart.Y));
        e.Handled = true;
    }

    private void OnScrollPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnScrollPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll || DataContext is not PdfWorkspaceViewModel pdf ||
            !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        double oldZoom = pdf.Zoom;
        if (oldZoom <= 0)
        {
            return;
        }

        e.Handled = true;
        Point position = e.GetPosition(scroll);
        Vector offset = scroll.Offset;
        pdf.AdjustZoom(e.Delta.Y > 0 ? 0.1 : -0.1);
        double scale = pdf.Zoom / oldZoom;
        double x = (offset.X + position.X) * scale - position.X;
        double y = (offset.Y + position.Y) * scale - position.Y;
        Dispatcher.UIThread.Post(() => scroll.Offset = new Vector(Math.Max(0, x), Math.Max(0, y)),
            DispatcherPriority.Render);
    }

    private enum BBoxDragZone
    {
        None,
        Move,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private void OnTreeNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PdfBBoxViewModel bbox } control || _workspace is null)
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(control).Properties;
        bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (properties.IsRightButtonPressed)
        {
            if (!bbox.IsSelected)
            {
                _workspace.SelectBox(bbox, additive);
            }
        }
        else if (additive || !bbox.IsSelected)
        {
            _workspace.SelectBox(bbox, additive);
        }
        else
        {
            // Left press on an already-selected box: keep the multi-selection so a tree
            // drag can move the whole selection; collapse to this box on release if no drag occurs.
            _treePendingSelectBox = bbox;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ReferenceEquals(_lastTreeBox, bbox) && now - _lastTreeClick < TimeSpan.FromMilliseconds(500) &&
            !bbox.IsLogicalPage && _workspace.IsEditMode)
        {
            _ = _workspace.OpenBoxEditorAsync(bbox);
        }

        _lastTreeBox = bbox;
        _lastTreeClick = now;
        if (properties.IsLeftButtonPressed)
        {
            _draggedTreeBox = bbox;
            _treeDragStart = e.GetPosition(BoxTreeItems);
            _isTreeDragging = false;
            e.Pointer.Capture(control);
        }
    }

    private void OnTreeNodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedTreeBox is null)
        {
            return;
        }

        Point position = e.GetPosition(BoxTreeItems);
        if (!_isTreeDragging)
        {
            bool beyondThreshold = Math.Abs(position.X - _treeDragStart.X) > 6 ||
                                   Math.Abs(position.Y - _treeDragStart.Y) > 6;
            if (!beyondThreshold || _workspace is not { IsEditMode: true })
            {
                return;
            }

            _isTreeDragging = true;
        }

        ClearDropIndicator();
        if (FindTreeRowAt(position) is { } row && row.DataContext is PdfBBoxViewModel target &&
            !ReferenceEquals(target, _draggedTreeBox))
        {
            _dropAbove = e.GetPosition(row).Y < row.Bounds.Height / 2;
            row.Classes.Add(_dropAbove ? "dropAbove" : "dropBelow");
            _dropTargetRow = row;
        }
    }

    private void OnTreeNodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isTreeDragging && _draggedTreeBox is { } moving && _workspace is not null &&
            _dropTargetRow?.DataContext is PdfBBoxViewModel target)
        {
            _ = _workspace.MoveBoxToAsync(moving, target, _dropAbove);
        }
        else if (!_isTreeDragging && _treePendingSelectBox is { } pending && _workspace is not null)
        {
            _workspace.SelectBox(pending, false);
        }

        _treePendingSelectBox = null;
        ClearDropIndicator();
        _isTreeDragging = false;
        _draggedTreeBox = null;
        e.Pointer.Capture(null);
    }

    private void ClearDropIndicator()
    {
        _dropTargetRow?.Classes.Remove("dropAbove");
        _dropTargetRow?.Classes.Remove("dropBelow");
        _dropTargetRow = null;
    }

    private Border? FindTreeRowAt(Point position)
    {
        foreach (Visual visual in BoxTreeItems.GetVisualsAt(position))
        {
            Border? row = visual.FindAncestorOfType<Border>(true);
            while (row is not null && !row.Classes.Contains("TreeRow"))
            {
                row = row.FindAncestorOfType<Border>();
            }

            if (row is not null)
            {
                return row;
            }
        }

        return null;
    }

    private void OnOpenBoxEditor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PdfBBoxViewModel bbox } && _workspace is not null)
        {
            _ = _workspace.OpenBoxEditorAsync(bbox);
        }
    }
}
