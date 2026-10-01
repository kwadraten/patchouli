using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.Reading;

/// <summary>
/// A read-only, single-visual reading surface for one or two <see cref="ReadingScene"/> columns.
/// Every block is laid out once per (content, width) and drawn with cached TextLayouts; Render
/// culls blocks outside the hosting ScrollViewer's viewport. With a <see cref="TranslationScene"/>
/// set, source and translation blocks pair up by index in one of the two compare layouts
/// (<see cref="ReadingCompareMode"/>); a missing translation partner renders the muted 未翻译
/// placeholder instead of falling back to source text. Text selection spans blocks inside one
/// column and never crosses columns; the context menu copies the selection and (source column,
/// when <see cref="CanEditSelection"/>) raises <see cref="EditSelectionRequested"/>. Media blocks
/// draw their normalized page region as a source sub-rect of an in-memory page bitmap.
/// </summary>
public sealed class ReadingView : Control, ICustomHitTest
{
    private const double BodyBlockGap = 8;
    private const double HeadingTopGap = 16;
    private const double CodePadding = 8;
    private const double EquationPadding = 6;
    private const double MediaPadding = 12;
    private const double MediaCaptionGap = 6;
    private const double PlaceholderPadding = 6;
    private const double MaxMediaImageHeight = 240;
    private const double TableCellPadX = 6;
    private const double TableCellPadY = 4;
    private const double MinColumnWidth = 36;
    private const double FallbackWidth = 600;
    private const double CompareGutter = 20;
    private const double StackedInnerGap = 4;
    private const double StackedTranslationIndent = 12;

    private static readonly FontFamily MonospaceFont = new("Consolas, Menlo, monospace");

    private static readonly ImmutableSolidColorBrush FallbackForeground = new(Color.Parse("#1B1C1C"));
    private static readonly ImmutableSolidColorBrush FallbackMuted = new(Color.Parse("#484553"));
    private static readonly ImmutableSolidColorBrush FallbackSurfaceHigh = new(Color.Parse("#E9E8E7"));
    private static readonly ImmutableSolidColorBrush FallbackOutline = new(Color.Parse("#CAC4D5"));
    private static readonly ImmutableSolidColorBrush FallbackCodeBackground = new(Color.Parse("#ECE6F0"));
    private static readonly ImmutableSolidColorBrush FallbackLink = new(Color.Parse("#553BB5"));
    private static readonly ImmutableSolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public static readonly StyledProperty<ReadingScene?> SourceSceneProperty =
        AvaloniaProperty.Register<ReadingView, ReadingScene?>(nameof(SourceScene));

    public static readonly StyledProperty<ReadingScene?> TranslationSceneProperty =
        AvaloniaProperty.Register<ReadingView, ReadingScene?>(nameof(TranslationScene));

    public static readonly StyledProperty<ReadingCompareMode> CompareModeProperty =
        AvaloniaProperty.Register<ReadingView, ReadingCompareMode>(nameof(CompareMode),
            ReadingCompareMode.SideBySide);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<ReadingView, double>(nameof(FontSize), 14);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<ReadingView, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<DocumentBoxId?> SelectedBoxIdProperty =
        AvaloniaProperty.Register<ReadingView, DocumentBoxId?>(nameof(SelectedBoxId));

    // Optional resolver for media block page bitmaps. Without one, media blocks stay placeholders.
    public static readonly StyledProperty<IReadingImageSource?> ImageSourceProperty =
        AvaloniaProperty.Register<ReadingView, IReadingImageSource?>(nameof(ImageSource));

    // Selection accent injected by the call site (e.g. {DynamicResource PrimaryBrush}) so the
    // highlight follows the global palette. The control derives the translucent fill and the
    // opaque marker bar from this brush; nothing is drawn while it is unset or not solid.
    public static readonly StyledProperty<IBrush?> SelectionAccentBrushProperty =
        AvaloniaProperty.Register<ReadingView, IBrush?>(nameof(SelectionAccentBrush));

    // Whether the source column's context menu offers 编辑选中文本. The host turns it on only for
    // surfaces where opening the box editor makes sense; translation selections never get it.
    public static readonly StyledProperty<bool> CanEditSelectionProperty =
        AvaloniaProperty.Register<ReadingView, bool>(nameof(CanEditSelection));

    static ReadingView()
    {
        SourceSceneProperty.Changed.AddClassHandler<ReadingView>((view, _) => view.OnSceneChanged());
        TranslationSceneProperty.Changed.AddClassHandler<ReadingView>((view, _) => view.OnSceneChanged());
        CompareModeProperty.Changed.AddClassHandler<ReadingView>((view, _) => view.OnLayoutInputChanged());
        FontSizeProperty.Changed.AddClassHandler<ReadingView>((view, _) => view.OnLayoutInputChanged());
        FontFamilyProperty.Changed.AddClassHandler<ReadingView>((view, _) => view.OnLayoutInputChanged());
        SelectedBoxIdProperty.Changed.AddClassHandler<ReadingView>((view, _) => view.InvalidateVisual());
        ImageSourceProperty.Changed.AddClassHandler<ReadingView>((view, _) => view.OnImageSourceChanged());
        SelectionAccentBrushProperty.Changed.AddClassHandler<ReadingView>((view, _) =>
            view.InvalidateVisual());
    }

    public ReadingView()
    {
        Focusable = true;
        _contextFlyout.Items.Add(_copyItem);
        _contextFlyout.Items.Add(_editItem);
        _copyItem.Click += OnCopyItemClick;
        _editItem.Click += OnEditItemClick;
        ContextRequested += OnContextRequested;
    }

    public ReadingScene? SourceScene
    {
        get => GetValue(SourceSceneProperty);
        set => SetValue(SourceSceneProperty, value);
    }

    public ReadingScene? TranslationScene
    {
        get => GetValue(TranslationSceneProperty);
        set => SetValue(TranslationSceneProperty, value);
    }

