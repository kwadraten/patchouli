using Avalonia;
using Avalonia.Media;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Reading;

/// <summary>Which side of a translation compare a block (or a text selection) belongs to.
/// Selection never crosses columns: one <see cref="ReadingSelection"/> lives in exactly one
/// column.</summary>
public enum ReadingColumnRole
{
    Source,
    Translation
}

/// <summary>The two translation compare layouts. Side-by-side aligns each paired source and
/// translation block on a shared top with the row height being the taller of the two; stacked
/// renders the translation underneath its source inside the same paired block. Single-column
/// reading is the absence of a translation scene, not a third mode.</summary>
public enum ReadingCompareMode
{
    SideBySide,
    Stacked
}

/// <summary>One row of a parsed GFM pipe table. Cells are raw inline-markdown text.</summary>
public sealed record ReadingTable(
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool HasHeader);

/// <summary>A media block's image region: the normalized bbox region of the owning page that the
/// block displays, drawn as a source sub-rect of the page's rendered pixel buffer. The image is
/// identified by <see cref="ImageKey"/> (opaque to the renderer; the host resolves it to a page
/// render), so no cropped image file is ever produced.</summary>
public sealed record ReadingImageRegion(string ImageKey, NormalizedBBox Region);

/// <summary>
/// A render-ready reading block: the unit <see cref="ReadingView"/> lays out and draws top to
/// bottom. Kind values: "heading", "paragraph", "list", "quote", "code", "equation", "table",
/// "media", "divider", "untranslated", or any kind passed through from the compiled markdown
/// model. Text carries the plain payload (paragraph text, code, latex, media description);
/// Inlines carries rich inline markup when the block kind supports it; Table carries the parsed
/// grid for Kind == "table"; Image carries the page-region source rect for Kind == "media".
/// </summary>
/// <param name="BoxId">Source box linkage, or null for content that maps to no box.</param>
/// <param name="PageIndex">Zero-based page the block was streamed from, or -1 when the block is
/// not page-scoped.</param>
public sealed record ReadingBlock(
    DocumentBoxId? BoxId,
    string Kind,
    int Level,
    string Text,
    IReadOnlyList<MarkdownInlineModel>? Inlines = null,
    ReadingTable? Table = null,
    string? MediaLabel = null,
    string? CodeLanguage = null,
    ReadingImageRegion? Image = null,
    int PageIndex = -1)
{
    /// <summary>Kind of the muted placeholder shown where a translation block is missing. It is
    /// never a source-text fallback: an untranslated block reads as 未翻译, not as its source.</summary>
    public const string UntranslatedKind = "untranslated";

    public const string UntranslatedText = "未翻译";

    /// <summary>Placeholder for a source block that has no translation block in a compare row.</summary>
    public static ReadingBlock Untranslated(DocumentBoxId? boxId = null, int pageIndex = -1)
    {
        return new ReadingBlock(boxId, UntranslatedKind, 0, UntranslatedText, PageIndex: pageIndex);
    }

    /// <summary>Placeholder for a compare row whose source side is missing entirely.</summary>
    public static ReadingBlock Empty(int pageIndex = -1)
    {
        return new ReadingBlock(null, "paragraph", 0, string.Empty, PageIndex: pageIndex);
    }
}

/// <summary>Immutable snapshot of one column's readable content, in reading order.</summary>
public sealed record ReadingScene(IReadOnlyList<ReadingBlock> Blocks)
{
    public static ReadingScene Empty { get; } = new([]);
}

/// <summary>An in-memory image source for media blocks. <see cref="LoadImageAsync"/> returns the
/// full rendered page bitmap for the opaque key; <see cref="ReadingView"/> draws only the block's
/// normalized region sub-rect. Returning null (or faulting) means "no preview available": the
/// view keeps the block's placeholder card and does not retry while the source stays set.</summary>
public interface IReadingImageSource
{
    Task<IImage?> LoadImageAsync(string imageKey, CancellationToken cancellationToken);

    // The source owns images it returns. The view calls this after removing an image from its
    // visible cache; a source sharing a workspace bitmap may leave the default implementation.
    void ReleaseImage(string imageKey, IImage image)
    {
    }
}

/// <summary>A position inside a reading column: a block index within the column's scene plus a
/// character offset into that block's displayed text. Offsets in non-text blocks (tables, media,
/// dividers) span the block's plain text.</summary>
public readonly record struct ReadingTextPosition(int BlockIndex, int CharacterIndex)
{
    public static ReadingTextPosition Min(ReadingTextPosition first, ReadingTextPosition second)
    {
        return Compare(first, second) <= 0 ? first : second;
    }

    public static ReadingTextPosition Max(ReadingTextPosition first, ReadingTextPosition second)
    {
        return Compare(first, second) >= 0 ? first : second;
    }

    public static int Compare(ReadingTextPosition first, ReadingTextPosition second)
    {
        int byBlock = first.BlockIndex.CompareTo(second.BlockIndex);
        return byBlock != 0 ? byBlock : first.CharacterIndex.CompareTo(second.CharacterIndex);
    }
}

/// <summary>A text selection inside exactly one column. <see cref="Start"/> and <see cref="End"/>
/// are the normalized (reading-order) bounds; anchor and focus remember the drag direction.</summary>
public sealed record ReadingSelection(
    ReadingColumnRole Column,
    ReadingTextPosition Anchor,
    ReadingTextPosition Focus)
{
    public ReadingTextPosition Start => ReadingTextPosition.Min(Anchor, Focus);

    public ReadingTextPosition End => ReadingTextPosition.Max(Anchor, Focus);

    public bool IsEmpty => Compare(Start, End) == 0;

    private static int Compare(ReadingTextPosition first, ReadingTextPosition second)
    {
        return ReadingTextPosition.Compare(first, second);
    }
}

/// <summary>One measured block rectangle, reported by <see cref="ReadingView.GetMeasuredBlocks"/>
/// after layout. Bounds are in the view's coordinate space.</summary>
public sealed record ReadingMeasuredBlock(
    ReadingColumnRole Column,
    int BlockIndex,
    Rect Bounds,
    string Kind,
    DocumentBoxId? BoxId,
    int PageIndex = -1);

/// <summary>Raised when a block is pressed: the column it lives in plus its source box linkage
/// (null on empty space or unlinked content).</summary>
public sealed record ReadingBlockActivation(ReadingColumnRole Column, DocumentBoxId? BoxId);

/// <summary>Raised by the source column's 编辑选中文本 command with the selected boxes in reading
/// order (the host opens the first one), the selected text, and the zero-based page of the first
/// selected block (-1 when unknown) so the host can navigate to that page before editing.</summary>
public sealed record ReadingEditSelectionRequest(
    IReadOnlyList<DocumentBoxId> BoxIds,
    string SelectedText,
    int PageIndex = -1);
