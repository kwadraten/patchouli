using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>Read-only, text-only service surface for the structured v3 MCP protocol adapters.</summary>
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

    Task<Result<McpBrowseItemPage>> BrowseItemsAsync(int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default);

    Task<Result<McpBrowseItemPage>> SearchItemsAsync(string query, bool literal, int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default);

    Task<Result<McpBrowseDocumentPage>> BrowseDocumentsAsync(int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default);

    /// <summary>Returns one database-side long projection for each requested text document.</summary>
    Task<Result<IReadOnlyList<McpTextResourceProjection>>> GetTextResourceProjectionsAsync(
        IReadOnlyList<DocumentInstanceId> documentInstanceIds, IReadOnlyList<McpWhereClause>? where = null,
        CancellationToken cancellationToken = default);

    /// <summary>Computes the current primary-document OCR indexing capability for one Item.</summary>
    Task<Result<string>> GetPrimaryDocumentOcrIndexStatusAsync(ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result<McpBrowseStylePage>> BrowseStylesAsync(int skip, int limit,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default);

    Task<Result<McpDocumentOutlineResponse>> GetDocumentOutlineAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the item that owns a document instance. Used by the cite command to support
    /// document, page, and evidence references.
    /// </summary>
    Task<Result<ItemId>> GetItemIdForDocumentAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the persistent identity and current protocol revision of the host's Library.
    /// </summary>
    Task<Result<McpLibraryStateResponse>> GetCurrentLibraryStateAsync(
        CancellationToken cancellationToken = default);
}
