using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaRichEditor.Controls;
using AvaloniaRichEditor.Documents;
using AvaloniaRichEditor.Formatters;
using Patchouli.Core.Ids;
using Patchouli.UI.Reading;
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

    // Reading-mode page rail: per-page HTML cache (so a font change can rebuild the document and
    // re-measure page offsets), the set of pages that arrived as front-inserts, the block count
    // each front-inserted page contributed (so a new batch lands in page order regardless of how
    // many batches arrived), and the page-index → vertical-offset map feeding badges.
    private readonly SortedDictionary<int, string> _bookReadingHtmlCache = new();
    private readonly HashSet<int> _bookReadingPrependedPages = [];
    private readonly SortedList<int, int> _bookReadingPrependedBlockCounts = new();
    private readonly BookReadingPageMap _bookReadingPageMap = new();

    // The translation pane keeps its own copy of the same bookkeeping: a translated page has a
    // different height than its source, so the source offsets cannot locate a page in this pane.
    // The HTML cache fills even while the pane is hidden so toggling compare on is a local rebuild.
    private readonly SortedDictionary<int, string> _bookTranslationHtmlCache = new();
    private readonly HashSet<int> _bookTranslationPrependedPages = [];
    private readonly SortedList<int, int> _bookTranslationPrependedBlockCounts = new();
    private readonly BookReadingPageMap _bookTranslationPageMap = new();

    // Real measured Y of the reading session's start page: recorded when the start page is
    // appended, then pushed down by every prepend. The page map cannot be used for this — its
    // start offset includes the empty document's own height, which broke the old compensation
    // condition. The workspace asks the view to snap here once the initial window settles.
    private double? _bookReadingAnchorY;

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
        if (_workspace is not null)
        {
            _workspace.BookReadingStarted -= OnBookReadingStarted;
            _workspace.BookReadingPageReady -= OnBookReadingPageReady;
            _workspace.BookReadingAnchorRequested -= OnBookReadingAnchorRequested;
            _workspace.PropertyChanged -= OnBookReadingPropertyChanged;
        }

        _workspace = DataContext as PdfWorkspaceViewModel;
        if (_workspace is not null)
        {
            _workspace.BookReadingStarted += OnBookReadingStarted;
            _workspace.BookReadingPageReady += OnBookReadingPageReady;
            _workspace.BookReadingAnchorRequested += OnBookReadingAnchorRequested;
            _workspace.PropertyChanged += OnBookReadingPropertyChanged;
            if (_workspace.IsBookReadingMode)
            {
                // The view was recreated after reading mode had already started (e.g. a tab switch
                // back). The start/page events fired before this subscription existed, so ask the
                // workspace to replay what it has delivered.
                _workspace.ReplayBookReading();
            }
        }
    }

    private void OnReadingBlockClicked(object? sender, DocumentBoxId? boxId)
    {
        _workspace?.SelectReadingBlock(boxId);
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
        _bookReadingHtmlCache.Clear();
        _bookReadingPrependedPages.Clear();
        _bookReadingPrependedBlockCounts.Clear();
        _bookReadingPageMap.Clear();
        _bookReadingAnchorY = null;
        _bookTranslationHtmlCache.Clear();
        _bookTranslationPrependedPages.Clear();
        _bookTranslationPrependedBlockCounts.Clear();
        _bookTranslationPageMap.Clear();
        BookReadingBadgeRail.Children.Clear();
        if (_workspace is null)
        {
            return;
        }

        BookReadingEditor.LoadHtml(string.Empty);
        BookTranslationEditor.LoadHtml(string.Empty);
        ApplyBookReadingFontFamily();
        ApplyBookReadingFontSize();
        BookReadingScroller.Offset = Vector.Zero;
        BookTranslationScroller.Offset = Vector.Zero;
        BookReadingEditor.InvalidateMeasure();
        BookTranslationEditor.InvalidateMeasure();
        // The compare toggle may have been left on from a previous session; reflect its column
        // layout before the first page arrives so the two panes never overlap.
        ApplyTranslationCompareVisibility();
    }

    // Snaps the scroll offset to the start page the view has been measuring since it arrived.
    // Raised when the initial window settles (before the prefetch guard lifts) and after a
    // replay, which is what finally keeps trailing layout scroll events from cascading backward
    // prefetches to page zero while the offset sits at the document top.
    private void OnBookReadingAnchorRequested()
    {
        if (_workspace is null || _bookReadingAnchorY is not { } anchorY)
        {
            return;
        }

        BookReadingEditor.UpdateLayout();
        BookReadingScroller.Offset = new Vector(0, anchorY);
    }

    private void OnBookReadingPageReady(BookReadingPage page)
    {
        if (_workspace is null)
        {
            return;
        }

        // Cache the translation even while its pane is hidden, so toggling compare on rebuilds
        // from what has already streamed instead of reloading it.
        _bookTranslationHtmlCache[page.PageIndex] = ResolveTranslationHtml(page);

        FlowDocument? document = BookReadingEditor.Document;
        if (document is null)
        {
            BookReadingEditor.LoadHtml(string.Empty);
            document = BookReadingEditor.Document;
        }

        if (document is null)
        {
            return;
        }

        _bookReadingHtmlCache[page.PageIndex] = page.Html;
        FlowDocument parsed = HtmlDocumentFormatter.ParseHtml(page.Html);
        // Stamp the current reading size on the incoming page so pages arriving after a
        // mid-stream font-size change match the rest of the document.
        ReadingFontCatalog.ApplyFontSize(parsed, _workspace.BookReadingFontSize);

        if (!page.IsPrepend)
        {
            // Pages after the start page: append at the bottom in arrival order. Settle the
            // pending measure first so the recorded page start is the real document height.
            BookReadingEditor.UpdateLayout();
            _bookReadingPageMap.RecordAppend(page.PageIndex, BookReadingEditor.DesiredSize.Height);
            _bookReadingAnchorY ??= BookReadingEditor.DesiredSize.Height;
            document.Blocks.AddRange(parsed.Blocks);
            BookReadingEditor.InvalidateMeasure();
        }
        else
        {
            // Earlier pages: front-insert preserving page order. Each page lands right after every
            // already-prepended page with a lower index, so page order holds no matter how the pages
            // were batched. Settle the pending measure first: appended pages only invalidate, so
            // without a layout pass heightBefore (and therefore the recorded insertion delta) could be
            // stale.
            BookReadingEditor.UpdateLayout();
            double heightBefore = BookReadingEditor.DesiredSize.Height;
            double offsetBefore = BookReadingScroller.Offset.Y;
            int insertAt = _bookReadingPrependedBlockCounts
                .Where(entry => entry.Key < page.PageIndex)
                .Sum(entry => entry.Value);
            int blockCount = parsed.Blocks.Count;
            foreach (Block block in parsed.Blocks)
            {
                document.Blocks.Insert(insertAt, block);
                insertAt++;
            }

            _bookReadingPrependedBlockCounts[page.PageIndex] = blockCount;
            _bookReadingPrependedPages.Add(page.PageIndex);
            BookReadingEditor.InvalidateMeasure();
            BookReadingEditor.UpdateLayout();

            // Keep the reading position stable: the inserted blocks sit above the viewport, so push
            // the scroll offset down by the amount the editor just grew. The insertion point comes
            // from the already-recorded page that follows this one in visual order.
            double delta = BookReadingEditor.DesiredSize.Height - heightBefore;
            double insertY = _bookReadingPageMap.GetInsertY(page.PageIndex);
            _bookReadingPageMap.RecordPrepend(page.PageIndex, insertY, delta);
            // Prepended blocks always land at or above the viewport top, so always compensate. The
            // old insertY <= offsetBefore condition never fired during initial anchoring: the start
            // page's recorded Y includes the empty document's own height (102px), so it compared
            // greater than the zero offset and the offset stayed pinned to the document top while
            // backward prefetches cascaded to page zero.
            if (delta > 0)
            {
                BookReadingScroller.Offset = new Vector(0, offsetBefore + delta);
                _bookReadingAnchorY += delta;
            }
        }

        UpdateBookReadingBadges();
        if (_workspace.IsTranslationCompareVisible)
        {
            InsertBookTranslationPage(page);
            SyncBookTranslationScroll();
        }
    }

    // Mirrors the source pane's append/prepend bookkeeping for the translation pane. A translated
    // page is a different height than its source, so the pane has its own page map and block
    // counts; the source offsets cannot locate a page here.
    private void InsertBookTranslationPage(BookReadingPage page)
    {
        if (_workspace is null)
        {
            return;
        }

        FlowDocument? document = BookTranslationEditor.Document;
        if (document is null)
        {
            BookTranslationEditor.LoadHtml(string.Empty);
            document = BookTranslationEditor.Document;
        }

        if (document is null)
        {
            return;
        }

        FlowDocument parsed = HtmlDocumentFormatter.ParseHtml(_bookTranslationHtmlCache[page.PageIndex]);
        ReadingFontCatalog.ApplyFontSize(parsed, _workspace.BookReadingFontSize);

        if (!page.IsPrepend)
        {
            BookTranslationEditor.UpdateLayout();
            _bookTranslationPageMap.RecordAppend(page.PageIndex, BookTranslationEditor.DesiredSize.Height);
            document.Blocks.AddRange(parsed.Blocks);
            BookTranslationEditor.InvalidateMeasure();
            return;
        }

        BookTranslationEditor.UpdateLayout();
        double heightBefore = BookTranslationEditor.DesiredSize.Height;
        int insertAt = _bookTranslationPrependedBlockCounts
            .Where(entry => entry.Key < page.PageIndex)
            .Sum(entry => entry.Value);
        int blockCount = parsed.Blocks.Count;
        foreach (Block block in parsed.Blocks)
        {
            document.Blocks.Insert(insertAt, block);
            insertAt++;
        }

        _bookTranslationPrependedBlockCounts[page.PageIndex] = blockCount;
        _bookTranslationPrependedPages.Add(page.PageIndex);
        BookTranslationEditor.InvalidateMeasure();
        BookTranslationEditor.UpdateLayout();
        double delta = BookTranslationEditor.DesiredSize.Height - heightBefore;
        _bookTranslationPageMap.RecordPrepend(
            page.PageIndex, _bookTranslationPageMap.GetInsertY(page.PageIndex), delta);
    }

    // The stream sends a placeholder for untranslated pages, but a substituted stream (tests) may
    // send nothing; resolve to the placeholder so the compare pane never shows an empty page.
    private static string ResolveTranslationHtml(BookReadingPage page)
    {
        return string.IsNullOrWhiteSpace(page.TranslationHtml)
            ? BookReadingHtml.CompileUntranslatedHtml()
            : page.TranslationHtml;
    }

    // Mirrors the source pane's reading position: the page under the source viewport top is found
    // through the source page map, then the translation pane scrolls to that page's first block
    // through its own page map. Pages stream into both panes together, so both maps normally know
    // the page; one still measuring is skipped until the next scroll event.
    private void SyncBookTranslationScroll()
    {
        if (_workspace is not { IsBookReadingMode: true, IsTranslationCompareVisible: true })
        {
            return;
        }

        if (_bookReadingPageMap.GetPageAt(BookReadingScroller.Offset.Y) is not { } pageIndex ||
            !_bookTranslationPageMap.TryGetStart(pageIndex, out double startY))
        {
            return;
        }

        BookTranslationScroller.Offset = new Vector(0, startY);
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

        SyncBookTranslationScroll();
    }

    private void OnBookReadingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_workspace is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(PdfWorkspaceViewModel.BookReadingFontSize):
                ApplyBookReadingFontSize();
                break;
            case nameof(PdfWorkspaceViewModel.BookReadingFontFamily):
                ApplyBookReadingFontFamily();
                break;
            case nameof(PdfWorkspaceViewModel.IsTranslationCompareVisible):
                ApplyTranslationCompareVisibility();
                break;
        }
    }

    private void ApplyBookReadingFontSize()
    {
        if (_workspace is null)
        {
            return;
        }

        RebuildBookReadingDocument();
    }

    private void ApplyBookReadingFontFamily()
    {
        if (_workspace is null)
        {
            return;
        }

        string family = _workspace.BookReadingFontFamily;
        FontFamily resolved =
            string.IsNullOrWhiteSpace(family) ||
            string.Equals(family, PdfWorkspaceViewModel.SystemDefaultReadingFontLabel, StringComparison.Ordinal)
                ? FontFamily.Default
                : new FontFamily(family);
        BookReadingEditor.DefaultFontFamily = resolved;
        BookTranslationEditor.DefaultFontFamily = resolved;
        RebuildBookReadingDocument();
    }

    // Calls the shared rebuild for the translation pane when compare is on. Kept separate from the
    // source rebuild because this pane has no badge rail and follows the source pane's scroll.
    private void RebuildBookTranslationDocument()
    {
        if (_workspace is null)
        {
            return;
        }

        RebuildEditorDocument(
            BookTranslationEditor,
            _bookTranslationPageMap,
            _bookTranslationHtmlCache,
            _bookTranslationPrependedPages,
            _bookTranslationPrependedBlockCounts,
            _workspace.BookReadingFontSize);
    }

    // Collapsing the translation column to zero width is what keeps the single-pane behavior
    // identical while compare is off: the source pane is then the only star column and fills the
    // surface. Turning compare on rebuilds the translation document from the HTML cached for the
    // pages delivered so far (pages that streamed while the pane was hidden were never inserted),
    // then aligns both panes on the current page.
    private void ApplyTranslationCompareVisibility()
    {
        bool visible = _workspace?.IsTranslationCompareVisible == true;
        // The source column is reset to a star on every toggle so a split ratio dragged earlier
        // cannot leave the source pane narrower than the viewport after compare is turned off.
        BookReadingSplitGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        BookReadingSplitGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        BookReadingSplitGrid.ColumnDefinitions[2].Width =
            visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BookReadingSplitter.IsVisible = visible;
        BookTranslationScroller.IsVisible = visible;
        if (!visible || _workspace is null)
        {
            return;
        }

        RebuildBookTranslationDocument();
        BookReadingEditor.UpdateLayout();
        SyncBookTranslationScroll();
    }

    // Font family/size changes re-flow the whole document, invalidating every recorded page
    // offset. With no block-geometry API on the editor, the only exact way to recover the badge
    // positions is to rebuild from the cached per-page HTML and re-measure page by page. Each
    // page forces a layout, so this is O(pages²) in measure work — acceptable for an explicit
    // user action, but it must never run per streamed page.
    private void RebuildBookReadingDocument()
    {
        if (_workspace is null)
        {
            return;
        }

        if (_bookReadingHtmlCache.Count == 0)
        {
            BookReadingEditor.InvalidateMeasure();
            return;
        }

        // The rebuild re-flows every page, so the measured start-page anchor no longer applies.
        _bookReadingAnchorY = null;
        BookReadingEditor.UpdateLayout();
        double extentBefore = BookReadingEditor.DesiredSize.Height;
        double scrollRatio = extentBefore > 0 ? BookReadingScroller.Offset.Y / extentBefore : 0;

        RebuildEditorDocument(
            BookReadingEditor,
            _bookReadingPageMap,
            _bookReadingHtmlCache,
            _bookReadingPrependedPages,
            _bookReadingPrependedBlockCounts,
            _workspace.BookReadingFontSize);

        BookReadingEditor.UpdateLayout();
        double extent = BookReadingEditor.DesiredSize.Height;
        if (extent > 0 && scrollRatio > 0)
        {
            BookReadingScroller.Offset = new Vector(0, scrollRatio * extent);
        }

        UpdateBookReadingBadges();
        if (_workspace.IsTranslationCompareVisible)
        {
            RebuildBookTranslationDocument();
            SyncBookTranslationScroll();
        }
    }

    // Rebuilds one pane's document from its cached per-page HTML, re-measuring page by page to
    // recover the page offsets a re-flow invalidates. Each page forces a layout, so this is
    // O(pages²) in measure work — acceptable for an explicit font action or a compare toggle, but
    // it must never run per streamed page.
    private static void RebuildEditorDocument(
        RichEditor editor,
        BookReadingPageMap pageMap,
        SortedDictionary<int, string> htmlCache,
        HashSet<int> prependedPages,
        SortedList<int, int> prependedBlockCounts,
        double fontSize)
    {
        editor.LoadHtml(string.Empty);
        FlowDocument? document = editor.Document;
        if (document is null)
        {
            return;
        }

        pageMap.Clear();
        prependedBlockCounts.Clear();
        foreach ((int pageIndex, string html) in htmlCache)
        {
            FlowDocument parsed = HtmlDocumentFormatter.ParseHtml(html);
            ReadingFontCatalog.ApplyFontSize(parsed, fontSize);
            int blockCount = parsed.Blocks.Count;
            editor.UpdateLayout();
            pageMap.RecordAppend(pageIndex, editor.DesiredSize.Height);
            document.Blocks.AddRange(parsed.Blocks);
            editor.InvalidateMeasure();
            if (prependedPages.Contains(pageIndex))
            {
                prependedBlockCounts[pageIndex] = blockCount;
            }
        }
    }

    // Pins one badge per streamed page in the left rail, aligned with the page's first block.
    // The rail shares the editor's scroll content, so badges scroll with the text. Clicking a
    // badge leaves reading mode and opens that page in the PDF workbench.
    private void UpdateBookReadingBadges()
    {
        BookReadingBadgeRail.Children.Clear();
        Thickness editorMargin = BookReadingEditor.Margin;
        foreach ((int pageIndex, double startY) in _bookReadingPageMap.StartOffsets)
        {
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
            Canvas.SetTop(badge, editorMargin.Top + startY);
            BookReadingBadgeRail.Children.Add(badge);
        }

        BookReadingBadgeRail.Height = editorMargin.Top + BookReadingEditor.DesiredSize.Height + editorMargin.Bottom;
    }

    private async void OnBookReadingBadgeClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null || sender is not Button { Tag: int pageIndex })
        {
            return;
        }

        await _workspace.ExitBookReadingToPageAsync(pageIndex);
    }
}
