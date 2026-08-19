using Dapper;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Tests.S3Ocr;

public sealed class OcrWorkingCommitPerformanceTests
{
    [Fact]
    public async Task Bulk_working_and_commit_preserve_a_large_committed_tree_in_place()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        const int boxCount = 1200;

        DocumentTreeRevision working = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(boxCount),
            DocumentTreeRevisionSource.Import)).Value;
        (await context.Trees.ListBoxesAsync(working.TreeRevisionId)).Value.Should().HaveCount(boxCount);

        Result<DocumentTreeRevision> committed =
            await context.Trees.CommitWorkingRevisionAsync(working.TreeRevisionId);

        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        committed.Value.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
        committed.Value.TreeRevisionId.Should().Be(working.TreeRevisionId);

        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(committed.Value.TreeRevisionId)).Value;
        boxes.Should().HaveCount(boxCount);
        boxes.Select(box => ((TextBoxPayload)box.Payload!).Markdown)
            .Should().BeEquivalentTo(Boxes.LeafText(boxCount).Select(seed => ((TextBoxPayload)seed.Payload!).Markdown));

        (await context.ScalarAsync(
            "select status from document_tree_revisions where tree_revision_id = @Id;",
            new { Id = working.TreeRevisionId.ToString() })).Should().Be(DocumentTreeRevisionStatus.Committed);
        (await context.CountAsync(
            "select count(1) from document_boxes where tree_revision_id = @Id;",
            new { Id = committed.Value.TreeRevisionId.ToString() })).Should().Be(boxCount);
    }

    [Fact]
    public async Task Bulk_commit_marks_all_search_units_of_committed_pages_stale_in_one_batch()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        DocumentTreeRevision first = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(50),
            DocumentTreeRevisionSource.Import)).Value;
        DocumentTreeRevision second = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Pages[1].PageId,
            Boxes.LeafText(40),
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(first.TreeRevisionId);
        await context.Trees.CommitWorkingRevisionAsync(second.TreeRevisionId);

        (await context.CountAsync(
            "select count(1) from search_units where status = 'current';")).Should().Be(0);
    }

    [Fact]
    public async Task Cancelled_commit_rolls_back_without_partial_current_tree()
    {
        await using OcrPerfContext context = await OcrPerfContext.CreateAsync();
        DocumentTreeRevision working = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            Boxes.LeafText(300),
            DocumentTreeRevisionSource.Import)).Value;
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> commit = () => context.Trees.CommitWorkingRevisionAsync(
            working.TreeRevisionId, cancellationToken: cts.Token);

        (await commit.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeNull();
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, context.Pages[0].PageId))
            .IsFailure.Should().BeTrue();
        (await context.ScalarAsync(
            "select status from document_tree_revisions where tree_revision_id = @Id;",
            new { Id = working.TreeRevisionId.ToString() })).Should().Be(DocumentTreeRevisionStatus.Working);
        (await context.CountAsync(
            "select count(1) from document_boxes where tree_revision_id = @Id;",
            new { Id = working.TreeRevisionId.ToString() })).Should().Be(300);
    }
}
