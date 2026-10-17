using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>Read-only, text-only service surface for the structured v3 MCP protocol adapters.</summary>
public interface IMcpReadApi
{
    Task<Result<string>> GetItemYamlAsync(ItemId itemId, bool includeTags,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<string>.Failure(AppErrorCodes.UnsupportedOperation,
            "YAML projection unavailable."));
    }

    Task<Result<McpPageXmlResponse>> GetPageXmlAsync(McpPageXmlRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<McpPageXmlResponse>.Failure(AppErrorCodes.UnsupportedOperation,
            "XML projection unavailable."));
    }

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

    /// <summary>
    /// Browses the derived <c>patchouli://translations/</c> directory. Each row carries the
    /// translation progress of one document against its pages' current committed revisions.
    /// When <paramref name="query"/> is present it filters document titles case-insensitively.
    /// </summary>
    Task<Result<McpBrowseTranslationPage>> BrowseTranslationsAsync(int skip, int limit, string? query,
        IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the page list of one document with each page's translation status. The page order
    /// and labels match <see cref="GetDocumentOutlineAsync"/>; <paramref name="query"/> filters
    /// page labels case-insensitively.
    /// </summary>
    Task<Result<McpTranslationOutlineResponse>> GetTranslationOutlineAsync(
        DocumentInstanceId documentInstanceId, string? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the compiled whole-page translation. A page without any translation fails with
    /// <see cref="AppErrorCodes.NotFound"/> so callers can point the agent at the source page.
    /// </summary>
    Task<Result<McpPageTranslationResponse>> GetPageTranslationAsync(McpPageTranslationRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<McpDocumentOutlineResponse>> GetDocumentOutlineAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <c>map.yml</c> YAML projection of a document's chapter/pagination map.
    /// Documents without a stored map get the discoverable, first-put-able empty-map
    /// projection. Plain fetch triggers no OCR or AI work.
    /// </summary>
    Task<Result<string>> GetDocumentMapYamlAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<string>.Failure(AppErrorCodes.UnsupportedOperation,
            "Page-map projection unavailable."));
    }

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

    /// <summary>
    /// Builds the patchouli://library.yml projection. Tags and Collections are included only
    /// when the caller's device-local exposure settings enable them; the fixed library_id and
    /// display_name are always present.
    /// </summary>
    Task<Result<McpLibraryProjection>> GetLibraryProjectionAsync(bool includeTags, bool includeCollections,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the Collection ids that contain one Item, ordered by Collection name. Used by
    /// item-URI singleton scopes so the <c>collection_id</c> filter matches the item scope
    /// semantics without reading the cleared legacy <c>collections_json</c> mirror.
    /// </summary>
    Task<Result<IReadOnlyList<CollectionId>>> GetItemCollectionIdsAsync(ItemId itemId,
        CancellationToken cancellationToken = default);
}