    public ReadingCompareMode CompareMode
    {
        get => GetValue(CompareModeProperty);
        set => SetValue(CompareModeProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public DocumentBoxId? SelectedBoxId
    {
        get => GetValue(SelectedBoxIdProperty);
        set => SetValue(SelectedBoxIdProperty, value);
    }

    public IReadingImageSource? ImageSource
    {
        get => GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public IBrush? SelectionAccentBrush
    {
        get => GetValue(SelectionAccentBrushProperty);
        set => SetValue(SelectionAccentBrushProperty, value);
    }

    public bool CanEditSelection
    {
        get => GetValue(CanEditSelectionProperty);
        set => SetValue(CanEditSelectionProperty, value);
    }

    /// <summary>Raised after <see cref="MeasureOverride"/> has refreshed the block layout, so a host
    /// can apply scroll anchoring once the reading surface has a real measured width.</summary>
    public event EventHandler? Measured;

    /// <summary>Raised on a press with the clicked block's column and source box linkage (null on
    /// empty space or unlinked content).</summary>
    public event EventHandler<ReadingBlockActivation>? BlockClicked;

    /// <summary>Raised by the source column's 编辑选中文本 command with the selected boxes in
    /// reading order, the selected text, and the first selected block's page index.</summary>
    public event EventHandler<ReadingEditSelectionRequest>? EditSelectionRequested;

    /// <summary>The active selection, or null when none was started. Selection never spans
    /// columns.</summary>
    public ReadingSelection? Selection => _selection;

    public bool HasSelection => _selection is { IsEmpty: false };

    // Theme-resolved brushes/pens; replaced in OnAttachedToVisualTree. Kept immutable so they can be
    // shared freely across render passes without allocating.
    private ImmutableSolidColorBrush _foreground = FallbackForeground;
    private ImmutableSolidColorBrush _muted = FallbackMuted;
    private ImmutableSolidColorBrush _surfaceHigh = FallbackSurfaceHigh;
    private ImmutableSolidColorBrush _outline = FallbackOutline;
    private ImmutableSolidColorBrush _codeBackground = FallbackCodeBackground;
    private ImmutableSolidColorBrush _link = FallbackLink;
    private ImmutablePen _gridPen = new(FallbackOutline, 1);
    private ImmutablePen _dividerPen = new(FallbackOutline, 1);

    // Derived from SelectionAccentBrush on demand; keyed by color so a live palette switch (which
    // mutates the shared brush instance in place) is picked up on the next render pass.
    private ImmutableSolidColorBrush? _selectionFill;
    private ImmutableSolidColorBrush? _selectionBar;
    private Color _selectionAccentColor;

    private readonly List<BlockLayout> _sourceBlocks = [];
    private readonly List<BlockLayout> _translationBlocks = [];
    private double _totalHeight;
    private double _layoutWidth = -1;
    private bool _layoutValid;

    private ReadingSelection? _selection;
    private bool _selecting;

    private readonly MenuFlyout _contextFlyout = new();
    private readonly MenuItem _copyItem = new() { Header = "复制" };
    private readonly MenuItem _editItem = new() { Header = "编辑选中文本" };

    // Decoded page bitmaps keyed by image key, plus the load bookkeeping that keeps one key from
    // being requested twice. All are touched on the UI thread only. The host layer above decides
    // retention (e.g. visible pages ±1 for whole-book reading) and drops keys from its cache; the
    // view only ever holds what its current blocks reference.
    private readonly Dictionary<string, IImage> _images = new(StringComparer.Ordinal);
    private readonly HashSet<string> _imagesInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _imagesFailed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _imageLoadCancellation;
    private IReadingImageSource? _cachedImageSource;
    private int _imageGeneration;
    private ScrollViewer? _imageScrollViewer;

    /// <inheritdoc/>
    public bool HitTest(Point point)
    {
        // Bounded to the control's own rect: the composition hit tester defers entirely to this
        // method for the whole visual (no bounds pre-check, ClipToBounds is off), so an
        // unconditional true would swallow every press inside the hosting ScrollViewer's clip —
        // including the page badge rail beside the view. Anywhere inside the bounds (including
        // below the last block) still reaches OnPointerPressed so empty space clears selection.
        return new Rect(Bounds.Size).Contains(point);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResolveThemeBrushes();
        // Foreground/background are baked into TextLayout, so a theme change invalidates the cache.
        _layoutValid = false;
        _imageLoadCancellation ??= new CancellationTokenSource();
        _imageScrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (_imageScrollViewer is not null)
        {
            _imageScrollViewer.ScrollChanged += OnImageScrollChanged;
        }

        InvalidateMeasure();
        InvalidateVisual();
        EnsureImageLoads();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_imageScrollViewer is not null)
        {
            _imageScrollViewer.ScrollChanged -= OnImageScrollChanged;
            _imageScrollViewer = null;
        }

        CancelImageLoads();
        ReleaseAllImages();
    }

    private void OnImageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Render culls blocks to the viewport and scrolling does not re-invoke Render on its own,
        // so without a repaint freshly exposed content stays blank until a click or a completed
        // image load happens to invalidate the view.
        InvalidateVisual();
        EnsureImageLoads();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        double width = NormalizeWidth(availableSize.Width);
        EnsureLayout(width);
        Measured?.Invoke(this, EventArgs.Empty);
        return new Size(width, _totalHeight);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double width = NormalizeWidth(Bounds.Width);
        EnsureLayout(width);
        EnsureImageLoads();
        // Transparent fill makes the transparent gaps between blocks hit-testable.
        context.FillRectangle(TransparentBrush, new Rect(Bounds.Size));
        (double visibleTop, double visibleBottom) = VisibleRange();
        foreach (BlockLayout layout in AllBlocks())
        {
            if (layout.Top + layout.Height < visibleTop || layout.Top > visibleBottom)
            {
                continue; // off-screen: advance nothing, issue no draw commands
            }

            DrawSelection(context, layout);
            DrawBlock(context, layout);
        }
    }

    /// <summary>Measured block rectangles in reading order per column, after layout. Exposes the
    /// block geometry the drawing pass uses (page rails, hit testing, tests).</summary>
    public IReadOnlyList<ReadingMeasuredBlock> GetMeasuredBlocks()
    {
        EnsureLayout(_layoutWidth > 0 ? _layoutWidth : NormalizeWidth(Bounds.Width));
        List<ReadingMeasuredBlock> measured = new(_sourceBlocks.Count + _translationBlocks.Count);
        foreach (BlockLayout layout in AllBlocks())
        {
            measured.Add(new ReadingMeasuredBlock(layout.Column, layout.BlockIndex,
                new Rect(layout.Left, layout.Top, layout.Width, layout.Height),
                layout.Source.Kind, layout.Source.BoxId, layout.Source.PageIndex));
        }

        return measured;
    }

    /// <summary>Hit-tests a view point to the column, block and character position under it.
    /// Character positions map to the block's displayed text; composite blocks (tables, media,
    /// dividers) resolve to the start or end of their plain text by vertical midpoint.</summary>
    public bool TryHitTestPosition(
        Point point, out ReadingColumnRole column, out int blockIndex, out int characterIndex)
    {
        EnsureLayout(_layoutWidth > 0 ? _layoutWidth : NormalizeWidth(Bounds.Width));
        foreach (ReadingColumnRole candidate in new[] { ReadingColumnRole.Source, ReadingColumnRole.Translation })
        {
            BlockLayout? layout = FindBlockAt(candidate, point);
            if (layout is null)
            {
                continue;
            }

            column = candidate;
            blockIndex = layout.BlockIndex;
            characterIndex = HitTestCharacterIndex(layout, point);
            return true;
        }

        column = ReadingColumnRole.Source;
        blockIndex = -1;
        characterIndex = 0;
        return false;
    }

    /// <summary>Starts (or collapses) the selection at a position in the given column.</summary>
    public void BeginSelection(ReadingColumnRole column, int blockIndex, int characterIndex)
    {
        ReadingTextPosition position = ClampPosition(column, blockIndex, characterIndex);
        _selection = new ReadingSelection(column, position, position);
        InvalidateVisual();
    }

    /// <summary>Extends the selection's focus to a position. A position in the other column is
    /// clamped to this column's boundary: a selection never crosses columns.</summary>
    public void ExtendSelection(ReadingColumnRole column, int blockIndex, int characterIndex)
    {
        if (_selection is not { } selection)
        {
            BeginSelection(column, blockIndex, characterIndex);
            return;
        }

        ReadingTextPosition focus = column == selection.Column
            ? ClampPosition(column, blockIndex, characterIndex)
            : ClampToColumnBounds(selection.Column, blockIndex, characterIndex, selection.Start);
        _selection = selection with { Focus = focus };
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_selection is not null)
        {
            _selection = null;
            InvalidateVisual();
        }
    }

