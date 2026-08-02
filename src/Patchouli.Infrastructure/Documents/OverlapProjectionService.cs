using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Infrastructure.Documents;

/// <summary>
/// Bounded, lazy overlap projection keyed by <c>(tree_revision_id, page_id, overlap-policy-basis)</c>.
/// A result is computed only on the first request for the triple and reused while the immutable
/// revision is unchanged; workspace drafts or Box edits invalidate only the affected page. The
/// projection is derived purely from the DocumentBox set supplied by the caller and never reads
/// or hashes the source file.
/// </summary>
public sealed class OverlapProjectionService : IOverlapProjectionService
{
    internal const long DefaultByteLimit = 4 * 1024 * 1024;

    private readonly SharedReadCache<OverlapProjectionKey, IReadOnlyList<DocumentBoxOverlap>> _cache;

    public OverlapProjectionService(long byteLimit = DefaultByteLimit)
    {
        _cache = new SharedReadCache<OverlapProjectionKey, IReadOnlyList<DocumentBoxOverlap>>(byteLimit,
            EstimateSize);
    }

    public OverlapProjectionMetrics Metrics
    {
        get
        {
            ReadCacheMetrics metrics = _cache.Metrics;
            return new OverlapProjectionMetrics(metrics.Hits, metrics.Misses, metrics.Evictions, metrics.Inserted,
                metrics.Failed, metrics.CachedEntries, metrics.CachedBytes);
        }
    }

    public Task<Result<IReadOnlyList<DocumentBoxOverlap>>> GetOrCreateAsync(
        DocumentTreeRevisionId treeRevisionId,
        PageId pageId,
        string overlapPolicyBasis,
        Func<CancellationToken, Task<Result<IReadOnlyList<DocumentBox>>>> boxesProvider,
        CancellationToken cancellationToken = default)
    {
        OverlapProjectionKey key = new(treeRevisionId, pageId, overlapPolicyBasis);
        return _cache.GetOrAddAsync(key, async token =>
        {
            Result<IReadOnlyList<DocumentBox>> boxes = await boxesProvider(token);
            return boxes.IsFailure
                ? Result<IReadOnlyList<DocumentBoxOverlap>>.Failure(boxes.ErrorCode!, boxes.ErrorMessage!)
                : Result<IReadOnlyList<DocumentBoxOverlap>>.Success(DocumentBoxOverlapDetector.Detect(boxes.Value));
        }, cancellationToken);
    }

    public void Invalidate(PageId pageId)
    {
        _cache.EvictWhere(key => key.PageId == pageId);
    }

    public void Invalidate(DocumentTreeRevisionId treeRevisionId)
    {
        _cache.EvictWhere(key => key.TreeRevisionId == treeRevisionId);
    }

    private static long EstimateSize(IReadOnlyList<DocumentBoxOverlap> overlaps)
    {
        return 128L + overlaps.Count * 256L;
    }

    private readonly record struct OverlapProjectionKey(
        DocumentTreeRevisionId TreeRevisionId,
        PageId PageId,
        string OverlapPolicyBasis);
}
