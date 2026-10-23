using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Contract tests for <see cref="HostTranslationJob"/>, the host-owned <see cref="ITranslationJob"/> the
/// extension drives. These run against the real migrated SQLite database and page-translation service so
/// page identity, revision, frozen context and atomic commit semantics are the production ones.
/// </summary>
/// <remarks>
/// R9 layering: this file owns the generic Host/RPC responsibilities that the real-script protocol cases
/// also touch — page issue order, internal skip/fail settle, radius-bounded frozen context, commit
/// rejection for foreign/unissued/settled pages, the conflict reload bound, settle-remaining and
/// commit-in-flight cancellation. Script-owned behaviours (request shape, prompt packing, retry/error
/// policy, derived caches) are NOT covered here; they stay real in
/// BuiltInTranslationExtensionProtocolTests and TranslationJobWorkflowTests.
/// </remarks>
public sealed class TranslationJobContractTests
{
    [Fact]
    public async Task Next_page_hands_out_requested_pages_in_order_and_settles_skipped_pages_internally()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "page one source"));
        Page second = await CreatePageAsync(context, 2);
        await CommitPageAsync(context, second.PageId,
            new DocumentBoxSeed(DocumentBoxId.New(), null, 0, DocumentBoxType.Equation, null, BBox,
                new EquationBoxPayload("x^2")));
        Page third = await CreatePageAsync(context, 3);
        await CommitPageAsync(context, third.PageId, TextSeed(DocumentBoxId.New(), 0, "page three source"));
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            Target(context.PageId, 1), Target(second.PageId, 2), Target(third.PageId, 3));

        TranslationPageInput? first = await fixture.Job.NextPageAsync();
        TranslationPageInput? secondIssued = await fixture.Job.NextPageAsync();
        TranslationPageInput? drained = await fixture.Job.NextPageAsync();

        first!.Page.PageIndex.Should().Be(1);
        secondIssued!.Page.PageIndex.Should().Be(3, "the equation-only page settles internally as skipped");
        drained.Should().BeNull();
        fixture.Published.Should().ContainSingle(outcome => outcome.State == TranslationPageRunState.Skipped &&
                                                            outcome.PageIndex == 2);
    }

    [Fact]
    public async Task An_unreadable_page_settles_failed_internally_without_stopping_later_pages()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "page one source"));
        PageId ghost = PageId.New();
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            Target(ghost, 1), Target(context.PageId, 2));

        TranslationPageInput? issued = await fixture.Job.NextPageAsync();
        TranslationPageInput? drained = await fixture.Job.NextPageAsync();

        issued!.Page.PageIndex.Should().Be(2, "the unreadable page is skipped over, not returned");
        drained.Should().BeNull();
        fixture.Published.Should().ContainSingle(outcome => outcome.State == TranslationPageRunState.Failed &&
                                                            outcome.PageIndex == 1);
    }

    [Fact]
    public async Task Context_reads_are_radius_bounded_frozen_per_page_and_include_the_stored_previous()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId firstBox = DocumentBoxId.New();
        await context.CommitAsync(TextSeed(firstBox, 0, "first page source"));
        Page second = await CreatePageAsync(context, 2);
        DocumentTreeRevision secondRevision = await CommitPageAsync(context, second.PageId,
            TextSeed(DocumentBoxId.New(), 0, "second page source"));
        Page third = await CreatePageAsync(context, 3);
        await CommitPageAsync(context, third.PageId, TextSeed(DocumentBoxId.New(), 0, "third page source"));
        // A current stored translation of page one in the run's target language becomes the previous window.
        Result<PageTranslationStatus> seeded = await context.Translations.PutPageBoxesAsync(
            context.DocumentId, context.PageId, await LatestRevisionAsync(context, context.PageId),
            0, new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [firstBox] = new TextBoxPayload("first page translated")
            }, targetLanguage: "en");
        seeded.IsSuccess.Should().BeTrue(seeded.ErrorMessage);

        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            [Target(second.PageId, 2)],
            context.Translations,
            [Target(context.PageId, 1), Target(second.PageId, 2), Target(third.PageId, 3)]);
        TranslationPageInput? issued = await fixture.Job.NextPageAsync();
        issued!.Page.PageIndex.Should().Be(2, "only the requested page is issued; the rest are context");

        TranslationContextSnapshot narrow = await fixture.Job.ReadContextAsync(issued, 0);
        narrow.Neighbors.Should().BeEmpty("radius zero reads no neighbours");
        narrow.PreviousStoredTranslation.Should().NotBeNull(
            "the previous page's stored translation is still part of the context");
        narrow.PreviousStoredTranslation!.Markdown.Should().Contain("first page translated");
        narrow.PreviousRunTranslation.Should().BeNull("run translations are read through the dedicated call");

        TranslationContextSnapshot wide = await fixture.Job.ReadContextAsync(issued, 1);
        wide.Neighbors.Select(neighbor => neighbor.Page.PageIndex).Should().Equal(1, 3);
        wide.Neighbors.Single(neighbor => neighbor.Page.PageIndex == 1).SourceText.Should()
            .Contain("first page source");

        // The first read of a neighbour is frozen: a newer commit is not observed mid-run.
        await CommitPageAsync(context, context.PageId, TextSeed(DocumentBoxId.New(), 0, "rewritten source"));
        TranslationContextSnapshot again = await fixture.Job.ReadContextAsync(issued, 1);
        again.Neighbors.Single(neighbor => neighbor.Page.PageIndex == 1).SourceText.Should()
            .Contain("first page source", "the frozen neighbour snapshot wins over the newer commit");
    }

    [Fact]
    public async Task Committed_page_translation_prefers_this_runs_commit_over_storage()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "first page source"));
        Page second = await CreatePageAsync(context, 2);
        await CommitPageAsync(context, second.PageId, TextSeed(DocumentBoxId.New(), 0, "second page source"));
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            Target(context.PageId, 1), Target(second.PageId, 2));

        TranslationPageInput? issued = await fixture.Job.NextPageAsync();
        TranslationPageCommit commit = await fixture.Job.CommitPageAsync(
            new TranslationPageResult(issued!.Page, TranslateAll(issued, "committed text")));

        commit.State.Should().Be(TranslationPageCommitState.Committed);
        TranslationContextTranslationSnapshot? snapshot =
            await fixture.Job.GetCommittedPageTranslationAsync(1);
        snapshot.Should().NotBeNull("a page committed inside this run is readable before any later read");
        snapshot!.Markdown.Should().Contain("committed text");
        (await fixture.Job.GetCommittedPageTranslationAsync(2)).Should().BeNull(
            "an uncommitted page has no translation in this run");
    }

    [Fact]
    public async Task Commits_of_foreign_unissued_or_settled_pages_are_rejected_without_resettling()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "page one source"));
        Page second = await CreatePageAsync(context, 2);
        await CommitPageAsync(context, second.PageId, TextSeed(DocumentBoxId.New(), 0, "page two source"));
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            Target(context.PageId, 1), Target(second.PageId, 2));

        TranslationPageInput? issued = await fixture.Job.NextPageAsync();
        // A page identity this job never issued, at an index outside the request, is foreign.
        TranslationPageCommit foreign = await fixture.Job.CommitPageAsync(new TranslationPageResult(
            new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(), DocumentTreeRevisionId.New(), 9),
            [new ExtensionBoxOutcome(DocumentBoxId.New(), ExtensionBoxOutcomeKind.Translated, "x")]));
        foreign.State.Should().Be(TranslationPageCommitState.Failed);
        foreign.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);

        // A requested page that was never handed out cannot be committed either.
        TranslationPageCommit unissued = await fixture.Job.CommitPageAsync(new TranslationPageResult(
            new ExtensionPageIdentity(context.DocumentId, second.PageId, DocumentTreeRevisionId.New(), 2),
            [new ExtensionBoxOutcome(DocumentBoxId.New(), ExtensionBoxOutcomeKind.Translated, "x")]));
        unissued.State.Should().Be(TranslationPageCommitState.Failed);
        unissued.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);

        // A settled page rejects a second commit and keeps its first outcome.
        TranslationPageCommit committed = await fixture.Job.CommitPageAsync(
            new TranslationPageResult(issued!.Page, TranslateAll(issued, "translated")));
        committed.State.Should().Be(TranslationPageCommitState.Committed);
        TranslationPageCommit again = await fixture.Job.CommitPageAsync(
            new TranslationPageResult(issued.Page, TranslateAll(issued, "second attempt")));
        again.State.Should().Be(TranslationPageCommitState.Failed);
        again.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);
        fixture.Published.Where(outcome => outcome.PageIndex == 1).Should().ContainSingle(
            "a settled page is never settled twice");
    }

    [Fact]
    public async Task Outcomes_for_unknown_or_untranslated_boxes_settle_failed_without_a_write()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        await context.CommitAsync(TextSeed(boxId, 0, "page one source"));
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            Target(context.PageId, 1));
        TranslationPageInput? issued = await fixture.Job.NextPageAsync();

        TranslationPageCommit commit = await fixture.Job.CommitPageAsync(new TranslationPageResult(
            issued!.Page,
            [
                new ExtensionBoxOutcome(DocumentBoxId.New(), ExtensionBoxOutcomeKind.Translated,
                    "a box the page does not own")
            ]));

        commit.State.Should().Be(TranslationPageCommitState.Failed);
        fixture.Published.Should().ContainSingle(outcome =>
            outcome.State == TranslationPageRunState.Failed && outcome.PageIndex == 1);
        (await ListRowsAsync(context, context.PageId)).Should().BeEmpty("nothing is written for a failed page");
    }

    [Fact]
    public async Task Conflicts_hand_the_reloaded_page_back_until_the_host_bound_settles_failed()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        await context.CommitAsync(TextSeed(boxId, 0, "page one source"));
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            Target(context.PageId, 1));
        TranslationPageInput? issued = await fixture.Job.NextPageAsync();

        // Each attempt loses the revision race once; the host reloads and hands the page back twice.
        TranslationPageInput current = issued!;
        for (int conflict = 1; conflict <= 2; conflict++)
        {
            await CommitPageAsync(context, context.PageId,
                TextSeed(boxId, 0, $"page one source v{conflict + 1}"));
            TranslationPageCommit conflicted = await fixture.Job.CommitPageAsync(new TranslationPageResult(
                current.Page,
                [new ExtensionBoxOutcome(boxId, ExtensionBoxOutcomeKind.Translated, $"attempt {conflict}")]));
            conflicted.State.Should().Be(TranslationPageCommitState.Conflict);
            conflicted.RetryPage.Should().NotBeNull();
            current = conflicted.RetryPage!;
        }

        // The third conflicting write exhausts the host conflict bound and settles the page failed.
        await CommitPageAsync(context, context.PageId, TextSeed(boxId, 0, "page one source v4"));
        TranslationPageCommit exhausted = await fixture.Job.CommitPageAsync(new TranslationPageResult(
            current.Page, [new ExtensionBoxOutcome(boxId, ExtensionBoxOutcomeKind.Translated, "attempt 3")]));

        exhausted.State.Should().Be(TranslationPageCommitState.Failed);
        exhausted.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        fixture.Published.Should().ContainSingle(outcome =>
            outcome.State == TranslationPageRunState.Failed && outcome.PageIndex == 1);
    }

    [Fact]
    public async Task Settle_remaining_reports_taken_pages_as_failed_and_untaken_pages_as_cancelled()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(TextSeed(DocumentBoxId.New(), 0, "page one source"));
        Page second = await CreatePageAsync(context, 2);
        await CommitPageAsync(context, second.PageId, TextSeed(DocumentBoxId.New(), 0, "page two source"));
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            Target(context.PageId, 1), Target(second.PageId, 2));

        (await fixture.Job.NextPageAsync()).Should().NotBeNull("page one is taken but never committed");
        fixture.Job.SettleRemaining(false);

        fixture.Published.Should().HaveCount(2);
        fixture.Published[0].State.Should().Be(TranslationPageRunState.Failed,
            "an extension that returns early fails the page it took");
        fixture.Published[1].State.Should().Be(TranslationPageRunState.Cancelled,
            "a page never taken is cancelled");

        (HostTranslationJob Job, List<TranslationPageOutcome> Published) cancelled = Job(context,
            Target(context.PageId, 1), Target(second.PageId, 2));
        (await cancelled.Job.NextPageAsync()).Should().NotBeNull();
        cancelled.Job.SettleRemaining(true);
        cancelled.Published.Should().OnlyContain(outcome => outcome.State == TranslationPageRunState.Cancelled);
    }

    [Fact]
    public async Task A_commit_inflight_when_cancelled_settles_exactly_once()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        await context.CommitAsync(TextSeed(boxId, 0, "page one source"));
        TaskCompletionSource commitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellingOnCommit translations = new(context.Translations, commitEntered, release);
        (HostTranslationJob Job, List<TranslationPageOutcome> Published) fixture = Job(context,
            [Target(context.PageId, 1)], translations);
        TranslationPageInput? issued = await fixture.Job.NextPageAsync();
        using CancellationTokenSource cancellation = new();

        Task<TranslationPageCommit> commit = fixture.Job
            .CommitPageAsync(new TranslationPageResult(issued!.Page,
                    [new ExtensionBoxOutcome(boxId, ExtensionBoxOutcomeKind.Translated, "translated")]),
                cancellation.Token)
            .AsTask();
        await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit.WaitAsync(TimeSpan.FromSeconds(10)));
        fixture.Job.SettleRemaining(true);

        // The cancelled commit may or may not have landed in storage, but the outcome slot is written once:
        // it can never be observed as both cancelled and committed.
        fixture.Published.Where(outcome => outcome.PageIndex == 1).Should().ContainSingle();
        fixture.Published[0].State.Should().Be(TranslationPageRunState.Cancelled);
    }

    private static (HostTranslationJob Job, List<TranslationPageOutcome> Published) Job(
        TranslationTestContext context,
        params TranslationPageTarget[] targets)
    {
        return Job(context, targets, context.Translations);
    }

    private static (HostTranslationJob Job, List<TranslationPageOutcome> Published) Job(
        TranslationTestContext context,
        TranslationPageTarget[] targets,
        IPageTranslationService translations,
        IReadOnlyList<TranslationPageTarget>? documentPages = null)
    {
        TranslationSettings settings = new()
        {
            TargetLanguage = "en",
            Concurrency = 4,
            MaxRetries = 0
        };
        TranslationRunRequest request = new(context.DocumentId, targets, settings,
            DocumentPages: documentPages);
        List<TranslationPageOutcome> published = [];
        HostTranslationJob job = new(context.Trees, translations, request, settings,
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0),
            new TranslationPageOutcome?[targets.Length], published.Add, null);
        return (job, published);
    }

    private static ExtensionBoxOutcome[] TranslateAll(TranslationPageInput page, string text)
    {
        return page.Boxes.Select(box =>
            new ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, text + " " + box.Text)).ToArray();
    }

    private static async Task<DocumentTreeRevisionId> LatestRevisionAsync(TranslationTestContext context,
        PageId pageId)
    {
        Result<DocumentTreeRevision> revision =
            await context.Trees.GetCurrentRevisionAsync(context.DocumentId, pageId);
        revision.IsSuccess.Should().BeTrue(revision.ErrorMessage);
        return revision.Value.TreeRevisionId;
    }

    private static NormalizedBBox BBox => new(0.01, 0.01, 0.5, 0.01);

    private static DocumentBoxSeed TextSeed(DocumentBoxId boxId, int sourceOrder, string markdown)
    {
        return new DocumentBoxSeed(boxId, null, sourceOrder, DocumentBoxType.Text, null, BBox,
            new TextBoxPayload(markdown));
    }

    private static TranslationPageTarget Target(PageId pageId, int pageIndex)
    {
        return new TranslationPageTarget(pageId, pageIndex);
    }

    private static async Task<Page> CreatePageAsync(TranslationTestContext context, int pageIndex)
    {
        PageService pages = new(context.ConnectionFactory, context.Clock);
        Result<Page> created = await pages.CreatePageAsync(context.DocumentId, pageIndex, $"{pageIndex + 1}",
            null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        return created.Value;
    }

    private static async Task<DocumentTreeRevision> CommitPageAsync(TranslationTestContext context,
        PageId pageId, params DocumentBoxSeed[] seeds)
    {
        Result<DocumentTreeRevision> working = await context.Trees.BeginWorkingRevisionAsync(
            context.DocumentId, pageId, seeds, DocumentTreeRevisionSource.Import);
        working.IsSuccess.Should().BeTrue(working.ErrorMessage);
        Result<DocumentTreeRevision> committed =
            await context.Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        committed.IsSuccess.Should().BeTrue(committed.ErrorMessage);
        return committed.Value;
    }

    private static async Task<IReadOnlyList<TranslationRow>> ListRowsAsync(TranslationTestContext context,
        PageId pageId)
    {
        await using SqliteConnection connection = context.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return
        [
            .. await connection.QueryAsync<TranslationRow>(
                "select box_id as BoxId, translated_md as TranslatedMd " +
                "from translation_boxes where page_id = @PageId order by ordinal;",
                new { PageId = pageId.ToString() })
        ];
    }

    private sealed class TranslationRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string TranslatedMd { get; set; } = string.Empty;
    }

    /// <summary>
    /// A page-translation service whose page write blocks until released and then honours the caller's
    /// cancellation token, standing in for a commit that is in flight when the run is cancelled.
    /// </summary>
    private sealed class CancellingOnCommit(
        IPageTranslationService inner,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IPageTranslationService
    {
        public Task<Result<PageXmlTranslation>> GetPageXmlAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, int pageIndex, CancellationToken cancellationToken = default)
        {
            return inner.GetPageXmlAsync(documentInstanceId, pageId, pageIndex, cancellationToken);
        }

        public Task<Result<PageTranslationStatus>> PutPageXmlAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, int pageIndex, string xml, CancellationToken cancellationToken = default,
            string? targetLanguage = null)
        {
            return inner.PutPageXmlAsync(documentInstanceId, pageId, pageIndex, xml, cancellationToken,
                targetLanguage);
        }

        public Task<Result<PageTranslationStatus>> PutBoxPayloadsAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, DocumentTreeRevisionId expectedRevision, int expectedTranslationVersion,
            IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default, string? targetLanguage = null)
        {
            return inner.PutBoxPayloadsAsync(documentInstanceId, pageId, expectedRevision,
                expectedTranslationVersion, replacements, cancellationToken, targetLanguage);
        }

        public async Task<Result<PageTranslationStatus>> PutPageBoxesAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, DocumentTreeRevisionId expectedRevision,
            int expectedTranslationVersion, IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
            CancellationToken cancellationToken = default, string? targetLanguage = null)
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await inner.PutPageBoxesAsync(documentInstanceId, pageId, expectedRevision,
                expectedTranslationVersion, replacements, cancellationToken, targetLanguage);
        }

        public Task<TranslatedPageMarkdown?> GetPageTranslationAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, CancellationToken cancellationToken = default)
        {
            return inner.GetPageTranslationAsync(documentInstanceId, pageId, cancellationToken);
        }

        public Task<Result<PageTranslationStatus>> PutPageTranslationAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, string markdown,
            CancellationToken cancellationToken = default)
        {
            return inner.PutPageTranslationAsync(documentInstanceId, pageId, markdown, cancellationToken);
        }
    }
}
