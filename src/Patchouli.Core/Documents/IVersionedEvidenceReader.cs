using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

/// <summary>
/// Reads evidence text from a versioned page URI.
/// The URI shape is <c>patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree-revision-id}&amp;box={box-id}</c>.
/// A URI with <c>rev</c> reads the immutable revision it names; a URI without <c>rev</c>
/// reads the current HEAD revision. Only committed revisions are externally referenceable.
/// </summary>
public interface IVersionedEvidenceReader
{
    /// <summary>
    /// Returns the markdown/plain text of a single box, or of the whole page when
    /// <paramref name="boxId"/> is null, from the specified committed revision (or HEAD
    /// when <paramref name="revisionId"/> is null). Validates that the document instance
    /// and page belong to the current library.
    /// </summary>
    Task<Result<EvidencePageText>> GetBoxTextAsync(
        DocumentInstanceId documentInstanceId,
        int pageIndex1Based,
        DocumentTreeRevisionId? revisionId = null,
        DocumentBoxId? boxId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Text resolved from a versioned evidence URI, together with the minimal metadata
/// required to cite it.
/// </summary>
public sealed record EvidencePageText(
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    int PageIndex1Based,
    string? PageLabel,
    string SourceTitle,
    DocumentTreeRevisionId TreeRevisionId,
    DocumentBoxId? BoxId,
    string Markdown);
