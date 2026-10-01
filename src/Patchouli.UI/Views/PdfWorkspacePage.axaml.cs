using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patchouli.Core.Ids;
using Patchouli.UI.Diagnostics;
using Patchouli.Reading;
using Patchouli.UI.Reading;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.Views;

public sealed partial class PdfWorkspacePage : UserControl
{
    private PdfWorkspaceViewModel? _workspace;
    private bool _workspaceEventsSubscribed;
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

    // Reading-mode page rail: the streamed pages cached by index (source + translation scenes),
    // so a font change or compare toggle rebuilds the combined reading document in page order. The
    // ReadingView renders one concatenated scene; page boundaries are pinned as badges in the left
    // rail at the measured top of each page's first block (see UpdateBookReadingBadges).
    private readonly SortedDictionary<int, ReadingScene> _bookReadingSourceScenes = new();
    private readonly SortedDictionary<int, ReadingScene?> _bookReadingTranslationScenes = new();

    // Real measured Y of the reading session's start page: recorded from the measured page offsets
    // after the initial window settles, so the reader opens on the page it started from rather than
    // the top of the book. The workspace asks the view to snap here once the window settles.
    private double? _bookReadingAnchorY;

    // When true, the next measured layout applies _bookReadingAnchorY to the scroller. Kept
    // pending across replay/initial delivery until the ReadingView has been measured at a real width.
    private bool _bookReadingAnchorPending;

    // Lazy, bounded page-image cache for whole-book media blocks: renders each page's pixel buffer
    // on demand and keeps only the visible page plus one neighbour on either side.
    private BookReadingImageSource? _bookReadingImages;

