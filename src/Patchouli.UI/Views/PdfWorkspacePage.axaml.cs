using Avalonia;
using Avalonia.Controls;
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
    private bool _resizeBBox;
    private DateTimeOffset _lastTreeClick;
    private PdfBBoxViewModel? _lastTreeBox;
    private PdfBBoxViewModel? _draggedTreeBox;
    private PdfBBoxViewModel? _draggedEditor;
    private Point _editorDragStart;
    private double _editorStartX;
    private double _editorStartY;

    public PdfWorkspacePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _workspace?.SelectionChanged -= OnSelectionChanged;

        _workspace = DataContext as PdfWorkspaceViewModel;
        _workspace?.SelectionChanged += OnSelectionChanged;
    }

    private void OnSelectionChanged(PdfBBoxViewModel? bbox)
    {
        if (bbox is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => CenterSelectedPair(bbox), DispatcherPriority.Render);
    }

    private void CenterSelectedPair(PdfBBoxViewModel bbox)
    {
        if (this.FindControl<ScrollViewer>("PdfScrollViewer") is { } pdfScroll &&
            this.FindControl<ItemsControl>("BBoxItems") is { } boxes &&
            FindItemVisual(boxes, bbox) is { } bboxVisual)
        {
            CenterVertically(pdfScroll, bboxVisual);
        }

        if (this.FindControl<ScrollViewer>("PreviewScrollViewer") is { } previewScroll &&
            this.FindControl<ItemsControl>("PreviewItems") is { } previews &&
            _workspace?.PreviewBlocks.FirstOrDefault(block => block.BoxId == bbox.BoxId) is { } preview &&
            FindItemVisual(previews, preview) is { } previewVisual)
        {
            CenterVertically(previewScroll, previewVisual);
        }
    }

    private static Control? FindItemVisual(ItemsControl items, object item)
    {
        return items.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, item));
    }

    private static void CenterVertically(ScrollViewer scrollViewer, Control target)
    {
        Point? position = target.TranslatePoint(default, scrollViewer);
        if (position is null || scrollViewer.Viewport.Height <= 0)
        {
            return;
        }

        double offset = Math.Max(0, position.Value.Y + target.Bounds.Height / 2 - scrollViewer.Viewport.Height / 2);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, offset);
    }

    private void OnBBoxPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is PdfBBoxViewModel bbox)
        {
            if (DataContext is PdfWorkspaceViewModel pdf)
            {
                pdf.SelectedBox = bbox;
                if (pdf.IsEditMode && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed &&
                    this.FindControl<ItemsControl>("BBoxItems") is { } boxes)
                {
                    Point point = e.GetPosition(control);
                    _draggedBBox = bbox;
                    _dragStart = e.GetPosition(boxes);
                    _dragLeft = bbox.Left;
                    _dragTop = bbox.Top;
                    _dragWidth = bbox.Width;
                    _dragHeight = bbox.Height;
                    _resizeBBox = point.X >= bbox.Width - 12 && point.Y >= bbox.Height - 12;
                    e.Pointer.Capture(control);
                    e.Handled = true;
                }
            }
        }
    }

    private void OnBBoxPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_draggedBBox is null || this.FindControl<ItemsControl>("BBoxItems") is not { } boxes)
        {
            return;
        }

        Point current = e.GetPosition(boxes);
        double dx = current.X - _dragStart.X;
        double dy = current.Y - _dragStart.Y;
        if (_resizeBBox)
        {
            _draggedBBox.SetCanvasBBox(_dragLeft, _dragTop, Math.Max(5, _dragWidth + dx),
                Math.Max(5, _dragHeight + dy));
        }
        else
        {
            _draggedBBox.SetCanvasBBox(_dragLeft + dx, _dragTop + dy, _dragWidth, _dragHeight);
        }
    }

    private void OnBBoxPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
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

    private void OnCanvasPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf && sender is Control control)
        {
            Point p = e.GetPosition(control);
            pdf.OnPointerPressed(p.X, p.Y);
        }
    }

    private void OnCanvasPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf && sender is Control control)
        {
            Point p = e.GetPosition(control);
            pdf.OnPointerMoved(p.X, p.Y);
        }
    }

    private void OnCanvasPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (DataContext is PdfWorkspaceViewModel pdf)
        {
            pdf.OnPointerReleased();
        }
    }

    private void OnTreeNodePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PdfBBoxViewModel bbox } || _workspace is null)
        {
            return;
        }

        _workspace.SelectedBox = bbox;
        _draggedTreeBox = bbox;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ReferenceEquals(_lastTreeBox, bbox) && now - _lastTreeClick < TimeSpan.FromMilliseconds(500) &&
            !bbox.IsLogicalPage && _workspace.IsEditMode)
        {
            bbox.IsEditorOpen = true;
        }

        _lastTreeBox = bbox;
        _lastTreeClick = now;
    }

    private void OnTreeNodePointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (sender is Control { DataContext: PdfBBoxViewModel target } && _draggedTreeBox is { } moving &&
            _workspace is { IsEditMode: true } && !ReferenceEquals(moving, target))
        {
            _ = _workspace.MoveBoxToAsync(moving, target);
        }

        _draggedTreeBox = null;
    }

    private void OnOpenBoxEditor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PdfBBoxViewModel bbox } && !bbox.IsLogicalPage)
        {
            bbox.IsEditorOpen = true;
        }
    }

    private void OnBoxEditorDragPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PdfBBoxViewModel bbox })
        {
            _draggedEditor = bbox;
            _editorDragStart = e.GetPosition(this);
            _editorStartX = bbox.EditorOffsetX;
            _editorStartY = bbox.EditorOffsetY;
            e.Pointer.Capture((Control)sender);
        }
    }

    private void OnBoxEditorDragMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_draggedEditor is null)
        {
            return;
        }

        Point current = e.GetPosition(this);
        _draggedEditor.EditorOffsetX = _editorStartX + current.X - _editorDragStart.X;
        _draggedEditor.EditorOffsetY = _editorStartY + current.Y - _editorDragStart.Y;
    }

    private void OnBoxEditorDragReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _draggedEditor = null;
        e.Pointer.Capture(null);
    }

    private void OnCloseBoxEditor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PdfBBoxViewModel bbox })
        {
            bbox.IsEditorOpen = false;
        }
    }
}
