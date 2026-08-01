using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>Read-only, text-only service surface for future MCP protocol adapters.</summary>
public interface IMcpReadApi
{
    Task<Result<McpSearchLibraryResponse>> SearchLibraryAsync(McpSearchLibraryRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<McpItemMetadataResponse>> GetItemMetadataAsync(ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result<McpDocumentStatusResponse>> GetDocumentStatusAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<McpPageTextResponse>> GetPageTextAsync(McpPageTextRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<McpPageBlocksResponse>> GetPageBlocksAsync(McpPageBlocksRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<McpSearchContextResponse>> GetSearchResultContextAsync(McpSearchContextRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<McpCslStyleSummary>>> ListCslStylesAsync(CancellationToken cancellationToken = default);
    Task<Result<McpCslStyleResponse>> GetCslStyleAsync(string styleId, CancellationToken cancellationToken = default);

    Task<Result<McpRenderBibliographyResponse>> RenderItemBibliographyAsync(ItemId itemId, string? styleId = null,
        string? locale = null, CancellationToken cancellationToken = default);

    Task<Result<McpRenderBibliographyResponse>> RenderItemsBibliographyAsync(McpRenderBibliographyRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<McpBrowseItemPage>> BrowseItemsAsync(string? cursor, int limit, string? itemType = null,
        string? status = null, CancellationToken cancellationToken = default);

    Task<Result<McpBrowseDocumentPage>> BrowseDocumentsAsync(string? cursor, int limit,
        CancellationToken cancellationToken = default);

    Task<Result<McpBrowseStylePage>> BrowseStylesAsync(string? cursor, int limit,
        CancellationToken cancellationToken = default);

    Task<Result<McpBrowseEvidencePage>> BrowseEvidenceAsync(string? cursor, int limit,
        CancellationToken cancellationToken = default);

    Task<Result<McpDocumentOutlineResponse>> GetDocumentOutlineAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<McpBrowseEvidenceRow>> GetEvidenceRecordAsync(string evidenceRefId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the item that owns a document instance. Used by the cite command to
    /// support evidence references: an evidence ref points at a page in a document
    /// instance, and the document instance belongs to an item.
    /// </summary>
    Task<Result<ItemId>> GetItemIdForDocumentAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);
}
