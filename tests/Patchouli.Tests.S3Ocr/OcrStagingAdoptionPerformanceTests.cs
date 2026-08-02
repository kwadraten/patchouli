using Dapper;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Tests.S3Ocr;

public sealed class OcrStagingAdoptionPerformanceTests
{
    [Fact]
    public async Task Bulk_staging_and_adoption_preserve_a_large_committed_tree()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        const int boxCount = 1200;

        DocumentTreeRevision staging = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(boxCount))).Value;
        (await context.Trees.ListBoxesAsync(staging.TreeRevisionId)).Value.Should().HaveCount(boxCount);

        Result<DocumentTreeRevision> adopted =
            await context.Trees.AdoptStagingRevisionAsync(staging.TreeRevisionId);

        adopted.IsSuccess.Should().BeTrue(adopted.ErrorMessage);
        IReadOnlyList<DocumentBox> committed = (await context.Trees.ListBoxesAsync(adopted.Value.TreeRevisionId)).Value;
        committed.Should().HaveCount(boxCount);
        committed.Select(box => ((TextBoxPayload)box.Payload!).Markdown)
            .Should().BeEquivalentTo(Boxes.LeafText(boxCount).Select(seed => ((TextBoxPayload)seed.Payload!).Markdown));

        (await context.ScalarAsync(
            "select status from document_tree_revisions where tree_revision_id = @Id;",
            new { Id = staging.TreeRevisionId.ToString() })).Should().Be(DocumentTreeRevisionStatus.Discarded);
        (await context.CountAsync(
            "select count(1) from document_boxes where tree_revision_id = @Id;",
            new { Id = adopted.Value.TreeRevisionId.ToString() })).Should().Be(boxCount);
    }

    [Fact]
    public async Task Bulk_adoption_marks_all_search_units_of_adopted_pages_stale_in_one_batch()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        DocumentTreeRevision first = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(50))).Value;
        DocumentTreeRevision second = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[1].PageId,
            Boxes.LeafText(40))).Value;
        await context.Trees.AdoptStagingRevisionsAsync([first.TreeRevisionId, second.TreeRevisionId]);

        (await context.CountAsync(
            "select count(1) from search_units where status = 'current';")).Should().Be(0);
    }

    [Fact]
    public async Task Cancelled_adoption_rolls_back_without_partial_current_tree()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        DocumentTreeRevision staging = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(300))).Value;
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> adopt = () => context.Trees.AdoptStagingRevisionAsync(
            staging.TreeRevisionId, cts.Token);

        (await adopt.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeNull();
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, context.Pages[0].PageId))
            .IsFailure.Should().BeTrue();
        (await context.ScalarAsync(
            "select status from document_tree_revisions where tree_revision_id = @Id;",
            new { Id = staging.TreeRevisionId.ToString() })).Should().Be(DocumentTreeRevisionStatus.Staging);
        (await context.CountAsync(
            "select count(1) from document_boxes where tree_revision_id = @Id;",
            new { Id = staging.TreeRevisionId.ToString() })).Should().Be(300);
    }
}
