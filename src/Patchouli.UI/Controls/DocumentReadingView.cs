using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;

namespace Patchouli.UI.Controls;

// A read-only, single-visual reading surface for a DocumentReadingScene. Every block is laid out
// once per (content, width) and drawn with cached TextLayouts; Render culls blocks outside the
// hosting ScrollViewer's viewport. Clicking a block that carries a BoxId raises BlockClicked so
// the host can sync the box selection.
public sealed class DocumentReadingView : Control, ICustomHitTest
{
    private const double BodyFontSize = 13;
    private const double BodyBlockGap = 8;
    private const double HeadingTopGap = 16;
    private const double CodePadding = 8;
    private const double EquationPadding = 6;
    private const double MediaPadding = 12;
    private const double MediaCaptionGap = 6;
    private const double MaxMediaImageHeight = 240;
    private const double TableCellPadX = 6;
    private const double TableCellPadY = 4;
    private const double MinColumnWidth = 36;
    private const double FallbackWidth = 600;

    private static readonly FontFamily MonospaceFont = new("Consolas, Menlo, monospace");

    private static readonly Typeface BodyTypeface = new(FontFamily.Default);
    private static readonly Typeface BodyBoldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface MonoTypeface = new(MonospaceFont);

    private static readonly ImmutableSolidColorBrush FallbackForeground = new(Color.Parse("#1B1C1C"));
    private static readonly ImmutableSolidColorBrush FallbackMuted = new(Color.Parse("#484553"));
    private static readonly ImmutableSolidColorBrush FallbackSurfaceHigh = new(Color.Parse("#E9E8E7"));
    private static readonly ImmutableSolidColorBrush FallbackOutline = new(Color.Parse("#CAC4D5"));
    private static readonly ImmutableSolidColorBrush FallbackCodeBackground = new(Color.Parse("#ECE6F0"));
    private static readonly ImmutableSolidColorBrush FallbackLink = new(Color.Parse("#553BB5"));
    private static readonly ImmutableSolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public static readonly StyledProperty<DocumentReadingScene?> SceneProperty =
        AvaloniaProperty.Register<DocumentReadingView, DocumentReadingScene?>(nameof(Scene));

    public static readonly StyledProperty<DocumentBoxId?> SelectedBoxIdProperty =
        AvaloniaProperty.Register<DocumentReadingView, DocumentBoxId?>(nameof(SelectedBoxId));

    // Optional resolver for media block asset ids. Without one, media blocks stay placeholders.
    public static readonly StyledProperty<IMediaImageLoader?> MediaLoaderProperty =
        AvaloniaProperty.Register<DocumentReadingView, IMediaImageLoader?>(nameof(MediaLoader));

    // Selection accent injected by the call site (e.g. {DynamicResource PrimaryBrush}) so the
    // highlight follows the global palette. The control derives the translucent fill and the
    // opaque marker bar from this brush; nothing is drawn while it is unset or not solid.
    public static readonly StyledProperty<IBrush?> SelectionAccentBrushProperty =
        AvaloniaProperty.Register<DocumentReadingView, IBrush?>(nameof(SelectionAccentBrush));

    static DocumentReadingView()
    {
        SceneProperty.Changed.AddClassHandler<DocumentReadingView>((view, _) => view.OnSceneChanged());
        SelectedBoxIdProperty.Changed.AddClassHandler<DocumentReadingView>((view, _) => view.InvalidateVisual());
        MediaLoaderProperty.Changed.AddClassHandler<DocumentReadingView>((view, _) => view.OnMediaLoaderChanged());
        SelectionAccentBrushProperty.Changed.AddClassHandler<DocumentReadingView>((view, _) =>
            view.InvalidateVisual());
    }

    public DocumentReadingScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public DocumentBoxId? SelectedBoxId
    {
        get => GetValue(SelectedBoxIdProperty);
        set => SetValue(SelectedBoxIdProperty, value);
    }

    public IMediaImageLoader? MediaLoader
    {
        get => GetValue(MediaLoaderProperty);
        set => SetValue(MediaLoaderProperty, value);
    }

    public IBrush? SelectionAccentBrush
    {
        get => GetValue(SelectionAccentBrushProperty);
        set => SetValue(SelectionAccentBrushProperty, value);
    }

