using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;

namespace Patchouli.Tests;

/// <summary>
/// AC16: overlap warnings are a bounded, lazy, revision-keyed projection. The projection is
/// computed only on entered/prefetched pages, immutable revision results are reused, a Box edit
/// invalidates only the affected page, and the projection never reads the source file or
/// computes a full hash.
/// </summary>
public sealed class OverlapProjectionServiceTests
{
    [Fact]
    public async Task Overlaps_are_computed_lazily_only_when_requested()
    {
        OverlapProjectionService service = new(1024 * 1024);
        int providerCalls = 0;
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        PageId page = PageId.New();

        Task<Result<IReadOnlyList<DocumentBox>>> Provider(CancellationToken _)
        {
            providerCalls++;
            return Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(OverlappingBoxes()));
        }

        providerCalls.Should().Be(0, "nothing may be computed before a page requests the projection");

        Result<IReadOnlyList<DocumentBoxOverlap>> overlaps =
            await service.GetOrCreateAsync(revision, page, DocumentBoxOverlapDetector.PolicyBasis, Provider);

        overlaps.IsSuccess.Should().BeTrue();
        overlaps.Value.Should().ContainSingle();
        providerCalls.Should().Be(1, "the first request computes exactly once");
        service.Metrics.Misses.Should().Be(1);
        service.Metrics.Inserted.Should().Be(1);
    }

    [Fact]
    public async Task Unchanged_immutable_revision_reuses_the_cached_projection()
    {
        OverlapProjectionService service = new(1024 * 1024);
        int providerCalls = 0;
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        PageId page = PageId.New();

        Task<Result<IReadOnlyList<DocumentBox>>> Provider(CancellationToken _)
        {
            providerCalls++;
            return Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(OverlappingBoxes()));
        }

        Result<IReadOnlyList<DocumentBoxOverlap>> first =
            await service.GetOrCreateAsync(revision, page, DocumentBoxOverlapDetector.PolicyBasis, Provider);
        Result<IReadOnlyList<DocumentBoxOverlap>> second =
            await service.GetOrCreateAsync(revision, page, DocumentBoxOverlapDetector.PolicyBasis, Provider);

        first.Value.Should().ContainSingle();
        second.Value.Should().ContainSingle();
        providerCalls.Should().Be(1, "an immutable revision result must be reused");
        service.Metrics.Hits.Should().Be(1);
        service.Metrics.Inserted.Should().Be(1);
    }

    [Fact]
    public async Task Invalidating_the_edited_page_recomputes_after_a_box_edit()
    {
        OverlapProjectionService service = new(1024 * 1024);
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        PageId page = PageId.New();
        MutableFlag boxesOverlap = new(true);

        (await service.GetOrCreateAsync(revision, page, DocumentBoxOverlapDetector.PolicyBasis,
                BoxProvider(() => boxesOverlap.Value ? OverlappingBoxes() : NonOverlappingBoxes()))).Value.Should()
            .ContainSingle();

        boxesOverlap.Value = false;
        service.Invalidate(page);

        Result<IReadOnlyList<DocumentBoxOverlap>> after = await service.GetOrCreateAsync(
            revision, page, DocumentBoxOverlapDetector.PolicyBasis,
            BoxProvider(() => boxesOverlap.Value ? OverlappingBoxes() : NonOverlappingBoxes()));
        after.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalidation_is_scoped_to_the_edited_page_only()
    {
        OverlapProjectionService service = new(1024 * 1024);
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        PageId edited = PageId.New();
        PageId other = PageId.New();

        (await service.GetOrCreateAsync(revision, edited, DocumentBoxOverlapDetector.PolicyBasis,
            BoxProvider())).Value.Should().ContainSingle();
        (await service.GetOrCreateAsync(revision, other, DocumentBoxOverlapDetector.PolicyBasis,
            BoxProvider())).Value.Should().ContainSingle();

        service.Invalidate(edited);

        (await service.GetOrCreateAsync(revision, other, DocumentBoxOverlapDetector.PolicyBasis,
            BoxProvider())).Value.Should().ContainSingle();
        service.Metrics.Hits.Should().Be(1, "the untouched page must still be served from cache");
    }

    [Fact]
    public async Task Different_overlap_policy_basis_keeps_separate_cache_entries()
    {
        OverlapProjectionService service = new(1024 * 1024);
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        PageId page = PageId.New();

        await service.GetOrCreateAsync(revision, page, DocumentBoxOverlapDetector.PolicyBasis, BoxProvider());
        (await service.GetOrCreateAsync(revision, page, $"{DocumentBoxOverlapDetector.PolicyBasis}-next",
            BoxProvider())).Value.Should().ContainSingle();

        service.Metrics.Inserted.Should().Be(2, "a policy basis change must not reuse the old projection");
    }

    [Fact]
    public async Task A_failed_box_load_is_never_cached_and_can_be_retried()
    {
        OverlapProjectionService service = new(1024 * 1024);
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        PageId page = PageId.New();
        MutableFlag fail = new(true);

        Result<IReadOnlyList<DocumentBoxOverlap>> failed = await service.GetOrCreateAsync(
            revision, page, DocumentBoxOverlapDetector.PolicyBasis,
            _ => Task.FromResult(fail.Value
                ? Result<IReadOnlyList<DocumentBox>>.Failure(AppErrorCodes.NotFound, "load failed")
                : Result<IReadOnlyList<DocumentBox>>.Success(OverlappingBoxes())));

        failed.IsFailure.Should().BeTrue();
        service.Metrics.CachedEntries.Should().Be(0, "failed generations must not be cached");

        fail.Value = false;
        (await service.GetOrCreateAsync(revision, page, DocumentBoxOverlapDetector.PolicyBasis,
                _ => Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(OverlappingBoxes())))).Value.Should()
            .ContainSingle();
        service.Metrics.CachedEntries.Should().Be(1);
    }

    private Func<CancellationToken, Task<Result<IReadOnlyList<DocumentBox>>>> BoxProvider(
        Func<IReadOnlyList<DocumentBox>>? supply = null)
    {
        return _ => Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(
            supply is null ? OverlappingBoxes() : supply()));
    }

    private static IReadOnlyList<DocumentBox> OverlappingBoxes()
    {
        return [Box(new NormalizedBBox(0.1, 0.1, 0.4, 0.4)), Box(new NormalizedBBox(0.3, 0.3, 0.4, 0.4))];
    }

    private static IReadOnlyList<DocumentBox> NonOverlappingBoxes()
    {
        return [Box(new NormalizedBBox(0.1, 0.1, 0.2, 0.2)), Box(new NormalizedBBox(0.5, 0.5, 0.2, 0.2))];
    }

    // Mutable holder so a test can flip the box set without reassigning a captured variable,
    // which R# would flag as a modified closure.
    private sealed class MutableFlag(bool value)
    {
        public bool Value { get; set; } = value;
    }

    private static DocumentBox Box(NormalizedBBox bbox)
    {
        DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
        return new DocumentBox(
            revisionId,
            DocumentBoxId.New(),
            DocumentInstanceId.New(),
            PageId.New(),
            null,
            null,
            DocumentBoxType.Text,
            null,
            null,
            bbox,
            new TextBoxPayload("text"),
            null,
            null,
            null,
            false);
    }
}