    /// <summary>The selected text: the covered blocks' displayed text in reading order, joined by
    /// line breaks, trimmed to the selection's character bounds in the boundary blocks.</summary>
    public string GetSelectedText()
    {
        if (_selection is not { } selection || selection.IsEmpty)
        {
            return string.Empty;
        }

        IReadOnlyList<BlockLayout> blocks = ColumnBlocks(selection.Column);
        ReadingTextPosition start = selection.Start;
        ReadingTextPosition end = selection.End;
        List<string> parts = [];
        for (int index = Math.Max(0, start.BlockIndex); index <= end.BlockIndex && index < blocks.Count; index++)
        {
            string text = blocks[index].PlainText;
            int from = index == start.BlockIndex ? Math.Clamp(start.CharacterIndex, 0, text.Length) : 0;
            int to = index == end.BlockIndex ? Math.Clamp(end.CharacterIndex, 0, text.Length) : text.Length;
            if (to > from)
            {
                parts.Add(text.Substring(from, to - from));
            }
        }

        return string.Join("\n", parts);
    }

    /// <summary>The distinct source boxes covered by the selection, in reading order. The host
    /// opens the first one for editing.</summary>
    public IReadOnlyList<DocumentBoxId> GetSelectedBoxIds()
    {
        if (_selection is not { } selection)
        {
            return [];
        }

        IReadOnlyList<BlockLayout> blocks = ColumnBlocks(selection.Column);
        ReadingTextPosition start = selection.Start;
        ReadingTextPosition end = selection.End;
        List<DocumentBoxId> ids = [];
        for (int index = Math.Max(0, start.BlockIndex); index <= end.BlockIndex && index < blocks.Count; index++)
        {
            if (blocks[index].Source.BoxId is { } boxId && !ids.Contains(boxId))
            {
                ids.Add(boxId);
            }
        }

        return ids;
    }

    /// <summary>Plain text of both columns for accessibility (see <see cref="ReadingAutomationPeer"/>).</summary>
    public string GetPlainText()
    {
        StringBuilder builder = new();
        AppendPlainText(builder, _sourceBlocks);
        if (TranslationScene is not null)
        {
            AppendPlainText(builder, _translationBlocks);
        }

        return builder.ToString();
    }

