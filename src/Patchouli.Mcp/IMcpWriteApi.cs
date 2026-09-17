using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public sealed record McpPutRequest(string Uri, string Content, bool PreserveTags = false);

public sealed record McpPutResponse(
    string Uri,
    string ResourceType,
    bool Committed,
    int ContentBytes,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// The single host write service. put replaces exactly one whole writable resource after
/// full-content validation and an atomic commit; it has no base-revision precondition. Resource
/// writes publish their authoritative <c>LibraryChangeSet</c> through the underlying host write
/// service; this transport API intentionally exposes no competing UI notification stream.
/// </summary>
public interface IMcpWriteApi
{
    Task<Result<McpPutResponse>> PutAsync(
        McpPutRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the full derived translation of one page. The submitted markdown must be
    /// structurally identical to the page's current source markdown; on mismatch the failure
    /// carries <see cref="TranslationStructureFailureDetails"/> describing every block mismatch.
    /// Replaces the page's rows atomically and has no base-revision precondition.
    /// </summary>
    Task<Result<McpPutResponse>> PutPageTranslationAsync(
        string uri,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string content,
        CancellationToken cancellationToken = default);
}
