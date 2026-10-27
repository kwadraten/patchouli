using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrQueueProjectionTests
{
    [Fact]
    public async Task Queued_tasks_never_borrow_progress_or_outcome_from_same_document_runs()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "Scope Mix");
        PageId[] pages = await CreatePagesAsync(context, documentId, 50);

        // A run in flight for this document: 32 of its own 50 page results succeeded.
        OcrRunId runningRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Running,
            pages.Select((page, index) => (page, index < 32
                ? OcrPageResultState.Succeeded
                : OcrPageResultState.Pending)).ToArray());

        // A completed historical run with a committed+indexed body; no task references it.
        DocumentTreeRevisionId historyRevision = DocumentTreeRevisionId.New();
        OcrRunId historyRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(pages.Select(page => page.ToString())),
            JsonSerializer.Serialize(new[] { historyRevision.ToString() }));
        await context.SeedRevisionAsync(documentId, pages[0], historyRevision, true);
        await context.SetIndexStatusAsync(documentId, "current");

        // Bind at real execution start, while recognition is still in flight. An incomplete
        // 32/50 run is not a valid retained-run adoption retry.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Executor.BindRunId = runningRun;
        context.Executor.Handler = async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return new OcrQueueExecutionResult(false, true);
        };
        Result<OcrQueueTask> attempt = await context.Scheduler.EnqueueMockPagesAsync(documentId,
            OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        try
        {
            await context.Scheduler.RunOneSchedulingTickAsync();
            await context.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Result<OcrQueueTask> scoped46 = await context.Scheduler.EnqueueMockPagesAsync(documentId,
                OcrPresetId.New(), pages.Take(46).ToArray(), OcrQueuePriority.UserStartedDocument);
            Result<OcrQueueTask> scoped50 = await context.Scheduler.EnqueueMockPagesAsync(documentId,
                OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);

            Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync();

            rows.IsSuccess.Should().BeTrue(rows.ErrorMessage);
            Dictionary<OcrQueueTaskId, OcrQueueRow> byTask = rows.Value.ToDictionary(row => row.TaskId);
            OcrQueueRow running = byTask[attempt.Value.TaskId];
            running.State.Should().Be(OcrQueueTaskState.Running);
            running.PageProgress.Should().Be(new OcrQueueProgress(32, 0, 0, 50));
            running.Outcome.Should().NotBeNull();
            running.Outcome!.RecognitionState.Should().Be(OcrRunState.Running);

            OcrQueueRow narrow = byTask[scoped46.Value.TaskId];
            narrow.State.Should().Be(OcrQueueTaskState.Queued);
            narrow.PageCount.Should().Be(46);
            narrow.PageProgress.Should().BeNull("a queued task with no linked run shows no pages");
            narrow.Outcome.Should().BeNull();

            OcrQueueRow wide = byTask[scoped50.Value.TaskId];
            wide.State.Should().Be(OcrQueueTaskState.Queued);
            wide.PageCount.Should().Be(50);
            wide.PageProgress.Should().BeNull("the running and historical runs are not its runs");
            wide.Outcome.Should().BeNull("the historical run's committed body must not leak in");
        }
        finally
        {
            release.TrySetResult();
            await context.Scheduler.CancelTaskAsync(attempt.Value.TaskId);
        }
    }

    [Fact]
    public async Task Queued_task_next_to_completed_history_shows_no_progress_and_no_outcome()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "History");
        PageId[] pages = await CreatePagesAsync(context, documentId, 3);
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        OcrRunId completed = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(pages.Select(page => page.ToString())),
            JsonSerializer.Serialize(new[] { revision.ToString() }));
        await context.SeedRevisionAsync(documentId, pages[0], revision, true);
        await context.SetIndexStatusAsync(documentId, "current");

        Result<OcrQueueTask> queued = await context.Scheduler.EnqueueMockPagesAsync(documentId,
            OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync();

        OcrQueueRow row = rows.Value.Should().ContainSingle().Subject;
        row.TaskId.Should().Be(queued.Value.TaskId);
        row.State.Should().Be(OcrQueueTaskState.Queued);
        row.PageProgress.Should().BeNull("a completed same-document run is not this task's run");
        row.Outcome.Should().BeNull("the historical run's outcome is not this task's outcome");
    }

    [Fact]
    public async Task Recognition_failure_retry_starts_without_the_failed_attempt_run()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "Retry");
        PageId[] pages = await CreatePagesAsync(context, documentId, 2);
        OcrRunId failedRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Failed,
            [(pages[0], OcrPageResultState.Failed), (pages[1], OcrPageResultState.Pending)]);

        // Pages failed during recognition, so the retry must not keep the failed run.
        context.Executor.Handler = (_, _) => Task.FromResult(new OcrQueueExecutionResult(
            false, false, "ocr_error", "recognition failed", failedRun, 0, 2));
        Result<OcrQueueTask> attempt = await context.Scheduler.EnqueueMockPagesAsync(documentId,
            OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, attempt.Value.TaskId, OcrQueueTaskState.Failed);

        Result<OcrQueueTask> retry = await context.Scheduler.RetryTaskAsync(attempt.Value.TaskId);
        retry.Value.RunId.Should().BeNull("recognition failures drop the failed attempt's run");

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync();

        OcrQueueRow row = rows.Value.Should().ContainSingle(row => row.TaskId == retry.Value.TaskId)
            .Subject;
        row.State.Should().Be(OcrQueueTaskState.Queued);
        row.PageProgress.Should().BeNull("the failed attempt's page results are not new progress");
        row.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task Retained_run_retry_keeps_recognition_results_and_true_outcome()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "Index Retry");
        PageId[] pages = await CreatePagesAsync(context, documentId, 3);
        DocumentTreeRevisionId[] revisions =
            [DocumentTreeRevisionId.New(), DocumentTreeRevisionId.New(), DocumentTreeRevisionId.New()];
        OcrRunId committedRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(pages.Select(page => page.ToString())),
            JsonSerializer.Serialize(revisions.Select(id => id.ToString())));
        for (int i = 0; i < pages.Length; i++)
        {
            await context.SeedRevisionAsync(documentId, pages[i], revisions[i], true);
        }

        // Recognition and commit succeeded; only the index rebuild failed afterwards.
        await context.SetIndexStatusAsync(documentId, "failed");

        context.Executor.Handler = (_, _) => Task.FromResult(new OcrQueueExecutionResult(
            false, false, "index_error", "index rebuild failed", committedRun, 3, 0));
        Result<OcrQueueTask> attempt = await context.Scheduler.EnqueueMockPagesAsync(documentId,
            OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, attempt.Value.TaskId, OcrQueueTaskState.Failed);
        Result<OcrQueueTask> retry = await context.Scheduler.RetryTaskAsync(attempt.Value.TaskId);
        retry.Value.RunId.Should().Be(committedRun);

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync();

        OcrQueueRow row = rows.Value.Should().ContainSingle(row => row.TaskId == retry.Value.TaskId)
            .Subject;
        row.State.Should().Be(OcrQueueTaskState.Queued);
        row.PageProgress.Should().Be(new OcrQueueProgress(3, 0, 0, 3),
            "an adoption/index retry keeps the retained run's recognition counters");
        row.Outcome.Should().Be(new OcrQueueOutcome(OcrRunState.Completed, true, 3, "failed"),
            "the durable outcome is preserved, not zeroed and not masked as indexed");
    }

    [Fact]
    public async Task Tasks_match_only_their_own_run_across_engines_and_attempts()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "Two Engines");
        PageId[] pages = await CreatePagesAsync(context, documentId, 4);

        OcrRunId engineARun = await context.SeedRunAsync(documentId, "engine-a",
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray());
        OcrRunId engineBRun = await context.SeedRunAsync(documentId, "engine-b",
            OcrRunState.Failed,
            pages.Select(page => (page, OcrPageResultState.Failed)).ToArray());

        Dictionary<OcrQueueTaskId, OcrQueueExecutionResult> results = [];
        context.Executor.Handler = (task, _) => Task.FromResult(results[task.TaskId]);
        Result<OcrQueueTask> taskA = await context.Scheduler.EnqueueAsync(new OcrQueueTaskRequest(
            documentId, OcrPresetId.New(), pages, OcrQueueTaskKind.Document, "engine-a",
            OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument));
        results[taskA.Value.TaskId] = new OcrQueueExecutionResult(true, false, RunId: engineARun,
            CompletedPageCount: 4);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, taskA.Value.TaskId, OcrQueueTaskState.Succeeded);
        Result<OcrQueueTask> taskB = await context.Scheduler.EnqueueAsync(new OcrQueueTaskRequest(
            documentId, OcrPresetId.New(), pages, OcrQueueTaskKind.Document, "engine-b",
            OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument));
        results[taskB.Value.TaskId] = new OcrQueueExecutionResult(false, false, "ocr_error", "x",
            engineBRun, 0, 4);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, taskB.Value.TaskId, OcrQueueTaskState.Failed);

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync(true);

        Dictionary<OcrQueueTaskId, OcrQueueRow> byTask = rows.Value.ToDictionary(row => row.TaskId);
        OcrQueueRow rowA = byTask[taskA.Value.TaskId];
        rowA.State.Should().Be(OcrQueueTaskState.Succeeded);
        rowA.PageProgress.Should().Be(new OcrQueueProgress(4, 0, 0, 4));
        rowA.Outcome!.RecognitionState.Should().Be(OcrRunState.Completed);

        OcrQueueRow rowB = byTask[taskB.Value.TaskId];
        rowB.State.Should().Be(OcrQueueTaskState.Failed);
        rowB.PageProgress.Should().Be(new OcrQueueProgress(0, 4, 0, 4));
        rowB.Outcome!.RecognitionState.Should().Be(OcrRunState.Failed);
        rowB.Outcome.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task Superseded_adoption_reports_superseded_instead_of_current_document_index()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "Superseded");
        PageId[] pages = await CreatePagesAsync(context, documentId, 2);

        // The adopted revision of page 0 was replaced by a newer committed revision.
        DocumentTreeRevisionId stale = DocumentTreeRevisionId.New();
        DocumentTreeRevisionId replacement = DocumentTreeRevisionId.New();
        DocumentTreeRevisionId current = DocumentTreeRevisionId.New();
        await context.SeedRevisionAsync(documentId, pages[0], stale, false);
        await context.SeedRevisionAsync(documentId, pages[0], replacement, true);
        await context.SeedRevisionAsync(documentId, pages[1], current, true);
        await context.SetIndexStatusAsync(documentId, "current");

        OcrRunId supersededRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(pages.Select(page => page.ToString())),
            JsonSerializer.Serialize(new[]
                { stale.ToString(), current.ToString() }));
        OcrRunId indexedRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(pages.Select(page => page.ToString())),
            JsonSerializer.Serialize(new[]
                { replacement.ToString(), current.ToString() }));

        Dictionary<OcrQueueTaskId, OcrQueueExecutionResult> results = [];
        context.Executor.Handler = (task, _) => Task.FromResult(results[task.TaskId]);
        Result<OcrQueueTask> supersededTask = await context.Scheduler.EnqueueMockPagesAsync(
            documentId, OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        results[supersededTask.Value.TaskId] = new OcrQueueExecutionResult(true, false,
            RunId: supersededRun, CompletedPageCount: 2);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, supersededTask.Value.TaskId,
            OcrQueueTaskState.Succeeded);
        Result<OcrQueueTask> indexedTask = await context.Scheduler.EnqueueMockPagesAsync(
            documentId, OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        results[indexedTask.Value.TaskId] = new OcrQueueExecutionResult(true, false,
            RunId: indexedRun, CompletedPageCount: 2);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, indexedTask.Value.TaskId,
            OcrQueueTaskState.Succeeded);

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync(true);

        Dictionary<OcrQueueTaskId, OcrQueueRow> byTask = rows.Value.ToDictionary(row => row.TaskId);
        byTask[supersededTask.Value.TaskId].Outcome.Should().Be(
            new OcrQueueOutcome(OcrRunState.Completed, true, 2, "superseded"),
            "an adopted revision that is no longer current must not borrow the live index");
        byTask[indexedTask.Value.TaskId].Outcome.Should().Be(
            new OcrQueueOutcome(OcrRunState.Completed, true, 2, "indexed"));
    }

    [Fact]
    public async Task Committed_run_without_index_row_reports_not_indexed_not_success()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "No Index Row");
        PageId[] pages = await CreatePagesAsync(context, documentId, 2);
        DocumentTreeRevisionId[] revisions = [DocumentTreeRevisionId.New(), DocumentTreeRevisionId.New()];
        OcrRunId run = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(pages.Select(page => page.ToString())),
            JsonSerializer.Serialize(revisions.Select(id => id.ToString())));
        for (int i = 0; i < pages.Length; i++)
        {
            await context.SeedRevisionAsync(documentId, pages[i], revisions[i], true);
        }

        context.Executor.Handler = (_, _) => Task.FromResult(new OcrQueueExecutionResult(
            true, false, RunId: run, CompletedPageCount: 2));
        Result<OcrQueueTask> task = await context.Scheduler.EnqueueMockPagesAsync(documentId,
            OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, task.Value.TaskId, OcrQueueTaskState.Succeeded);

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync(true);

        rows.Value.Single(row => row.TaskId == task.Value.TaskId).Outcome.Should().Be(
            new OcrQueueOutcome(OcrRunState.Completed, true, 2, "not_indexed"),
            "a missing search_index_status row is not fabricated into an indexed success");
    }

    [Fact]
    public async Task Malformed_adoption_json_is_tolerated_with_parse_as_empty_semantics()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "Malformed");
        PageId[] pages = await CreatePagesAsync(context, documentId, 4);
        DocumentTreeRevisionId current = DocumentTreeRevisionId.New();
        await context.SeedRevisionAsync(documentId, pages[0], current, true);
        await context.SetIndexStatusAsync(documentId, "current");

        // Neither payload parses as JSON: committed page count and superseded fall back to empty.
        OcrRunId broken = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            [(pages[0], OcrPageResultState.Succeeded)],
            "not-json", "{oops");
        // Valid JSON that is not a string array is still not a revision list.
        OcrRunId scalar = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            [(pages[1], OcrPageResultState.Succeeded)],
            JsonSerializer.Serialize(new[] { pages[1].ToString() }),
            "123");
        // A mixed-type array is not a string list either.
        OcrRunId mixed = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            [(pages[2], OcrPageResultState.Succeeded)],
            JsonSerializer.Serialize(new[] { pages[2].ToString() }),
            $"[\"{current}\",7]");
        // A JSON null element cannot be a current revision, so the adoption is superseded.
        OcrRunId nullEntry = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            [(pages[3], OcrPageResultState.Succeeded)],
            JsonSerializer.Serialize(new[] { pages[3].ToString() }),
            $"[\"{current}\",null]");

        Dictionary<OcrQueueTaskId, OcrQueueExecutionResult> results = [];
        context.Executor.Handler = (task, _) => Task.FromResult(results[task.TaskId]);
        List<OcrQueueTaskId> taskOrder = [];
        foreach (OcrRunId run in new[] { broken, scalar, mixed, nullEntry })
        {
            Result<OcrQueueTask> task = await context.Scheduler.EnqueueMockPagesAsync(documentId,
                OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
            results[task.Value.TaskId] = new OcrQueueExecutionResult(true, false, RunId: run,
                CompletedPageCount: 1);
            taskOrder.Add(task.Value.TaskId);
            await context.Scheduler.RunOneSchedulingTickAsync();
            await WaitForStateAsync(context.Scheduler, task.Value.TaskId, OcrQueueTaskState.Succeeded);
        }

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync(true);

        rows.IsSuccess.Should().BeTrue(rows.ErrorMessage);
        OcrQueueOutcome?[] outcomes = taskOrder
            .Select(id => rows.Value.Single(row => row.TaskId == id).Outcome).ToArray();
        outcomes[0].Should().Be(new OcrQueueOutcome(OcrRunState.Completed, false, 0, null),
            "unparseable JSON is an empty adoption, not a crash or a fabricated commit");
        outcomes[1].Should().Be(new OcrQueueOutcome(OcrRunState.Completed, true, 1, "indexed"),
            "a scalar is not a revision list, so nothing counts as superseded");
        outcomes[2].Should().Be(new OcrQueueOutcome(OcrRunState.Completed, true, 1, "indexed"),
            "a mixed array is not a string list, so nothing counts as superseded");
        outcomes[3].Should().Be(new OcrQueueOutcome(OcrRunState.Completed, true, 1, "superseded"),
            "a null element is a well-formed entry that no current revision matches");
    }

    [Fact]
    public async Task Large_adoption_sets_project_through_single_collection_parameters()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "Large");
        PageId[] pages = await CreatePagesAsync(context, documentId, 2);

        // ~40k adopted revision references: expanding them into IN variables would exceed the
        // SQLite variable limit outright and bound quadratically before that.
        string[] absentRevisions = Enumerable.Range(0, 40_000)
            .Select(index => $"absent-{index}").ToArray();
        OcrRunId supersededRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(Enumerable.Range(0, 40_000)
                .Select(index => $"page-{index}")),
            JsonSerializer.Serialize(absentRevisions));

        // A large adopted set that stayed current still reports indexed, not superseded.
        const int currentCount = 2_000;
        string[] currentRevisions =
            await context.SeedCurrentRevisionSetAsync(documentId, 10_000,
                currentCount);
        OcrRunId indexedRun = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Completed,
            pages.Select(page => (page, OcrPageResultState.Succeeded)).ToArray(),
            JsonSerializer.Serialize(Enumerable.Range(0, currentCount)
                .Select(index => $"page-{index}")),
            JsonSerializer.Serialize(currentRevisions));
        await context.SetIndexStatusAsync(documentId, "current");

        Dictionary<OcrQueueTaskId, OcrQueueExecutionResult> results = [];
        context.Executor.Handler = (task, _) => Task.FromResult(results[task.TaskId]);
        Result<OcrQueueTask> supersededTask = await context.Scheduler.EnqueueMockPagesAsync(
            documentId, OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        results[supersededTask.Value.TaskId] = new OcrQueueExecutionResult(true, false,
            RunId: supersededRun, CompletedPageCount: 2);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, supersededTask.Value.TaskId,
            OcrQueueTaskState.Succeeded);
        Result<OcrQueueTask> indexedTask = await context.Scheduler.EnqueueMockPagesAsync(
            documentId, OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        results[indexedTask.Value.TaskId] = new OcrQueueExecutionResult(true, false,
            RunId: indexedRun, CompletedPageCount: 2);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, indexedTask.Value.TaskId,
            OcrQueueTaskState.Succeeded);

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync(true);

        rows.IsSuccess.Should().BeTrue(rows.ErrorMessage);
        Dictionary<OcrQueueTaskId, OcrQueueRow> byTask = rows.Value.ToDictionary(row => row.TaskId);
        byTask[supersededTask.Value.TaskId].Outcome.Should().Be(
            new OcrQueueOutcome(OcrRunState.Completed, true, 40_000, "superseded"));
        byTask[indexedTask.Value.TaskId].Outcome.Should().Be(
            new OcrQueueOutcome(OcrRunState.Completed, true, currentCount, "indexed"));
    }

    [Fact]
    public async Task Page_result_states_map_to_progress_buckets()
    {
        await using TestContext context = await CreateContextAsync();
        DocumentInstanceId documentId = await CreateDocumentAsync(context, "States");
        PageId[] pages = await CreatePagesAsync(context, documentId, 7);
        OcrRunId run = await context.SeedRunAsync(documentId, OcrEngineIds.Mock,
            OcrRunState.Running,
            [
                (pages[0], OcrPageResultState.Succeeded),
                (pages[1], OcrPageResultState.Succeeded),
                (pages[2], OcrPageResultState.Failed),
                (pages[3], OcrPageResultState.Skipped),
                (pages[4], OcrPageResultState.Cancelled),
                (pages[5], OcrPageResultState.Pending),
                (pages[6], OcrPageResultState.Processing)
            ]);

        context.Executor.Handler = (_, _) => Task.FromResult(new OcrQueueExecutionResult(
            false, false, "index_error", "x", run, 2, 0));
        Result<OcrQueueTask> task = await context.Scheduler.EnqueueMockPagesAsync(documentId,
            OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);
        await context.Scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(context.Scheduler, task.Value.TaskId, OcrQueueTaskState.Failed);

        Result<IReadOnlyList<OcrQueueRow>> rows = await context.Rows.ListRowsAsync(true);

        rows.Value.Single(row => row.TaskId == task.Value.TaskId).PageProgress.Should()
            .Be(new OcrQueueProgress(2, 3, 1, 7));
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateMigratedAsync();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        Result<LibraryMetadata> createdLibrary = await library.CreateLibraryAsync("Projection Tests");
        DelegateExecutor executor = new();
        OcrQueueScheduler scheduler = new(createdLibrary.Value.LibraryId, clock, executor);
        return new TestContext(
            database,
            clock,
            createdLibrary.Value.LibraryId,
            new ItemService(database.ConnectionFactory, library, clock),
            new DocumentInstanceService(database.ConnectionFactory, clock),
            new PageService(database.ConnectionFactory, clock),
            scheduler,
            new OcrQueueRowService(scheduler, database.ConnectionFactory),
            executor);
    }

    private static async Task<DocumentInstanceId> CreateDocumentAsync(TestContext context, string title)
    {
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", title);
        Result<DocumentInstance> document = await context.Documents.AttachDocumentInstanceAsync(
            item.Value.ItemId, null, DocumentInstanceType.PrimaryScan, makePrimary: true);
        return document.Value.DocumentInstanceId;
    }

    private static async Task<PageId[]> CreatePagesAsync(TestContext context, DocumentInstanceId documentId,
        int count)
    {
        PageId[] pages = new PageId[count];
        for (int index = 0; index < count; index++)
        {
            Result<Page> page = await context.Pages.CreatePageAsync(documentId, index,
                (index + 1).ToString(), null, null, 0, CoordinateBasis.NormalizedPage, null, null,
                "renderer-v1", null);
            page.IsSuccess.Should().BeTrue(page.ErrorMessage);
            pages[index] = page.Value.PageId;
        }

        return pages;
    }

    /// <summary>
    ///     The scheduler completes claimed work on a background task; poll for the expected state
    ///     instead of WaitForIdleAsync, which stays busy while any other task sits queued.
    /// </summary>
    private static async Task WaitForStateAsync(OcrQueueScheduler scheduler, OcrQueueTaskId taskId,
        string state)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            Result<OcrQueueTask> task = await scheduler.GetTaskAsync(taskId);
            if (task.IsSuccess && task.Value.State == state)
            {
                return;
            }

            await Task.Delay(10);
        }

        Result<OcrQueueTask> final = await scheduler.GetTaskAsync(taskId);
        final.Value.State.Should().Be(state);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId,
            ItemService items, DocumentInstanceService documents, PageService pages,
            OcrQueueScheduler scheduler, OcrQueueRowService rows, DelegateExecutor executor)
        {
            Database = database;
            Clock = clock;
            LibraryId = libraryId;
            Items = items;
            Documents = documents;
            Pages = pages;
            Scheduler = scheduler;
            Rows = rows;
            Executor = executor;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public ItemService Items { get; }
        public DocumentInstanceService Documents { get; }
        public PageService Pages { get; }
        public OcrQueueScheduler Scheduler { get; }
        public OcrQueueRowService Rows { get; }
        public DelegateExecutor Executor { get; }

        /// <summary>
        ///     Inserts a preset, preset version, durable run, optional page results and an optional
        ///     candidate adoption. The raw adoption JSON parameters are passed through unchanged so
        ///     tests can seed exactly the legacy payload they need — including malformed values.
        /// </summary>
        public async Task<OcrRunId> SeedRunAsync(
            DocumentInstanceId documentId,
            string engineId,
            string runState,
            IReadOnlyList<(PageId, string)> pageResults,
            string? adoptedPagesJson = null,
            string? adoptedRevisionsJson = null)
        {
            string presetId = OcrPresetId.New().ToString();
            string versionId = OcrPresetVersionId.New().ToString();
            string runId = OcrRunId.New().ToString();
            string now = Clock.UtcNow.ToString("O");
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into ocr_presets (
                    preset_id, library_id, name, description, current_version_id, archived,
                    created_at, updated_at)
                values (@PresetId, @LibraryId, 'Projection preset', null, @VersionId, 0, @Now, @Now);

                insert into ocr_preset_versions (
                    preset_version_id, preset_id, engine_id, model_id, parameters_json,
                    apply_on_success, created_at)
                values (@VersionId, @PresetId, @EngineId, 'test-model', '{}', 1, @Now);

                insert into ocr_runs (
                    ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id,
                    model_id, parameters_snapshot_json, source_tree_revision_id,
                    output_tree_revision_id, retry_of_run_id, state, created_at, updated_at, hidden)
                values (@RunId, @DocumentId, @PresetId, @VersionId, @EngineId, 'test-model', '{}',
                        null, null, null, @State, @Now, @Now, 0);
                """,
                new
                {
                    PresetId = presetId,
                    LibraryId = LibraryId.ToString(),
                    VersionId = versionId,
                    EngineId = engineId,
                    RunId = runId,
                    DocumentId = documentId.ToString(),
                    State = runState,
                    Now = now
                });

            foreach ((PageId pageId, string state) in pageResults)
            {
                await connection.ExecuteAsync(
                    """
                    insert into ocr_page_results (
                        result_id, ocr_run_id, page_id, state, working_tree_revision_id,
                        error_code, error_message, created_at, updated_at)
                    values (@ResultId, @RunId, @PageId, @State, null, null, null, @Now, @Now);
                    """,
                    new
                    {
                        ResultId = OcrPageResultId.New().ToString(),
                        RunId = runId,
                        PageId = pageId.ToString(),
                        State = state,
                        Now = now
                    });
            }

            if (adoptedPagesJson is not null || adoptedRevisionsJson is not null)
            {
                await connection.ExecuteAsync(
                    """
                    insert into ocr_candidate_adoptions (
                        adoption_id, ocr_run_id, document_instance_id, adopted_tree_revisions_json,
                        adopted_pages_json, created_at)
                    values (@AdoptionId, @RunId, @DocumentId, @Revisions, @Pages, @Now);
                    """,
                    new
                    {
                        AdoptionId = OcrCandidateAdoptionId.New().ToString(),
                        RunId = runId,
                        DocumentId = documentId.ToString(),
                        Revisions = adoptedRevisionsJson ?? "[]",
                        Pages = adoptedPagesJson ?? "[]",
                        Now = now
                    });
            }

            return OcrRunId.Parse(runId);
        }

        /// <summary>
        ///     Inserts one committed document tree revision for the given page. Only one revision
        ///     per page may be current; passing <paramref name="isCurrent"/> false models a
        ///     revision superseded by a later commit.
        /// </summary>
        public async Task SeedRevisionAsync(DocumentInstanceId documentId, PageId pageId,
            DocumentTreeRevisionId revisionId, bool isCurrent)
        {
            string now = Clock.UtcNow.ToString("O");
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_tree_revisions (
                    tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
                    source, status, is_current, created_at, committed_at)
                values (@RevisionId, @DocumentId, @PageId, null, 'ocr_adopted', 'committed',
                        @IsCurrent, @Now, @Now);
                """,
                new
                {
                    RevisionId = revisionId.ToString(),
                    DocumentId = documentId.ToString(),
                    PageId = pageId.ToString(),
                    IsCurrent = isCurrent ? 1 : 0,
                    Now = now
                });
        }

        /// <summary>
        ///     Bulk-inserts <paramref name="count"/> pages and one current committed revision per
        ///     page, returning the revision ids in insertion order for adoption JSON payloads.
        /// </summary>
        public async Task<string[]> SeedCurrentRevisionSetAsync(DocumentInstanceId documentId,
            int pageIndexStart, int count)
        {
            string pagePrefix = $"bulk-{Guid.NewGuid():N}-p";
            string revisionPrefix = $"bulk-{Guid.NewGuid():N}-r";
            string now = Clock.UtcNow.ToString("O");
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                with recursive seq(n) as (
                    select 1 union all select n + 1 from seq where n < @Count)
                insert into pages (
                    page_id, document_instance_id, page_index, rotation, coordinate_basis,
                    renderer_basis_version, created_at, updated_at)
                select @PagePrefix || n, @DocumentId, @PageStart + n - 1, 0, 'normalized_page',
                       'renderer-v1', @Now, @Now
                from seq;
                """,
                new
                {
                    PagePrefix = pagePrefix,
                    DocumentId = documentId.ToString(),
                    PageStart = pageIndexStart,
                    Count = count,
                    Now = now
                });
            await connection.ExecuteAsync(
                """
                with recursive seq(n) as (
                    select 1 union all select n + 1 from seq where n < @Count)
                insert into document_tree_revisions (
                    tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
                    source, status, is_current, created_at, committed_at)
                select @RevisionPrefix || n, @DocumentId, @PagePrefix || n, null, 'ocr_adopted',
                       'committed', 1, @Now, @Now
                from seq;
                """,
                new
                {
                    RevisionPrefix = revisionPrefix,
                    DocumentId = documentId.ToString(),
                    PagePrefix = pagePrefix,
                    Count = count,
                    Now = now
                });
            return Enumerable.Range(1, count).Select(index => $"{revisionPrefix}{index}").ToArray();
        }

        public async Task SetIndexStatusAsync(DocumentInstanceId documentId, string status)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into search_index_status (
                    scope_type, scope_id, status, pending_document_count, pending_unit_count,
                    progress_percent, affected_scopes_summary, reason, updated_at)
                values ('document_instance', @ScopeId, @Status, 0, 0, null, null, null, @Now);
                """,
                new
                {
                    ScopeId = documentId.ToString(),
                    Status = status,
                    Now = Clock.UtcNow.ToString("O")
                });
        }

        public async ValueTask DisposeAsync()
        {
            Result<IReadOnlyList<OcrQueueTask>> tasks =
                await Scheduler.ListTasksAsync(new OcrQueueTaskFilter());
            if (tasks.IsSuccess)
            {
                foreach (OcrQueueTask task in tasks.Value)
                {
                    if (task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or
                        OcrQueueTaskState.Paused)
                    {
                        await Scheduler.CancelTaskAsync(task.TaskId);
                    }
                }
            }

            await Scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Scheduler.StopAsync();
            await Database.DisposeAsync();
        }
    }

    /// <summary>Executes whichever canned result the test installed for the claimed task.</summary>
    private sealed class DelegateExecutor : IOcrQueueTaskExecutor
    {
        public OcrRunId? BindRunId { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<OcrQueueTask, CancellationToken, Task<OcrQueueExecutionResult>> Handler { get; set; } =
            (_, _) => Task.FromResult(new OcrQueueExecutionResult(true, false));

        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task,
            CancellationToken cancellationToken, IProgress<OcrTaskProgressReport>? progress = null)
        {
            return Handler(task, cancellationToken);
        }

        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task,
            OcrRunExecutionContext? execution, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            if (BindRunId is { } runId)
            {
                execution.Should().NotBeNull();
                Result bound = execution!.ReportRunCreated(runId);
                bound.IsSuccess.Should().BeTrue(bound.ErrorMessage);
            }

            Started.TrySetResult();
            return Handler(task, cancellationToken);
        }
    }
}
