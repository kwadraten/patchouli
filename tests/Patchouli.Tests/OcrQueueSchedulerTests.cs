using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrQueueSchedulerTests
{
    [Fact]
    public async Task Enqueue_task_kinds_create_queued_tasks()
    {
        OcrQueueScheduler scheduler = Create(out _);
        (await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()],
            OcrQueuePriority.UserStartedDocument)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        (await scheduler.EnqueueImagePageAsync(DocumentInstanceId.New(), OcrPresetId.New(), PageId.New(),
                "/tmp/image.png", OcrQueuePriority.UserStartedDocument)).Value.TaskKind.Should()
            .Be(OcrQueueTaskKind.ImagePage);
        (await scheduler.EnqueueRenderedPdfPageAsync(DocumentInstanceId.New(), OcrPresetId.New(), PageId.New(), 200,
            OcrQueuePriority.UserStartedDocument)).Value.TaskKind.Should().Be(OcrQueueTaskKind.RenderedPdfPage);
    }

    [Fact]
    public async Task Deferred_library_id_resolver_is_invoked_on_first_enqueue_and_cached()
    {
        int invocations = 0;
        LibraryId libraryId = LibraryId.New();
        OcrQueueScheduler scheduler = new(
            _ =>
            {
                invocations++;
                return Task.FromResult(Result<LibraryId>.Success(libraryId));
            },
            new FixedClock(DateTimeOffset.UtcNow), new FakeExecutor(),
            loopInterval: TimeSpan.FromMilliseconds(5));

        Result<OcrQueueTask> first = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        Result<OcrQueueTask> second = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        invocations.Should().Be(1, "the resolved library id is cached after the first enqueue");
        first.Value.LibraryId.Should().Be(libraryId);
        second.Value.LibraryId.Should().Be(libraryId);
    }

    [Fact]
    public async Task Deferred_library_id_resolution_failure_fails_enqueue_without_caching()
    {
        int invocations = 0;
        OcrQueueScheduler scheduler = new(
            _ =>
            {
                invocations++;
                return Task.FromResult(Result<LibraryId>.Failure(AppErrorCodes.InvalidState, "no library"));
            },
            new FixedClock(DateTimeOffset.UtcNow), new FakeExecutor(),
            loopInterval: TimeSpan.FromMilliseconds(5));

        Result<OcrQueueTask> result = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        invocations.Should().Be(1);
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Deferred_library_id_resolution_does_not_hold_queue_gate()
    {
        TaskCompletionSource<Result<LibraryId>> resolution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        OcrQueueScheduler scheduler = new(
            _ => resolution.Task,
            new FixedClock(DateTimeOffset.UtcNow), new FakeExecutor(),
            loopInterval: TimeSpan.FromMilliseconds(5));

        Task<Result<OcrQueueTask>> enqueue = scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        Task clearFinished = Task.Run(scheduler.ClearFinishedTasks);

        await clearFinished.WaitAsync(TimeSpan.FromSeconds(2));
        resolution.SetResult(Result<LibraryId>.Success(LibraryId.New()));
        (await enqueue).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RunOneTick_starts_highest_priority_and_marks_success()
    {
        OcrQueueScheduler scheduler = Create(out FakeExecutor executor);
        Result<OcrQueueTask> low = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.Maintenance);
        Result<OcrQueueTask> high = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.InteractiveCurrentPage);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(low.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        await WaitForStateAsync(scheduler, high.Value.TaskId, OcrQueueTaskState.Succeeded);
        executor.Executed.Single().TaskId.Should().Be(high.Value.TaskId);
        (await scheduler.GetTaskAsync(high.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
    }

    [Fact]
    public async Task Aging_improves_old_low_priority_task()
    {
        FixedClock clock = new(DateTimeOffset.UtcNow);
        OcrQueueScheduler scheduler = Create(clock, out _);
        Result<OcrQueueTask> task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.Maintenance);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        Result<IReadOnlyList<OcrQueueTask>> tasks = await scheduler.ListTasksAsync(new OcrQueueTaskFilter());
        tasks.Value.Single().TaskId.Should().Be(task.Value.TaskId);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
    }

    [Fact]
    public async Task Limits_pause_and_filters_are_respected()
    {
        OcrQueueScheduler scheduler = Create(out _, new OcrQueueLimits(1, 1, 0, 1, 1));
        Result<OcrQueueTask> cloud = await scheduler.EnqueueAsync(new OcrQueueTaskRequest(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], "future_cloud", "cloud", OcrAdapterKind.CloudApi, "provider-a",
            OcrQueuePriority.UserStartedDocument));
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(cloud.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        await scheduler.PauseAsync(OcrPauseScope.Global);
        Result<OcrQueueTask> local = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        await scheduler.ResumeAsync(OcrPauseScope.Global);
        await scheduler.PauseAsync(OcrPauseScope.Task, local.Value.TaskId.ToString());
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter(EngineId: OcrEngineIds.Mock))).Value.Should()
            .ContainSingle(x => x.TaskId == local.Value.TaskId);
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter(ProviderId: "provider-a"))).Value.Should()
            .ContainSingle(x => x.TaskId == cloud.Value.TaskId);
    }

    [Fact]
    public async Task Retry_policy_requeues_transient_and_blocks_manual_repair()
    {
        FakeExecutor executor = new()
            { Result = new OcrQueueExecutionResult(false, false, "network_timeout", "temporary") };
        OcrQueueScheduler scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor);
        Result<OcrQueueTask> transient = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        OcrQueueTask retried = (await scheduler.GetTaskAsync(transient.Value.TaskId)).Value;
        retried.State.Should().Be(OcrQueueTaskState.Queued);
        retried.AttemptCount.Should().Be(1);
        retried.ScheduledAfter.Should().NotBeNull();
        executor.Result = new OcrQueueExecutionResult(false, false, "missing_executable", "install it");
        Result<OcrQueueTask> blocked = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        (await scheduler.GetTaskAsync(blocked.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Blocked);
    }

    [Fact]
    public async Task Manual_retry_creates_queued_copy_with_original_request_fields()
    {
        FakeExecutor executor = new()
            { Result = new OcrQueueExecutionResult(false, false, "missing_executable", "install") };
        OcrQueueScheduler scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor);
        OcrQueueTaskRequest request = new(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()],
            OcrQueueTaskKind.Region, "engine", OcrAdapterKind.LocalLibrary, "provider", OcrQueuePriority.Maintenance,
            "image.png", 300, 5, new NormalizedBBox(0.1, 0.2, 0.3, 0.4), false);
        Result<OcrQueueTask> original = await scheduler.EnqueueAsync(request);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        (await scheduler.GetTaskAsync(original.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Blocked);

        Result<OcrQueueTask> retried = await scheduler.RetryTaskAsync(original.Value.TaskId);

        retried.IsSuccess.Should().BeTrue(retried.ErrorMessage);
        retried.Value.TaskId.Should().NotBe(original.Value.TaskId);
        retried.Value.State.Should().Be(OcrQueueTaskState.Queued);
        retried.Value.Priority.Should().Be(OcrQueuePriority.BackgroundRetry);
        retried.Value.RetryOfTaskId.Should().Be(original.Value.TaskId);
        retried.Value.DocumentInstanceId.Should().Be(original.Value.DocumentInstanceId);
        retried.Value.PresetId.Should().Be(original.Value.PresetId);
        retried.Value.PageIds.Should().Equal(original.Value.PageIds);
        retried.Value.TaskKind.Should().Be(original.Value.TaskKind);
        retried.Value.EngineId.Should().Be(original.Value.EngineId);
        retried.Value.AdapterKind.Should().Be(original.Value.AdapterKind);
        retried.Value.ProviderId.Should().Be(original.Value.ProviderId);
        retried.Value.ImagePath.Should().Be(original.Value.ImagePath);
        retried.Value.Dpi.Should().Be(original.Value.Dpi);
        retried.Value.RegionBBox.Should().Be(original.Value.RegionBBox);
        retried.Value.CommitOnCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task Manual_retry_rejects_nonterminal_and_missing_tasks()
    {
        OcrQueueScheduler scheduler = Create(out _);
        Result<OcrQueueTask> queued = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);

        (await scheduler.RetryTaskAsync(queued.Value.TaskId)).ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        (await scheduler.RetryTaskAsync(OcrQueueTaskId.New())).ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Cancel_queued_task_and_preset_pause_behave_explicitly()
    {
        OcrQueueScheduler scheduler = Create(out _);
        Result<OcrQueueTask> task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        (await scheduler.PauseAsync("preset")).ErrorCode.Should().Be("unsupported_operation");
        (await scheduler.CancelTaskAsync(task.Value.TaskId)).IsSuccess.Should().BeTrue();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
    }

    [Fact]
    public async Task Cancel_terminal_tasks_returns_invalid_state_without_mutating_them()
    {
        FakeExecutor executor = new();
        OcrQueueScheduler scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor);

        Result<OcrQueueTask> succeeded = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();

        executor.Result = new OcrQueueExecutionResult(false, false, "local_ocr_process_failed", "failed");
        Result<OcrQueueTask> failed = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();

        executor.Result = new OcrQueueExecutionResult(false, false, "missing_executable", "blocked");
        Result<OcrQueueTask> blocked = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();

        Result<OcrQueueTask> cancelled = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        (await scheduler.CancelTaskAsync(cancelled.Value.TaskId)).IsSuccess.Should().BeTrue();

        foreach (OcrQueueTaskId taskId in new[]
                     { succeeded.Value.TaskId, failed.Value.TaskId, blocked.Value.TaskId, cancelled.Value.TaskId })
        {
            OcrQueueTask before = (await scheduler.GetTaskAsync(taskId)).Value;
            Result result = await scheduler.CancelTaskAsync(taskId);
            OcrQueueTask after = (await scheduler.GetTaskAsync(taskId)).Value;

            result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
            after.Should().Be(before);
        }
    }

    [Fact]
    public async Task Snapshot_reads_observe_cancellation_tokens()
    {
        OcrQueueScheduler scheduler = Create(out _);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.GetTaskAsync(OcrQueueTaskId.New(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.ListTasksAsync(new OcrQueueTaskFilter(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.GetQueueStatusAsync(cancellation.Token));
    }

    [Fact]
    public async Task Global_engine_and_provider_limits_block_second_running_task()
    {
        FakeExecutor executor = new() { Block = true };
        OcrQueueScheduler scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor,
            new OcrQueueLimits(1, 1, 2, 1, 1));
        Result<OcrQueueTask> first = await scheduler.EnqueueAsync(new OcrQueueTaskRequest(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], "future_cloud", "engine-a", OcrAdapterKind.CloudApi, "provider-a",
            OcrQueuePriority.UserStartedDocument));
        Result<OcrQueueTask> second = await scheduler.EnqueueAsync(new OcrQueueTaskRequest(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], "future_cloud", "engine-a", OcrAdapterKind.CloudApi, "provider-a",
            OcrQueuePriority.UserStartedDocument));
        await scheduler.RunOneSchedulingTickAsync();
        await WaitForAsync(() => executor.Executed.Length == 1);
        executor.Executed.Should().ContainSingle(x => x.TaskId == first.Value.TaskId);
        await scheduler.RunOneSchedulingTickAsync();
        executor.Executed.Should().HaveCount(1);
        OcrQueueStatus status = (await scheduler.GetQueueStatusAsync()).Value;
        status.RunningByEngine["engine-a"].Should().Be(1);
        status.RunningByProvider["provider-a"].Should().Be(1);
        executor.Release();
        await WaitForStateAsync(scheduler, first.Value.TaskId, OcrQueueTaskState.Succeeded);
        (await scheduler.GetTaskAsync(second.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
    }

    [Fact]
    public async Task Tick_claims_all_eligible_tasks_and_runs_them_concurrently()
    {
        GatedExecutor executor = new();
        OcrQueueScheduler scheduler = new(LibraryId.New(), new FixedClock(DateTimeOffset.UtcNow), executor,
            limits: new OcrQueueLimits(4, 4, 4, 4, 4), loopInterval: TimeSpan.FromMilliseconds(5));
        Result<OcrQueueTask> first = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        Result<OcrQueueTask> second = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await WaitForAsync(() => executor.Executed.Length == 2);
        (await scheduler.GetTaskAsync(first.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Running);
        (await scheduler.GetTaskAsync(second.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Running);
        executor.Release();
        await scheduler.WaitForIdleAsync();
        (await scheduler.GetTaskAsync(first.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
        (await scheduler.GetTaskAsync(second.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
    }

    [Fact]
    public async Task Local_pause_and_running_cancellation_are_honored()
    {
        OcrQueueScheduler scheduler = Create(out _);
        Result<OcrQueueTask> local = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.PauseAsync(OcrPauseScope.Local);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        await scheduler.ResumeAsync(OcrPauseScope.Local);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);

        FakeExecutor blocking = new() { Block = true };
        OcrQueueScheduler secondScheduler = Create(new FixedClock(DateTimeOffset.UtcNow), blocking);
        Result<OcrQueueTask> task = await secondScheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await secondScheduler.RunOneSchedulingTickAsync();
        await WaitForAsync(() => blocking.Executed.Length == 1);
        await secondScheduler.CancelTaskAsync(task.Value.TaskId);
        blocking.CancellationRequested.Should().BeTrue();
        await WaitForStateAsync(secondScheduler, task.Value.TaskId, OcrQueueTaskState.Cancelled);
        (await secondScheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
    }

    [Fact]
    public async Task Queue_status_counts_and_excludes_completed_tasks_when_requested()
    {
        OcrQueueScheduler scheduler = Create(out _);
        Result<OcrQueueTask> task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        OcrQueueStatus status = (await scheduler.GetQueueStatusAsync()).Value;
        status.Succeeded.Should().Be(1);
        status.Queued.Should().Be(0);
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter(IncludeCompleted: false))).Value.Should()
            .NotContain(x => x.TaskId == task.Value.TaskId);
    }

    [Fact]
    public async Task Worker_exception_is_logged_and_requeued_without_crashing_scheduler()
    {
        Exception? logged = null;
        ThrowingExecutor executor = new();
        OcrQueueScheduler scheduler = new(LibraryId.New(), new FixedClock(DateTimeOffset.UtcNow), executor,
            loopErrorLogger: ex => logged = ex);
        Result<OcrQueueTask> task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(),
            [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        logged.Should().NotBeNull();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.LastErrorCode.Should().Be("worker_crashed");
    }

    [Fact]
    public async Task Start_stop_are_idempotent_and_status_is_path_free()
    {
        OcrQueueScheduler scheduler = Create(out _);
        await scheduler.StartAsync();
        await scheduler.StartAsync();
        (await scheduler.GetQueueStatusAsync()).Value.IsRunning.Should().BeTrue();
        await scheduler.StopAsync();
        await scheduler.StopAsync();
        OcrQueueStatus status = (await scheduler.GetQueueStatusAsync()).Value;
        status.IsRunning.Should().BeFalse();
        System.Text.Json.JsonSerializer.Serialize(status).Should().NotContain("/tmp").And.NotContain("image.png");
    }

    [Fact]
    public void Retry_classification_covers_timeout_and_non_retryable_errors()
    {
        OcrRetryPolicy policy = new();
        policy.Classify(OcrFailureCode.BBoxCoordinateTransformFailed).Should()
            .Be(OcrRetryClassification.ManualRepairRequired);
        policy.Classify(OcrFailureCode.LocalOcrTimeout).Should().Be(OcrRetryClassification.TransientRetryable);
        policy.Classify(OcrFailureCode.RendererTimeout).Should().Be(OcrRetryClassification.ManualRepairRequired);
        policy.Classify("local_ocr_process_failed").Should().Be(OcrRetryClassification.NonRetryable);
    }

    [Fact]
    public async Task Queue_stops_page_on_render_timeout_without_retry_loop()
    {
        FakeExecutor executor = new()
        {
            Result = new OcrQueueExecutionResult(false, false, OcrFailureCode.RendererTimeout,
                "PDF renderer timed out.")
        };
        OcrQueueScheduler scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor);
        Result<OcrQueueTask> task = await scheduler.EnqueueRenderedPdfPageAsync(DocumentInstanceId.New(),
            OcrPresetId.New(), PageId.New(), 100, OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        await scheduler.WaitForIdleAsync();
        OcrQueueTask updated = (await scheduler.GetTaskAsync(task.Value.TaskId)).Value;
        updated.State.Should().Be(OcrQueueTaskState.Blocked);
        updated.AttemptCount.Should().Be(0);
        updated.LastErrorCode.Should().Be(OcrFailureCode.RendererTimeout);
    }

    [Fact]
    public async Task Executor_delegates_each_task_kind_and_forwards_cancellation()
    {
        RecordingCoordinator coordinator = new();
        OcrQueueTaskExecutor executor = new(coordinator);
        using CancellationTokenSource cts = new();
        foreach (string kind in new[]
                     { OcrQueueTaskKind.MockPages, OcrQueueTaskKind.ImagePage, OcrQueueTaskKind.RenderedPdfPage })
        {
            OcrQueueTask task = new(OcrQueueTaskId.New(), LibraryId.New(), DocumentInstanceId.New(), OcrPresetId.New(),
                [PageId.New()], kind, "engine", OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, OcrQueueTaskState.Queued, 0, 3, null, null, null, null,
                "/tmp/input.png", 150);
            (await executor.ExecuteAsync(task, cts.Token)).Succeeded.Should().BeFalse();
        }

        coordinator.Calls.Should().Equal("mock", "image", "rendered");
        coordinator.Token.Should().Be(cts.Token);
    }

    [Fact]
    public async Task Executor_returns_page_failure_when_coordinator_run_result_is_success()
    {
        PageFailureCoordinator coordinator = new();
        OcrQueueTaskExecutor executor = new(coordinator);
        OcrQueueExecutionResult result = await executor.ExecuteAsync(
            new OcrQueueTask(OcrQueueTaskId.New(), LibraryId.New(), coordinator.DocumentId, coordinator.PresetId,
                [coordinator.PageId], OcrQueueTaskKind.ImagePage, OcrEngineIds.Mock, OcrAdapterKind.Mock, null,
                OcrQueuePriority.UserStartedDocument, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                OcrQueueTaskState.Queued, 0, 3, null, null, null, null, "/tmp/input.png", null), default);
        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("empty_ocr_output");
    }

    private static OcrQueueScheduler Create(out FakeExecutor executor, OcrQueueLimits? limits = null)
    {
        executor = new FakeExecutor();
        return Create(new FixedClock(DateTimeOffset.UtcNow), executor, limits);
    }

    private static OcrQueueScheduler Create(FixedClock clock, out FakeExecutor executor, OcrQueueLimits? limits = null)
    {
        executor = new FakeExecutor();
        return Create(clock, executor, limits);
    }

    private static OcrQueueScheduler Create(FixedClock clock, FakeExecutor executor, OcrQueueLimits? limits = null)
    {
        return new OcrQueueScheduler(LibraryId.New(), clock, executor, limits: limits,
            loopInterval: TimeSpan.FromMilliseconds(5));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the scheduler should reach the expected condition in time");
    }

    private static async Task WaitForStateAsync(OcrQueueScheduler scheduler, OcrQueueTaskId taskId, string state)
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

    private sealed class FakeExecutor : IOcrQueueTaskExecutor
    {
        private readonly object _sync = new();
        private readonly List<OcrQueueTask> _executed = new();

        public OcrQueueTask[] Executed
        {
            get
            {
                lock (_sync)
                {
                    return _executed.ToArray();
                }
            }
        }

        public OcrQueueExecutionResult Result { get; set; } = new(true, false);
        public bool Block { get; set; }
        public bool CancellationRequested { get; private set; }

        private TaskCompletionSource<OcrQueueExecutionResult> Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            lock (_sync)
            {
                _executed.Add(task);
            }

            if (!Block)
            {
                return Task.FromResult(Result);
            }

            cancellationToken.Register(() =>
            {
                CancellationRequested = true;
                Gate.TrySetResult(new OcrQueueExecutionResult(false, true));
            });
            return Gate.Task;
        }

        public void Release()
        {
            Gate.TrySetResult(Result);
        }
    }

    private sealed class GatedExecutor : IOcrQueueTaskExecutor
    {
        private readonly object _sync = new();
        private readonly List<OcrQueueTask> _executed = new();

        public OcrQueueTask[] Executed
        {
            get
            {
                lock (_sync)
                {
                    return _executed.ToArray();
                }
            }
        }

        private TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            lock (_sync)
            {
                _executed.Add(task);
            }

            await Gate.Task;
            return new OcrQueueExecutionResult(true, false);
        }

        public void Release()
        {
            Gate.TrySetResult();
        }
    }

    private sealed class RecordingCoordinator : IOcrRunEngine
    {
        public event EventHandler<OcrCommitCompletedEventArgs>? CommitCompleted
        {
            add { }
            remove { }
        }

        public List<string> Calls { get; } = new();
        public CancellationToken Token { get; private set; }

        private Task<Result<OcrRun>> Fail(string call, CancellationToken token)
        {
            Calls.Add(call);
            Token = token;
            return Task.FromResult(Result<OcrRun>.Failure("not_found", "test"));
        }

        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId d, OcrPresetId p,
            CancellationToken c = default, IProgress<OcrTaskStageProgress>? progress = null)
        {
            return Fail("document", c);
        }

        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, CancellationToken c = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            return Fail("mock", c);
        }

        public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            NormalizedBBox bbox, CancellationToken c = default)
        {
            return Fail("region", c);
        }

        public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId d, OcrPresetId p,
            PageId page, NormalizedBBox bbox, CancellationToken c = default)
        {
            Calls.Add("region_candidate");
            Token = c;
            return Task.FromResult(Result<OcrRegionCandidate>.Failure("not_found", "test"));
        }

        public Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            string path, CancellationToken c = default)
        {
            return Fail("image", c);
        }

        public Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            int dpi = 200, CancellationToken c = default)
        {
            return Fail("rendered", c);
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

        public Task<Result<OcrCandidateCommit>> CommitCandidateRunAsync(OcrRunId r,
            IReadOnlyList<PageId>? pages = null, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrCandidateCommit>.Failure("not_found", "test"));
        }

        public Task<Result<OcrRun>> GetRunAsync(OcrRunId r, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrRun>.Failure("not_found", "test"));
        }

        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId r,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<IReadOnlyList<OcrPageResult>>.Failure("not_found", "test"));
        }

        public Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, string engineId, string adapterKind, string? providerId, string priority,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("unsupported_operation", "test"));
        }

        public Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(DocumentInstanceId d,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<IReadOnlyList<PageId>>.Success(Array.Empty<PageId>()));
        }

        public Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(OcrPresetId p, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrPresetVersion>.Failure("not_found", "test"));
        }

        public Task<Result> ReconcileInterruptedRunsAsync(CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class PageFailureCoordinator : IOcrRunEngine
    {
        public event EventHandler<OcrCommitCompletedEventArgs>? CommitCompleted
        {
            add { }
            remove { }
        }

        public DocumentInstanceId DocumentId { get; } = DocumentInstanceId.New();
        public OcrPresetId PresetId { get; } = OcrPresetId.New();
        public PageId PageId { get; } = PageId.New();
        private OcrRunId RunId { get; } = OcrRunId.New();

        private Result<OcrRun> Run()
        {
            return Result<OcrRun>.Success(new OcrRun(RunId, DocumentId, PresetId, OcrPresetVersionId.New(),
                OcrEngineIds.Mock,
                "model", "{}", null, null, null, OcrRunState.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId d, OcrPresetId p,
            CancellationToken c = default, IProgress<OcrTaskStageProgress>? progress = null)
        {
            return Task.FromResult(Run());
        }

        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, CancellationToken c = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            return Task.FromResult(Run());
        }

        public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            NormalizedBBox bbox, CancellationToken c = default)
        {
            return Task.FromResult(Run());
        }

        public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId d, OcrPresetId p,
            PageId page, NormalizedBBox bbox, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrRegionCandidate>.Failure("not_found", "test"));
        }

        public Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            string path, CancellationToken c = default)
        {
            return Task.FromResult(Run());
        }

        public Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            int dpi = 200, CancellationToken c = default)
        {
            return Task.FromResult(Run());
        }

        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId r,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<IReadOnlyList<OcrPageResult>>.Success([
                new OcrPageResult(OcrPageResultId.New(), RunId, PageId, OcrPageResultState.Failed, null,
                    "empty_ocr_output",
                    "No text", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]));
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

        public Task<Result<OcrCandidateCommit>> CommitCandidateRunAsync(OcrRunId r, IReadOnlyList<PageId>? p = null,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrCandidateCommit>.Failure("not_found", "test"));
        }

        public Task<Result<OcrRun>> GetRunAsync(OcrRunId r, CancellationToken c = default)
        {
            return Task.FromResult(Run());
        }

        public Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, string engineId, string adapterKind, string? providerId, string priority,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("unsupported_operation", "test"));
        }

        public Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(DocumentInstanceId d,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<IReadOnlyList<PageId>>.Success(Array.Empty<PageId>()));
        }

        public Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(OcrPresetId p, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrPresetVersion>.Failure("not_found", "test"));
        }

        public Task<Result> ReconcileInterruptedRunsAsync(CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class ThrowingExecutor : IOcrQueueTaskExecutor
    {
        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            throw new InvalidOperationException("boom");
        }
    }
}