    /// <summary>Copies the selection to the platform clipboard. Returns false when there is no
    /// selection or no clipboard is available.</summary>
    public async Task<bool> CopySelectionAsync()
    {
        if (!HasSelection)
        {
            return false;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        await clipboard.SetTextAsync(GetSelectedText());
        return true;
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new ReadingAutomationPeer(this);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point point = e.GetPosition(this);
        Focus();
        if (TryHitTestPosition(point, out ReadingColumnRole column, out int blockIndex, out int characterIndex))
        {
            BeginSelection(column, blockIndex, characterIndex);
            BlockClicked?.Invoke(this,
                new ReadingBlockActivation(column, ColumnBlocks(column)[blockIndex].Source.BoxId));
        }
        else
        {
            ClearSelection();
            BlockClicked?.Invoke(this, new ReadingBlockActivation(ReadingColumnRole.Source, null));
        }

        _selecting = true;
        e.Pointer.Capture(this);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (_selecting && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (TryHitTestPosition(point, out ReadingColumnRole column, out int blockIndex, out int characterIndex))
            {
                ExtendSelection(column, blockIndex, characterIndex);
            }

            return;
        }

        bool overBox = TryHitTestPosition(point, out ReadingColumnRole hovered, out int hoveredIndex, out _) &&
                       ColumnBlocks(hovered)[hoveredIndex].Source.BoxId is not null;
        Cursor = overBox ? HandCursor : Cursor.Default;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Cursor = Cursor.Default;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && HasSelection)
        {
            e.Handled = true;
            _ = CopySelectionAsync();
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        _copyItem.IsEnabled = HasSelection;
        _editItem.IsVisible = CanEditSelection && _selection?.Column == ReadingColumnRole.Source && HasSelection;
        // Show at the pointer (like a ContextFlyout), not anchored to the control: the menu must
        // appear on the selection the user just made, wherever it is inside the reading surface.
        _contextFlyout.ShowAt(this, true);
    }

    private void OnCopyItemClick(object? sender, RoutedEventArgs e)
    {
        _ = CopySelectionAsync();
    }

    private void OnEditItemClick(object? sender, RoutedEventArgs e)
    {
        RaiseEditSelection();
    }

    /// <summary>Raises <see cref="EditSelectionRequested"/> for the active source-column selection.
    /// Invoked by the context menu's 编辑选中文本 item and by tests.</summary>
    public void RaiseEditSelection()
    {
        if (_selection is not { Column: ReadingColumnRole.Source } selection || selection.IsEmpty)
        {
            return;
        }

        IReadOnlyList<BlockLayout> blocks = ColumnBlocks(selection.Column);
        int pageIndex = selection.Start.BlockIndex >= 0 && selection.Start.BlockIndex < blocks.Count
            ? blocks[selection.Start.BlockIndex].Source.PageIndex
            : -1;
        EditSelectionRequested?.Invoke(this,
            new ReadingEditSelectionRequest(GetSelectedBoxIds(), GetSelectedText(), pageIndex));
    }

    private void OnSceneChanged()
    {
        ResetImageLoads();
        ReleaseAllImages();
        _layoutValid = false;
        ClearSelection();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnLayoutInputChanged()
    {
        _layoutValid = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnImageSourceChanged()
    {
        ResetImageLoads();
        ReleaseAllImages();
        _layoutValid = false;
        InvalidateMeasure();
        InvalidateVisual();
        EnsureImageLoads();
    }

    private static double NormalizeWidth(double width)
    {
        return double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ? FallbackWidth : width;
    }

    private void ResolveThemeBrushes()
    {
        _foreground = ResolveBrush("OnSurfaceBrush", FallbackForeground);
        _muted = ResolveBrush("OnSurfaceVariantBrush", FallbackMuted);
        _surfaceHigh = ResolveBrush("SurfaceContainerHighBrush", FallbackSurfaceHigh);
        _outline = ResolveBrush("OutlineVariantBrush", FallbackOutline);
        _codeBackground = ResolveBrush("SurfaceContainerHighBrush", FallbackCodeBackground);
        _link = ResolveBrush("PrimaryBrush", FallbackLink);
        _gridPen = new ImmutablePen(_outline, 1);
        _dividerPen = new ImmutablePen(_outline, 1);
    }

    private ImmutableSolidColorBrush ResolveBrush(string key, ImmutableSolidColorBrush fallback)
    {
        return this.TryFindResource(key, out object? value) && value is ISolidColorBrush brush
            ? new ImmutableSolidColorBrush(brush.Color)
            : fallback;
    }

    private void EnsureLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        if (_layoutValid && Math.Abs(width - _layoutWidth) < 0.01)
        {
            return;
        }

        BuildLayout(width);
        EnsureImageLoads();
    }

    private void BuildLayout(double width)
    {
        _sourceBlocks.Clear();
        _translationBlocks.Clear();
        IReadOnlyList<ReadingBlock> source = SourceScene?.Blocks ?? [];
        IReadOnlyList<ReadingBlock> translation = TranslationScene?.Blocks ?? [];
        bool compare = TranslationScene is not null;
        int rowCount = Math.Max(source.Count, compare ? translation.Count : 0);
        double y = 0;
        if (!compare)
        {
            foreach (ReadingBlock block in source)
            {
                y = PlaceSingleColumn(block, width, y);
            }
        }
        else if (CompareMode == ReadingCompareMode.SideBySide)
        {
            double columnWidth = Math.Max(1, (width - CompareGutter) / 2);
            for (int index = 0; index < rowCount; index++)
            {
                ReadingBlock sourceBlock = index < source.Count ? source[index] : ReadingBlock.Empty();
                ReadingBlock translationBlock = index < translation.Count
                    ? translation[index]
                    : ReadingBlock.Untranslated(sourceBlock.BoxId, sourceBlock.PageIndex);
                BlockLayout left = BuildBlock(sourceBlock, columnWidth);
                BlockLayout right = BuildBlock(translationBlock, columnWidth);
                // Matched tops: both sides of a pair start at the same y; the row is as tall as
                // its taller side, so the next pair's top lines up across columns again.
                double top = y + Math.Max(TopGap(sourceBlock.Kind), TopGap(translationBlock.Kind));
                left.Column = ReadingColumnRole.Source;
                left.BlockIndex = index;
                left.Left = 0;
                left.Top = top;
                right.Column = ReadingColumnRole.Translation;
                right.BlockIndex = index;
                right.Left = columnWidth + CompareGutter;
                right.Top = top;
                _sourceBlocks.Add(left);
                _translationBlocks.Add(right);
                y = top + Math.Max(left.Height, right.Height) +
                    Math.Max(BottomGap(sourceBlock.Kind), BottomGap(translationBlock.Kind));
            }
        }
        else
        {
            for (int index = 0; index < rowCount; index++)
            {
                ReadingBlock sourceBlock = index < source.Count ? source[index] : ReadingBlock.Empty();
                ReadingBlock translationBlock = index < translation.Count
                    ? translation[index]
                    : ReadingBlock.Untranslated(sourceBlock.BoxId, sourceBlock.PageIndex);
                BlockLayout above = BuildBlock(sourceBlock, width);
                BlockLayout below = BuildBlock(translationBlock, Math.Max(1, width - StackedTranslationIndent));
                // Stacked: the translation sits underneath its source inside the same paired
                // block, slightly indented so the pair reads as one unit.
                double top = y + TopGap(sourceBlock.Kind);
                above.Column = ReadingColumnRole.Source;
                above.BlockIndex = index;
                above.Left = 0;
                above.Top = top;
                below.Column = ReadingColumnRole.Translation;
                below.BlockIndex = index;
                below.Left = StackedTranslationIndent;
                below.Top = top + above.Height + StackedInnerGap;
                _sourceBlocks.Add(above);
                _translationBlocks.Add(below);
                y = below.Top + below.Height + BottomGap(sourceBlock.Kind);
            }
        }

        _totalHeight = y;
        _layoutWidth = width;
        _layoutValid = true;
    }

    private double PlaceSingleColumn(ReadingBlock block, double width, double y)
    {
        BlockLayout layout = BuildBlock(block, width);
        layout.Column = ReadingColumnRole.Source;
        layout.BlockIndex = _sourceBlocks.Count;
        layout.Left = 0;
        layout.Top = y + TopGap(block.Kind);
        _sourceBlocks.Add(layout);
        return layout.Top + layout.Height + BottomGap(block.Kind);
    }

    private BlockLayout BuildBlock(ReadingBlock block, double width)
    {
        BlockLayout layout = new() { Source = block, PlainText = block.Text, Width = width };
        double contentWidth = Math.Max(1, width);
        double bodySize = double.IsNaN(FontSize) || FontSize <= 0 ? 14 : FontSize;
        switch (block.Kind)
        {
            case "heading":
                layout.Text = BuildRawLayout(
                    block.Text, BodyBoldTypeface(), HeadingFontSize(block.Level, bodySize), _foreground,
                    contentWidth);
                layout.Height = layout.Text.Height;
                break;
            case "code":
                layout.Text = BuildRawLayout(
                    block.Text, MonoTypeface(), bodySize, _foreground, contentWidth - 2 * CodePadding);
                layout.Height = layout.Text.Height + 2 * CodePadding;
                break;
            case "equation":
                layout.Text = BuildRawLayout(
                    block.Text, BodyTypeface(), bodySize, _foreground, contentWidth - 2 * EquationPadding,
                    TextAlignment.Center, FontStyle.Italic);
                layout.Height = layout.Text.Height + 2 * EquationPadding;
                break;
            case "table" when block.Table is { Rows.Count: > 0 } table:
                layout.Table = BuildTable(table, contentWidth, bodySize);
                layout.PlainText = TablePlainText(table);
                layout.Height = layout.Table.Height;
                break;
            case "table":
                layout.Text = BuildRawLayout(
                    block.Text, MonoTypeface(), bodySize, _foreground, contentWidth);
                layout.Height = layout.Text.Height;
                break;
            case "media":
                layout = BuildMedia(block, contentWidth, bodySize);
                break;
            case "divider":
                layout.Height = 1;
                break;
            case ReadingBlock.UntranslatedKind:
                layout.Text = BuildRawLayout(
                    block.Text, BodyTypeface(), bodySize, _muted, contentWidth - 2 * PlaceholderPadding,
                    fontStyle: FontStyle.Italic);
                layout.Height = layout.Text.Height + 2 * PlaceholderPadding;
                break;
            default:
                layout.Text = BuildTextLayout(block, contentWidth, bodySize);
                layout.Height = layout.Text.Height;
                break;
        }

        return layout;
    }

    private TextLayout BuildTextLayout(ReadingBlock block, double width, double bodySize)
    {
        IBrush foreground = block.Kind == "quote" ? _muted : _foreground;
        if (block.Inlines is { Count: > 0 } inlines)
        {
            return BuildInlineLayout(inlines, foreground, width, bodySize);
        }

        return BuildRawLayout(block.Text, BodyTypeface(), bodySize, foreground, width);
    }

    private TextLayout BuildRawLayout(
        string text,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth,
        TextAlignment alignment = TextAlignment.Left,
        FontStyle fontStyle = FontStyle.Normal,
        TextWrapping wrapping = TextWrapping.Wrap,
        double lineSpacing = double.NaN)
    {
        Typeface face = fontStyle == FontStyle.Normal
            ? typeface
            : new Typeface(typeface.FontFamily, fontStyle, typeface.Weight);
        return new TextLayout(
            text,
            face,
            fontSize,
            foreground,
            alignment,
            wrapping,
            TextTrimming.None,
            null,
            FlowDirection.LeftToRight,
            Math.Max(1, maxWidth));
    }

    // Inline markup is flattened into one string plus per-range run-property overrides, so a single
    // TextLayout wraps bold/italic/code/link spans across lines the way flowing text should.
    private TextLayout BuildInlineLayout(
        IReadOnlyList<MarkdownInlineModel> inlines, IBrush foreground, double maxWidth, double bodySize)
    {
        StringBuilder builder = new();
        List<ValueSpan<TextRunProperties>> spans = [];
        AppendInlines(inlines, InlineStyle.Default, foreground, bodySize, builder, spans);
        if (spans.Count == 0)
        {
            return BuildRawLayout(string.Empty, BodyTypeface(), bodySize, foreground, maxWidth);
        }

        return new TextLayout(
            builder.ToString(),
            BodyTypeface(),
            bodySize,
            foreground,
            TextAlignment.Left,
            TextWrapping.Wrap,
            TextTrimming.None,
            null,
            FlowDirection.LeftToRight,
            Math.Max(1, maxWidth),
            double.PositiveInfinity,
            double.NaN,
            0,
            0,
            null,
            spans);
    }

    private void AppendInlines(
        IReadOnlyList<MarkdownInlineModel> inlines,
        InlineStyle style,
        IBrush foreground,
        double bodySize,
        StringBuilder builder,
        List<ValueSpan<TextRunProperties>> spans)
    {
        foreach (MarkdownInlineModel inline in inlines)
        {
            switch (inline.Kind)
            {
                case "text":
                    AppendRun(inline.Text, style, foreground, bodySize, builder, spans);
                    break;
                case "line_break":
                    AppendRun("\n", style, foreground, bodySize, builder, spans);
                    break;
                case "strong":
                    AppendChildren(inline, style with { Bold = true }, foreground, bodySize, builder, spans);
                    break;
                case "emphasis":
                    AppendChildren(inline, style with { Italic = true }, foreground, bodySize, builder, spans);
                    break;
                case "strikethrough":
                    AppendChildren(inline, style with { Strikethrough = true }, foreground, bodySize, builder,
                        spans);
                    break;
                case "code":
                    AppendRun(inline.Text, style with { Code = true }, foreground, bodySize, builder, spans);
                    break;
                case "link":
                    AppendChildren(inline, style with { Link = true }, foreground, bodySize, builder, spans);
                    break;
                case "superscript":
                    AppendRun(Flatten(inline.Children), style, foreground, bodySize, builder, spans);
                    break;
                default:
                    AppendChildren(inline, style, foreground, bodySize, builder, spans);
                    break;
            }
        }
    }

    private void AppendChildren(
        MarkdownInlineModel inline,
        InlineStyle style,
        IBrush foreground,
        double bodySize,
        StringBuilder builder,
        List<ValueSpan<TextRunProperties>> spans)
    {
        if (inline.Children is { Count: > 0 } children)
        {
            AppendInlines(children, style, foreground, bodySize, builder, spans);
        }
        else
        {
            AppendRun(inline.Text, style, foreground, bodySize, builder, spans);
        }
    }

    private void AppendRun(
        string? text,
        InlineStyle style,
        IBrush foreground,
        double bodySize,
        StringBuilder builder,
        List<ValueSpan<TextRunProperties>> spans)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int start = builder.Length;
        builder.Append(text);
        spans.Add(new ValueSpan<TextRunProperties>(start, text.Length,
            CreateRunProperties(style, foreground, bodySize)));
    }

    private TextRunProperties CreateRunProperties(InlineStyle style, IBrush foreground, double bodySize)
    {
        FontStyle fontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
        FontWeight fontWeight = style.Bold ? FontWeight.SemiBold : FontWeight.Normal;
        Typeface typeface = new(style.Code ? MonospaceFont : FontFamily.Default, fontStyle, fontWeight);

        TextDecorationCollection? decorations = null;
        if (style.Link)
        {
            decorations = style.Strikethrough
                ? new TextDecorationCollection(TextDecorations.Underline.Concat(TextDecorations.Strikethrough))
                : TextDecorations.Underline;
        }
        else if (style.Strikethrough)
        {
            decorations = TextDecorations.Strikethrough;
        }

        IBrush runForeground = style.Link ? _link : foreground;
        IBrush? background = style.Code ? _codeBackground : null;
        return new GenericTextRunProperties(typeface, bodySize, decorations, runForeground, background);
    }

    private static string Flatten(IReadOnlyList<MarkdownInlineModel>? inlines)
    {
        return inlines is null
            ? string.Empty
            : string.Concat(inlines.Select(inline => inline.Text + Flatten(inline.Children)));
    }

    private TableLayout BuildTable(ReadingTable table, double width, double bodySize)
    {
        int rows = table.Rows.Count;
        int columns = 1;
        foreach (IReadOnlyList<string> row in table.Rows)
        {
            columns = Math.Max(columns, row.Count);
        }

        double available = Math.Max(MinColumnWidth, width - 1);
        // Pass 1: each column's natural width from unshaped wrapping ("no wrap" measurement).
        double[] natural = new double[columns];
        for (int column = 0; column < columns; column++)
        {
            double best = MinColumnWidth;
            for (int row = 0; row < rows; row++)
            {
                string text = CellText(table.Rows[row], column);
                if (text.Length == 0)
                {
                    continue;
                }

                TextLayout measure = BuildRawLayout(
                    text, BodyTypeface(), bodySize, _foreground, double.PositiveInfinity,
                    wrapping: TextWrapping.NoWrap);
                best = Math.Max(best, measure.Width + 2 * TableCellPadX);
            }

            natural[column] = best;
        }

        // Cap each column, then share the full width proportionally so the grid spans the viewport.
        double total = 0;
        double cap = Math.Max(80, available * 0.55);
        for (int column = 0; column < columns; column++)
        {
            natural[column] = Math.Min(natural[column], cap);
            total += natural[column];
        }

        double[] columnWidths = new double[columns];
        double assigned = 0;
        for (int column = 0; column < columns; column++)
        {
            columnWidths[column] = Math.Max(MinColumnWidth,
                total <= 0 ? available / columns : natural[column] / total * available);
            assigned += columnWidths[column];
        }

        if (assigned > 0)
        {
            double scale = available / assigned;
            for (int column = 0; column < columns; column++)
            {
                columnWidths[column] *= scale;
            }
        }

        // Pass 2: build the wrapped cell layouts at the assigned widths.
        TableLayout result = new()
        {
            Rows = rows,
            Columns = columns,
            ColumnWidths = columnWidths,
            RowHeights = new double[rows],
            Cells = new TextLayout?[rows * columns],
            HasHeader = table.HasHeader,
            Width = available
        };
        for (int row = 0; row < rows; row++)
        {
            bool header = table.HasHeader && row == 0;
            double rowHeight = 0;
            for (int column = 0; column < columns; column++)
            {
                TextLayout cell = BuildRawLayout(
                    CellText(table.Rows[row], column),
                    header ? BodyBoldTypeface() : BodyTypeface(),
                    bodySize,
                    _foreground,
                    Math.Max(8, columnWidths[column] - 2 * TableCellPadX));
                result.Cells[row * columns + column] = cell;
                rowHeight = Math.Max(rowHeight, cell.Height);
            }

            result.RowHeights[row] = Math.Max(18, rowHeight + 2 * TableCellPadY);
        }

        double tableHeight = 0;
        foreach (double rowHeight in result.RowHeights)
        {
            tableHeight += rowHeight;
        }

        result.Height = tableHeight;
        return result;
    }

    private BlockLayout BuildMedia(ReadingBlock block, double width, double bodySize)
    {
        BlockLayout layout = new()
        {
            Source = block,
            // Media blocks are built here rather than by BuildBlock's initializer, so the block
            // width has to be carried over: DrawMedia centres the image with (Width - DestWidth)/2.
            Width = Math.Max(1, width),
            PlainText = string.IsNullOrWhiteSpace(block.Text)
                ? block.MediaLabel ?? string.Empty
                : $"{block.MediaLabel ?? string.Empty}\n{block.Text}"
        };
        double innerWidth = Math.Max(1, width - 2 * MediaPadding);
        layout.MediaLabel = BuildRawLayout(
            block.MediaLabel ?? "媒体", BodyBoldTypeface(), bodySize, _foreground, innerWidth,
            TextAlignment.Center);
        layout.Text = string.IsNullOrWhiteSpace(block.Text)
            ? null
            : BuildRawLayout(block.Text, BodyTypeface(), bodySize, _muted, innerWidth, TextAlignment.Center);
        double captionHeight = layout.MediaLabel.Height + (layout.Text is null ? 0 : 4 + layout.Text.Height);
        IImage? image = block.Image is { } region && region.ImageKey.Length > 0 &&
                        _images.TryGetValue(region.ImageKey, out IImage? loaded)
            ? loaded
            : null;
        if (image is null || block.Image is not { } imageRegion)
        {
            // No image (yet): the placeholder card keeps the block's height stable.
            layout.Height = 2 * MediaPadding + captionHeight;
            return layout;
        }

        // The normalized region is drawn straight out of the in-memory page bitmap: no cropped
        // image file is ever produced.
        (Rect source, Size destination) = ReadingMediaGeometry.ComputeDrawRects(
            image.Size, imageRegion.Region, innerWidth, MaxMediaImageHeight);
        layout.MediaImage = image;
        layout.MediaSourceRect = source;
        layout.MediaDestWidth = destination.Width;
        layout.MediaDestHeight = destination.Height;
        layout.Height = 2 * MediaPadding + destination.Height + MediaCaptionGap + captionHeight;
        return layout;
    }

    private void DrawSelection(DrawingContext context, BlockLayout layout)
    {
        if (TryGetSelectionBrushes(out ImmutableSolidColorBrush fill, out _) &&
            SelectionSpansBlock(layout))
        {
            DrawBlockHighlight(context, layout, fill);
        }

        if (SelectedBoxId is { } selected && layout.Source.BoxId == selected &&
            TryGetSelectionBrushes(out fill, out ImmutableSolidColorBrush bar))
        {
            double top = Math.Max(0, layout.Top - 3);
            double bottom = layout.Top + layout.Height + 3;
            context.FillRectangle(fill, new Rect(layout.Left, top, layout.Width, bottom - top));
            context.FillRectangle(bar, new Rect(layout.Left, top, 3, bottom - top));
        }
    }

    private void DrawBlockHighlight(DrawingContext context, BlockLayout layout, ImmutableSolidColorBrush fill)
    {
        if (layout.Text is { } text)
        {
            ReadingSelection selection = _selection!;
            string plain = layout.PlainText;
            int from = layout.BlockIndex == selection.Start.BlockIndex
                ? Math.Clamp(selection.Start.CharacterIndex, 0, plain.Length)
                : 0;
            int to = layout.BlockIndex == selection.End.BlockIndex
                ? Math.Clamp(selection.End.CharacterIndex, 0, plain.Length)
                : plain.Length;
            foreach (Rect rect in text.HitTestTextRange(from, Math.Max(0, to - from)))
            {
                context.FillRectangle(fill,
                    new Rect(layout.Left + rect.X, layout.Top + rect.Y, rect.Width, rect.Height));
            }

            return;
        }

        // Composite blocks (tables, media, dividers) highlight as a whole rectangle.
        context.FillRectangle(fill, new Rect(layout.Left, layout.Top, layout.Width, layout.Height));
    }

    private bool SelectionSpansBlock(BlockLayout layout)
    {
        if (_selection is not { } selection || selection.IsEmpty || selection.Column != layout.Column)
        {
            return false;
        }

        return layout.BlockIndex >= selection.Start.BlockIndex && layout.BlockIndex <= selection.End.BlockIndex;
    }

    internal bool TryGetSelectionBrushes(
        out ImmutableSolidColorBrush fill,
        out ImmutableSolidColorBrush bar)
    {
        if (SelectionAccentBrush is not ISolidColorBrush accent)
        {
            _selectionFill = null;
            _selectionBar = null;
            fill = FallbackForeground;
            bar = FallbackForeground;
            return false;
        }

        if (_selectionFill is null || _selectionAccentColor != accent.Color)
        {
            _selectionAccentColor = accent.Color;
            _selectionFill = new ImmutableSolidColorBrush(Color.FromArgb(30, accent.Color.R, accent.Color.G,
                accent.Color.B));
            _selectionBar = new ImmutableSolidColorBrush(Color.FromRgb(accent.Color.R, accent.Color.G,
                accent.Color.B));
        }

        fill = _selectionFill;
        bar = _selectionBar!;
        return true;
    }

    private void DrawBlock(DrawingContext context, BlockLayout layout)
    {
        double width = layout.Width;
        switch (layout.Source.Kind)
        {
            case "heading":
                layout.Text?.Draw(context, new Point(layout.Left, layout.Top));
                break;
            case "code":
                context.FillRectangle(_surfaceHigh,
                    new Rect(layout.Left, layout.Top, width, layout.Height), 4);
                layout.Text?.Draw(context, new Point(layout.Left + CodePadding, layout.Top + CodePadding));
                break;
            case "equation":
                context.FillRectangle(_surfaceHigh,
                    new Rect(layout.Left, layout.Top, width, layout.Height), 4);
                layout.Text?.Draw(context,
                    new Point(layout.Left + EquationPadding, layout.Top + EquationPadding));
                break;
            case "table" when layout.Table is { } table:
                DrawTable(context, layout, table);
                break;
            case "media":
                DrawMedia(context, layout);
                break;
            case "divider":
                double y = layout.Top + layout.Height / 2;
                context.DrawLine(_dividerPen, new Point(layout.Left, y), new Point(layout.Left + width, y));
                break;
            case ReadingBlock.UntranslatedKind:
                context.FillRectangle(_surfaceHigh,
                    new Rect(layout.Left, layout.Top, width, layout.Height), 4);
                layout.Text?.Draw(context,
                    new Point(layout.Left + PlaceholderPadding, layout.Top + PlaceholderPadding));
                break;
            default:
                layout.Text?.Draw(context, new Point(layout.Left, layout.Top));
                break;
        }
    }

    private void DrawTable(DrawingContext context, BlockLayout layout, TableLayout table)
    {
        double y = layout.Top;
        for (int row = 0; row < table.Rows; row++)
        {
            if (table.HasHeader && row == 0)
            {
                context.FillRectangle(_surfaceHigh,
                    new Rect(layout.Left, y, table.Width, table.RowHeights[row]));
            }

            double x = layout.Left;
            for (int column = 0; column < table.Columns; column++)
            {
                table.Cells[row * table.Columns + column]?.Draw(
                    context, new Point(x + TableCellPadX, y + TableCellPadY));
                x += table.ColumnWidths[column];
            }

            y += table.RowHeights[row];
        }

        double lineY = layout.Top;
        for (int row = 0; row <= table.Rows; row++)
        {
            context.DrawLine(_gridPen, new Point(layout.Left, lineY),
                new Point(layout.Left + table.Width, lineY));
            if (row < table.Rows)
            {
                lineY += table.RowHeights[row];
            }
        }

        double lineX = layout.Left;
        for (int column = 0; column <= table.Columns; column++)
        {
            context.DrawLine(_gridPen, new Point(lineX, layout.Top),
                new Point(lineX, layout.Top + table.Height));
            if (column < table.Columns)
            {
                lineX += table.ColumnWidths[column];
            }
        }
    }

    private void DrawMedia(DrawingContext context, BlockLayout layout)
    {
        if (layout.MediaImage is { } image)
        {
            double x = layout.Left + (layout.Width - layout.MediaDestWidth) / 2;
            context.DrawImage(
                image,
                layout.MediaSourceRect,
                new Rect(x, layout.Top + MediaPadding, layout.MediaDestWidth, layout.MediaDestHeight));
            DrawMediaCaption(context, layout, layout.Top + MediaPadding + layout.MediaDestHeight +
                                              MediaCaptionGap);
            return;
        }

        Rect card = new(layout.Left, layout.Top, layout.Width, layout.Height);
        context.FillRectangle(_surfaceHigh, card, 6);
        context.DrawRectangle(_gridPen, card, 6);
        DrawMediaCaption(context, layout, layout.Top + MediaPadding);
    }

    private void DrawMediaCaption(DrawingContext context, BlockLayout layout, double top)
    {
        double y = top;
        if (layout.MediaLabel is { } label)
        {
            label.Draw(context, new Point(layout.Left + MediaPadding, y));
            y += label.Height + 4;
        }

        layout.Text?.Draw(context, new Point(layout.Left + MediaPadding, y));
    }

    // Loads one page bitmap exactly once, then hands the result back to the UI thread. A faulted
    // load is treated like a missing image: the placeholder card stays and no retry is scheduled.
    private void EnsureImageLoads()
    {
        IReadingImageSource? source = ImageSource;
        if (source is null || _imageLoadCancellation is not { } cancellation ||
            cancellation.IsCancellationRequested)
        {
            return;
        }

        (double visibleTop, double visibleBottom) = VisibleRange();
        HashSet<string> visibleKeys = new(StringComparer.Ordinal);
        foreach (BlockLayout layout in AllBlocks())
        {
            if (layout.Top + layout.Height < visibleTop || layout.Top > visibleBottom)
            {
                continue;
            }

            string? key = layout.Source.Image?.ImageKey;
            if (key is not { Length: > 0 })
            {
                continue;
            }

            visibleKeys.Add(key);
            if (
                _images.ContainsKey(key) ||
                _imagesInFlight.Contains(key) ||
                _imagesFailed.Contains(key))
            {
                continue;
            }

            _imagesInFlight.Add(key);
            _ = LoadImageAsync(key, source, cancellation.Token, _imageGeneration);
        }

        foreach (string key in _images.Keys.Where(key => !visibleKeys.Contains(key)).ToArray())
        {
            source.ReleaseImage(key, _images[key]);
            _images.Remove(key);
        }

        _imagesFailed.RemoveWhere(key => !visibleKeys.Contains(key));
    }

    private async Task LoadImageAsync(
        string imageKey, IReadingImageSource source, CancellationToken cancellationToken, int generation)
    {
        IImage? image = null;
        try
        {
            image = await source.LoadImageAsync(imageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Scene replacement and detach cancel pending loads.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"reading-view image load failed ({imageKey}): {exception}");
        }
        finally
        {
            // The source may complete on a pool thread, so the cache is only touched on the UI thread.
            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyImage(imageKey, image, source, generation);
            }
            else
            {
                Dispatcher.UIThread.Post(() => ApplyImage(imageKey, image, source, generation));
            }
        }
    }

