using FluentAssertions;
using Patchouli.Agent;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Host.Workflows;
using Patchouli.Ocr;
using Patchouli.Workflows;

namespace Patchouli.Tests;

/// <summary>
///     Verifies the S4 host run-event wakeup (ADR 0036): an armed <c>WaitRunEvent</c> is satisfied by
///     the OCR queue's completion notification — never a poll —, a session control event cancels the
///     wait with an explicit cancellation result, host shutdown cancels every armed wait, concurrent
///     sessions wake independently and the workflow adapter completes a deferred wait through the real
///     host primitives instead of refusing it.
/// </summary>
/// <remarks>The OCR queue, the page listing and the preset service are stubs; no scheduler state is touched.</remarks>
public sealed class HostPrimitivesWakeupTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Enqueue_then_completion_wakes_the_armed_wait_with_a_structured_payload()
    {
        DocumentInstanceId document = DocumentInstanceId.New();
        StubOcrQueueScheduler queue = new();
        OcrQueueHostPrimitives primitives = Primitives(queue, document);

        AgentOcrEnqueueOutcome enqueued =
            await primitives.EnqueueOcrAsync(Id(document), "1-2", CancellationToken.None);
        enqueued.Accepted.Should().BeTrue();
        enqueued.Payload.Should().Contain(OcrQueueHostPrimitives.OcrRunUriPrefix + Id(document));
        queue.Enqueued.Should().ContainSingle();
        queue.Enqueued[0].PageIds.Should().HaveCount(2);
        queue.Started.Should().BeTrue();

        Task<Result<string>> wait = primitives.WaitRunEventAsync(
            OcrQueueHostPrimitives.OcrRunUriPrefix + Id(document), 1L, CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        queue.Complete(queue.Enqueued[0].TaskId, OcrQueueTaskState.Succeeded);

        Result<string> result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("\"state\":\"succeeded\"");
        result.Value.Should().Contain(Id(document));
    }

    [Fact]
    public async Task Wait_arms_against_a_task_that_is_already_terminal_without_hanging()
    {
        DocumentInstanceId document = DocumentInstanceId.New();
        StubOcrQueueScheduler queue = new();
        OcrQueueHostPrimitives primitives = Primitives(queue, document);
        await primitives.EnqueueOcrAsync(Id(document), string.Empty, CancellationToken.None);
        queue.Complete(queue.Enqueued[0].TaskId, OcrQueueTaskState.Succeeded);

        // The completion notification fired before the wait armed; the race check against the queue's
        // current state must satisfy the wait without waiting for another event.
        Result<string> result = await primitives
            .WaitRunEventAsync(OcrQueueHostPrimitives.OcrRunUriPrefix + Id(document), 1L, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain("\"state\":\"succeeded\"");
    }

    [Fact]
    public async Task Session_cancellation_completes_the_wait_with_a_cancellation_event()
    {
        DocumentInstanceId document = DocumentInstanceId.New();
        StubOcrQueueScheduler queue = new();
        OcrQueueHostPrimitives primitives = Primitives(queue, document);
        using CancellationTokenSource session = new();

        Task<Result<string>> wait = primitives.WaitRunEventAsync(
            OcrQueueHostPrimitives.OcrRunUriPrefix + Id(document), 1L, session.Token);
        await session.CancelAsync();

        Result<string> result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(OcrQueueHostPrimitives.WaitCancelledCode);

        // The disarmed wait no longer reacts to a completion event.
        queue.CompleteFor(document, OcrQueueTaskState.Succeeded);
        await Task.Delay(50);
        result.ErrorCode.Should().Be(OcrQueueHostPrimitives.WaitCancelledCode);
    }

    [Fact]
    public async Task Concurrent_waits_on_distinct_documents_wake_independently()
    {
        DocumentInstanceId first = DocumentInstanceId.New();
        DocumentInstanceId second = DocumentInstanceId.New();
        StubOcrQueueScheduler queue = new();
        OcrQueueHostPrimitives primitives = new(
            queue,
            new StubPageService((first, PagesFor(first, 2)), (second, PagesFor(second, 2))),
            new StubPresetService(),
            () => OcrEngineIds.RapidOcr);

        Task<Result<string>> waitFirst = primitives.WaitRunEventAsync(
            OcrQueueHostPrimitives.OcrRunUriPrefix + Id(first), 1L, CancellationToken.None);
        Task<Result<string>> waitSecond = primitives.WaitRunEventAsync(
            OcrQueueHostPrimitives.OcrRunUriPrefix + Id(second), 2L, CancellationToken.None);

        queue.CompleteFor(first, OcrQueueTaskState.Succeeded);
        Result<string> firstResult = await waitFirst.WaitAsync(TimeSpan.FromSeconds(10));
        firstResult.IsSuccess.Should().BeTrue();
        firstResult.Value.Should().Contain(Id(first));
        waitSecond.IsCompleted.Should().BeFalse();

        queue.CompleteFor(second, OcrQueueTaskState.Failed, "ocr-engine", "recognizer crashed");
        Result<string> secondResult = await waitSecond.WaitAsync(TimeSpan.FromSeconds(10));
        secondResult.IsSuccess.Should().BeTrue();
        secondResult.Value.Should().Contain(Id(second));
        secondResult.Value.Should().Contain("\"state\":\"failed\"");
        secondResult.Value.Should().Contain("recognizer crashed");
    }

    [Fact]
    public async Task Host_shutdown_cancels_every_armed_wait_explicitly()
    {
        DocumentInstanceId first = DocumentInstanceId.New();
        DocumentInstanceId second = DocumentInstanceId.New();
        StubOcrQueueScheduler queue = new();
        using CancellationTokenSource hostLifetime = new();
        OcrQueueHostPrimitives primitives = new(
            queue,
            new StubPageService((first, PagesFor(first, 1)), (second, PagesFor(second, 1))),
            new StubPresetService(),
            () => OcrEngineIds.RapidOcr,
            hostLifetime.Token);

        Task<Result<string>> waitFirst = primitives.WaitRunEventAsync(
            OcrQueueHostPrimitives.OcrRunUriPrefix + Id(first), 1L, CancellationToken.None);
        Task<Result<string>> waitSecond = primitives.WaitRunEventAsync(
            OcrQueueHostPrimitives.OcrRunUriPrefix + Id(second), 2L, CancellationToken.None);

        await hostLifetime.CancelAsync();

        Result<string> firstResult = await waitFirst.WaitAsync(TimeSpan.FromSeconds(10));
        Result<string> secondResult = await waitSecond.WaitAsync(TimeSpan.FromSeconds(10));
        firstResult.ErrorCode.Should().Be(OcrQueueHostPrimitives.WaitHostShutdownCode);
        secondResult.ErrorCode.Should().Be(OcrQueueHostPrimitives.WaitHostShutdownCode);
    }

    [Fact]
    public async Task Deferred_wait_run_event_is_completed_through_the_host_primitives()
    {
        DocumentInstanceId document = DocumentInstanceId.New();
        StubOcrQueueScheduler queue = new();
        OcrQueueHostPrimitives primitives = Primitives(queue, document);
        WorkflowEffectHostAdapter adapter = new(
            new DeferredInterpreter(),
            _ => "instructions",
            primitives);
        Effect wait = Effect.NewWaitRunEvent(
            EffectId.NewEffectId(1L), WaitId.NewWaitId(7L), OcrQueueHostPrimitives.OcrRunUriPrefix + Id(document));

        Task<WorkflowEffectOutcome> running =
            adapter.ExecuteEffectAsync("session-1", Scope(), wait, CancellationToken.None);
        await Task.Yield();
        running.IsCompleted.Should().BeFalse();

        queue.CompleteFor(document, OcrQueueTaskState.Succeeded);

        WorkflowEffectOutcome outcome = await running.WaitAsync(TimeSpan.FromSeconds(10));
        WorkflowEffectOutcome.CompletedEvent completed =
            outcome.Should().BeOfType<WorkflowEffectOutcome.CompletedEvent>().Subject;
        completed.summary.Should().Contain("host run event");
        Event.RunEvent runEvent = completed.resultEvent.Should().BeOfType<Event.RunEvent>().Subject;
        runEvent.waitId.Item.Should().Be(7L);
        runEvent.payload.Should().Contain("\"state\":\"succeeded\"");
    }

    private static OcrQueueHostPrimitives Primitives(StubOcrQueueScheduler queue, DocumentInstanceId document)
    {
        return new OcrQueueHostPrimitives(
            queue,
            new StubPageService((document, PagesFor(document, 3))),
            new StubPresetService(),
            () => OcrEngineIds.RapidOcr);
    }

    private static string Id(DocumentInstanceId document)
    {
        return document.Value.ToString();
    }

    private static Page[] PagesFor(DocumentInstanceId document, int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => new Page(PageId.New(), document, index, null, null, null, 0, "pdf", null, null,
                "v1", null, Now, Now))
            .ToArray();
    }

    private static WorkflowEffectScope Scope()
    {
        return new WorkflowEffectScope(AgentCoreModule.initial);
    }

    /// <summary>The queue stub: it records enqueues and raises <see cref="IOcrQueueScheduler.Changed" />.</summary>
    private sealed class StubOcrQueueScheduler : IOcrQueueScheduler
    {
        private readonly Dictionary<OcrQueueTaskId, OcrQueueTask> _tasks = [];

        public event EventHandler<OcrQueueChangedEventArgs>? Changed;

        public List<OcrQueueTask> Enqueued { get; } = [];

        public bool Started { get; private set; }

        public void Complete(OcrQueueTaskId taskId, string state, string? errorCode = null,
            string? errorMessage = null)
        {
            OcrQueueTask terminal = _tasks[taskId] with
            {
                State = state,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastErrorCode = errorCode,
                LastErrorMessage = errorMessage
            };
            _tasks[taskId] = terminal;
            Changed?.Invoke(this, new OcrQueueChangedEventArgs(terminal, OcrQueueChangeKind.Updated));
        }

        public void CompleteFor(DocumentInstanceId document, string state, string? errorCode = null,
            string? errorMessage = null)
        {
            OcrQueueTask? task =
                _tasks.Values.FirstOrDefault(candidate => candidate.DocumentInstanceId == document);
            if (task is null)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                task = new OcrQueueTask(
                    OcrQueueTaskId.New(), LibraryId.New(), document, OcrPresetId.New(), [],
                    OcrQueueTaskKind.Document, OcrEngineIds.RapidOcr, OcrAdapterKind.LocalLibrary, null,
                    OcrQueuePriority.UserStartedDocument, now, now, OcrQueueTaskState.Queued, 0, 3, null, null,
                    null, null, null, null);
                _tasks[task.TaskId] = task;
            }

            Complete(task.TaskId, state, errorCode, errorMessage);
        }

        public Task<Result<OcrQueueTask>> EnqueueAsync(OcrQueueTaskRequest request, CancellationToken c = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            OcrQueueTask task = new(
                OcrQueueTaskId.New(), LibraryId.New(), request.DocumentInstanceId, request.PresetId,
                request.PageIds, request.TaskKind, request.EngineId, request.AdapterKind, request.ProviderId,
                request.Priority, now, now, OcrQueueTaskState.Queued, 0, request.MaxAttempts, null, null, null,
                null, request.ImagePath, request.Dpi);
            _tasks[task.TaskId] = task;
            Enqueued.Add(task);
            Changed?.Invoke(this, new OcrQueueChangedEventArgs(task, OcrQueueChangeKind.Enqueued));
            return Task.FromResult(Result<OcrQueueTask>.Success(task));
        }

        public Task<Result<OcrQueueTask>> EnqueueDocumentAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, string engineId, string adapterKind, string? providerId, string priority,
            CancellationToken c = default)
        {
            return EnqueueAsync(new OcrQueueTaskRequest(d, p, pages, OcrQueueTaskKind.Document, engineId,
                adapterKind, providerId, priority), c);
        }

        public Task StartAsync(CancellationToken c = default)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task<Result<IReadOnlyList<OcrQueueTask>>> ListTasksAsync(OcrQueueTaskFilter f,
            CancellationToken c = default)
        {
            IReadOnlyList<OcrQueueTask> tasks = _tasks.Values.ToArray();
            return Task.FromResult(Result<IReadOnlyList<OcrQueueTask>>.Success(tasks));
        }

        public Task<Result<OcrQueueTask>> GetTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
        {
            return Task.FromResult(_tasks.TryGetValue(id, out OcrQueueTask? task)
                ? Result<OcrQueueTask>.Success(task)
                : Result<OcrQueueTask>.Failure("NOT_FOUND", "no such task"));
        }

        public Task StopAsync(CancellationToken c = default)
        {
            return Task.CompletedTask;
        }

        public Task WaitForIdleAsync(CancellationToken c = default)
        {
            return Task.CompletedTask;
        }

        public Task<Result> PauseAsync(string scope, string? target = null, CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ResumeAsync(string scope, string? target = null, CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> CancelTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrQueueTask>> RetryTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("UNSUPPORTED", "the stub never retries"));
        }

        public Task<Result<OcrQueueStatus>> GetQueueStatusAsync(CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueStatus>.Success(new OcrQueueStatus(false, 0, 0, 0, 0, 0, 0, [],
                OcrQueueLimits.Default, new Dictionary<string, int>(), new Dictionary<string, int>())));
        }

        public Task RunOneSchedulingTickAsync(CancellationToken c = default)
        {
            return Task.CompletedTask;
        }

        public OcrTaskProgressReport? GetTaskProgress(OcrQueueTaskId taskId)
        {
            return null;
        }

        public DateTimeOffset? GetTaskFinishedAt(OcrQueueTaskId taskId)
        {
            return null;
        }

        public void ClearFinishedTasks()
        {
        }

        public void UpdateLimits(OcrQueueLimits limits)
        {
        }

        public void WakeScheduler()
        {
        }

        public Task<Result<OcrQueueTask>> EnqueueMockPagesAsync(DocumentInstanceId d, OcrPresetId p,
            IReadOnlyList<PageId> pages, string priority, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("UNSUPPORTED", "the stub never enqueues mock pages"));
        }

        public Task<Result<OcrQueueTask>> EnqueueImagePageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            string imagePath, string priority, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("UNSUPPORTED", "the stub never enqueues image pages"));
        }

        public Task<Result<OcrQueueTask>> EnqueueRenderedPdfPageAsync(DocumentInstanceId d, OcrPresetId p,
            PageId page, int dpi, string priority, CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("UNSUPPORTED", "the stub never enqueues pdf pages"));
        }

        public Task<Result<OcrQueueTask>> EnqueueRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
            NormalizedBBox regionBBox, string engineId, string adapterKind, string? providerId, string priority,
            CancellationToken c = default)
        {
            return Task.FromResult(Result<OcrQueueTask>.Failure("UNSUPPORTED", "the stub never enqueues regions"));
        }
    }

    private sealed class StubPageService(params (DocumentInstanceId Document, Page[] Pages)[] documents)
        : IPageService
    {
        private readonly IReadOnlyDictionary<DocumentInstanceId, Page[]> _documents =
            documents.ToDictionary(entry => entry.Document, entry => entry.Pages);

        public Task<Result<IReadOnlyList<Page>>> ListPagesAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_documents.TryGetValue(documentInstanceId, out Page[]? pages)
                ? Result<IReadOnlyList<Page>>.Success(pages)
                : Result<IReadOnlyList<Page>>.Failure("NOT_FOUND", "no pages for the document"));
        }

        public Task<Result<Page>> CreatePageAsync(DocumentInstanceId documentInstanceId, int pageIndex,
            string? pageLabel, double? width, double? height, int rotation, string coordinateBasis,
            double? basisWidth, double? basisHeight, string rendererBasisVersion, string? sourceFileHash,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<Page>.Failure("UNSUPPORTED", "the stub never creates pages"));
        }

        public Task<Result<Page>> GetPageAsync(PageId pageId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<Page>.Failure("UNSUPPORTED", "the stub never reads one page"));
        }

        public Task<Result<FileAssetId>> GetFileAssetIdAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<FileAssetId>.Failure("UNSUPPORTED", "the stub never resolves file assets"));
        }
    }

    private sealed class StubPresetService : IOcrPresetService
    {
        private static readonly OcrPreset Preset = new(
            OcrPresetId.New(), LibraryId.New(), "test preset", null, null, false, Now, Now);

        public Task<Result<OcrPreset?>> FindActivePresetByEngineIdAsync(string engineId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrPreset?>.Success(Preset));
        }

        public Task<Result<OcrPreset>> CreatePresetAsync(string name, string? description, string engineId,
            string modelId, string? modelPath, string parametersJson, bool applyOnSuccess,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrPreset>.Success(Preset));
        }

        public Task<Result<OcrPreset>> GetPresetAsync(OcrPresetId presetId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrPreset>.Success(Preset));
        }

        public Task<Result<OcrPresetVersion>> CreatePresetVersionAsync(OcrPresetId presetId, string engineId,
            string modelId, string? modelPath, string parametersJson, bool applyOnSuccess,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrPresetVersion>.Failure("UNSUPPORTED", "the stub never versions presets"));
        }

        public Task<Result<OcrPresetVersion>> GetCurrentVersionAsync(OcrPresetId presetId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrPresetVersion>.Failure("UNSUPPORTED", "the stub never versions presets"));
        }

        public Task<Result<OcrPresetVersion>> RebindModelPathAsync(OcrPresetId presetId, string newModelPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrPresetVersion>.Failure("UNSUPPORTED", "the stub never rebinds models"));
        }

        public Task<Result> ArchivePresetAsync(OcrPresetId presetId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>An interpreter that parks every effect as deferred, as a future event-driven interpreter would.</summary>
    private sealed class DeferredInterpreter : IAgentEffectInterpreter
    {
        public Task<AgentEffectOutcome> ExecuteAsync(AgentEffectContext context, Effect effect,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new AgentEffectOutcome(AgentEffectDisposition.Deferred, null, false, null,
                "waiting for a host control event"));
        }
    }
}
