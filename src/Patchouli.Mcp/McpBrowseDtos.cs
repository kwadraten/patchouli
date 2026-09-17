using Patchouli.Core.Ids;

namespace Patchouli.Mcp;

public sealed record McpBrowseItemRow(
    ItemId ItemId,
    string Title,
    string ItemType,
    string? Status,
    string? CitationKey,
    DateTimeOffset UpdatedAt,
    string PrimaryDocumentOcrIndexStatus = "no_primary_document");

public sealed record McpBrowseItemPage(
    IReadOnlyList<McpBrowseItemRow> Rows,
    bool HasMore,
    int DomainTotal = 0,
    int FilteredTotal = 0);

public sealed record McpBrowseDocumentRow(
    DocumentInstanceId DocumentInstanceId,
    string? Title,
    string? Revision,
    DateTimeOffset CreatedAt,
    ItemId? ItemId = null,
    string? ItemStatus = null,
    string DocumentStatus = "missing_source",
    string SourceStatus = "unavailable",
    string OcrIndexStatus = "no_ocr",
    bool Citable = false);

public sealed record McpTextResourceProjection(
    DocumentInstanceId DocumentInstanceId,
    ItemId? ItemId,
    string? ItemStatus,
    string DocumentStatus,
    string SourceStatus,
    string OcrIndexStatus,
    bool Citable);

public sealed record McpBrowseDocumentPage(
    IReadOnlyList<McpBrowseDocumentRow> Rows,
    bool HasMore,
    int DomainTotal = 0,
    int FilteredTotal = 0);

public sealed record McpBrowseStyleRow(
    string StyleId,
    string DisplayName,
    string ContentHash,
    string? Locale,
    bool Enabled);

public sealed record McpBrowseStylePage(
    IReadOnlyList<McpBrowseStyleRow> Rows,
    bool HasMore,
    int DomainTotal = 0,
    int FilteredTotal = 0);

public sealed record McpDocumentPageRef(PageId PageId, string? PageLabel, int PageIndex, string Uri);

public sealed record McpDocumentOutlineResponse(
    DocumentInstanceId DocumentInstanceId,
    string? Title,
    string? Revision,
    IReadOnlyList<McpDocumentPageRef> Pages,
    ItemId? ItemId = null);

/// <summary>
/// Progress projection for one DocumentInstance in the <c>patchouli://translations/</c>
/// directory. Counts are evaluated against each page's current committed tree revision, so a
/// page counts as stale whenever its stored translation rows predate that revision.
/// </summary>
public sealed record McpTranslationDocumentRow(
    DocumentInstanceId DocumentInstanceId,
    string? Title,
    ItemId? ItemId,
    int PageCount,
    int TranslatedPageCount,
    int PartialPageCount,
    int UntranslatedPageCount,
    int StalePageCount);

public sealed record McpBrowseTranslationPage(
    IReadOnlyList<McpTranslationDocumentRow> Rows,
    bool HasMore,
    int DomainTotal = 0,
    int FilteredTotal = 0);

/// <summary>
/// One page inside a translation document directory. <see cref="Status"/> is one of
/// <see cref="McpTranslationStatus"/>; counts describe the current committed revision.
/// </summary>
public sealed record McpTranslationPageRow(
    PageId PageId,
    string? PageLabel,
    int PageIndex,
    int TranslatedBoxCount,
    int TotalBoxCount,
    string Status,
    string Uri);

public sealed record McpTranslationOutlineResponse(
    DocumentInstanceId DocumentInstanceId,
    string? Title,
    IReadOnlyList<McpTranslationPageRow> Pages,
    ItemId? ItemId = null);

/// <summary>Stable per-page translation status names shared by browse and fetch.</summary>
public static class McpTranslationStatus
{
    public const string Untranslated = "untranslated";
    public const string Partial = "partial";
    public const string Translated = "translated";
    public const string Stale = "stale";
}