    private void ApplyImage(string imageKey, IImage? image, IReadingImageSource source, int generation)
    {
        if (generation != _imageGeneration)
        {
            return;
        }

        if (!_imagesInFlight.Remove(imageKey))
        {
            return;
        }

        if (image is null)
        {
            _imagesFailed.Add(imageKey);
            return;
        }

        _images[imageKey] = image;
        _cachedImageSource = source;
        _layoutValid = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void CancelImageLoads()
    {
        _imageGeneration++;
        CancellationTokenSource? cancellation = _imageLoadCancellation;
        _imageLoadCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        // Cancelled keys drop out of the in-flight set so a later attach retries them.
        _imagesInFlight.Clear();
    }

    private void ResetImageLoads()
    {
        bool wasActive = _imageLoadCancellation is not null;
        CancelImageLoads();
        if (wasActive)
        {
            _imageLoadCancellation = new CancellationTokenSource();
        }
    }

    private void ReleaseAllImages()
    {
        if (_cachedImageSource is { } source)
        {
            foreach ((string key, IImage image) in _images)
            {
                source.ReleaseImage(key, image);
            }
        }

        _images.Clear();
        _imagesFailed.Clear();
        _cachedImageSource = null;
    }

    private IEnumerable<BlockLayout> AllBlocks()
    {
        foreach (BlockLayout layout in _sourceBlocks)
        {
            yield return layout;
        }

        foreach (BlockLayout layout in _translationBlocks)
        {
            yield return layout;
        }
    }

    private IReadOnlyList<BlockLayout> ColumnBlocks(ReadingColumnRole column)
    {
        return column == ReadingColumnRole.Source ? _sourceBlocks : _translationBlocks;
    }

    private BlockLayout? FindBlockAt(ReadingColumnRole column, Point point)
    {
        IReadOnlyList<BlockLayout> blocks = ColumnBlocks(column);
        if (blocks.Count == 0 || point.Y < blocks[0].Top - 4)
        {
            return null;
        }

        int low = 0;
        int high = blocks.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (blocks[mid].Top <= point.Y + 4)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        BlockLayout layout = blocks[found];
        if (point.Y > layout.Top + layout.Height + 4 ||
            point.X < layout.Left - 4 || point.X > layout.Left + layout.Width + 4)
        {
            return null;
        }

        return layout;
    }

    private int HitTestCharacterIndex(BlockLayout layout, Point point)
    {
        if (layout.Text is not { } text)
        {
            // Composite blocks resolve to their plain-text start or end by vertical midpoint.
            return point.Y < layout.Top + layout.Height / 2 ? 0 : layout.PlainText.Length;
        }

        Point local = new(point.X - layout.Left, point.Y - layout.Top);
        TextHitTestResult hit = text.HitTestPoint(in local);
        int index = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        return Math.Clamp(index, 0, layout.PlainText.Length);
    }

    private ReadingTextPosition ClampPosition(ReadingColumnRole column, int blockIndex, int characterIndex)
    {
        IReadOnlyList<BlockLayout> blocks = ColumnBlocks(column);
        if (blocks.Count == 0)
        {
            return new ReadingTextPosition(0, 0);
        }

        int clampedBlock = Math.Clamp(blockIndex, 0, blocks.Count - 1);
        int clampedCharacter = Math.Clamp(characterIndex, 0, blocks[clampedBlock].PlainText.Length);
        return new ReadingTextPosition(clampedBlock, clampedCharacter);
    }

    // Cross-column drags clamp to the selection column's far boundary instead of entering the
    // other column.
    private ReadingTextPosition ClampToColumnBounds(
        ReadingColumnRole column, int blockIndex, int characterIndex, ReadingTextPosition inColumnStart)
    {
        IReadOnlyList<BlockLayout> blocks = ColumnBlocks(column);
        if (blocks.Count == 0)
        {
            return new ReadingTextPosition(0, 0);
        }

        if ((blockIndex < inColumnStart.BlockIndex && characterIndex <= 0) || blockIndex < 0)
        {
            return new ReadingTextPosition(0, 0);
        }

        if (blockIndex >= blocks.Count)
        {
            return new ReadingTextPosition(blocks.Count - 1, blocks[^1].PlainText.Length);
        }

        return ClampPosition(column, blockIndex, characterIndex);
    }

    private static void AppendPlainText(StringBuilder builder, IReadOnlyList<BlockLayout> blocks)
    {
        foreach (BlockLayout layout in blocks)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(layout.PlainText);
        }
    }