    // Raised on a left press with the BoxId of the clicked block, or null on empty space.
    public event EventHandler<DocumentBoxId?>? BlockClicked;

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

    private readonly List<BlockLayout> _blocks = [];
    private double[] _blockTops = [];
    private double _totalHeight;
    private double _layoutWidth = -1;
    private int _layoutHash;
    private DocumentReadingScene? _layoutScene;
    private bool _layoutValid;

    // Decoded media images keyed by asset id, plus the load bookkeeping that keeps one asset from
    // being requested twice. All three are touched on the UI thread only.
    private readonly Dictionary<string, IImage> _mediaImages = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mediaInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mediaFailed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _mediaLoadCancellation;

    /// <inheritdoc/>
    public bool HitTest(Point point)
    {
        // Transparent fill + this: presses anywhere in the control (including below the last block)
        // must reach OnPointerPressed so "click empty space" can clear the selection.
        return true;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ResolveThemeBrushes();
        // Foreground/background are baked into TextLayout, so a theme change invalidates the cache.
        _layoutValid = false;
        _mediaLoadCancellation ??= new CancellationTokenSource();
        InvalidateMeasure();
        InvalidateVisual();
        EnsureMediaLoads();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CancelMediaLoads();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        double width = NormalizeWidth(availableSize.Width);
        EnsureLayout(width);
        return new Size(width, _totalHeight);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        double width = NormalizeWidth(Bounds.Width);
        EnsureLayout(width);
        // Transparent fill makes the transparent gaps between blocks hit-testable.
        context.FillRectangle(TransparentBrush, new Rect(Bounds.Size));
        if (_blocks.Count == 0)
        {
            return;
        }

        (double visibleTop, double visibleBottom) = VisibleRange();
        DocumentBoxId? selectedBoxId = SelectedBoxId;
        foreach (BlockLayout layout in _blocks)
        {
            if (layout.Top + layout.Height < visibleTop || layout.Top > visibleBottom)
            {
                continue; // off-screen: advance nothing, issue no draw commands
            }

            DrawSelection(context, layout, selectedBoxId, width);
            DrawBlock(context, layout, width);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BlockLayout? hit = FindBlockAt(e.GetPosition(this).Y);
        BlockClicked?.Invoke(this, hit?.Source.BoxId);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        bool overBox = FindBlockAt(e.GetPosition(this).Y)?.Source.BoxId is not null;
        Cursor = overBox ? HandCursor : Cursor.Default;
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Cursor = Cursor.Default;
    }

    private void OnSceneChanged()
    {
        _layoutValid = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnMediaLoaderChanged()
    {
        // A different loader may resolve assets that failed before. Images already decoded stay
        // cached: asset identity, not the loader instance, keys them.
        _mediaFailed.Clear();
        _layoutValid = false;
        InvalidateMeasure();
        InvalidateVisual();
        EnsureMediaLoads();
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

    // (content hash, width) keys the layout cache: a new scene instance with identical content and a
    // resize-free measure pass both reuse the existing layouts instead of reshaping every block.
    private void EnsureLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        DocumentReadingScene scene = Scene ?? DocumentReadingScene.Empty;
        // Fast path: the scene instance is immutable and OnSceneChanged already invalidated the cache,
        // so an unchanged reference needs no content re-hash on every render pass.
        if (_layoutValid && ReferenceEquals(scene, _layoutScene) && Math.Abs(width - _layoutWidth) < 0.01)
        {
            return;
        }

        int hash = ComputeSceneHash(scene);
        if (_layoutValid && hash == _layoutHash && Math.Abs(width - _layoutWidth) < 0.01)
        {
            return;
        }

        BuildLayout(scene, width, hash);
        EnsureMediaLoads();
    }

    private void BuildLayout(DocumentReadingScene scene, double width, int hash)
    {
        _blocks.Clear();
        double y = 0;
        foreach (ReadingBlock block in scene.Blocks)
        {
            BlockLayout layout = BuildBlock(block, width);
            layout.Top = y + TopGap(block.Kind);
            y = layout.Top + layout.Height + BottomGap(block.Kind);
            _blocks.Add(layout);
        }

        _totalHeight = y;
        _blockTops = new double[_blocks.Count];
        for (int index = 0; index < _blocks.Count; index++)
        {
            _blockTops[index] = _blocks[index].Top;
        }

        _layoutWidth = width;
        _layoutHash = hash;
        _layoutScene = scene;
        _layoutValid = true;
    }

    private BlockLayout BuildBlock(ReadingBlock block, double width)
    {
        BlockLayout layout = new() { Source = block };
        double contentWidth = Math.Max(1, width);
        switch (block.Kind)
        {
            case "heading":
                layout.Text = BuildRawLayout(
                    block.Text, BodyBoldTypeface, HeadingFontSize(block.Level), _foreground, contentWidth);
                layout.Height = layout.Text.Height;
                break;
            case "code":
                layout.Text = BuildRawLayout(
                    block.Text, MonoTypeface, BodyFontSize, _foreground, contentWidth - 2 * CodePadding);
                layout.Height = layout.Text.Height + 2 * CodePadding;
                break;
            case "equation":
                layout.Text = BuildRawLayout(
                    block.Text, BodyTypeface, BodyFontSize, _foreground, contentWidth - 2 * EquationPadding,
                    TextAlignment.Center, FontStyle.Italic);
                layout.Height = layout.Text.Height + 2 * EquationPadding;
                break;
            case "table" when block.Table is { Rows.Count: > 0 } table:
                layout.Table = BuildTable(table, contentWidth);
                layout.Height = layout.Table.Height;
                break;
            case "table":
                layout.Text = BuildRawLayout(
                    block.Text, MonoTypeface, BodyFontSize, _foreground, contentWidth);
                layout.Height = layout.Text.Height;
                break;
            case "media":
                layout = BuildMedia(block, contentWidth);
                break;
            case "divider":
                layout.Height = 1;
                break;
            default:
                layout.Text = BuildTextLayout(block, contentWidth);
                layout.Height = layout.Text.Height;
                break;
        }

        return layout;
    }

    private BlockLayout BuildMedia(ReadingBlock block, double width)
    {
        BlockLayout layout = new() { Source = block };
        double innerWidth = width - 2 * MediaPadding;
        layout.MediaLabel = BuildRawLayout(
            block.MediaLabel ?? "媒体", BodyBoldTypeface, BodyFontSize, _foreground, innerWidth,
            TextAlignment.Center);
        layout.Text = string.IsNullOrWhiteSpace(block.Text)
            ? null
            : BuildRawLayout(block.Text, BodyTypeface, BodyFontSize, _muted, innerWidth, TextAlignment.Center);
        double captionHeight = layout.MediaLabel.Height + (layout.Text is null ? 0 : 4 + layout.Text.Height);
        if (ResolveMediaImage(block) is not { } image)
        {
            // No image (yet): the placeholder card keeps the block's height stable.
            layout.Height = 2 * MediaPadding + captionHeight;
            return layout;
        }

        Size imageSize = FitMediaImage(image.Size, innerWidth);
        layout.MediaImage = image;
        layout.MediaImageWidth = imageSize.Width;
        layout.MediaImageHeight = imageSize.Height;
        layout.Height = 2 * MediaPadding + imageSize.Height + MediaCaptionGap + captionHeight;
        return layout;
    }

    private IImage? ResolveMediaImage(ReadingBlock block)
    {
        return block.MediaAssetId is { Length: > 0 } assetId &&
               _mediaImages.TryGetValue(assetId, out IImage? image)
            ? image
            : null;
    }

    // Media images fill the block's content width; a tall image is capped by height instead and is
    // centred horizontally, leaving the card's gutters as whitespace.
    private static Size FitMediaImage(Size source, double maxWidth)
    {
        if (source.Width <= 0 || source.Height <= 0)
        {
            return new Size(Math.Max(1, maxWidth), MaxMediaImageHeight);
        }

        double scale = maxWidth / source.Width;
        double height = source.Height * scale;
        if (height > MaxMediaImageHeight)
        {
            scale = MaxMediaImageHeight / source.Height;
            height = MaxMediaImageHeight;
        }

        return new Size(Math.Max(1, source.Width * scale), Math.Max(1, height));
    }

    private TextLayout BuildTextLayout(ReadingBlock block, double width)
    {
        IBrush foreground = block.Kind == "quote" ? _muted : _foreground;
        if (block.Inlines is { Count: > 0 } inlines)
        {
            return BuildInlineLayout(block.Text, inlines, foreground, width);
        }

        return BuildRawLayout(block.Text, BodyTypeface, BodyFontSize, foreground, width);
    }

    private static TextLayout BuildRawLayout(
        string text,
        Typeface typeface,
        double fontSize,
        IBrush foreground,
        double maxWidth,
        TextAlignment alignment = TextAlignment.Left,
        FontStyle fontStyle = FontStyle.Normal,
        TextWrapping wrapping = TextWrapping.Wrap)
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
        string text, IReadOnlyList<MarkdownInlineModel> inlines, IBrush foreground, double maxWidth)
    {
        StringBuilder builder = new();
        List<ValueSpan<TextRunProperties>> spans = [];
        AppendInlines(inlines, InlineStyle.Default, foreground, builder, spans);
        if (spans.Count == 0)
        {
            return BuildRawLayout(text, BodyTypeface, BodyFontSize, foreground, maxWidth);
        }

        return new TextLayout(
            builder.ToString(),
            BodyTypeface,
            BodyFontSize,
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
        StringBuilder builder,
        List<ValueSpan<TextRunProperties>> spans)
    {
        foreach (MarkdownInlineModel inline in inlines)
        {
            switch (inline.Kind)
            {
                case "text":
                    AppendRun(inline.Text, style, foreground, builder, spans);
                    break;
                case "line_break":
                    AppendRun("\n", style, foreground, builder, spans);
                    break;
                case "strong":
                    AppendChildren(inline, style with { Bold = true }, foreground, builder, spans);
                    break;
                case "emphasis":
                    AppendChildren(inline, style with { Italic = true }, foreground, builder, spans);
                    break;
                case "strikethrough":
                    AppendChildren(inline, style with { Strikethrough = true }, foreground, builder, spans);
                    break;
                case "code":
                    AppendRun(inline.Text, style with { Code = true }, foreground, builder, spans);
                    break;
                case "link":
                    AppendChildren(inline, style with { Link = true }, foreground, builder, spans);
                    break;
                case "superscript":
                    AppendRun(Flatten(inline.Children), style, foreground, builder, spans);
                    break;
                default:
                    AppendChildren(inline, style, foreground, builder, spans);
                    break;
            }
        }
    }

    private void AppendChildren(
        MarkdownInlineModel inline,
        InlineStyle style,
        IBrush foreground,
        StringBuilder builder,
        List<ValueSpan<TextRunProperties>> spans)
    {
        if (inline.Children is { Count: > 0 } children)
        {
            AppendInlines(children, style, foreground, builder, spans);
        }
        else
        {
            AppendRun(inline.Text, style, foreground, builder, spans);
        }
    }

    private void AppendRun(
        string? text,
        InlineStyle style,
        IBrush foreground,
        StringBuilder builder,
        List<ValueSpan<TextRunProperties>> spans)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int start = builder.Length;
        builder.Append(text);
        spans.Add(new ValueSpan<TextRunProperties>(start, text.Length, CreateRunProperties(style, foreground)));
    }

    private TextRunProperties CreateRunProperties(InlineStyle style, IBrush foreground)
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
        return new GenericTextRunProperties(typeface, BodyFontSize, decorations, runForeground, background);
    }

