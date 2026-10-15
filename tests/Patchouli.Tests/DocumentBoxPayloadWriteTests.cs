using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Tests;

public sealed class DocumentBoxPayloadWriteTests
{
    [Fact]
    public async Task Replacement_commits_one_revision_and_preserves_box_identity_and_metadata()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision original = await context.CommitAsync(Seed(boxId));
        int originalCommitCount = await CountForPageAsync(context, "document_commits");
        DocumentBox before = Assert.Single(await context.ListBoxesAsync(original.TreeRevisionId));

        Result<DocumentTreeRevision> result = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [boxId] = new TextBoxPayload("replacement") });

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
        result.Value.ParentTreeRevisionId.Should().Be(original.TreeRevisionId);
        result.Value.IsCurrent.Should().BeTrue();
        DocumentBox after = Assert.Single(await context.ListBoxesAsync(result.Value.TreeRevisionId));
        after.BoxId.Should().Be(before.BoxId);
        after.TreeRevisionId.Should().Be(result.Value.TreeRevisionId);
        after.Payload.Should().Be(new TextBoxPayload("replacement"));
        after.SubType.Should().Be(before.SubType);
        after.BaseType.Should().Be(before.BaseType);
        after.BBox.Should().Be(before.BBox);
        after.Confidence.Should().Be(before.Confidence);
        after.Suppressed.Should().Be(before.Suppressed);
        after.HeadingLevel.Should().Be(before.HeadingLevel);
        after.CodeLanguage.Should().Be(before.CodeLanguage);
        (await context.ListBoxesAsync(original.TreeRevisionId)).Should().ContainSingle()
            .Which.Payload.Should().Be(new TextBoxPayload("original"));
        (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageId)).Value.TreeRevisionId
            .Should().Be(result.Value.TreeRevisionId);
        (await CountForPageAsync(context, "document_commits")).Should().Be(originalCommitCount + 1);
        (await CountForPageAsync(context, "document_tree_revisions")).Should().Be(2);
    }

    [Fact]
    public async Task Stale_revision_and_active_edit_are_conflicts_without_new_commits()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision original = await context.CommitAsync(Seed(boxId));
        int originalCommitCount = await CountForPageAsync(context, "document_commits");

        Result<DocumentTreeRevision> stale = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            DocumentTreeRevisionId.New(),
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [boxId] = new TextBoxPayload("stale") });
        stale.IsFailure.Should().BeTrue();
        stale.ErrorCode.Should().Be("conflict");

        Result<PageEditSession> edit = await context.Trees.BeginPageEditAsync(context.DocumentId, context.PageId);
        edit.IsSuccess.Should().BeTrue(edit.ErrorMessage);
        Result<DocumentTreeRevision> activeDraft = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [boxId] = new TextBoxPayload("draft conflict") });
        activeDraft.IsFailure.Should().BeTrue();
        activeDraft.ErrorCode.Should().Be("conflict");
        (await CountForPageAsync(context, "document_commits")).Should().Be(originalCommitCount);
        (await CountForPageAsync(context, "document_tree_revisions")).Should().Be(2);
    }

    [Fact]
    public async Task Invalid_batch_member_reports_its_box_and_rolls_back_all_changes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId textId = DocumentBoxId.New();
        DocumentBoxId missingId = DocumentBoxId.New();
        DocumentTreeRevision original = await context.CommitAsync(Seed(textId));
        int originalCommitCount = await CountForPageAsync(context, "document_commits");

        Result<DocumentTreeRevision> result = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [textId] = new TextBoxPayload("would be changed"),
                [missingId] = new TextBoxPayload("unknown")
            });

        result.IsFailure.Should().BeTrue();
        result.Details.Should().BeOfType<BoxContentFailureDetails>()
            .Which.Errors.Should().ContainSingle().Which.BoxId.Should().Be(missingId.ToString());
        (await CountForPageAsync(context, "document_commits")).Should().Be(originalCommitCount);
        (await CountForPageAsync(context, "document_tree_revisions")).Should().Be(1);
        (await context.ListBoxesAsync(original.TreeRevisionId)).Single().Payload
            .Should().Be(new TextBoxPayload("original"));

        Result<DocumentTreeRevision> wrongType = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [textId] = new CodeBoxPayload("code") });
        wrongType.IsFailure.Should().BeTrue();
        wrongType.Details.Should().BeOfType<BoxContentFailureDetails>()
            .Which.Errors.Should().ContainSingle().Which.BoxId.Should().Be(textId.ToString());
    }

    [Fact]
    public async Task Logical_page_box_cannot_be_replaced_even_when_leaf_child_exists()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId pageBoxId = DocumentBoxId.New();
        DocumentBoxId leafId = DocumentBoxId.New();
        DocumentTreeRevision original = await context.CommitAsync(
            new DocumentBoxSeed(pageBoxId, null, 0, DocumentBoxType.LogicalPage, null, null,
                new NormalizedBBox(0.02, 0.02, 0.9, 0.9), null),
            new DocumentBoxSeed(leafId, pageBoxId, 0, DocumentBoxType.Text, null, null,
                new NormalizedBBox(0.1, 0.1, 0.5, 0.2), new TextBoxPayload("original")));
        int originalCommitCount = await CountForPageAsync(context, "document_commits");

        Result<DocumentTreeRevision> result = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [pageBoxId] = new TextBoxPayload("invalid") });

        result.IsFailure.Should().BeTrue();
        result.Details.Should().BeOfType<BoxContentFailureDetails>()
            .Which.Errors.Should().ContainSingle().Which.BoxId.Should().Be(pageBoxId.ToString());
        (await CountForPageAsync(context, "document_commits")).Should().Be(originalCommitCount);
    }

    [Fact]
    public async Task Replacement_preserves_media_assets_and_table_html_from_existing_payloads()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId mediaId = DocumentBoxId.New();
        DocumentBoxId tableId = DocumentBoxId.New();
        const string tableMarkdown = "[Table]";
        DocumentTreeRevision original = await context.CommitAsync(
            new DocumentBoxSeed(mediaId, null, 0, DocumentBoxType.Image, null, null,
                new NormalizedBBox(0.02, 0.02, 0.4, 0.3), new MediaBoxPayload("asset-original", "old description")),
            new DocumentBoxSeed(tableId, null, 1, DocumentBoxType.Table, null, null,
                new NormalizedBBox(0.55, 0.02, 0.4, 0.3),
                new TableBoxPayload(tableMarkdown, "<table><tr><td>hidden</td></tr></table>")));

        Result<DocumentTreeRevision> result = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [mediaId] = new MediaBoxPayload("asset-injected", "new description"),
                [tableId] = new TableBoxPayload(tableMarkdown, "<table><tr><td>injected</td></tr></table>")
            });

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        IReadOnlyList<DocumentBox> updated = await context.ListBoxesAsync(result.Value.TreeRevisionId);
        updated.Single(box => box.BoxId == mediaId).Payload
            .Should().Be(new MediaBoxPayload("asset-original", "new description"));
        updated.Single(box => box.BoxId == tableId).Payload
            .Should().Be(new TableBoxPayload(tableMarkdown, "<table><tr><td>hidden</td></tr></table>"));
    }

    [Fact]
    public async Task Replacement_rejects_nonlogical_boxes_that_have_children()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId imageId = DocumentBoxId.New();
        DocumentBoxId captionId = DocumentBoxId.New();
        DocumentTreeRevision original = await context.CommitAsync(
            new DocumentBoxSeed(imageId, null, 0, DocumentBoxType.Image, null, null,
                new NormalizedBBox(0.02, 0.02, 0.7, 0.5), new MediaBoxPayload("asset", "description")),
            new DocumentBoxSeed(captionId, imageId, 0, DocumentBoxType.ImageCaption, null, null,
                new NormalizedBBox(0.1, 0.1, 0.5, 0.1), new TextBoxPayload("caption")));
        int originalCommitCount = await CountForPageAsync(context, "document_commits");

        Result<DocumentTreeRevision> result = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [imageId] = new MediaBoxPayload("asset", "edited") });

        result.IsFailure.Should().BeTrue();
        result.Details.Should().BeOfType<BoxContentFailureDetails>()
            .Which.Errors.Should().ContainSingle().Which.BoxId.Should().Be(imageId.ToString());
        (await CountForPageAsync(context, "document_commits")).Should().Be(originalCommitCount);
    }

    [Fact]
    public async Task Empty_batches_and_cancelled_calls_leave_revision_history_unchanged()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision original = await context.CommitAsync(Seed(boxId));
        int originalCommitCount = await CountForPageAsync(context, "document_commits");

        Result<DocumentTreeRevision> empty = await context.Trees.ReplaceBoxPayloadsAsync(context.DocumentId,
            context.PageId,
            original.TreeRevisionId, new Dictionary<DocumentBoxId, DocumentBoxPayload>());
        empty.IsFailure.Should().BeTrue();
        empty.ErrorCode.Should().Be("invalid_argument");

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Trees.ReplaceBoxPayloadsAsync(
            context.DocumentId, context.PageId, original.TreeRevisionId,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [boxId] = new TextBoxPayload("cancelled") },
            canceled.Token));

        (await CountForPageAsync(context, "document_commits")).Should().Be(originalCommitCount);
        (await CountForPageAsync(context, "document_tree_revisions")).Should().Be(1);
        (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.PageId)).Value.TreeRevisionId
            .Should().Be(original.TreeRevisionId);
    }

    private static DocumentBoxSeed Seed(DocumentBoxId boxId)
    {
        return new DocumentBoxSeed(boxId, null, 0, DocumentBoxType.Text, "source-subtype", "text",
            new NormalizedBBox(0.1, 0.1, 0.5, 0.2), new TextBoxPayload("original"),
            Confidence: 0.73, Suppressed: true);
    }

    private static async Task<int> CountForPageAsync(TranslationTestContext context, string table)
    {
        await using SqliteConnection connection = context.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        string filter = table == "document_commits"
            ? "document_instance_id = @DocumentId"
            : "page_id = @PageId";
        return await connection.ExecuteScalarAsync<int>($"select count(1) from {table} where {filter};",
            new { DocumentId = context.DocumentId.ToString(), PageId = context.PageId.ToString() });
    }
}