    public PdfWorkspacePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ReadingView.BlockClicked += OnReadingBlockClicked;
        TranslationReadingView.BlockClicked += OnReadingBlockClicked;
        // Tunnel so Ctrl+wheel is handled (and swallowed) before ScrollViewer's own bubble-phase scrolling.
        PdfScrollViewer.AddHandler(PointerWheelChangedEvent, OnScrollPointerWheelChanged,
            RoutingStrategies.Tunnel);
        BookReadingScroller.ScrollChanged += OnBookReadingScrollChanged;
        BookReadingScroller.LayoutUpdated += OnBookReadingScrollerLayoutUpdated;
        BookReadingView.Measured += OnBookReadingViewMeasured;
        BookReadingView.EditSelectionRequested += OnBookReadingEditSelectionRequested;
    }

    private void OnBBoxContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (_workspace is { IsEditMode: true } && sender is Control { DataContext: PdfBBoxViewModel box } control &&
            FlyoutBase.GetAttachedFlyout(this) is MenuFlyout menu)
        {
            if (!box.IsSelected)
            {
                _workspace.SelectBox(box, false);
            }

            menu.ShowAt(control);
        }
    }

    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _workspace is { IsEditMode: true } &&
            e.Source is not TextBox && (e.Source as Visual)?.FindAncestorOfType<TextBox>() is null)
        {
            _workspace.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeWorkspaceEvents();
        _workspace = DataContext as PdfWorkspaceViewModel;
        SubscribeWorkspaceEvents();
        if (VisualRoot is not null)
        {
            ReplayBookReadingIfNeeded();
        }
    }

    private void SubscribeWorkspaceEvents()
    {
        if (_workspaceEventsSubscribed || _workspace is null)
        {
            return;
        }

        _workspace.BookReadingStarted += OnBookReadingStarted;
        _workspace.BookReadingPageReady += OnBookReadingPageReady;
        _workspace.BookReadingAnchorRequested += OnBookReadingAnchorRequested;
        _workspace.PropertyChanged += OnBookReadingPropertyChanged;
        _workspaceEventsSubscribed = true;
    }

    private void ReplayBookReadingIfNeeded()
    {
        if (_workspace is { IsBookReadingMode: true })
        {
            // The view was recreated after reading mode had already started (e.g. a tab switch
            // back). The start/page events fired before this subscription existed, so ask the
            // workspace to replay what it has delivered.
            _workspace.ReplayBookReading();
        }
    }

    private void UnsubscribeWorkspaceEvents()
    {
        if (!_workspaceEventsSubscribed || _workspace is null)
        {
            return;
        }

        _workspace.BookReadingStarted -= OnBookReadingStarted;
        _workspace.BookReadingPageReady -= OnBookReadingPageReady;
        _workspace.BookReadingAnchorRequested -= OnBookReadingAnchorRequested;
        _workspace.PropertyChanged -= OnBookReadingPropertyChanged;
        _workspaceEventsSubscribed = false;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeWorkspaceEvents();
        ReplayBookReadingIfNeeded();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnsubscribeWorkspaceEvents();
        _bookReadingImages?.Dispose();
        _bookReadingImages = null;
    }

    private void OnReadingBlockClicked(object? sender, ReadingBlockActivation activation)
    {
        _workspace?.SelectReadingBlock(activation.BoxId);
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

        if (properties.IsRightButtonPressed && !pdf.IsEditMode)
        {
            // View mode: the right button is fully disabled (no selection, no context menu).
            e.Handled = true;
            return;
        }

        Focus();
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
        if (DataContext is PdfWorkspaceViewModel pdf && sender is Control control &&
            e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            Focus();
            Point p = e.GetPosition(control);
            pdf.OnPointerPressed(p.X, p.Y);
            if (pdf.IsDrawing)
            {
                e.Pointer.Capture(control);
                e.Handled = true;
            }
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
            e.Pointer.Capture(null);
        }
    }

    private void OnCanvasBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            Focus();
            _workspace?.ClearSelection();
        }
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

    private void OnOpenBoxEditor(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PdfBBoxViewModel bbox } && _workspace is not null)
        {
            _ = _workspace.OpenBoxEditorAsync(bbox);
        }
    }

    private void OnOverlapMarkerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PdfOverlapMarkerViewModel marker } && _workspace is not null)
        {
            _workspace.SelectOverlapPair(marker);
            e.Handled = true;
        }
    }

    private void OnCrossPageMarkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: PdfCrossPageContinuationViewModel marker } && _workspace is not null)
        {
            _ = marker.Continuation.JumpToContinuationSourceCommand.ExecuteAsync();
            e.Handled = true;
        }
    }

    private void OnTreeExpandToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PdfBBoxViewModel box } && _workspace is not null)
        {
            _workspace.ToggleTreeExpansion(box);
        }
    }

    // Keep chevron presses from bubbling to the row (selection / drag / double-click editor).
    private void OnTreeExpandTogglePressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnPageNumberKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox || _workspace is null)
        {
            return;
        }

        e.Handled = true;
        if (int.TryParse(textBox.Text, out int pageNumber))
        {
            _ = _workspace.GoToPageAsync(pageNumber);
        }
        else
        {
            textBox.Text = _workspace.PageNumberText;
        }
    }

    private void OnPageNumberGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }


    private void OnBookReadingStarted()
    {
        _bookReadingSourceScenes.Clear();
        _bookReadingTranslationScenes.Clear();
        _bookReadingAnchorY = null;
        _bookReadingAnchorPending = true;
        BookReadingBadgeRail.Children.Clear();
        if (_workspace is null)
        {
            return;
        }

        _bookReadingImages?.Dispose();
        _bookReadingImages = new BookReadingImageSource(_workspace.RenderBookReadingPageAsync);
        BookReadingView.SourceScene = ReadingScene.Empty;
        BookReadingView.TranslationScene = null;
        BookReadingView.ImageSource = _bookReadingImages;
        BookReadingScroller.Offset = Vector.Zero;
        ApplyBookReadingFont();
        ApplyTranslationCompareVisibility();
        BookReadingView.InvalidateMeasure();
    }

    // Snaps the scroll offset to the start page the view has been measuring since it arrived.
    // Raised when the initial window settles (before the prefetch guard lifts) and after a
    // replay. The actual offset is applied as soon as the ReadingView has been measured at a
    // real width; until then the request stays pending and ScrollChanged retries after layout.
    private void OnBookReadingAnchorRequested()
    {
        _bookReadingAnchorPending = true;
        TryApplyBookReadingAnchor();
    }

    private void OnBookReadingScrollerLayoutUpdated(object? sender, EventArgs e)
    {
        TryApplyBookReadingAnchor();
    }

    private void OnBookReadingViewMeasured(object? sender, EventArgs e)
    {
        TryApplyBookReadingAnchor();
        // Badge positions derive from the measured block tops, so re-pin them after every reflow:
        // a view recreated by a tab switch first measures at the fallback width, and media image
        // loads change block heights after the initial pass — both would leave stale badges.
        UpdateBookReadingBadges();
    }

    // Computes the start page's measured top and scrolls there. No-op while the ReadingView has
    // no real width yet (e.g. a recreated view before its first layout pass).
    private void TryApplyBookReadingAnchor()
    {
        if (!_bookReadingAnchorPending || _workspace is null)
        {
            return;
        }

        if (!_bookReadingSourceScenes.ContainsKey(_workspace.BookReadingStartPageForBadge))
        {
            return;
        }

        if (BookReadingView.Bounds.Width <= 0)
        {
            // Wait for the first real layout; OnBookReadingScrollChanged retries when the
            // ScrollViewer measures the recreated view.
            return;
        }

        _bookReadingAnchorY = GetBookReadingPageTop(_workspace.BookReadingStartPageForBadge);
        BookReadingScroller.Offset = new Vector(0, _bookReadingAnchorY ?? 0);
        _bookReadingAnchorPending = false;
    }

    // A streamed page is cached by index and the whole reading document is reassembled in page
    // order (source and translation together). The ReadingView lays out one concatenated scene, so
    // arrival order — append or prepend — no longer drives block surgery; the sorted cache keeps
    // page order regardless of how batches arrive.
    private void OnBookReadingPageReady(BookReadingPage page)
    {
        if (_workspace is null)
        {
            return;
        }

        _bookReadingSourceScenes[page.PageIndex] = page.Source;
        _bookReadingTranslationScenes[page.PageIndex] = page.Translation;
        RebuildBookReadingDocument();
    }

    // Rebuilds the combined source/translation scenes from the sorted per-page cache and re-measures
    // page offsets for the badge rail. Preserves the reading position across the re-flow unless the
    // session anchor is still pending (the initial window). Runs on every delivered page, on font
    // changes and on compare toggles.
    private void RebuildBookReadingDocument()
    {
        if (_workspace is null)
        {
            return;
        }

        double extentBefore = BookReadingView.DesiredSize.Height;
        double scrollRatio = extentBefore > 0 ? BookReadingScroller.Offset.Y / extentBefore : 0;

        (ReadingScene sourceScene, ReadingScene? translationScene) = BookReadingDocument.Assemble(
            _bookReadingSourceScenes, _bookReadingTranslationScenes, _workspace.IsTranslationCompareVisible);
        BookReadingView.SourceScene = sourceScene;
        // The translation column exists only while the compare toggle is on and at least one
        // page carried a translation; otherwise the view stays single-column.
        BookReadingView.TranslationScene = translationScene;
        BookReadingView.InvalidateMeasure();

        double extent = BookReadingView.DesiredSize.Height;
        if (!_bookReadingAnchorPending && extent > 0 && scrollRatio > 0)
        {
            // Preserve the visible reading position across re-flows (font changes, compare
            // toggles, and page batches delivered after the session anchor is in place).
            BookReadingScroller.Offset = new Vector(0, scrollRatio * extent);
        }

        UpdateBookReadingBadges();
    }

    // Measured top of a page's first block in the scroller's content space, or 0 when the page has
    // not streamed yet. The reading view sits at Margin.Top inside that content, so a view-local
    // block top only becomes a scroll offset (and a rail position) after that margin is added.
    private double GetBookReadingPageTop(int pageIndex)
    {
        foreach (ReadingMeasuredBlock block in BookReadingView.GetMeasuredBlocks())
        {
            if (block.BlockIndex >= 0 && block.PageIndex == pageIndex)
            {
                return ContentTop(block.Bounds.Top);
            }
        }

        return 0;
    }

    // The reading content's coordinate space: the reading view's own space shifted by its top
    // margin. Both the badge rail and the scroll anchor use it.
    private double ContentTop(double viewLocalTop)
    {
        return BookReadingView.Margin.Top + viewLocalTop;
    }

    // Pins one badge per streamed page in the left rail, aligned with the page's first block. The
    // rail is part of the scroller's content, so badges scroll with the text and keep that
    // block-level alignment at any scroll offset. Clicking a badge leaves reading mode and opens
    // that page in the PDF workbench.
    private void UpdateBookReadingBadges()
    {
        BookReadingBadgeRail.Children.Clear();
        HashSet<int> placed = [];
        double contentBottom = 0;
        foreach (ReadingMeasuredBlock block in BookReadingView.GetMeasuredBlocks())
        {
            contentBottom = Math.Max(contentBottom, block.Bounds.Bottom);
            if (block.Column != ReadingColumnRole.Source || block.PageIndex < 0 ||
                !placed.Add(block.PageIndex))
            {
                continue;
            }

            int pageIndex = block.PageIndex;
            int pageNumber = pageIndex + 1;
            Button badge = new()
            {
                Classes = { "PageBadge" },
                Content = $"第 {pageNumber} 页",
                Tag = pageIndex
            };
            ToolTip.SetTip(badge, $"退出阅读模式并跳转到第 {pageNumber} 页");
            badge.Click += OnBookReadingBadgeClick;
            Canvas.SetLeft(badge, 10);
            Canvas.SetTop(badge, ContentTop(block.Bounds.Top));
            BookReadingBadgeRail.Children.Add(badge);
        }

        // Height comes from the measured blocks, not DesiredSize: this runs inside the view's own
        // measure pass (via Measured), where DesiredSize still holds the previous layout's value.
        BookReadingBadgeRail.Height = BookReadingView.Margin.Top + contentBottom +
                                      BookReadingView.Margin.Bottom;
    }

    // Pulls the next page window when the reader nears either end of the loaded range. Prefetch
    // is deliberately generous (a full viewport of slack): a batch is cheap, and waiting until
    // the very edge would show a blank frame while it loads.
    private void OnBookReadingScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_workspace is not { IsBookReadingMode: true } workspace)
        {
            return;
        }

        TryApplyBookReadingAnchor();

        if (_bookReadingAnchorPending)
        {
            // The reading position is not anchored yet; a scroll event at the document top is
            // just the first layout of a recreated view. Prefetch decisions must wait until the
            // anchor is applied so backward batches are not requested from offset zero.
            return;
        }

        double viewport = BookReadingScroller.Viewport.Height;
        double extent = BookReadingScroller.Extent.Height;
        if (viewport <= 0 || extent <= 0)
        {
            return;
        }

        double offsetY = BookReadingScroller.Offset.Y;
        if (offsetY + 2 * viewport >= extent)
        {
            _ = workspace.RequestBookReadingForwardAsync();
        }

        if (offsetY <= viewport)
        {
            _ = workspace.RequestBookReadingBackwardAsync();
        }

        // Track the page at the viewport centre so the media cache keeps only the pages being read.
        workspace.BookReadingVisiblePage = GetBookReadingPageAt(offsetY + viewport / 2);
    }

    // The page whose first block is the last one at or above <paramref name="offsetY"/>.
    private int GetBookReadingPageAt(double offsetY)
    {
        int page = -1;
        double best = double.NegativeInfinity;
        foreach (ReadingMeasuredBlock block in BookReadingView.GetMeasuredBlocks())
        {
            if (block.Column != ReadingColumnRole.Source || block.PageIndex < 0)
            {
                continue;
            }

            double top = ContentTop(block.Bounds.Top);
            if (top <= offsetY && top > best)
            {
                best = top;
                page = block.PageIndex;
            }
        }

        return page < 0 ? 0 : page;
    }

    private void OnBookReadingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(PdfWorkspaceViewModel.IsBookReadingMode):
                if (!_workspace.IsBookReadingMode)
                {
                    _bookReadingImages?.Dispose();
                    _bookReadingImages = null;
                }

                break;
            case nameof(PdfWorkspaceViewModel.BookReadingFontSize):
            case nameof(PdfWorkspaceViewModel.BookReadingFontFamily):
                ApplyBookReadingFont();
                break;
            case nameof(PdfWorkspaceViewModel.IsTranslationCompareVisible):
            case nameof(PdfWorkspaceViewModel.BookReadingCompareMode):
                ApplyTranslationCompareVisibility();
                break;
        }
    }

    // Font family/size changes re-flow the reading surface; the ReadingView owns the text layout,
    // so the view only re-stamps the properties and re-measures page offsets for the badges.
    private void ApplyBookReadingFont()
    {
        if (_workspace is null)
        {
            return;
        }

        string family = _workspace.BookReadingFontFamily;
        BookReadingView.FontFamily =
            string.IsNullOrWhiteSpace(family) ||
            string.Equals(family, PdfWorkspaceViewModel.SystemDefaultReadingFontLabel, StringComparison.Ordinal)
                ? FontFamily.Default
                : new FontFamily(family);
        BookReadingView.FontSize = _workspace.BookReadingFontSize;
        RebuildBookReadingDocument();
    }

    // The compare toggle and layout mode feed the scene assembly (the translation column is
    // gated on the toggle), so both changes go through a full document rebuild.
    private void ApplyTranslationCompareVisibility()
    {
        if (_workspace is null)
        {
            return;
        }

        BookReadingView.CompareMode = _workspace.BookReadingCompareMode;
        RebuildBookReadingDocument();
    }

    private void OnBookReadingBadgeClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null || sender is not Button { Tag: int pageIndex })
        {
            return;
        }

        _ = ExitBookReadingToPageAsync(pageIndex);
    }

    private async Task ExitBookReadingToPageAsync(int pageIndex)
    {
        if (_workspace is null)
        {
            return;
        }

        try
        {
            await _workspace.ExitBookReadingToPageAsync(pageIndex);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, nameof(PdfWorkspacePage),
                nameof(ExitBookReadingToPageAsync));
        }
    }

    private void OnBookReadingEditSelectionRequested(object? sender, ReadingEditSelectionRequest request)
    {
        _ = EditBookReadingSelectionAsync(request);
    }

    // 编辑选中文本: leaves reading mode, enters edit mode on the page owning the first selected
    // box and opens that box in the editor dialog.
    private async Task EditBookReadingSelectionAsync(ReadingEditSelectionRequest request)
    {
        if (_workspace is null)
        {
            return;
        }

        try
        {
            await _workspace.EditBookReadingSelectionAsync(request.BoxIds, request.PageIndex);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, nameof(PdfWorkspacePage),
                nameof(EditBookReadingSelectionAsync));
        }
    }
}
