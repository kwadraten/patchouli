using Patchouli.Core.Ids;

namespace Patchouli.Mcp;

public sealed record McpBrowseItemRow(
    ItemId ItemId,
    string Title,
    string ItemType,
    string? Status,
    string? CitationKey,
    DateTimeOffset UpdatedAt);

public sealed record McpBrowseItemPage(
    IReadOnlyList<McpBrowseItemRow> Rows,
    string? NextCursor);

public sealed record McpBrowseDocumentRow(
    DocumentInstanceId DocumentInstanceId,
    string? Title,
    string? Revision,
    DateTimeOffset CreatedAt,
    ItemId? ItemId = null);

public sealed record McpBrowseDocumentPage(
    IReadOnlyList<McpBrowseDocumentRow> Rows,
    string? NextCursor);

public sealed record McpBrowseStyleRow(
    string StyleId,
    string DisplayName,
    string ContentHash,
    string? Locale,
    bool Enabled);

public sealed record McpBrowseStylePage(
    IReadOnlyList<McpBrowseStyleRow> Rows,
    string? NextCursor);

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

public sealed record McpBrowseEvidencePage(
    IReadOnlyList<McpBrowseEvidenceRow> Rows,
    string? NextCursor);

public sealed record McpDocumentPageRef(PageId PageId, string? PageLabel, int PageIndex, string Uri);

public sealed record McpDocumentOutlineResponse(
    DocumentInstanceId DocumentInstanceId,
    string? Title,
    string? Revision,
    IReadOnlyList<McpDocumentPageRef> Pages,
    ItemId? ItemId = null);
