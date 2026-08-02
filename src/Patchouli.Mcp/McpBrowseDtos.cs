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

public sealed record McpBrowseEvidenceRow(
    string EvidenceRefId,
    string? SourceTitle,
    string? PageLabel,
    int PageIndex,
    string Status,
    string? PinnedText,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    DateTimeOffset CreatedAt,
    ItemId? ItemId = null);

public sealed record McpDocumentPageRef(PageId PageId, string? PageLabel, int PageIndex, string Uri);

public sealed record McpDocumentOutlineResponse(
    DocumentInstanceId DocumentInstanceId,
    string? Title,
    string? Revision,
    IReadOnlyList<McpDocumentPageRef> Pages,
    ItemId? ItemId = null);
