using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Coordinates;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrLifecycleBoxTreeTests
{
    [Fact]
    public async Task Document_ocr_creates_working_revisions_and_explicit_commit_makes_them_current()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;

        Result<OcrRun> run = await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Completed);
        IReadOnlyList<OcrPageResult> results =
            (await context.Coordinator.ListPageResultsAsync(run.Value.OcrRunId)).Value;
        results.Should().HaveCount(2).And.OnlyContain(result =>
            result.State == OcrPageResultState.Succeeded && result.WorkingTreeRevisionId.HasValue);
        foreach (Page page in context.Pages)
        {
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).IsFailure
                .Should().BeTrue();
        }

        Result<OcrCandidateCommit> commit = await context.Coordinator.CommitCandidateRunAsync(run.Value.OcrRunId);

        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);
        commit.Value.CommittedTreeRevisionIds.Should().HaveCount(2);
        foreach (Page page in context.Pages)
        {
            DocumentTreeRevision current =
                (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).Value;
            current.Status.Should().Be(DocumentTreeRevisionStatus.Committed);
            current.IsCurrent.Should().BeTrue();
            current.Source.Should().Be(DocumentTreeRevisionSource.Import);
            commit.Value.CommittedTreeRevisionIds.Should().Contain(current.TreeRevisionId);

            OcrPageResult pageResult = results.Single(result => result.PageId == page.PageId);
            pageResult.WorkingTreeRevisionId.Should().Be(current.TreeRevisionId);
        }
    }

    [Fact]
    public async Task Local_document_ocr_reports_progress_after_each_physical_page()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        RecordingProgress<OcrTaskStageProgress> progress = new();

        Result<OcrRun> run = await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId, progress: progress);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        progress.Values.Should().Equal(
            new OcrTaskStageProgress(OcrTaskStage.Recognizing, 0, "pages:0/2"),
            new OcrTaskStageProgress(OcrTaskStage.Recognizing, 0.5, "pages:1/2"),
            new OcrTaskStageProgress(OcrTaskStage.Recognizing, 1, "pages:2/2"));
    }

    [Fact]
    public async Task Partially_failed_ocr_can_commit_only_successful_page_candidates()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Partially failing mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null,
            "{\"failPages\":[1]}", false)).Value;

        OcrRun run = (await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        IReadOnlyList<OcrPageResult> results =
            (await context.Coordinator.ListPageResultsAsync(run.OcrRunId)).Value;
        OcrPageResult succeeded = results.Single(result => result.State == OcrPageResultState.Succeeded);
        OcrPageResult failed = results.Single(result => result.State == OcrPageResultState.Failed);

        run.State.Should().Be(OcrRunState.CompletedWithErrors);
        (await context.Coordinator.CommitCandidateRunAsync(run.OcrRunId, [failed.PageId])).IsFailure.Should().BeTrue();
        Result<OcrCandidateCommit> commit =
            await context.Coordinator.CommitCandidateRunAsync(run.OcrRunId, [succeeded.PageId]);

        commit.IsSuccess.Should().BeTrue(commit.ErrorMessage);
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, succeeded.PageId)).IsSuccess
            .Should().BeTrue();
        (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, failed.PageId)).IsFailure
            .Should().BeTrue();
    }

    [Fact]
    public async Task Apply_on_success_commits_completed_run_through_candidate_commit()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Auto apply", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", true)).Value;

        OcrRun run = (await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;

        run.State.Should().Be(OcrRunState.Completed);
        foreach (Page page in context.Pages)
        {
            DocumentTreeRevision current =
                (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).Value;
            current.Source.Should().Be(DocumentTreeRevisionSource.Import);
        }
    }

    [Fact]
    public async Task Source_bbox_is_converted_before_working_revision()
    {
        await using Context context = await Context.CreateAsync(new PixelBBoxEngine(), true);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Pixel bbox", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;

        OcrRun run = (await context.Coordinator.RunPresetOnPagesAsync(
            context.Document.DocumentInstanceId, preset.PresetId, [context.Pages[0].PageId])).Value;
        OcrPageResult pageResult = (await context.Coordinator.ListPageResultsAsync(run.OcrRunId)).Value.Single();
        DocumentBox box = (await context.Trees.ListBoxesAsync(pageResult.WorkingTreeRevisionId!.Value)).Value.Single();

        box.BBox.Should().Be(new NormalizedBBox(.1, .1, .3, .2));
    }

    [Fact]
    public async Task Post_run_exception_fails_run_and_terminalizes_page_results()
    {
        await using Context context = await Context.CreateAsync(treeImporter: new ThrowingTreeImporter());
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Throwing importer", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;

        OcrRun run = (await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;

        run.State.Should().Be(OcrRunState.Failed);
        (await context.Coordinator.ListPageResultsAsync(run.OcrRunId)).Value.Should()
            .OnlyContain(result => result.State == OcrPageResultState.Failed &&
                                   result.ErrorMessage!.Contains("working exploded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_a_pending_run_preserves_the_current_box_trees()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Pending mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun pending = (await context.Coordinator.CreatePendingRunForTestAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;

        (await context.Coordinator.CancelRunAsync(pending.OcrRunId)).IsSuccess.Should().BeTrue();
        OcrRun cancelled = (await context.Coordinator.GetRunAsync(pending.OcrRunId)).Value;

        cancelled.State.Should().Be(OcrRunState.Cancelled);
        foreach (Page page in context.Pages)
        {
            (await context.Trees.GetCurrentRevisionAsync(context.Document.DocumentInstanceId, page.PageId)).IsFailure
                .Should().BeTrue();
        }
    }

    [Fact]
    public async Task Startup_reconciliation_fails_interrupted_runs_and_page_results()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Reconcile", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRun completed = (await context.Coordinator.RunPresetOnDocumentAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        completed.State.Should().Be(OcrRunState.Completed);
        OcrRun pending = (await context.Coordinator.CreatePendingRunForTestAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        OcrRun running = (await context.Coordinator.CreatePendingRunForTestAsync(
            context.Document.DocumentInstanceId, preset.PresetId)).Value;
        string now = DateTimeOffset.UtcNow.ToString("O");
        await context.ExecuteAsync(
            """
            update ocr_runs set state = 'running' where ocr_run_id = @RunId;
            insert into ocr_page_results (
                result_id, ocr_run_id, page_id, state, working_tree_revision_id,
                error_code, error_message, created_at, updated_at)
            values (@ResultId, @RunId, @PageId, @State, null, null, null, @Now, @Now);
            """,
            new
            {
                RunId = running.OcrRunId.ToString(),
                ResultId = OcrPageResultId.New().ToString(),
                PageId = context.Pages[0].PageId.ToString(),
                State = OcrPageResultState.Processing,
                Now = now
            });
        await context.ExecuteAsync(
            """
            insert into ocr_page_results (
                result_id, ocr_run_id, page_id, state, working_tree_revision_id,
                error_code, error_message, created_at, updated_at)
            values (@ResultId, @RunId, @PageId, @State, null, null, null, @Now, @Now);
            """,
            new
            {
                RunId = pending.OcrRunId.ToString(),
                ResultId = OcrPageResultId.New().ToString(),
                PageId = context.Pages[0].PageId.ToString(),
                State = OcrPageResultState.Pending,
                Now = now
            });

        Result reconcile = await context.Coordinator.ReconcileInterruptedRunsAsync();

        reconcile.IsSuccess.Should().BeTrue(reconcile.ErrorMessage);
        (await context.Coordinator.GetRunAsync(pending.OcrRunId)).Value.State.Should().Be(OcrRunState.Failed);
        (await context.Coordinator.GetRunAsync(running.OcrRunId)).Value.State.Should().Be(OcrRunState.Failed);
        (await context.Coordinator.GetRunAsync(completed.OcrRunId)).Value.State.Should()
            .Be(OcrRunState.Completed);
        (await context.Coordinator.ListPageResultsAsync(running.OcrRunId)).Value.Should().OnlyContain(result =>
            result.State == OcrPageResultState.Failed && result.ErrorCode == OcrFailureCode.Interrupted);
        (await context.Coordinator.ListPageResultsAsync(pending.OcrRunId)).Value.Should().OnlyContain(result =>
            result.State == OcrPageResultState.Failed && result.ErrorCode == OcrFailureCode.Interrupted);
        (await context.Coordinator.ListPageResultsAsync(completed.OcrRunId)).Value.Should().OnlyContain(result =>
            result.State == OcrPageResultState.Succeeded);
    }

    [Fact]
    public async Task Region_candidate_is_ephemeral_and_does_not_create_an_ocr_run_or_working_tree()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Local candidate", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        NormalizedBBox region = new(.2, .3, .4, .2);

        Result<OcrRegionCandidate> candidate = await context.Coordinator.RecognizeRegionCandidateAsync(
            context.Document.DocumentInstanceId, preset.PresetId, context.Pages[0].PageId, region);

        candidate.IsSuccess.Should().BeTrue(candidate.ErrorMessage);
        candidate.Value.BBox.Should().Be(region);
        candidate.Value.Payload.Should().BeOfType<TextBoxPayload>();
        (await context.CountAsync("select count(1) from ocr_runs;")).Should().Be(0);
        (await context.CountAsync("select count(1) from document_tree_revisions;")).Should().Be(0);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstance document,
            IReadOnlyList<Page> pages,
            IDocumentTreeService trees,
            IOcrPresetService presets,
            OcrRunEngine coordinator)
        {
            _database = database;
            Document = document;
            Pages = pages;
            Trees = trees;
            Presets = presets;
            Coordinator = coordinator;
        }

        public DocumentInstance Document { get; }
        public IReadOnlyList<Page> Pages { get; }
        public IDocumentTreeService Trees { get; }
        public IOcrPresetService Presets { get; }
        public OcrRunEngine Coordinator { get; }

        public async Task<int> CountAsync(string sql)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(sql);
        }

        public async Task ExecuteAsync(string sql, object parameters)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql, parameters);
        }

        public static async Task<Context> CreateAsync(IOcrEngine? engine = null, bool configureCoordinates = false,
            IOcrDocumentTreeImporter? treeImporter = null)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("OCR lifecycle");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "OCR lifecycle")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page first = (await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            Page second = (await pages.CreatePageAsync(document.DocumentInstanceId, 1, "2", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            IDocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            SearchUnitBuilder search = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
            OcrRunEngine coordinator = new(
                database.ConnectionFactory,
                clock,
                engine ?? new MockOcrEngine(),
                search,
                treeImporter ?? new OcrDocumentTreeImporter(trees),
                pageCoordinateService: configureCoordinates
                    ? new PageCoordinateService(database.ConnectionFactory)
                    : null);
            return new Context(
                database,
                document,
                [first, second],
                trees,
                new OcrPresetService(database.ConnectionFactory, libraries, clock),
                coordinator);
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }

    private sealed class PixelBBoxEngine : IOcrEngine
    {
        public string EngineId => OcrEngineIds.Mock;

        public Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new OcrEnginePageResult(page.PageId, true, "pixels",
                new NormalizedBBox(0, 0, 1, 1), null, null,
                new SourceBBox(10, 20, 30, 40, SourceBBoxCoordinateSystem.ImagePixels, 100, 200)));
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }

    private sealed class ThrowingTreeImporter : IOcrDocumentTreeImporter
    {
        public Task<Result<OcrDocumentTreeImportResult>> BeginWorkingAsync(OcrDocumentTreeImportRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("working exploded");
        }

        public Task<Result<IReadOnlyList<DocumentTreeRevisionId>>> CommitAsync(
            IReadOnlyList<DocumentTreeRevisionId> workingRevisionIds,
            DocumentCommitId? commitId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