    private Typeface BodyTypeface()
    {
        return new Typeface(FontFamily);
    }

    private Typeface BodyBoldTypeface()
    {
        return new Typeface(FontFamily, FontStyle.Normal, FontWeight.SemiBold);
    }

    private static Typeface MonoTypeface()
    {
        return new Typeface(MonospaceFont);
    }

    private static double HeadingFontSize(int level, double bodySize)
    {
        double scale = level switch
        {
            <= 1 => 1.6,
            2 => 1.4,
            3 => 1.25,
            >= 4 and <= 5 => 1.1,
            _ => 1.0
        };
        return bodySize * scale;
    }

    private static string CellText(IReadOnlyList<string> row, int column)
    {
        return column < row.Count ? row[column] ?? string.Empty : string.Empty;
    }

    private static string TablePlainText(ReadingTable table)
    {
        return string.Join("\n", table.Rows.Select(row => string.Join("\t", row)));
    }

    private (double Top, double Bottom) VisibleRange()
    {
        if (this.FindAncestorOfType<ScrollViewer>() is { } scroller &&
            scroller.TranslatePoint(default, this) is { } viewTop &&
            scroller.TranslatePoint(new Point(0, scroller.Viewport.Height), this) is { } viewBottom)
        {
            double slack = Math.Max(400, viewBottom.Y - viewTop.Y);
            return (viewTop.Y - slack, viewBottom.Y + slack);
        }

        return (0, Bounds.Height);
    }

