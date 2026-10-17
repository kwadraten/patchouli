using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Pagination;

/// <summary>
/// Owns the authoritative structured page map of each document instance: chapter structure
/// and printed page-number rules. Writes replace the whole map atomically; an empty map
/// clears it. The MCP <c>map.yml</c> resource is a virtual-file projection of the stored
/// definition.
/// </summary>
public interface IDocumentPageMapService
{
    /// <summary>
    /// Returns the resolved map, or null when the document has no stored map. Resolution
    /// never throws the whole map away: rules needing repair pause computation locally and
    /// keep their original definition.
    /// </summary>
    Task<Result<ResolvedDocumentPageMap?>> GetResolvedAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the <c>map.yml</c> projection YAML: the stored map, or the discoverable
    /// empty-map projection when the document has no map.
    /// </summary>
    Task<Result<string>> GetMapYamlAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses, validates, and atomically replaces the document's page map from
    /// <c>map.yml</c> YAML. An empty definition clears the map. On success the Library
    /// revision advances and the change is published after the transaction commits.
    /// </summary>
    Task<Result<DocumentPageMap>> ReplaceFromYamlAsync(
        DocumentInstanceId documentInstanceId,
        string yaml,
        CancellationToken cancellationToken = default);
}
