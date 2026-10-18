using Patchouli.Core.Ids;

namespace Patchouli.Core.Pagination;

/// <summary>
/// A navigation position inside a document: a 1-based physical PDF page plus an optional
/// logical-page Box ID. The logical page is always addressed by Box ID, never by a serial
/// number, so positions stay stable when surrounding content changes.
/// </summary>
public sealed record NavigationPosition(int PdfPage, DocumentBoxId? LogicalPage = null);

/// <summary>
/// Pagination rule for physical pages: every PDF page in [StartPdfPage, EndPdfPage]
/// (inclusive on both ends) maps to <c>PrintedStart + pdfPage - StartPdfPage</c>.
/// </summary>
public sealed record PhysicalPaginationRule(int StartPdfPage, int EndPdfPage, int PrintedStart);

/// <summary>
/// One logical page inside a logical pagination rule. The list order is the caller-supplied
/// reading order; the resolver never sorts by Box ID or geometry.
/// </summary>
public sealed record LogicalPageReadingEntry(DocumentBoxId BoxId, int PdfPage);

/// <summary>
/// Pagination rule for logical pages: logical pages are enumerated in physical-then-reading
/// order within [StartPdfPage, EndPdfPage], each getting <c>PrintedStart + index</c> where
/// index is the zero-based position in <see cref="ReadingOrder"/>.
/// </summary>
public sealed record LogicalPaginationRule(
    int StartPdfPage,
    int EndPdfPage,
    int PrintedStart,
    IReadOnlyList<LogicalPageReadingEntry> ReadingOrder);

/// <summary>
/// The target of an explicit page label: either a 1-based physical PDF page or a logical-page
/// Box ID. Exactly one of the two is set.
/// </summary>
public sealed record PageLabelTarget(int? PdfPage, DocumentBoxId? LogicalPage);

/// <summary>
/// An explicit string label that overrides the computed value for the same target.
/// </summary>
public sealed record PageLabelOverride(PageLabelTarget Target, string Label);

/// <summary>
/// One chapter node. The target stores a <see cref="NavigationPosition"/> only; the printed
/// number shown to the reader is resolved at read time (unknown numbers degrade to the
/// physical page).
/// </summary>
public sealed record ChapterNode(
    string ChapterId,
    string Title,
    NavigationPosition Target,
    IReadOnlyList<ChapterNode> Children)
{
    public static ChapterNode Leaf(string chapterId, string title, NavigationPosition target)
    {
        return new ChapterNode(chapterId, title, target, []);
    }
}

/// <summary>
/// The authoritative structured page map of one document instance. The database holds this
/// model; the MCP <c>map.yml</c> resource is a virtual-file projection of it. Full per-page
/// maps are never persisted.
/// </summary>
public sealed record DocumentPageMap(
    DocumentInstanceId DocumentInstanceId,
    IReadOnlyList<PhysicalPaginationRule> PhysicalRules,
    IReadOnlyList<LogicalPaginationRule> LogicalRules,
    IReadOnlyList<PageLabelOverride> PageLabels,
    IReadOnlyList<ChapterNode> Chapters)
{
    public static DocumentPageMap Empty(DocumentInstanceId documentInstanceId)
    {
        return new DocumentPageMap(documentInstanceId, [], [], [], []);
    }

    public bool IsEmpty =>
        PhysicalRules.Count == 0 && LogicalRules.Count == 0 &&
        PageLabels.Count == 0 && Chapters.Count == 0;
}

/// <summary>A machine-readable diagnostic attached to a resolution or health check.</summary>
public sealed record MapDiagnostic(string Code, string Message);

/// <summary>Per-rule invalidation state for a logical pagination rule.</summary>
public sealed record LogicalRuleHealth(
    int RuleIndex,
    bool NeedsRepair,
    IReadOnlyList<MapDiagnostic> Diagnostics)
{
    public static LogicalRuleHealth Healthy(int ruleIndex)
    {
        return new LogicalRuleHealth(ruleIndex, false, []);
    }
}

/// <summary>A chapter with its printed number resolved at read time.</summary>
public sealed record ResolvedChapterNode(
    string ChapterId,
    string Title,
    NavigationPosition Target,
    int? PrintedNumber,
    string? DisplayLabel,
    IReadOnlyList<MapDiagnostic> Diagnostics,
    IReadOnlyList<ResolvedChapterNode> Children);

/// <summary>
/// The read-time view of a document's page map: the stored definition plus the current
/// invalidation health and resolved chapters. <see cref="HasMap"/> is false when the
/// document has no stored map.
/// </summary>
public sealed record ResolvedDocumentPageMap(
    DocumentInstanceId DocumentInstanceId,
    bool HasMap,
    DocumentPageMap Definition,
    IReadOnlyList<LogicalRuleHealth> RuleHealth,
    IReadOnlyList<ResolvedChapterNode> Chapters,
    IReadOnlyList<MapDiagnostic> Diagnostics,
    DateTimeOffset? UpdatedAt);
