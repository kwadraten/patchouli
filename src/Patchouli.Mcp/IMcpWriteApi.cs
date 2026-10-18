using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public sealed record McpBoxReplacement(string BoxId, string Content);

public sealed record McpPutRequest(
    string Uri,
    string? Content = null,
    bool PreserveTags = false,
    string? SourceRevision = null,
    int? TranslationVersion = null,
    IReadOnlyList<McpBoxReplacement>? Boxes = null);

public sealed record McpPutResponse(
    string Uri,
    string ResourceType,
    bool Committed,
    int ContentBytes,
    IReadOnlyList<string>? Warnings = null,
    string? SourceRevision = null,
    int? TranslationVersion = null,
    IReadOnlyList<string>? ModifiedBoxIds = null);

/// <summary>
/// The single host write service. put validates and atomically replaces a resource or a
/// same-page batch of box payloads. XML writes require source and translation versions. Resource
/// writes publish their authoritative <c>LibraryChangeSet</c> through the underlying host write
/// service; this transport API intentionally exposes no competing UI notification stream.
/// </summary>
public interface IMcpWriteApi
{
    Task<Result<McpPutResponse>> PutPageXmlAsync(McpPutRequest request, DocumentInstanceId documentId,
        PageId pageId, int pageIndex, bool translation, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
            "XML writes unavailable."));
    }

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

    /// <summary>
    /// Parses, validates, and atomically replaces a document's chapter/pagination map from
    /// <c>map.yml</c> YAML. An empty definition clears the map. The write advances the
    /// Library revision and publishes the change after the transaction commits.
    /// </summary>
    Task<Result<McpPutResponse>> PutDocumentMapAsync(
        string uri,
        DocumentInstanceId documentInstanceId,
        string content,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<McpPutResponse>.Failure(AppErrorCodes.UnsupportedOperation,
            "Page-map writes unavailable."));
    }
}
