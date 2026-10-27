using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrTaskRunBindingTests
{
    [Fact]
    public async Task Bound_run_is_visible_on_the_task_while_recognition_is_blocked()
    {
        OcrRunId runId = OcrRunId.New();
        BindingExecutor executor = new() { BindRunId = runId, Block = true };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        OcrQueueTask running = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;
        running.State.Should().Be(OcrQueueTaskState.Running);
        running.RunId.Should().Be(runId, "the created run binds to the task before page progress");

        executor.Release.TrySetResult();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Succeeded);
        (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task Failed_recognition_keeps_the_bound_run_id()
    {
        OcrRunId runId = OcrRunId.New();
        BindingExecutor executor = new()
        {
            BindRunId = runId,
            Outcome = new OcrQueueExecutionResult(false, false, "ocr_page_failed", "page failed")
        };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Failed);

        OcrQueueTask failed = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;
        failed.RunId.Should().Be(runId);
        failed.LastErrorCode.Should().Be("ocr_page_failed");
    }

    [Fact]
    public async Task Cancelled_recognition_keeps_the_bound_run_id()
    {
        OcrRunId runId = OcrRunId.New();
        BindingExecutor executor = new() { BindRunId = runId, Block = true };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await scheduler.CancelTaskAsync(queued.Value.TaskId)).IsSuccess.Should().BeTrue();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Cancelled);

        OcrQueueTask cancelled = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;
        cancelled.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task Same_document_tasks_keep_their_own_run_binding_and_a_queued_task_binds_none()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        BindingExecutor executor = new() { Block = true, BindFor = _ => OcrRunId.New() };
        OcrQueueScheduler scheduler = Create(executor, new OcrQueueLimits(1, 1, 1, 1, 1));
        Result<OcrQueueTask> document = await scheduler.EnqueueAsync(
            new OcrQueueTaskRequest(documentId, OcrPresetId.New(), [PageId.New(), PageId.New()],
                OcrQueueTaskKind.Document, "engine", OcrAdapterKind.LocalLibrary, null,
                OcrQueuePriority.UserStartedDocument));

        await scheduler.RunOneSchedulingTickAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Result<OcrQueueTask> region = await scheduler.EnqueueAsync(
            new OcrQueueTaskRequest(documentId, OcrPresetId.New(), [PageId.New()],
                OcrQueueTaskKind.Region, "engine", OcrAdapterKind.LocalLibrary, null,
                OcrQueuePriority.InteractiveCurrentPage, RegionBBox: new NormalizedBBox(0, 0, 1, 1),
                CommitOnCompletion: false));

        OcrQueueTask running = (await scheduler.GetTaskAsync(document.Value.TaskId)).Value;
        OcrQueueTask waiting = (await scheduler.GetTaskAsync(region.Value.TaskId)).Value;
        running.RunId.Should().Be(executor.ContextFor(document.Value.TaskId)!.BoundRunId);
        waiting.State.Should().Be(OcrQueueTaskState.Queued);
        waiting.RunId.Should().BeNull(
            "a queued task must not display another same-document task's run identity");

        executor.Release.TrySetResult();
        await WaitForStateAsync(scheduler, document.Value.TaskId, OcrQueueTaskState.Succeeded);
        executor.Block = false;
        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, region.Value.TaskId, OcrQueueTaskState.Succeeded);

        OcrQueueTask finishedRegion = (await scheduler.GetTaskAsync(region.Value.TaskId)).Value;
        finishedRegion.RunId.Should().Be(executor.ContextFor(region.Value.TaskId)!.BoundRunId);
        finishedRegion.RunId.Should().NotBe(running.RunId,
            "each attempt owns the run its own execution created");
    }

    [Fact]
    public async Task Late_binding_from_a_superseded_attempt_is_ignored()
    {
        FixedClock clock = new(DateTimeOffset.UtcNow);
        BindingExecutor executor = new()
        {
            Outcome = new OcrQueueExecutionResult(false, false, "network_timeout", "temporary")
        };
        OcrQueueScheduler scheduler = Create(executor, clock: clock);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForAsync(() => executor.Contexts.Count == 1);
        OcrRunExecutionContext staleContext = executor.Contexts.Single();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Queued);

        OcrRunId replacementRun = OcrRunId.New();
        executor.BindRunId = replacementRun;
        executor.Outcome = new OcrQueueExecutionResult(true, false);
        clock.UtcNow += TimeSpan.FromSeconds(6);
        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Succeeded);

        Result stale = staleContext.ReportRunCreated(OcrRunId.New());

        stale.IsFailure.Should().BeTrue();
        (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value.RunId.Should().Be(replacementRun);
    }

    [Fact]
    public async Task Late_progress_report_from_a_superseded_attempt_is_ignored()
    {
        BindingExecutor executor = new() { Block = true };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        OcrQueueTask running = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;

        executor.ProgressFor(queued.Value.TaskId)!
            .Report(new OcrTaskProgressReport(queued.Value.TaskId, OcrTaskStage.Recognizing, 0.5, null,
                running.AttemptCount + 1));
        scheduler.GetTaskProgress(queued.Value.TaskId).Should().BeNull(
            "a report stamped with a foreign attempt must not be recorded");

        executor.ProgressFor(queued.Value.TaskId)!
            .Report(new OcrTaskProgressReport(queued.Value.TaskId, OcrTaskStage.Recognizing, 0.5, null,
                running.AttemptCount));
        scheduler.GetTaskProgress(queued.Value.TaskId).Should().NotBeNull();

        executor.Release.TrySetResult();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Succeeded);
        executor.ProgressFor(queued.Value.TaskId)!
            .Report(new OcrTaskProgressReport(queued.Value.TaskId, OcrTaskStage.Indexing, 1.0, "done",
                running.AttemptCount));
        scheduler.GetTaskProgress(queued.Value.TaskId)!.Stage.Should().Be(OcrTaskStage.Recognizing,
            "reports arriving after the terminal state are dropped");
    }

    [Fact]
    public async Task Conflicting_run_binding_fails_the_attempt_and_keeps_the_first_run()
    {
        OcrRunId first = OcrRunId.New();
        BindingExecutor executor = new() { BindRunId = first, BindExtraRunId = OcrRunId.New() };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Failed);

        OcrQueueTask failed = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;
        failed.RunId.Should().Be(first);
        failed.LastErrorCode.Should().Be(AppErrorCodes.Conflict);
        executor.ContextFor(queued.Value.TaskId)!.ConflictingReport!.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_identical_binding_is_idempotent()
    {
        OcrRunId runId = OcrRunId.New();
        BindingExecutor executor = new() { BindRunId = runId, RepeatBind = true };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Succeeded);

        executor.ContextFor(queued.Value.TaskId)!.RepeatedReport!.IsSuccess.Should().BeTrue();
        (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task Retained_completed_run_retry_retries_adoption_and_index_without_rerecognizing()
    {
        OcrRunId retained = OcrRunId.New();
        RetainedRunEngine engine = new(retained);
        RecordingSearchUnits units = new();
        RecordingSearchIndex index = new();
        OcrQueueTaskExecutor executor = new(engine, units, index);
        OcrQueueTaskId taskId = OcrQueueTaskId.New();
        OcrQueueTask task = new(taskId, LibraryId.New(), engine.Run.DocumentInstanceId,
            engine.Run.PresetId, [engine.PageId], OcrQueueTaskKind.Document, OcrEngineIds.Mock,
            OcrAdapterKind.Mock, null, OcrQueuePriority.BackgroundRetry, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, OcrQueueTaskState.Running, 1, 3, null, null, null, null, retained);
        List<OcrRunId> reported = [];
        OcrRunExecutionContext execution = new(taskId, task.AttemptCount, runId =>
        {
            reported.Add(runId);
            return Result.Success();
        });

        OcrQueueExecutionResult result = await executor.ExecuteAsync(task, execution, default);

        result.Succeeded.Should().BeTrue();
        result.RunId.Should().Be(retained);
        engine.ExecuteCalls.Should().BeEmpty("the retained completed run is reused, not re-recognized");
        reported.Should().BeEmpty("no new run was created, so no binding was reported");
        engine.Calls.Should().Equal("get_run", "list_results", "list_results", "adopt");
        units.Rebuilt.Should().Be(1);
        index.Rebuilt.Should().Be(1);
    }

    [Fact]
    public async Task Rejected_binding_terminalizes_the_orphaned_run()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Rejected bind", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrRunExecutionContext execution = new(OcrQueueTaskId.New(), 0,
            _ => Result.Failure(AppErrorCodes.Conflict, "A different OCR run owns this attempt."));

        Result<OcrRun> result = await context.Coordinator.ExecuteAsync(
            new OcrRunExecutionRequest(context.Document.DocumentInstanceId, preset.PresetId,
                [context.Pages[0].PageId], OcrRunExecutionScope.Pages), execution);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        (await context.CountAsync("select count(*) from ocr_runs;")).Should().Be(1);
        (await context.CountAsync("select count(*) from ocr_runs where state='failed';")).Should().Be(1);
        (await context.CountAsync("select count(*) from ocr_page_results where state='failed';"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Bound_run_is_visible_while_real_recognition_is_blocked()
    {
        GatedEngine engine = new();
        await using Context context = await Context.CreateAsync(engine);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Gated", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrQueueScheduler queue = new(LibraryId.New(), new FixedClock(DateTimeOffset.UtcNow),
            new OcrQueueTaskExecutor(context.Coordinator), loopInterval: TimeSpan.FromMilliseconds(5));
        QueuedOcrRunCoordinator facade = new(queue, context.Coordinator);
        try
        {
            Task<Result<OcrRun>> running = facade.RunPresetOnDocumentAsync(
                context.Document.DocumentInstanceId, preset.PresetId);
            await engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            OcrQueueTask task = (await queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
            task.State.Should().Be(OcrQueueTaskState.Running);
            task.RunId.Should().NotBeNull("the run binds while recognition is still blocked");
            (await context.Coordinator.GetRunAsync(task.RunId!.Value)).Value.State.Should()
                .Be(OcrRunState.Running);

            engine.Release.TrySetResult();
            Result<OcrRun> run = await running.WaitAsync(TimeSpan.FromSeconds(15));

            run.IsSuccess.Should().BeTrue(run.ErrorMessage);
            run.Value.OcrRunId.Should().Be(task.RunId!.Value);
            (await queue.GetTaskAsync(task.TaskId)).Value.State.Should()
                .Be(OcrQueueTaskState.Succeeded);
        }
        finally
        {
            engine.Release.TrySetResult();
            await queue.StopAsync();
        }
    }

    [Fact]
    public async Task Queued_attempts_execute_the_pinned_version_for_every_ocr_branch()
    {
        RecordingVersionEngine version = new();
        await using Context context = await Context.CreateAsync(version);
        OcrPreset preset = (await context.Presets.CreatePresetAsync(
            "Frozen", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrPresetVersion v1 =
            (await context.Presets.GetCurrentVersionAsync(preset.PresetId)).Value;
        OcrQueueScheduler queue = new(LibraryId.New(), new FixedClock(DateTimeOffset.UtcNow),
            new OcrQueueTaskExecutor(context.Coordinator), loopInterval: TimeSpan.FromMilliseconds(5));
        QueuedOcrRunCoordinator facade = new(queue, context.Coordinator);
        try
        {
            await queue.PauseAsync(OcrPauseScope.Global);
            DocumentInstanceId documentId = context.Document.DocumentInstanceId;
            OcrPresetId presetId = preset.PresetId;
            PageId page = context.Pages[0].PageId;
            NormalizedBBox region = new(0.1, 0.1, 0.5, 0.5);
            Task<Result<OcrRun>>[] pending =
            [
                facade.RunPresetOnDocumentAsync(documentId, presetId),
                facade.RunPresetOnPagesAsync(documentId, presetId, [page]),
                facade.RunWorkingOnPageAsync(documentId, presetId, page),
                facade.RunPresetOnRegionAsync(documentId, presetId, page, region)
            ];
            await WaitForAsync(() =>
                queue.ListTasksAsync(new OcrQueueTaskFilter()).Result.Value.Count == 4);
            OcrQueueTask[] queued = (await queue.ListTasksAsync(new OcrQueueTaskFilter())).Value
                .ToArray();
            queued.Should().OnlyContain(task => task.PresetVersionId == v1.PresetVersionId);

            OcrPresetVersion v2 = (await context.Presets.CreatePresetVersionAsync(presetId,
                OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{\"v\":2}", false)).Value;
            (await context.Presets.GetCurrentVersionAsync(presetId)).Value.PresetVersionId.Should()
                .Be(v2.PresetVersionId);

            await queue.ResumeAsync(OcrPauseScope.Global);
            foreach (Task<Result<OcrRun>> call in pending)
            {
                Result<OcrRun> run = await call.WaitAsync(TimeSpan.FromSeconds(15));
                run.IsSuccess.Should().BeTrue(run.ErrorMessage);
                run.Value.PresetVersionId.Should().Be(v1.PresetVersionId,
                    "the accepted task executes the version frozen at enqueue, not the new current");
            }

            version.ExecutedVersionIds.Should().NotBeEmpty().And
                .OnlyContain(id => id == v1.PresetVersionId);
        }
        finally
        {
            await queue.StopAsync();
        }
    }

    [Fact]
    public async Task Contradictory_result_run_id_fails_and_keeps_the_bound_run()
    {
        OcrRunId bound = OcrRunId.New();
        BindingExecutor executor = new()
        {
            BindRunId = bound,
            Outcome = new OcrQueueExecutionResult(true, false, RunId: OcrRunId.New(),
                CompletedPageCount: 1)
        };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Failed);

        OcrQueueTask failed = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;
        failed.RunId.Should().Be(bound, "the bound identity wins over a contradictory worker result");
        failed.LastErrorCode.Should().Be("run_identity_mismatch");
    }

    [Fact]
    public async Task Unstamped_progress_from_a_superseded_attempt_sink_is_dropped()
    {
        FixedClock clock = new(DateTimeOffset.UtcNow);
        BindingExecutor executor = new() { Block = true };
        OcrQueueScheduler scheduler = Create(executor, clock: clock);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        IProgress<OcrTaskProgressReport> staleSink = executor.ProgressFor(queued.Value.TaskId)!;

        executor.Outcome = new OcrQueueExecutionResult(false, false, "network_timeout", "temporary");
        executor.Release.TrySetResult();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Queued);

        clock.UtcNow += TimeSpan.FromSeconds(6);
        await scheduler.RunOneSchedulingTickAsync();
        await WaitForAsync(() => executor.Executions == 2);

        staleSink.Report(new OcrTaskProgressReport(queued.Value.TaskId, OcrTaskStage.Recognizing,
            0.9, "stale"));
        scheduler.GetTaskProgress(queued.Value.TaskId).Should().BeNull(
            "an unstamped report through the superseded attempt's sink is stamped with the old " +
            "attempt and dropped");

        executor.ProgressFor(queued.Value.TaskId)!.Report(
            new OcrTaskProgressReport(queued.Value.TaskId, OcrTaskStage.Recognizing, 0.4, "fresh"));
        scheduler.GetTaskProgress(queued.Value.TaskId)!.Attempt.Should().Be(1,
            "the live sink stamps an omitted attempt with the bound attempt");

        executor.Outcome = new OcrQueueExecutionResult(true, false);
        executor.SecondRelease.TrySetResult();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Succeeded);
    }

    [Fact]
    public async Task Progress_reports_after_ClearFinishedTasks_are_dropped()
    {
        BindingExecutor executor = new()
        {
            BindRunId = OcrRunId.New(),
            Outcome = new OcrQueueExecutionResult(false, false, "ocr_page_failed", "page failed")
        };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Failed);

        scheduler.ClearFinishedTasks();

        executor.ProgressFor(queued.Value.TaskId)!.Report(
            new OcrTaskProgressReport(queued.Value.TaskId, OcrTaskStage.Recognizing, 0.5, null));
        scheduler.GetTaskProgress(queued.Value.TaskId).Should().BeNull(
            "reports for removed tasks are dropped");
        (await scheduler.GetTaskAsync(queued.Value.TaskId)).IsFailure.Should().BeTrue(
            "a late report must not resurrect the removed task");
    }

    [Fact]
    public async Task Foreign_task_progress_is_rejected_at_the_sink()
    {
        BindingExecutor executor = new() { Block = true };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        executor.ProgressFor(queued.Value.TaskId)!.Report(
            new OcrTaskProgressReport(OcrQueueTaskId.New(), OcrTaskStage.Recognizing, 0.5, null));
        scheduler.GetTaskProgress(queued.Value.TaskId).Should().BeNull(
            "a report carrying a foreign task id is rejected at the bound sink");

        executor.Release.TrySetResult();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Succeeded);
    }

    [Fact]
    public async Task Requeued_attempt_clears_the_failed_run_and_stale_progress()
    {
        OcrRunId runId = OcrRunId.New();
        BindingExecutor executor = new()
        {
            BindRunId = runId, Block = true,
            Outcome = new OcrQueueExecutionResult(false, false, "network_timeout", "temporary", null, 1)
        };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New(), PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        executor.ProgressFor(queued.Value.TaskId)!.Report(
            new OcrTaskProgressReport(queued.Value.TaskId, OcrTaskStage.Recognizing, 0.5, null));
        scheduler.GetTaskProgress(queued.Value.TaskId).Should().NotBeNull();

        executor.Release.TrySetResult();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Queued);

        OcrQueueTask requeued = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;
        requeued.RunId.Should().BeNull(
            "the failed bound run must not masquerade as the next attempt's identity");
        requeued.CompletedPageCount.Should().Be(0);
        scheduler.GetTaskProgress(queued.Value.TaskId).Should().BeNull(
            "the previous attempt's progress is cleared on requeue");
    }

    [Fact]
    public async Task Automatic_post_recognition_retry_retains_the_completed_run()
    {
        OcrRunId runId = OcrRunId.New();
        BindingExecutor executor = new()
        {
            BindRunId = runId,
            Outcome = new OcrQueueExecutionResult(false, false, "network_timeout", "index retry", null, 1)
        };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Queued);

        OcrQueueTask retry = (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value;
        retry.RunId.Should().Be(runId);
        retry.CompletedPageCount.Should().Be(1);
        retry.FailedPageCount.Should().Be(0);
        scheduler.GetTaskProgress(retry.TaskId).Should().BeNull();
    }

    [Fact]
    public async Task Retry_with_a_different_version_drops_the_completed_run()
    {
        OcrRunId runId = OcrRunId.New();
        OcrPresetVersionId v1 = OcrPresetVersionId.New();
        OcrPresetVersionId v2 = OcrPresetVersionId.New();
        BindingExecutor executor = new()
        {
            BindRunId = runId,
            Outcome = new OcrQueueExecutionResult(false, false, "ocr_page_failed", "x", null, 1)
        };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueAsync(
            new OcrQueueTaskRequest(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()],
                OcrQueueTaskKind.Pages, OcrEngineIds.Mock, OcrAdapterKind.Mock, null,
                OcrQueuePriority.UserStartedDocument, PresetVersionId: v1));

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Failed);
        (await scheduler.GetTaskAsync(queued.Value.TaskId)).Value.RunId.Should().Be(runId);

        Result<OcrQueueTask> retry =
            await scheduler.RetryTaskWithVersionAsync(queued.Value.TaskId, v2);
        retry.Value.RunId.Should().BeNull(
            "an explicit different version must re-recognize rather than adopt the old run");
        retry.Value.PresetVersionId.Should().Be(v2);

        Result<OcrQueueTask> sameVersion =
            await scheduler.RetryTaskWithVersionAsync(queued.Value.TaskId, v1);
        sameVersion.Value.RunId.Should().Be(runId,
            "retrying the same version keeps the retained completed recognition");

        Result<OcrQueueTask> plain = await scheduler.RetryTaskAsync(queued.Value.TaskId);
        plain.Value.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task Incomplete_recognition_is_not_retained_on_retry()
    {
        OcrRunId runId = OcrRunId.New();
        BindingExecutor executor = new()
        {
            BindRunId = runId,
            // Two requested pages but only one succeeded result: coverage is incomplete.
            Outcome = new OcrQueueExecutionResult(false, false, "ocr_page_failed", "x", null, 1)
        };
        OcrQueueScheduler scheduler = Create(executor);
        Result<OcrQueueTask> queued = await scheduler.EnqueueAsync(
            new OcrQueueTaskRequest(DocumentInstanceId.New(), OcrPresetId.New(),
                [PageId.New(), PageId.New()], OcrQueueTaskKind.Pages, OcrEngineIds.Mock,
                OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument));

        await scheduler.RunOneSchedulingTickAsync();
        await WaitForStateAsync(scheduler, queued.Value.TaskId, OcrQueueTaskState.Failed);

        Result<OcrQueueTask> retry = await scheduler.RetryTaskAsync(queued.Value.TaskId);
        retry.Value.RunId.Should().BeNull(
            "partial page coverage must not be treated as completed recognition");
        retry.Value.CompletedPageCount.Should().Be(0);
    }

    [Fact]
    public async Task Retained_run_with_incomplete_page_coverage_is_rerecognized()
    {
        OcrRunId retained = OcrRunId.New();
        RetainedRunEngine engine = new(retained);
        engine.PageResults.Clear();
        OcrQueueTaskExecutor executor = new(engine, new RecordingSearchUnits(),
            new RecordingSearchIndex());
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(), engine.Run.DocumentInstanceId,
            engine.Run.PresetId, [engine.PageId], OcrQueueTaskKind.Document, OcrEngineIds.Mock,
            OcrAdapterKind.Mock, null, OcrQueuePriority.BackgroundRetry, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, OcrQueueTaskState.Running, 1, 3, null, null, null, null, retained);

        OcrQueueExecutionResult result = await executor.ExecuteAsync(task, null, default);

        result.Succeeded.Should().BeFalse();
        engine.ExecuteCalls.Should().HaveCount(1,
            "a retained run without complete requested-page coverage re-runs recognition");
    }

    [Fact]
    public async Task Retained_run_from_a_different_version_is_rerecognized()
    {
        OcrRunId retained = OcrRunId.New();
        RetainedRunEngine engine = new(retained);
        OcrQueueTaskExecutor executor = new(engine, new RecordingSearchUnits(),
            new RecordingSearchIndex());
        OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(), engine.Run.DocumentInstanceId,
            engine.Run.PresetId, [engine.PageId], OcrQueueTaskKind.Document, OcrEngineIds.Mock,
            OcrAdapterKind.Mock, null, OcrQueuePriority.BackgroundRetry, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, OcrQueueTaskState.Running, 1, 3, null, null, null, null,
            retained, PresetVersionId: OcrPresetVersionId.New());

        OcrQueueExecutionResult result = await executor.ExecuteAsync(task, null, default);

        result.Succeeded.Should().BeFalse();
        engine.ExecuteCalls.Should().HaveCount(1,
            "a retained run built by another preset version must not be reused");

        engine.ExecuteCalls.Clear();
        OcrQueueTask sameVersion = task with { PresetVersionId = engine.Run.PresetVersionId };
        OcrQueueExecutionResult retainedResult = await executor.ExecuteAsync(sameVersion, null, default);

        retainedResult.Succeeded.Should().BeTrue();
        engine.ExecuteCalls.Should().BeEmpty("the matching-version retained run is reused");
    }

    [Fact]
    public async Task Two_tasks_may_reference_the_same_completed_run()
    {
        OcrRunId retained = OcrRunId.New();
        RetainedRunEngine engine = new(retained);
        OcrQueueTaskExecutor executor = new(engine, new RecordingSearchUnits(),
            new RecordingSearchIndex());
        OcrQueueTask first = new(OcrQueueTaskId.New(), LibraryId.New(), engine.Run.DocumentInstanceId,
            engine.Run.PresetId, [engine.PageId], OcrQueueTaskKind.Document, OcrEngineIds.Mock,
            OcrAdapterKind.Mock, null, OcrQueuePriority.BackgroundRetry, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, OcrQueueTaskState.Running, 1, 3, null, null, null, null, retained);
        OcrQueueTask second = first with { TaskId = OcrQueueTaskId.New() };

        (await executor.ExecuteAsync(first, null, default)).Succeeded.Should().BeTrue();
        (await executor.ExecuteAsync(second, null, default)).Succeeded.Should().BeTrue();
        engine.ExecuteCalls.Should().BeEmpty(
            "legitimate adoption/index retries share the completed run without re-recognizing");
    }

    [Fact]
    public async Task QueueDocumentOcrAsync_rejects_routing_that_disagrees_with_the_frozen_version()
    {
        RetainedRunEngine engine = new(OcrRunId.New());
        OcrQueueScheduler scheduler = Create(new BindingExecutor());
        QueuedOcrRunCoordinator facade = new(scheduler, engine);
        try
        {
            DocumentInstanceId documentId = DocumentInstanceId.New();
            OcrPresetId presetId = engine.Run.PresetId;
            PageId[] pages = [PageId.New()];

            Result<OcrQueueTask> wrongEngine = await facade.QueueDocumentOcrAsync(documentId,
                presetId, pages, "other-engine", OcrAdapterKind.LocalLibrary, null,
                OcrQueuePriority.UserStartedDocument);
            wrongEngine.IsFailure.Should().BeTrue();
            wrongEngine.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);

            Result<OcrQueueTask> wrongAdapter = await facade.QueueDocumentOcrAsync(documentId,
                presetId, pages, OcrEngineIds.Mock, OcrAdapterKind.CloudApi, null,
                OcrQueuePriority.UserStartedDocument);
            wrongAdapter.IsFailure.Should().BeTrue();
            wrongAdapter.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);

            Result<OcrQueueTask> accepted = await facade.QueueDocumentOcrAsync(documentId, presetId,
                pages, OcrEngineIds.Mock, OcrAdapterKind.LocalLibrary, null,
                OcrQueuePriority.UserStartedDocument);
            accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
            accepted.Value.EngineId.Should().Be(OcrEngineIds.Mock);
            accepted.Value.AdapterKind.Should().Be(OcrAdapterKind.LocalLibrary);
            accepted.Value.PresetVersionId.Should().NotBeNull();
            await scheduler.WaitForIdleAsync();
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task Enqueue_snapshots_the_caller_page_list()
    {
        OcrQueueScheduler scheduler = Create(new BindingExecutor());
        PageId first = PageId.New();
        PageId second = PageId.New();
        List<PageId> pages = [first, second];

        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), pages, OcrQueuePriority.UserStartedDocument);

        pages.Clear();
        pages.Add(PageId.New());

        queued.Value.PageIds.Should().Equal(first, second);
        (queued.Value.PageIds is PageId[]).Should().BeFalse(
            "the accepted page set is a read-only snapshot, not the caller's mutable list");
    }

    [Fact]
    public void Execution_request_snapshots_the_caller_page_list()
    {
        List<PageId> pages = [PageId.New(), PageId.New()];
        OcrRunExecutionRequest request = new(DocumentInstanceId.New(), OcrPresetId.New(), pages,
            OcrRunExecutionScope.Pages);

        pages.Clear();

        request.PageIds.Should().HaveCount(2);
        (request.PageIds is PageId[]).Should().BeFalse(
            "the frozen request copies the page set into a read-only snapshot");
    }

    private static OcrQueueScheduler Create(BindingExecutor executor, OcrQueueLimits? limits = null,
        FixedClock? clock = null)
    {
        return new OcrQueueScheduler(LibraryId.New(), clock ?? new FixedClock(DateTimeOffset.UtcNow),
            executor, limits: limits, loopInterval: TimeSpan.FromMilliseconds(5));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the scheduler should reach the expected condition in time");
    }

    private static async Task WaitForStateAsync(OcrQueueScheduler scheduler, OcrQueueTaskId taskId,
        string state)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, OcrQueueChangedEventArgs args)
        {
            if (args.Task is { } task && task.TaskId == taskId && task.State == state)
            {
                completion.TrySetResult();
            }
        }

        scheduler.Changed += Handler;
        try
        {
            Result<OcrQueueTask> current = await scheduler.GetTaskAsync(taskId);
            if (current.IsSuccess && current.Value.State == state)
            {
                return;
            }

            Task timeout = Task.Delay(TimeSpan.FromSeconds(10));
            (await Task.WhenAny(completion.Task, timeout)).Should().Be(completion.Task,
                $"task should reach state {state} in time");
        }
        finally
        {
            scheduler.Changed -= Handler;
        }
    }

    /// <summary>
    /// Queue executor fake implementing the execution-context overload: it reports a created run
    /// through the typed context (like the real engine does after InsertRun) and can gate the
    /// attempt, fail, or return a canned result.
    /// </summary>
    private sealed class BindingExecutor : IOcrQueueTaskExecutor
    {
        private readonly object _sync = new();
        private readonly Dictionary<OcrQueueTaskId, RecordedAttempt> _attempts = new();

        public OcrRunId? BindRunId { get; set; }
        public OcrRunId? BindExtraRunId { get; set; }
        public Func<OcrQueueTask, OcrRunId?>? BindFor { get; set; }
        public bool RepeatBind { get; set; }
        public bool Block { get; set; }
        public OcrQueueExecutionResult Outcome { get; set; } = new(true, false);

        public int Executions
        {
            get
            {
                lock (_sync)
                {
                    return _executions;
                }
            }
        }

        private int _executions;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<OcrRunExecutionContext> Contexts
        {
            get
            {
                lock (_sync)
                {
                    return _attempts.Values.Select(attempt => attempt.Context!).ToList();
                }
            }
        }

        public RecordedAttempt? ContextFor(OcrQueueTaskId taskId)
        {
            lock (_sync)
            {
                return _attempts.GetValueOrDefault(taskId);
            }
        }

        public IProgress<OcrTaskProgressReport>? ProgressFor(OcrQueueTaskId taskId)
        {
            return ContextFor(taskId)?.Progress;
        }

        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task,
            CancellationToken cancellationToken, IProgress<OcrTaskProgressReport>? progress = null)
        {
            return ExecuteAsync(task, null, cancellationToken, progress);
        }

        public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task,
            OcrRunExecutionContext? execution, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            RecordedAttempt attempt = new(execution, progress);
            TaskCompletionSource gate;
            lock (_sync)
            {
                _executions++;
                _attempts[task.TaskId] = attempt;
                gate = _executions == 1 ? Release : SecondRelease;
            }

            OcrRunId? runId = BindFor?.Invoke(task) ?? BindRunId;
            if (runId is { } bound)
            {
                execution?.ReportRunCreated(bound);
                attempt.BoundRunId = bound;
            }

            if (RepeatBind && runId is { } repeated)
            {
                attempt.RepeatedReport = execution?.ReportRunCreated(repeated);
            }

            if (BindExtraRunId is { } conflicting)
            {
                attempt.ConflictingReport = execution?.ReportRunCreated(conflicting);
                return new OcrQueueExecutionResult(false, false,
                    attempt.ConflictingReport?.ErrorCode, attempt.ConflictingReport?.ErrorMessage);
            }

            Entered.TrySetResult();
            if (Block)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return Outcome;
        }

        public sealed class RecordedAttempt(
            OcrRunExecutionContext? context,
            IProgress<OcrTaskProgressReport>? progress)
        {
            public OcrRunExecutionContext? Context { get; } = context;
            public IProgress<OcrTaskProgressReport>? Progress { get; } = progress;
            public OcrRunId? BoundRunId { get; set; }
            public Result? RepeatedReport { get; set; }
            public Result? ConflictingReport { get; set; }
        }
    }

    /// <summary>
    /// Engine fake for the retained-run retry path: GetRunAsync returns a completed run whose page
    /// results all succeeded, so the executor must skip recognition and only retry adoption/index.
    /// </summary>
    private sealed class RetainedRunEngine : IOcrRunEngine
    {
        public event EventHandler<OcrCommitCompletedEventArgs>? CommitCompleted
        {
            add { }
            remove { }
        }

        public RetainedRunEngine(OcrRunId runId)
        {
            Run = new OcrRun(runId, DocumentInstanceId.New(), OcrPresetId.New(), OcrPresetVersionId.New(),
                OcrEngineIds.Mock, "model", "{}", null, null, null, OcrRunState.Completed,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            PageResults =
            [
                new OcrPageResult(OcrPageResultId.New(), runId, PageId, OcrPageResultState.Succeeded,
                    DocumentTreeRevisionId.New(), null, null, DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow)
            ];
        }

        public List<string> Calls { get; } = [];
        public List<OcrRunExecutionRequest> ExecuteCalls { get; } = [];
        public List<OcrPageResult> PageResults { get; }
        public OcrRun Run { get; }
        public PageId PageId { get; } = PageId.New();
        public Result<OcrRun>? ExecuteOutcome { get; set; }

        public Task<Result<OcrRun>> ExecuteAsync(OcrRunExecutionRequest request,
            OcrRunExecutionContext? execution = null, CancellationToken cancellationToken = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            ExecuteCalls.Add(request);
            return Task.FromResult(ExecuteOutcome ?? Result<OcrRun>.Failure("re_recognition_forbidden",
                "The retained run must not be re-recognized."));
        }

        public Task<Result<OcrRun>> GetRunAsync(OcrRunId r, CancellationToken c = default)
        {
            Calls.Add("get_run");
            return Task.FromResult(Result<OcrRun>.Success(Run));
        }

        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId r,
            CancellationToken c = default)
        {
            Calls.Add("list_results");
            return Task.FromResult(Result<IReadOnlyList<OcrPageResult>>.Success(PageResults.ToArray()));
        }

        public Task<Result<OcrCandidateCommit>> CommitCandidateRunAsync(OcrRunId r,
            IReadOnlyList<PageId>? pages = null, CancellationToken c = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            Calls.Add("adopt");
            return Task.FromResult(Result<OcrCandidateCommit>.Success(
                new OcrCandidateCommit(OcrCandidateAdoptionId.New(), r, Run.DocumentInstanceId, [], "[]",
                    DateTimeOffset.UtcNow)));
        }

        public Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(DocumentInstanceId d,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<IReadOnlyList<PageId>>.Success([PageId]));
        }

        public Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(OcrPresetId p,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrPresetVersion>.Success(new OcrPresetVersion(
                OcrPresetVersionId.New(), p, OcrEngineIds.Mock, "model", null, "{}", false,
                DateTimeOffset.UtcNow)));
        }

        public Task<Result> ReconcileInterruptedRunsAsync(CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, string engineId, string adapterKind, string? providerId,
            string priority, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure(AppErrorCodes.UnsupportedOperation, "x"));
        }

        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId d, OcrPresetId p,
            CancellationToken c = default, IProgress<OcrTaskStageProgress>? progress = null)
        {
            return Task.FromResult(Result<OcrRun>.Success(Run));
        }

        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, CancellationToken c = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            return Task.FromResult(Result<OcrRun>.Success(Run));
        }

        public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId d, OcrPresetId p,
            PageId page, NormalizedBBox bbox, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrRun>.Success(Run));
        }

        public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId d,
            OcrPresetId p, PageId page, NormalizedBBox bbox, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrRegionCandidate>.Failure("not_found", "x"));
        }

        public Task<Result> CancelRunAsync(OcrRunId r, CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId d, CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> HideOcrRunAsync(OcrRunId r, CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class RecordingSearchUnits : Core.Search.ISearchUnitBuilder
    {
        public int Rebuilt { get; private set; }

        public Task<Result> RebuildForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            Rebuilt++;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RebuildForPageAsync(PageId pageId, DocumentTreeRevisionId treeRevisionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> MarkDocumentInstanceDirtyAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class RecordingSearchIndex : Core.Search.ISearchIndexRebuilder
    {
        public int Rebuilt { get; private set; }

        public Task<Result> EnsureCacheAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RebuildFtsForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            Rebuilt++;
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RebuildFtsForLibraryAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SetIndexUnavailableAsync(string scopeType, string scopeId, string reason,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Records the preset version identity the OCR page engine actually executed.</summary>
    private sealed class RecordingVersionEngine : IOcrEngine
    {
        private readonly MockOcrEngine _inner = new();

        public string EngineId => OcrEngineIds.Mock;
        public List<OcrPresetVersionId> ExecutedVersionIds { get; } = [];

        public async Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            ExecutedVersionIds.Add(presetVersion.PresetVersionId);
            return await _inner.RunPageAsync(page, presetVersion, cancellationToken);
        }
    }

    private sealed class GatedEngine : IOcrEngine
    {
        private readonly MockOcrEngine _inner = new();

        public string EngineId => OcrEngineIds.Mock;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await _inner.RunPageAsync(page, presetVersion, cancellationToken);
        }
    }

    private sealed class StubPageRenderService(string imagePath) : IPageRenderService
    {
        public Task<Result<OcrInputDescriptor>> BuildOcrInputFromRenderedPageAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, int dpi = 200,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrInputDescriptor>.Success(new OcrInputDescriptor(pageId,
                documentInstanceId, OcrInputKinds.PageImage, imagePath, null, null, "available", null)));
        }

        public Task<Result<string>> RenderRegionPngAsync(DocumentInstanceId d, PageId p,
            NormalizedBBox region, int dpi = 200, CancellationToken c = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<PageRenderResult>> RenderPageAsync(PageRenderRequest request,
            CancellationToken c = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<string?>> GetCachedRenderPathAsync(PageRenderRequest request,
            CancellationToken c = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<PdfPagePixelBufferLease>> RenderPreviewAsync(PageRenderRequest request,
            CancellationToken c = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> ClearRenderCacheForDocumentAsync(DocumentInstanceId d,
            CancellationToken c = default)
        {
            throw new NotSupportedException();
        }

        public Task<PdfRendererAvailability> GetRendererAvailabilityAsync(CancellationToken c = default)
        {
            throw new NotSupportedException();
        }

        public Task ReleaseDocumentSessionAsync(DocumentInstanceId d, CancellationToken c = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(TemporarySqliteDatabase database, string imagePath, DocumentInstance document,
            IReadOnlyList<Page> pages, IOcrPresetService presets, OcrRunEngine coordinator)
        {
            _database = database;
            Document = document;
            Pages = pages;
            Presets = presets;
            Coordinator = coordinator;
            ImagePath = imagePath;
        }

        public DocumentInstance Document { get; }
        public IReadOnlyList<Page> Pages { get; }
        public IOcrPresetService Presets { get; }
        public OcrRunEngine Coordinator { get; }
        public string ImagePath { get; }

        public async Task<int> CountAsync(string sql)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(sql);
        }

        public static async Task<Context> CreateAsync(IOcrEngine? engine = null)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("OCR task-run binding");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "OCR task-run binding")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory,
                    clock).AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan))
                .Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page[] created = new Page[2];
            for (int index = 0; index < created.Length; index++)
            {
                created[index] = (await pages.CreatePageAsync(document.DocumentInstanceId, index,
                    (index + 1).ToString(), null, null, 0, CoordinateBasis.NormalizedPage, null, null,
                    "test", null)).Value;
            }

            string imagePath = Path.Combine(Path.GetTempPath(),
                $"ocr-binding-render-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(imagePath, [137, 80, 78, 71]);
            IDocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            OcrRunEngine coordinator = new(
                database.ConnectionFactory,
                clock,
                engine ?? new MockOcrEngine(),
                treeImporter: new OcrDocumentTreeImporter(trees),
                pageRenderService: new StubPageRenderService(imagePath));
            return new Context(database, imagePath, document, created,
                new OcrPresetService(database.ConnectionFactory, libraries, clock), coordinator);
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(ImagePath))
            {
                File.Delete(ImagePath);
            }

            return _database.DisposeAsync();
        }
    }
}