    private static string Flatten(IReadOnlyList<MarkdownInlineModel>? inlines)
    {
        return inlines is null
            ? string.Empty
            : string.Concat(inlines.Select(inline => inline.Text + Flatten(inline.Children)));
    }

    private TableLayout BuildTable(ReadingTable table, double width)
    {
        int rows = table.Rows.Count;
        int columns = 1;
        foreach (IReadOnlyList<string> row in table.Rows)
        {
            columns = Math.Max(columns, row.Count);
        }

        double available = Math.Max(MinColumnWidth, width - 1);
        // Pass 1: each column's natural width from unhaped wrapping ("no wrap" measurement).
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
                    text, BodyTypeface, BodyFontSize, _foreground, double.PositiveInfinity,
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
                    header ? BodyBoldTypeface : BodyTypeface,
                    BodyFontSize,
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

    private static string CellText(IReadOnlyList<string> row, int column)
    {
        return column < row.Count ? row[column] ?? string.Empty : string.Empty;
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

    private void DrawSelection(DrawingContext context, BlockLayout layout, DocumentBoxId? selectedBoxId, double width)
    {
        if (selectedBoxId is not { } selected || layout.Source.BoxId != selected)
        {
            return;
        }

        if (!TryGetSelectionBrushes(out ImmutableSolidColorBrush fill, out ImmutableSolidColorBrush bar))
        {
            return;
        }

        double top = Math.Max(0, layout.Top - 3);
        double bottom = layout.Top + layout.Height + 3;
        context.FillRectangle(fill, new Rect(0, top, width, bottom - top));
        context.FillRectangle(bar, new Rect(0, top, 3, bottom - top));
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

    private void DrawBlock(DrawingContext context, BlockLayout layout, double width)
    {
        switch (layout.Source.Kind)
        {
            case "heading":
                layout.Text?.Draw(context, new Point(0, layout.Top));
                break;
            case "code":
                context.FillRectangle(_surfaceHigh, new Rect(0, layout.Top, width, layout.Height), 4);
                layout.Text?.Draw(context, new Point(CodePadding, layout.Top + CodePadding));
                break;
            case "equation":
                context.FillRectangle(_surfaceHigh, new Rect(0, layout.Top, width, layout.Height), 4);
                layout.Text?.Draw(context, new Point(EquationPadding, layout.Top + EquationPadding));
                break;
            case "table" when layout.Table is { } table:
                DrawTable(context, layout, table);
                break;
            case "media":
                DrawMedia(context, layout, width);
                break;
            case "divider":
                double y = layout.Top + layout.Height / 2;
                context.DrawLine(_dividerPen, new Point(0, y), new Point(width, y));
                break;
            default:
                layout.Text?.Draw(context, new Point(0, layout.Top));
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
                context.FillRectangle(_surfaceHigh, new Rect(0, y, table.Width, table.RowHeights[row]));
            }

            double x = 0;
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
            context.DrawLine(_gridPen, new Point(0, lineY), new Point(table.Width, lineY));
            if (row < table.Rows)
            {
                lineY += table.RowHeights[row];
            }
        }

        double lineX = 0;
        for (int column = 0; column <= table.Columns; column++)
        {
            context.DrawLine(_gridPen, new Point(lineX, layout.Top), new Point(lineX, layout.Top + table.Height));
            if (column < table.Columns)
            {
                lineX += table.ColumnWidths[column];
            }
        }
    }

    private void DrawMedia(DrawingContext context, BlockLayout layout, double width)
    {
        if (layout.MediaImage is { } image)
        {
            double x = (width - layout.MediaImageWidth) / 2;
            context.DrawImage(
                image,
                new Rect(x, layout.Top + MediaPadding, layout.MediaImageWidth, layout.MediaImageHeight));
            DrawMediaCaption(context, layout, layout.Top + MediaPadding + layout.MediaImageHeight +
                                              MediaCaptionGap);
            return;
        }

        Rect card = new(0, layout.Top, width, layout.Height);
        context.FillRectangle(_surfaceHigh, card, 6);
        context.DrawRectangle(_gridPen, card, 6);
        DrawMediaCaption(context, layout, layout.Top + MediaPadding);
    }

    private void DrawMediaCaption(DrawingContext context, BlockLayout layout, double top)
    {
        double y = top;
        if (layout.MediaLabel is { } label)
        {
            label.Draw(context, new Point(MediaPadding, y));
            y += label.Height + 4;
        }

        layout.Text?.Draw(context, new Point(MediaPadding, y));
    }

    // Loads one media asset exactly once, then hands the result back to the UI thread. A faulted
    // load is treated like a missing image: the placeholder card stays and no retry is scheduled.
    private void EnsureMediaLoads()
    {
        IMediaImageLoader? loader = MediaLoader;
        if (loader is null || _mediaLoadCancellation is not { } cancellation ||
            cancellation.IsCancellationRequested)
        {
            return;
        }

        foreach (BlockLayout layout in _blocks)
        {
            if (layout.Source.MediaAssetId is not { Length: > 0 } assetId ||
                _mediaImages.ContainsKey(assetId) ||
                _mediaInFlight.Contains(assetId) ||
                _mediaFailed.Contains(assetId))
            {
                continue;
            }

            _mediaInFlight.Add(assetId);
            LoadMediaImageAsync(assetId, loader, cancellation.Token)
                .Observe("document-reading-media", assetId);
        }
    }

    private async Task LoadMediaImageAsync(
        string assetId, IMediaImageLoader loader, CancellationToken cancellationToken)
    {
        IImage? image = null;
        try
        {
            image = await loader.LoadAsync(assetId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The loader may complete on a pool thread, so the cache is only touched on the UI thread.
            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyMediaImage(assetId, image);
            }
            else
            {
                Dispatcher.UIThread.Post(() => ApplyMediaImage(assetId, image));
            }
        }
    }

    private void ApplyMediaImage(string assetId, IImage? image)
    {
        if (!_mediaInFlight.Remove(assetId))
        {
            // The load was cancelled (the control detached) after completing: drop the late result.
            (image as IDisposable)?.Dispose();
            return;
        }

        if (image is null)
        {
            _mediaFailed.Add(assetId);
            return;
        }

        _mediaImages[assetId] = image;
        _layoutValid = false;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void CancelMediaLoads()
    {
        CancellationTokenSource? cancellation = _mediaLoadCancellation;
        _mediaLoadCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        // Cancelled assets drop out of the in-flight set so a later attach retries them.
        _mediaInFlight.Clear();
    }

    private BlockLayout? FindBlockAt(double y)
    {
        if (_blocks.Count == 0)
        {
            return null;
        }

        int low = 0;
        int high = _blocks.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (_blockTops[mid] <= y + 4)
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

        BlockLayout layout = _blocks[found];
        return y <= layout.Top + layout.Height + 4 ? layout : null;
    }

    private static double HeadingFontSize(int level)
    {
        return level switch
        {
            <= 1 => 20,
            2 => 18,
            3 => 16,
            4 => 15,
            5 => 14,
            _ => 13
        };
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

    private static int ComputeSceneHash(DocumentReadingScene scene)
    {
        HashCode hash = new();
        hash.Add(scene.Blocks.Count);
        foreach (ReadingBlock block in scene.Blocks)
        {
            hash.Add(block.BoxId);
            hash.Add(block.Kind, StringComparer.Ordinal);
            hash.Add(block.Level);
            hash.Add(block.Text, StringComparer.Ordinal);
            hash.Add(block.MediaLabel, StringComparer.Ordinal);
            hash.Add(block.MediaAssetId, StringComparer.Ordinal);
            hash.Add(block.CodeLanguage, StringComparer.Ordinal);
            if (block.Table is { } table)
            {
                hash.Add(table.HasHeader);
                foreach (IReadOnlyList<string> row in table.Rows)
                {
                    hash.Add(row.Count);
                    foreach (string cell in row)
                    {
                        hash.Add(cell, StringComparer.Ordinal);
                    }
                }
            }

            if (block.Inlines is { } inlines)
            {
                AddInlinesHash(ref hash, inlines);
            }
        }

        return hash.ToHashCode();
    }

    private static void AddInlinesHash(ref HashCode hash, IReadOnlyList<MarkdownInlineModel> inlines)
    {
        hash.Add(inlines.Count);
        foreach (MarkdownInlineModel inline in inlines)
        {
            hash.Add(inline.Kind, StringComparer.Ordinal);
            hash.Add(inline.Text, StringComparer.Ordinal);
            if (inline.Children is { } children)
            {
                AddInlinesHash(ref hash, children);
            }
        }
    }

    private sealed class BlockLayout
    {
        public ReadingBlock Source = null!;
        public double Top;
        public double Height;
        public TextLayout? Text;
        public TextLayout? MediaLabel;
        public TableLayout? Table;
        public IImage? MediaImage;
        public double MediaImageWidth;
        public double MediaImageHeight;
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
