using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>Read-only, text-only service surface for future MCP protocol adapters.</summary>
public interface IMcpReadApi
{
    Task<Result<McpSearchLibraryResponse>> SearchLibraryAsync(McpSearchLibraryRequest request, CancellationToken cancellationToken = default);
    Task<Result<McpItemMetadataResponse>> GetItemMetadataAsync(ItemId itemId, CancellationToken cancellationToken = default);
    Task<Result<McpDocumentStatusResponse>> GetDocumentStatusAsync(DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default);
    Task<Result<McpPageTextResponse>> GetPageTextAsync(McpPageTextRequest request, CancellationToken cancellationToken = default);
    Task<Result<McpPageBlocksResponse>> GetPageBlocksAsync(McpPageBlocksRequest request, CancellationToken cancellationToken = default);
    Task<Result<McpSearchContextResponse>> GetSearchResultContextAsync(McpSearchContextRequest request, CancellationToken cancellationToken = default);
}
