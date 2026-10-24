using System.Data.Common;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ContinuationMutationInvariantTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cross_page_type_or_suppression_mutations_are_rejected_at_commit(
        bool editFollower, bool suppress)
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(source, "source"));
        await CommitAsync(context, context.Pages[1], Text(follower, "follower", source));

        PageId pageId = editFollower ? context.Pages[1] : context.Pages[0];
        DocumentBoxId changedId = editFollower ? follower : source;
        PageEditSession draft = (await context.Trees.BeginPageEditAsync(context.DocumentId, pageId)).Value;
        if (suppress)
        {
            Result suppression = await context.Trees.SetSuppressedAsync(draft.SessionId, changedId, true);
            suppression.IsSuccess.Should().BeTrue(suppression.ErrorMessage);
        }
        else
        {
            string type = editFollower ? DocumentBoxType.List : DocumentBoxType.Title;
            DocumentBoxPayload payload = editFollower
                ? new ListBoxPayload("- follower")
                : new TextBoxPayload("source");
            Result update = await context.Trees.UpdateLeafAsync(draft.SessionId,
                new UpdateLeafCommand(changedId, type, payload, type == DocumentBoxType.Title ? 1 : null));
            update.IsSuccess.Should().BeTrue(update.ErrorMessage);
        }

        Result<DocumentTreeRevision> commit = await context.Trees.CommitPageEditAsync(draft.SessionId);
        commit.IsFailure.Should().BeTrue();
        (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, pageId)).Value.TreeRevisionId
            .Should().NotBe(draft.DraftRevisionId);
        (await context.Trees.ListBoxesAsync(draft.DraftRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == changedId);
        DocumentTreeRevision current = (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, pageId)).Value;
        DocumentBox committed = (await context.Trees.ListBoxesAsync(current.TreeRevisionId)).Value
            .Single(box => box.BoxId == changedId);
        committed.BoxType.Should().Be(DocumentBoxType.Text);
        committed.Suppressed.Should().BeFalse();
        committed.ContinuesFromBoxId.Should().Be(editFollower ? source : null);
    }

    [Fact]
    public async Task Working_revision_commit_checks_cross_page_relation_and_batch_future_snapshot()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(source, "source"));
        await CommitAsync(context, context.Pages[1], Text(follower, "follower", source));

        Result<DocumentTreeRevision> invalid = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, context.Pages[0], [Text(source, "source", type: DocumentBoxType.Title)],
            DocumentTreeRevisionSource.Import);
        invalid.IsSuccess.Should().BeTrue(invalid.ErrorMessage);
        (await context.Trees.CommitWorkingRevisionAsync(invalid.Value.TreeRevisionId)).IsFailure.Should().BeTrue();
        (await context.Trees.ListBoxesAsync(invalid.Value.TreeRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == source && box.BoxType == DocumentBoxType.Title);
        (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.Pages[0])).Value.TreeRevisionId
            .Should().NotBe(invalid.Value.TreeRevisionId);

        await using Context batchContext = await Context.CreateAsync();
        DocumentBoxId batchSourceId = DocumentBoxId.New();
        DocumentBoxId batchFollowerId = DocumentBoxId.New();
        await CommitAsync(batchContext, batchContext.Pages[0], Text(batchSourceId, "source"));
        await CommitAsync(batchContext, batchContext.Pages[1], Text(batchFollowerId, "follower", batchSourceId));

        Result<DocumentTreeRevision> newSource = await batchContext.Trees.BeginWorkingRevisionAsync(
            batchContext.DocumentId, batchContext.Pages[0],
            [Text(batchSourceId, "- source", type: DocumentBoxType.List)],
            DocumentTreeRevisionSource.Import);
        Result<DocumentTreeRevision> newFollower = await batchContext.Trees.BeginWorkingRevisionAsync(
            batchContext.DocumentId, batchContext.Pages[1],
            [Text(batchFollowerId, "- follower", batchSourceId, DocumentBoxType.List)],
            DocumentTreeRevisionSource.Import);
        newSource.IsSuccess.Should().BeTrue(newSource.ErrorMessage);
        newFollower.IsSuccess.Should().BeTrue(newFollower.ErrorMessage);
        await using SqliteConnection connection = batchContext.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        Result<IReadOnlyList<DocumentTreeRevision>> batch = await batchContext.Trees
            .CommitWorkingRevisionsInTransactionAsync(connection, transaction,
                [newSource.Value.TreeRevisionId, newFollower.Value.TreeRevisionId]);
        batch.IsSuccess.Should().BeTrue(batch.ErrorMessage);
        await transaction.CommitAsync();
        (await batchContext.Trees.GetCurrentRevisionAsync(batchContext.DocumentId, batchContext.Pages[1])).Value
            .TreeRevisionId
            .Should().Be(newFollower.Value.TreeRevisionId);
    }

    [Fact]
    public async Task Unrelated_boxes_on_a_page_with_cross_page_links_remain_editable()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId unrelated = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(source, "source"), Text(unrelated, "unrelated", order: 1));
        await CommitAsync(context, context.Pages[1], Text(follower, "follower", source));

        PageEditSession draft = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.Pages[0])).Value;
        Result update = await context.Trees.UpdateLeafAsync(draft.SessionId,
            new UpdateLeafCommand(unrelated, DocumentBoxType.List, new ListBoxPayload("- unrelated")));
        update.IsSuccess.Should().BeTrue(update.ErrorMessage);
        Result suppress = await context.Trees.SetSuppressedAsync(draft.SessionId, unrelated, true);
        suppress.IsSuccess.Should().BeTrue(suppress.ErrorMessage);
        Result<DocumentTreeRevision> commit = await context.Trees.CommitPageEditAsync(draft.SessionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);
        DocumentTreeRevision current = (await context.Trees.GetCurrentRevisionAsync(
            context.DocumentId, context.Pages[0])).Value;
        DocumentBox updated = (await context.Trees.ListBoxesAsync(current.TreeRevisionId)).Value
            .Single(box => box.BoxId == unrelated);
        updated.BoxType.Should().Be(DocumentBoxType.List);
        updated.Suppressed.Should().BeTrue();
    }

    [Fact]
    public async Task Explicit_second_follower_is_rejected_without_changing_existing_relation()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(source, "source"));
        await CommitAsync(context, context.Pages[1], Text(first, "first", source));
        await CommitAsync(context, context.Pages[2], Text(second, "second"));

        PageEditSession idempotent = (await context.Trees.BeginPageEditAsync(
            context.DocumentId, context.Pages[1])).Value;
        Result sameLink = await context.Trees.SetContinuationAsync(idempotent.SessionId, first, source);
        sameLink.IsSuccess.Should().BeTrue(sameLink.ErrorMessage);
        Result discarded = await context.Trees.DiscardPageEditAsync(idempotent.SessionId);
        discarded.IsSuccess.Should().BeTrue(discarded.ErrorMessage);

        PageEditSession draft = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.Pages[2])).Value;
        Result secondLink = await context.Trees.SetContinuationAsync(draft.SessionId, second, source);
        secondLink.IsFailure.Should().BeTrue();
        DocumentTreeRevision firstCurrent = (await context.Trees.GetCurrentRevisionAsync(
            context.DocumentId, context.Pages[1])).Value;
        (await context.Trees.ListBoxesAsync(firstCurrent.TreeRevisionId)).Value
            .Single(box => box.BoxId == first).ContinuesFromBoxId.Should().Be(source);
        (await context.Trees.ListBoxesAsync(draft.DraftRevisionId)).Value
            .Single(box => box.BoxId == second).ContinuesFromBoxId.Should().BeNull();
        Result discardedBranchDraft = await context.Trees.DiscardPageEditAsync(draft.SessionId);
        discardedBranchDraft.IsSuccess.Should().BeTrue(discardedBranchDraft.ErrorMessage);
    }

    [Fact]
    public async Task Same_page_unlink_then_relink_uses_the_caller_draft_for_branch_checks()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId oldFollower = DocumentBoxId.New();
        DocumentBoxId newFollower = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(source, "source"),
            Text(oldFollower, "old follower", source, order: 1), Text(newFollower, "new follower", order: 2));

        PageEditSession draft = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.Pages[0])).Value;
        Result unlink = await context.Trees.SetContinuationAsync(draft.SessionId, oldFollower, null);
        unlink.IsSuccess.Should().BeTrue(unlink.ErrorMessage);
        Result link = await context.Trees.SetContinuationAsync(draft.SessionId, newFollower, source);
        link.IsSuccess.Should().BeTrue(link.ErrorMessage);
        Result<DocumentTreeRevision> commit = await context.Trees.CommitPageEditAsync(draft.SessionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);
        IReadOnlyList<DocumentBox> after = (await context.Trees.ListBoxesAsync(commit.Value.TreeRevisionId)).Value;
        after.Single(box => box.BoxId == oldFollower).ContinuesFromBoxId.Should().BeNull();
        after.Single(box => box.BoxId == newFollower).ContinuesFromBoxId.Should().Be(source);
    }

    [Fact]
    public async Task Batch_commit_rejects_a_new_explicit_branch_without_persisting_either_page()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(source, "source"));
        await CommitAsync(context, context.Pages[1], Text(first, "first"));
        await CommitAsync(context, context.Pages[2], Text(second, "second"));

        Result<DocumentTreeRevision> firstDraft = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, context.Pages[1], [Text(first, "first", source)], DocumentTreeRevisionSource.Import);
        Result<DocumentTreeRevision> secondDraft = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, context.Pages[2], [Text(second, "second", source)], DocumentTreeRevisionSource.Import);
        firstDraft.IsSuccess.Should().BeTrue(firstDraft.ErrorMessage);
        secondDraft.IsSuccess.Should().BeTrue(secondDraft.ErrorMessage);

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        Result<IReadOnlyList<DocumentTreeRevision>> batch = await context.Trees
            .CommitWorkingRevisionsInTransactionAsync(connection, transaction,
                [firstDraft.Value.TreeRevisionId, secondDraft.Value.TreeRevisionId]);
        batch.IsFailure.Should().BeTrue();
        await transaction.RollbackAsync();
        (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.Pages[1])).Value.TreeRevisionId
            .Should().NotBe(firstDraft.Value.TreeRevisionId);
        (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.Pages[2])).Value.TreeRevisionId
            .Should().NotBe(secondDraft.Value.TreeRevisionId);
        (await context.Trees.ListBoxesAsync(firstDraft.Value.TreeRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == first && box.ContinuesFromBoxId == source);
        (await context.Trees.ListBoxesAsync(secondDraft.Value.TreeRevisionId)).Value
            .Should().ContainSingle(box => box.BoxId == second && box.ContinuesFromBoxId == source);
    }

    [Fact]
    public async Task Legacy_unrecovered_state_survives_source_changes_until_local_text_is_written()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId originalSource = DocumentBoxId.New();
        DocumentBoxId replacementSource = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(originalSource, "source full text"));
        await CommitAsync(context, context.Pages[1], Text(replacementSource, "other source"));
        await CommitAsync(context, context.Pages[2], Text(follower, "", originalSource,
            state: DocumentContinuationState.LegacyUnrecovered));

        DocumentTreeRevision originalRevision = (await context.Trees.GetCurrentRevisionAsync(
            context.DocumentId, context.Pages[0])).Value;
        DocumentTreeRevision replacementRevision = (await context.Trees.GetCurrentRevisionAsync(
            context.DocumentId, context.Pages[1])).Value;
        DocumentBox originalBefore = (await context.Trees.ListBoxesAsync(originalRevision.TreeRevisionId)).Value
            .Single(box => box.BoxId == originalSource);
        DocumentBox replacementBefore = (await context.Trees.ListBoxesAsync(replacementRevision.TreeRevisionId)).Value
            .Single(box => box.BoxId == replacementSource);

        PageEditSession draft = (await context.Trees.BeginPageEditAsync(context.DocumentId, context.Pages[2])).Value;
        Result idempotent = await context.Trees.SetContinuationAsync(draft.SessionId, follower, originalSource);
        idempotent.IsSuccess.Should().BeTrue(idempotent.ErrorMessage);
        DocumentBox current = (await context.Trees.ListBoxesAsync(draft.DraftRevisionId)).Value
            .Single(box => box.BoxId == follower);
        current.Payload.Should().Be(new TextBoxPayload(string.Empty));
        current.ContinuationState.Should().Be(DocumentContinuationState.LegacyUnrecovered);

        Result corrected = await context.Trees.SetContinuationAsync(draft.SessionId, follower, replacementSource);
        corrected.IsSuccess.Should().BeTrue(corrected.ErrorMessage);
        current = (await context.Trees.ListBoxesAsync(draft.DraftRevisionId)).Value
            .Single(box => box.BoxId == follower);
        current.ContinuesFromBoxId.Should().Be(replacementSource);
        current.Payload.Should().Be(new TextBoxPayload(string.Empty));
        current.ContinuationState.Should().Be(DocumentContinuationState.LegacyUnrecovered);

        Result writeOwnText = await context.Trees.UpdateLeafAsync(draft.SessionId,
            new UpdateLeafCommand(follower, DocumentBoxType.Text, new TextBoxPayload("recovered locally")));
        writeOwnText.IsSuccess.Should().BeTrue(writeOwnText.ErrorMessage);
        current = (await context.Trees.ListBoxesAsync(draft.DraftRevisionId)).Value
            .Single(box => box.BoxId == follower);
        current.Payload.Should().Be(new TextBoxPayload("recovered locally"));
        current.ContinuationState.Should().BeNull();
        Result<DocumentTreeRevision> commit = await context.Trees.CommitPageEditAsync(draft.SessionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);

        DocumentBox originalAfter = (await context.Trees.ListBoxesAsync(originalRevision.TreeRevisionId)).Value
            .Single(box => box.BoxId == originalSource);
        DocumentBox replacementAfter = (await context.Trees.ListBoxesAsync(replacementRevision.TreeRevisionId)).Value
            .Single(box => box.BoxId == replacementSource);
        originalAfter.Should().Be(originalBefore);
        replacementAfter.Should().Be(replacementBefore);
    }

    [Fact]
    public async Task Legacy_star_relationships_remain_readable_and_can_be_cleared_individually()
    {
        await using Context context = await Context.CreateAsync();
        DocumentBoxId source = DocumentBoxId.New();
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        await CommitAsync(context, context.Pages[0], Text(source, "legacy full text"));
        await CommitAsync(context, context.Pages[1],
            Text(first, "", source, state: DocumentContinuationState.LegacyUnrecovered),
            Text(second, "", source, state: DocumentContinuationState.LegacyUnrecovered, order: 1));
        DocumentTreeRevision current = (await context.Trees.GetCurrentRevisionAsync(
            context.DocumentId, context.Pages[1])).Value;
        IReadOnlyList<DocumentBox> beforeClear = (await context.Trees.ListBoxesAsync(current.TreeRevisionId)).Value;
        beforeClear.Should().Contain(box => box.BoxId == first &&
                                            box.ContinuationState == DocumentContinuationState.LegacyUnrecovered);
        beforeClear.Should().Contain(box => box.BoxId == second &&
                                            box.ContinuationState == DocumentContinuationState.LegacyUnrecovered);

        PageEditSession legacyDraft = (await context.Trees.BeginPageEditAsync(
            context.DocumentId, context.Pages[1])).Value;
        Result unlink = await context.Trees.SetContinuationAsync(legacyDraft.SessionId, first, null);
        unlink.IsSuccess.Should().BeTrue(unlink.ErrorMessage);
        Result<DocumentTreeRevision> commit = await context.Trees.CommitPageEditAsync(legacyDraft.SessionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);
        current = (await context.Trees.GetCurrentRevisionAsync(context.DocumentId, context.Pages[1])).Value;
        IReadOnlyList<DocumentBox> afterClear = (await context.Trees.ListBoxesAsync(current.TreeRevisionId)).Value;
        afterClear.Single(box => box.BoxId == first).ContinuesFromBoxId.Should().BeNull();
        afterClear.Single(box => box.BoxId == second).ContinuesFromBoxId.Should().Be(source);
    }

    private static DocumentBoxSeed Text(
        DocumentBoxId id, string markdown, DocumentBoxId? source = null,
        string type = DocumentBoxType.Text, string? state = null, int order = 0)
    {
        DocumentBoxPayload payload = type switch
        {
            DocumentBoxType.List => new ListBoxPayload(markdown),
            _ => new TextBoxPayload(markdown)
        };
        return new DocumentBoxSeed(id, null, order, type, null,
            new NormalizedBBox(.1, .1, .8, .1), payload,
            type == DocumentBoxType.Title ? 1 : null,
            ContinuesFromBoxId: source, ContinuationState: state);
    }

    private static async Task CommitAsync(Context context, PageId page, params DocumentBoxSeed[] seeds)
    {
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, page, seeds, DocumentTreeRevisionSource.Import);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        Result<DocumentTreeRevision> commit =
            await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, DocumentTreeService trees,
            DocumentInstanceId documentId, IReadOnlyList<PageId> pages)
        {
            Database = database;
            Trees = trees;
            DocumentId = documentId;
            Pages = pages;
        }

        public TemporarySqliteDatabase Database { get; }
        public DocumentTreeService Trees { get; }
        public DocumentInstanceId DocumentId { get; }
        public IReadOnlyList<PageId> Pages { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-10-24T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            LibraryMetadata library = (await libraries.CreateLibraryAsync("Continuation invariant")).Value;
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "Continuation invariant")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            for (int index = 0; index < 3; index++)
            {
                await pages.CreatePageAsync(document.DocumentInstanceId, index, (index + 1).ToString(),
                    null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);
            }

            await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            IEnumerable<string> ids = await connection.QueryAsync<string>(
                "select page_id from pages where document_instance_id = @Id order by page_index;",
                new { Id = document.DocumentInstanceId.ToString() });
            return new Context(database, BoxTreeTestData.CreateService(database.ConnectionFactory, clock),
                document.DocumentInstanceId, ids.Select(PageId.Parse).ToArray());
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
