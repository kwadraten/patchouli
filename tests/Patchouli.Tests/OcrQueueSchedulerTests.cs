using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrQueueSchedulerTests
{
    [Fact] public async Task Enqueue_task_kinds_create_queued_tasks()
    {
        var scheduler = Create(out _);
        (await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        (await scheduler.EnqueueImagePageAsync(DocumentInstanceId.New(), OcrPresetId.New(), PageId.New(), "/tmp/image.png", OcrQueuePriority.UserStartedDocument)).Value.TaskKind.Should().Be(OcrQueueTaskKind.ImagePage);
        (await scheduler.EnqueueRenderedPdfPageAsync(DocumentInstanceId.New(), OcrPresetId.New(), PageId.New(), 200, OcrQueuePriority.UserStartedDocument)).Value.TaskKind.Should().Be(OcrQueueTaskKind.RenderedPdfPage);
    }

    [Fact] public async Task RunOneTick_starts_highest_priority_and_marks_success()
    {
        var scheduler = Create(out var executor);
        var low = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.Maintenance);
        var high = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.InteractiveCurrentPage);
        await scheduler.RunOneSchedulingTickAsync();
        executor.Executed.Single().TaskId.Should().Be(high.Value.TaskId);
        (await scheduler.GetTaskAsync(high.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
        (await scheduler.GetTaskAsync(low.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
    }

    [Fact] public async Task Aging_improves_old_low_priority_task()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow); var scheduler = Create(clock, out _);
        var task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.Maintenance);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var tasks = await scheduler.ListTasksAsync(new OcrQueueTaskFilter());
        tasks.Value.Single().TaskId.Should().Be(task.Value.TaskId);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
    }

    [Fact] public async Task Limits_pause_and_filters_are_respected()
    {
        var scheduler = Create(out _, new OcrQueueLimits(1, 1, 0, 1, 1));
        var cloud = await scheduler.EnqueueAsync(new(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], "future_cloud", "cloud", OcrAdapterKind.CloudApi, "provider-a", OcrQueuePriority.UserStartedDocument));
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(cloud.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        await scheduler.PauseAsync(OcrPauseScope.Global);
        var local = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        await scheduler.ResumeAsync(OcrPauseScope.Global);
        await scheduler.PauseAsync(OcrPauseScope.Task, local.Value.TaskId.ToString());
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter(EngineId: OcrEngineIds.Mock))).Value.Should().ContainSingle(x => x.TaskId == local.Value.TaskId);
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter(ProviderId: "provider-a"))).Value.Should().ContainSingle(x => x.TaskId == cloud.Value.TaskId);
    }

    [Fact] public async Task Retry_policy_requeues_transient_and_blocks_manual_repair()
    {
        var executor = new FakeExecutor { Result = new(false, false, "network_timeout", "temporary") };
        var scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor);
        var transient = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        var retried = (await scheduler.GetTaskAsync(transient.Value.TaskId)).Value;
        retried.State.Should().Be(OcrQueueTaskState.Queued); retried.AttemptCount.Should().Be(1); retried.ScheduledAfter.Should().NotBeNull();
        executor.Result = new(false, false, "missing_executable", "install it");
        var blocked = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(blocked.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Blocked);
    }

    [Fact] public async Task Cancel_queued_task_and_preset_pause_behave_explicitly()
    {
        var scheduler = Create(out _); var task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        (await scheduler.PauseAsync("preset")).ErrorCode.Should().Be("unsupported_operation");
        (await scheduler.CancelTaskAsync(task.Value.TaskId)).IsSuccess.Should().BeTrue();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
        await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
    }

    [Fact] public async Task Global_engine_and_provider_limits_block_second_running_task()
    {
        var executor = new FakeExecutor { Block = true };
        var scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor, new OcrQueueLimits(1, 1, 2, 1, 1));
        var first = await scheduler.EnqueueAsync(new(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], "future_cloud", "engine-a", OcrAdapterKind.CloudApi, "provider-a", OcrQueuePriority.UserStartedDocument));
        var second = await scheduler.EnqueueAsync(new(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], "future_cloud", "engine-a", OcrAdapterKind.CloudApi, "provider-a", OcrQueuePriority.UserStartedDocument));
        var runningTick = scheduler.RunOneSchedulingTickAsync();
        executor.Executed.Should().ContainSingle(x => x.TaskId == first.Value.TaskId);
        await scheduler.RunOneSchedulingTickAsync();
        executor.Executed.Should().HaveCount(1);
        var status = (await scheduler.GetQueueStatusAsync()).Value;
        status.RunningByEngine["engine-a"].Should().Be(1);
        status.RunningByProvider["provider-a"].Should().Be(1);
        executor.Release(); await runningTick;
        (await scheduler.GetTaskAsync(second.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
    }

    [Fact] public async Task Local_pause_and_running_cancellation_are_honored()
    {
        var scheduler = Create(out _);
        var local = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.PauseAsync(OcrPauseScope.Local); await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Queued);
        await scheduler.ResumeAsync(OcrPauseScope.Local); await scheduler.RunOneSchedulingTickAsync();
        (await scheduler.GetTaskAsync(local.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);

        var blocking = new FakeExecutor { Block = true }; var secondScheduler = Create(new FixedClock(DateTimeOffset.UtcNow), blocking);
        var task = await secondScheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        var tick = secondScheduler.RunOneSchedulingTickAsync(); blocking.Executed.Should().ContainSingle();
        await secondScheduler.CancelTaskAsync(task.Value.TaskId); blocking.CancellationRequested.Should().BeTrue();
        await tick; (await secondScheduler.GetTaskAsync(task.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
    }

    [Fact] public async Task Queue_status_counts_and_excludes_completed_tasks_when_requested()
    {
        var scheduler = Create(out _);
        var task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        var status = (await scheduler.GetQueueStatusAsync()).Value;
        status.Succeeded.Should().Be(1); status.Queued.Should().Be(0);
        (await scheduler.ListTasksAsync(new OcrQueueTaskFilter(IncludeCompleted: false))).Value.Should().NotContain(x => x.TaskId == task.Value.TaskId);
    }

    [Fact] public async Task Worker_exception_is_logged_and_requeued_without_crashing_scheduler()
    {
        Exception? logged = null; var executor = new ThrowingExecutor();
        var scheduler = new OcrQueueScheduler(LibraryId.New(), new FixedClock(DateTimeOffset.UtcNow), executor, loopErrorLogger: ex => logged = ex);
        var task = await scheduler.EnqueueMockPagesAsync(DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        logged.Should().NotBeNull();
        (await scheduler.GetTaskAsync(task.Value.TaskId)).Value.LastErrorCode.Should().Be("worker_crashed");
    }

    [Fact] public async Task Start_stop_are_idempotent_and_status_is_path_free()
    {
        var scheduler = Create(out _);
        await scheduler.StartAsync(); await scheduler.StartAsync();
        (await scheduler.GetQueueStatusAsync()).Value.IsRunning.Should().BeTrue();
        await scheduler.StopAsync(); await scheduler.StopAsync();
        var status = (await scheduler.GetQueueStatusAsync()).Value;
        status.IsRunning.Should().BeFalse();
        System.Text.Json.JsonSerializer.Serialize(status).Should().NotContain("/tmp").And.NotContain("image.png");
    }

    [Fact] public void Retry_classification_covers_timeout_and_non_retryable_errors()
    {
        var policy = new OcrRetryPolicy();
        policy.Classify(OcrFailureCode.BBoxCoordinateTransformFailed).Should().Be(OcrRetryClassification.ManualRepairRequired);
        policy.Classify(OcrFailureCode.LocalOcrTimeout).Should().Be(OcrRetryClassification.TransientRetryable);
        policy.Classify(OcrFailureCode.RendererTimeout).Should().Be(OcrRetryClassification.ManualRepairRequired);
        policy.Classify("local_ocr_process_failed").Should().Be(OcrRetryClassification.NonRetryable);
    }

    [Fact] public async Task Queue_stops_page_on_render_timeout_without_retry_loop()
    {
        var executor = new FakeExecutor { Result = new(false, false, OcrFailureCode.RendererTimeout, "PDF renderer timed out.") };
        var scheduler = Create(new FixedClock(DateTimeOffset.UtcNow), executor);
        var task = await scheduler.EnqueueRenderedPdfPageAsync(DocumentInstanceId.New(), OcrPresetId.New(), PageId.New(), 100, OcrQueuePriority.UserStartedDocument);
        await scheduler.RunOneSchedulingTickAsync();
        var updated = (await scheduler.GetTaskAsync(task.Value.TaskId)).Value;
        updated.State.Should().Be(OcrQueueTaskState.Blocked);
        updated.AttemptCount.Should().Be(0);
        updated.LastErrorCode.Should().Be(OcrFailureCode.RendererTimeout);
    }

    [Fact] public async Task Executor_delegates_each_task_kind_and_forwards_cancellation()
    {
        var coordinator = new RecordingCoordinator(); var executor = new OcrQueueTaskExecutor(coordinator); using var cts = new CancellationTokenSource();
        foreach (var kind in new[] { OcrQueueTaskKind.MockPages, OcrQueueTaskKind.ImagePage, OcrQueueTaskKind.RenderedPdfPage })
        {
            var task = new OcrQueueTask(OcrQueueTaskId.New(), LibraryId.New(), DocumentInstanceId.New(), OcrPresetId.New(), [PageId.New()], kind, "engine", OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, OcrQueueTaskState.Queued, 0, 3, null, null, null, null, "/tmp/input.png", 150);
            (await executor.ExecuteAsync(task, cts.Token)).Succeeded.Should().BeFalse();
        }
        coordinator.Calls.Should().Equal("mock", "image", "rendered");
        coordinator.Token.Should().Be(cts.Token);
    }

    [Fact] public async Task Executor_returns_page_failure_when_coordinator_run_result_is_success()
    {
        var coordinator = new PageFailureCoordinator(); var executor = new OcrQueueTaskExecutor(coordinator);
        var result = await executor.ExecuteAsync(new OcrQueueTask(OcrQueueTaskId.New(), LibraryId.New(), coordinator.DocumentId, coordinator.PresetId, [coordinator.PageId], OcrQueueTaskKind.ImagePage, OcrEngineIds.Mock, OcrAdapterKind.Mock, null, OcrQueuePriority.UserStartedDocument, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, OcrQueueTaskState.Queued, 0, 3, null, null, null, null, "/tmp/input.png", null), default);
        result.Succeeded.Should().BeFalse(); result.ErrorCode.Should().Be("empty_ocr_output");
    }

    private static OcrQueueScheduler Create(out FakeExecutor executor, OcrQueueLimits? limits = null)
    {
        executor = new FakeExecutor(); return Create(new FixedClock(DateTimeOffset.UtcNow), executor, limits);
    }

    private static OcrQueueScheduler Create(FixedClock clock, out FakeExecutor executor, OcrQueueLimits? limits = null)
    {
        executor = new FakeExecutor(); return Create(clock, executor, limits);
    }

    private static OcrQueueScheduler Create(FixedClock clock, FakeExecutor executor, OcrQueueLimits? limits = null) => new(LibraryId.New(), clock, executor, limits: limits, loopInterval: TimeSpan.FromMilliseconds(5));

    private sealed class FakeExecutor : IOcrQueueTaskExecutor
    {
        public List<OcrQueueTask> Executed { get; } = new();
        public OcrQueueExecutionResult Result { get; set; } = new(true, false);
        public bool Block { get; set; }
        public bool CancellationRequested { get; private set; }
        private TaskCompletionSource<OcrQueueExecutionResult> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken)
        {
            Executed.Add(task);
            if (!Block) return Task.FromResult(Result);
            cancellationToken.Register(() => { CancellationRequested = true; Gate.TrySetResult(new(false, true)); });
            return Gate.Task;
        }
        public void Release() => Gate.TrySetResult(Result);
    }

    private sealed class RecordingCoordinator : IOcrRunCoordinator
    {
        public List<string> Calls { get; } = new(); public CancellationToken Token { get; private set; }
        private Task<Result<OcrRun>> Fail(string call, CancellationToken token) { Calls.Add(call); Token = token; return Task.FromResult(Result<OcrRun>.Failure("not_found", "test")); }
        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId d, OcrPresetId p, CancellationToken c = default) => Fail("document", c);
        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId d, OcrPresetId p, IReadOnlyList<PageId> pages, CancellationToken c = default) => Fail("mock", c);
        public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page, NormalizedBBox bbox, CancellationToken c = default) => Fail("region", c);
        public Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId d, OcrPresetId p, PageId page, string path, CancellationToken c = default) => Fail("image", c);
        public Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId d, OcrPresetId p, PageId page, int dpi = 200, CancellationToken c = default) => Fail("rendered", c);
        public Task<Result> CancelRunAsync(OcrRunId r, CancellationToken c = default) => Task.FromResult(Result.Success());
        public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId d, CancellationToken c = default) => Task.FromResult(Result.Success());
        public Task<Result> HideOcrRunAsync(OcrRunId r, CancellationToken c = default) => Task.FromResult(Result.Success());
        public Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId r, IReadOnlyList<PageId>? pages = null, CancellationToken c = default) => Task.FromResult(Result<OcrCandidateAdoption>.Failure("not_found", "test"));
        public Task<Result<OcrRun>> GetRunAsync(OcrRunId r, CancellationToken c = default) => Task.FromResult(Result<OcrRun>.Failure("not_found", "test"));
        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId r, CancellationToken c = default) => Task.FromResult(Result<IReadOnlyList<OcrPageResult>>.Failure("not_found", "test"));
    }
    private sealed class PageFailureCoordinator : IOcrRunCoordinator
    {
        public DocumentInstanceId DocumentId { get; } = DocumentInstanceId.New(); public OcrPresetId PresetId { get; } = OcrPresetId.New(); public PageId PageId { get; } = PageId.New(); private OcrRunId RunId { get; } = OcrRunId.New();
        private Result<OcrRun> Run() => Result<OcrRun>.Success(new(RunId, DocumentId, PresetId, OcrPresetVersionId.New(), OcrEngineIds.Mock, "model", "{}", null, null, null, OcrRunState.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId d, OcrPresetId p, CancellationToken c = default) => Task.FromResult(Run());
        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId d,OcrPresetId p,IReadOnlyList<PageId> pages,CancellationToken c=default)=>Task.FromResult(Run()); public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId d,OcrPresetId p,PageId page,NormalizedBBox bbox,CancellationToken c=default)=>Task.FromResult(Run()); public Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId d,OcrPresetId p,PageId page,string path,CancellationToken c=default)=>Task.FromResult(Run()); public Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId d,OcrPresetId p,PageId page,int dpi=200,CancellationToken c=default)=>Task.FromResult(Run());
        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId r,CancellationToken c=default)=>Task.FromResult(Result<IReadOnlyList<OcrPageResult>>.Success([new(OcrPageResultId.New(),RunId,PageId,OcrPageResultState.Failed,null,"empty_ocr_output","No text",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)]));
        public Task<Result> CancelRunAsync(OcrRunId r,CancellationToken c=default)=>Task.FromResult(Result.Success()); public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId d,CancellationToken c=default)=>Task.FromResult(Result.Success()); public Task<Result> HideOcrRunAsync(OcrRunId r,CancellationToken c=default)=>Task.FromResult(Result.Success()); public Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId r,IReadOnlyList<PageId>? p=null,CancellationToken c=default)=>Task.FromResult(Result<OcrCandidateAdoption>.Failure("not_found","test")); public Task<Result<OcrRun>> GetRunAsync(OcrRunId r,CancellationToken c=default)=>Task.FromResult(Run());
    }
    private sealed class ThrowingExecutor : IOcrQueueTaskExecutor { public Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken) => throw new InvalidOperationException("boom"); }
}