    private static double TopGap(string kind)
    {
        return kind switch
        {
            "heading" => HeadingTopGap,
            "code" or "equation" or "table" or "media" => 4,
            "divider" => BodyBlockGap,
            _ => 0
        };
    }

    private static double BottomGap(string kind)
    {
        return kind switch
        {
            "heading" => BodyBlockGap,
            "table" => 10,
            _ => BodyBlockGap
        };
    }

    private sealed class BlockLayout
    {
        public ReadingBlock Source = null!;
        public ReadingColumnRole Column;
        public int BlockIndex;
        public double Left;
        public double Top;
        public double Width;
        public double Height;
        public string PlainText = string.Empty;
        public TextLayout? Text;
        public TextLayout? MediaLabel;
        public TableLayout? Table;
        public IImage? MediaImage;
        public Rect MediaSourceRect;
        public double MediaDestWidth;
        public double MediaDestHeight;
    }

    private sealed class TableLayout
    {
        public int Rows;
        public int Columns;
        public double[] ColumnWidths = [];
        public double[] RowHeights = [];
        public TextLayout?[] Cells = [];
        public bool HasHeader;
        public double Width;
        public double Height;
    }

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strikethrough, bool Code, bool Link)
    {
        public static readonly InlineStyle Default = new(false, false, false, false, false);
    }
}
