using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrQueueLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Accepted_provider_lane_comes_from_the_pinned_component_not_a_mutable_default()
    {
        await using Harness fixture = new();
        fixture.Runtime.ProviderId = "preset-specific-provider";
        Result<OcrQueueTask> queued = await fixture.EnqueueAsync();
        queued.IsSuccess.Should().BeTrue(queued.ErrorMessage);
        queued.Value.ProviderId.Should().Be("preset-specific-provider");
        fixture.Runtime.ProviderId = "new-default";
        (await fixture.Queue.GetTaskAsync(queued.Value.TaskId)).Value.ProviderId
            .Should().Be("preset-specific-provider");
    }

    [Fact]
    public async Task Stopped_queue_preserves_queued_pins_and_accepts_work_after_restart()
    {
        await using Harness fixture = new();
        await fixture.Queue.PauseAsync(OcrPauseScope.Global);
        await fixture.Queue.StartAsync();
        Result<OcrQueueTask> first = await fixture.EnqueueAsync();
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);

        await fixture.Queue.StopAsync();

        fixture.Runtime.Leases[0].Disposed.Should().Be(0, "Stop only pauses scheduling, not accepted generations");
        fixture.Registry.Get(first.Value.TaskId).Should().BeSameAs(fixture.Runtime.Leases[0]);
        Result<OcrQueueTask> second = await fixture.EnqueueAsync();
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        await fixture.Queue.ResumeAsync(OcrPauseScope.Global);
        await fixture.Queue.StartAsync();
        await fixture.Queue.WaitForIdleAsync().WaitAsync(Deadline);
        (await fixture.Queue.GetTaskAsync(first.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
        (await fixture.Queue.GetTaskAsync(second.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
        fixture.Runtime.Leases.Should().OnlyContain(lease => lease.Disposed == 1);
    }

    [Fact]
    public async Task Idle_wait_includes_generation_release_not_just_published_terminal_state()
    {
        await using Harness fixture = new();
        Result<OcrQueueTask> queued = await fixture.EnqueueAsync();
        Lease lease = fixture.Runtime.Leases.Single();
        lease.DelayDisposal = true;
        await fixture.Queue.RunOneSchedulingTickAsync();
        await lease.Disposing.Task.WaitAsync(Deadline);

        Task idle = fixture.Queue.WaitForIdleAsync();
        idle.IsCompleted.Should().BeFalse("the actual worker still owns a generation-release operation");
        lease.AllowDispose.TrySetResult();
        await idle.WaitAsync(Deadline);
        lease.Disposed.Should().Be(1);
        (await fixture.Queue.GetTaskAsync(queued.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
    }

    [Fact]
    public async Task Shutdown_requests_cancellation_and_waits_for_real_executor_and_lease_completion()
    {
        await using Harness fixture = new();
        fixture.Executor.Block = true;
        Result<OcrQueueTask> queued = await fixture.EnqueueAsync();
        await fixture.Queue.RunOneSchedulingTickAsync();
        await fixture.Executor.Started.Task.WaitAsync(Deadline);
        Lease lease = fixture.Runtime.Leases.Single();

        Task firstDispose = fixture.Queue.DisposeAsync().AsTask();
        Task secondDispose = fixture.Queue.DisposeAsync().AsTask();
        secondDispose.Should().BeSameAs(firstDispose);
        await fixture.Executor.CancellationRequested.Task.WaitAsync(Deadline);
        firstDispose.IsCompleted.Should().BeFalse("cancellation is not completion of a blocking recognizer/importer");
        lease.Disposed.Should().Be(0);
        fixture.Executor.AllowExit.TrySetResult();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(Deadline);

        lease.Disposed.Should().Be(1);
        fixture.Executor.Exited.Should().BeTrue();
        (await fixture.Queue.GetTaskAsync(queued.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Cancelled);
        (await fixture.EnqueueAsync()).IsFailure.Should().BeTrue();
        OcrQueueScheduler queue = fixture.Queue;
        Action restart = () => queue.StartAsync();
        restart.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Rejected_acceptance_leaves_a_durable_failed_task_row()
    {
        await using Harness fixture = new();
        fixture.Runtime.AllowAcquisition = false;

        Result<OcrQueueTask> queued = await fixture.EnqueueAsync();

        queued.IsFailure.Should().BeTrue();
        queued.ErrorCode.Should().Be("extension_missing");
        OcrQueueTask[] tasks = (await fixture.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value
            .ToArray();
        OcrQueueTask row = tasks.Should().ContainSingle(
            "a rejected acceptance must stay visible in the queue instead of vanishing").Subject;
        row.State.Should().Be(OcrQueueTaskState.Failed);
        row.LastErrorCode.Should().Be("extension_missing");
        row.LastErrorMessage.Should().Be("Recognition is unavailable.");
    }

    [Fact]
    public async Task Rejected_retry_leaves_a_durable_failed_task_row()
    {
        await using Harness fixture = new();
        fixture.Runtime.AllowAcquisition = false;
        Result<OcrQueueTask> queued = await fixture.EnqueueAsync();
        queued.IsFailure.Should().BeTrue();
        OcrQueueTask rejected = (await fixture.Queue.ListTasksAsync(new OcrQueueTaskFilter()))
            .Value.Single();

        Result<OcrQueueTask> retry =
            await fixture.Queue.RetryTaskWithVersionAsync(rejected.TaskId, fixture.VersionId);

        retry.IsFailure.Should().BeTrue();
        retry.ErrorCode.Should().Be("extension_missing");
        OcrQueueTask[] tasks = (await fixture.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value
            .ToArray();
        tasks.Should().HaveCount(2);
        tasks.Should().OnlyContain(task => task.State == OcrQueueTaskState.Failed);
        tasks.Should().ContainSingle(task => task.RetryOfTaskId == rejected.TaskId &&
                                             task.LastErrorCode == "extension_missing");
    }

    [Fact]
    public async Task Pin_acquisition_exception_leaves_a_durable_failed_task_row()
    {
        await using Harness fixture = new();
        fixture.Runtime.Fault = new InvalidOperationException(
            "Extension 'pdf-text.native-xycut' failed to activate: CS0001 C:\\secret\\path.fsx:12");

        Result<OcrQueueTask> queued = await fixture.EnqueueAsync();

        queued.IsFailure.Should().BeTrue();
        queued.ErrorCode.Should().Be("engine_generation_pin_failed");
        OcrQueueTask[] tasks = (await fixture.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value
            .ToArray();
        OcrQueueTask row = tasks.Should().ContainSingle().Subject;
        row.State.Should().Be(OcrQueueTaskState.Failed);
        row.LastErrorCode.Should().Be("engine_generation_pin_failed");
        row.LastErrorMessage.Should().NotContain("secret",
            "raw activation exceptions can carry local paths; only the safe message is recorded");
    }

    [Fact]
    public async Task Completed_run_retry_never_acquires_a_new_recognition_generation()
    {
        await using Harness fixture = new();
        fixture.Executor.FailAdoptionOnce = true;
        Result<OcrQueueTask> queued = await fixture.EnqueueAsync();
        await fixture.Queue.RunOneSchedulingTickAsync();
        await fixture.Queue.WaitForIdleAsync().WaitAsync(Deadline);
        OcrQueueTask failed = (await fixture.Queue.GetTaskAsync(queued.Value.TaskId)).Value;
        failed.State.Should().Be(OcrQueueTaskState.Failed);
        failed.RunId.Should().Be(fixture.Executor.RunId);
        fixture.Runtime.AllowAcquisition = false;

        Result<OcrQueueTask> retry = await fixture.Queue.RetryTaskWithVersionAsync(failed.TaskId, fixture.VersionId);

        retry.IsSuccess.Should().BeTrue(retry.ErrorMessage);
        retry.Value.RunId.Should().Be(fixture.Executor.RunId);
        fixture.Runtime.Acquisitions.Should().Be(1, "adoption/index retries use existing durable recognition");
        await fixture.Queue.RunOneSchedulingTickAsync();
        await fixture.Queue.WaitForIdleAsync().WaitAsync(Deadline);
        (await fixture.Queue.GetTaskAsync(retry.Value.TaskId)).Value.State.Should().Be(OcrQueueTaskState.Succeeded);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly OcrPresetId PresetId = OcrPresetId.New();
        public readonly OcrPresetVersionId VersionId = OcrPresetVersionId.New();
        public readonly Runtime Runtime = new();
        public readonly Executor Executor = new();
        public readonly OcrEngineLeaseRegistry Registry;
        public readonly OcrQueueScheduler Queue;

        public Harness()
        {
            Registry = new OcrEngineLeaseRegistry(Runtime, (id, _) => Task.FromResult(
                Result<OcrPresetVersion>.Success(new OcrPresetVersion(id, PresetId, "test-lifetime", "test-model",
                    null, "{}", false, DateTimeOffset.UnixEpoch))));
            Queue = new OcrQueueScheduler(LibraryId.New(), new FixedClock(DateTimeOffset.UnixEpoch), Executor,
                limits: new OcrQueueLimits(1, 1, 1, 1, 1), engineLeases: Registry);
        }

        public Task<Result<OcrQueueTask>> EnqueueAsync()
        {
            return Queue.EnqueueAsync(new OcrQueueTaskRequest(DocumentInstanceId.New(), PresetId,
                [PageId.New()], OcrQueueTaskKind.Pages, "test-lifetime", OcrAdapterKind.LocalLibrary, null,
                OcrQueuePriority.UserStartedDocument, 0, PresetVersionId: VersionId,
                Source: OcrQueueSource.Test));
        }

        public async ValueTask DisposeAsync()
        {
            Executor.AllowExit.TrySetResult();
            foreach (Lease lease in Runtime.Leases)
            {
                lease.AllowDispose.TrySetResult();
            }

            await Queue.DisposeAsync();
        }
    }

    private sealed class Executor : IOcrQueueTaskExecutor
    {
        public readonly OcrRunId RunId = OcrRunId.New();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowExit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly TaskCompletionSource CancellationRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Block;
        public bool Exited;
        public bool FailAdoptionOnce;
        private int _calls;

        public async Task<OcrQueueExecutionResult> ExecuteAsync(OcrQueueTask task, CancellationToken cancellationToken,
            IProgress<OcrTaskProgressReport>? progress = null)
        {
            int call = Interlocked.Increment(ref _calls);
            using CancellationTokenRegistration registration =
                cancellationToken.Register(() => CancellationRequested.TrySetResult());
            Started.TrySetResult();
            if (Block)
            {
                // Deliberately ignores cancellation until the test releases actual work.
                await AllowExit.Task;
            }

            Exited = true;
            if (cancellationToken.IsCancellationRequested)
            {
                return new OcrQueueExecutionResult(false, true, RunId: RunId);
            }

            return FailAdoptionOnce && call == 1
                ? new OcrQueueExecutionResult(false, false, "revision_conflict", "An edit blocks adoption.", RunId, 1)
                : new OcrQueueExecutionResult(true, false, RunId: RunId, CompletedPageCount: 1);
        }
    }

    private sealed class Runtime : IOcrEngineRuntime
    {
        public readonly List<Lease> Leases = [];
        public bool AllowAcquisition = true;
        public Exception? Fault;
        public string? ProviderId;
        public int Acquisitions;

        public IReadOnlyList<OcrEngineDefinition> ListDefinitions()
        {
            return [Lease.Metadata];
        }

        public Task<Result<OcrEngineDefinition>> GetDefinitionAsync(string engineId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrEngineDefinition>.Success(Lease.Metadata));
        }

        public Task<Result<IOcrEngineLease>> AcquireAsync(OcrEngineLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Acquisitions);
            if (Fault is { } fault)
            {
                throw fault;
            }

            if (!AllowAcquisition)
            {
                return Task.FromResult(Result<IOcrEngineLease>.Failure("extension_missing",
                    "Recognition is unavailable."));
            }

            Lease lease = new() { ProviderId = ProviderId };
            Leases.Add(lease);
            return Task.FromResult(Result<IOcrEngineLease>.Success(lease));
        }
    }

    private sealed class Lease : IOcrEngineLease
    {
        public static OcrEngineDefinition Metadata => new("test-lifetime", "Lifetime test", OcrAdapterKind.LocalLibrary,
            "test-model", [OcrInputKinds.PageImage]);

        public readonly TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayDisposal;
        public int Disposed;
        public OcrEngineDefinition Definition => Metadata;
        public string EngineId => Metadata.EngineId;
        public string? ProviderId { get; init; }
        public bool IsBuiltIn => false;
        public string? EngineExtensionId => "test-lifetime";
        public string? EngineFingerprint => "lifetime-test";
        public int? EngineConfigurationVersion => 1;
        public string? ModelIdentity => "test-model";
        public string? ModelDigest => null;
        public ExtensionConfigSnapshot? EngineConfig => null;
        public string? TextLayerProviderId => null;
        public string? TextLayerExtensionId => null;
        public string? TextLayerFingerprint => null;
        public int? TextLayerConfigurationVersion => null;
        public ExtensionConfigSnapshot? TextLayerConfig => null;

        public Task<Result> CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrDomainRunResult>> RunOcrJobAsync(OcrDomainJobRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The scheduler fixture does not recognize images.");
        }

        public Task<Result<PdfTextLayerResult>> ParseTextLayerAsync(DocumentInstanceId documentId, PageId pageId,
            int pageIndex, DocumentTreeRevisionId revisionId, NormalizedBBox? region,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("No text provider in scheduler fixture.");
        }

        public async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();
            if (DelayDisposal)
            {
                await AllowDispose.Task;
            }

            Interlocked.Increment(ref Disposed);
        }
    }
}
