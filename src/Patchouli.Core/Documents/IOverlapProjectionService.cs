using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

/// <summary>
/// Bounded, lazy, revision-keyed projection of sibling-box overlap warnings for a physical
/// page. A result is computed only on the first request for a given
/// <c>(tree_revision_id, page_id, overlap-policy-basis)</c> triple and reused while the
/// immutable revision is unchanged; workspace drafts or Box edits invalidate only the affected
/// page. The projection is derived purely from the Box set and never reads or hashes the
/// source file.
/// </summary>
public interface IOverlapProjectionService
{
    /// <summary>
    /// Returns the overlap warnings for the page, computing them lazily through
    /// <paramref name="boxesProvider"/> on a cache miss only. A cached immutable revision result
    /// is reused without invoking the provider again; the provider is the only path that touches
    /// box data, so the projection itself never triggers a source file hash.
    /// </summary>
    Task<Result<IReadOnlyList<DocumentBoxOverlap>>> GetOrCreateAsync(
        DocumentTreeRevisionId treeRevisionId,
        PageId pageId,
        string overlapPolicyBasis,
        Func<CancellationToken, Task<Result<IReadOnlyList<DocumentBox>>>> boxesProvider,
        CancellationToken cancellationToken = default);

    /// <summary>Invalidates every cached overlap projection of one page after a Box edit.</summary>
    void Invalidate(PageId pageId);

    /// <summary>Invalidates the cached overlap projection of one immutable revision.</summary>
    void Invalidate(DocumentTreeRevisionId treeRevisionId);

    /// <summary>Observable cache counters; safe for performance logging, they carry no content.</summary>
    OverlapProjectionMetrics Metrics { get; }
}

/// <summary>Immutable snapshot of the observable counters of an <see cref="IOverlapProjectionService"/>.</summary>
public sealed record OverlapProjectionMetrics(
    long Hits,
    long Misses,
    long Evictions,
    long Inserted,
    long Failed,
    long CachedEntries,
    long CachedBytes);
