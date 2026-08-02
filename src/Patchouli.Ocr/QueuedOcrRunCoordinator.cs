using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

/// <summary>
/// Deep <see cref="IOcrRunCoordinator"/> façade: every awaitable run is enqueued on the shared
/// <see cref="IOcrQueueScheduler"/> and awaited until the queue task reaches a terminal state.
/// </summary>
public sealed class QueuedOcrRunCoordinator : IOcrRunCoordinator
{
    private readonly IOcrRunEngine _engine;

    public QueuedOcrRunCoordinator(IOcrQueueScheduler queue, IOcrRunEngine engine)
    {
        Queue = queue;
        _engine = engine;
        _engine.AdoptionCommitted += (_, args) => AdoptionCommitted?.Invoke(this, args);
    }

    public IOcrQueueScheduler Queue { get; }

    /// <inheritdoc />
    public event EventHandler<OcrAdoptionCommittedEventArgs>? AdoptionCommitted;

    public async Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        string engineId,
        string adapterKind,
        string? providerId,
        string priority,
        CancellationToken cancellationToken = default)
    {
        Result<OcrQueueTask> queued = await Queue.EnqueueDocumentAsync(
            documentInstanceId, presetId, pageIds, engineId, adapterKind, providerId, priority, cancellationToken);
        if (queued.IsSuccess)
        {
            await Queue.StartAsync(cancellationToken);
        }

        return queued;
    }

    public async Task<Result<OcrRun>> RunPresetOnDocumentAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        Result<IReadOnlyList<PageId>> pages = await _engine.ListPageIdsAsync(documentInstanceId, cancellationToken);
        if (pages.IsFailure)
        {
            return Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        if (pages.Value.Count == 0)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                "Document instance has no pages to OCR.");
        }

        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueDocumentAsync(documentInstanceId, presetId, pages.Value, version.Value.EngineId,
                AdapterKindFor(version.Value), ProviderIdFor(version.Value), OcrQueuePriority.UserStartedDocument,
                cancellationToken),
            cancellationToken);
    }

    public async Task<Result<OcrRun>> RunPresetOnPagesAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueAsync(
                new OcrQueueTaskRequest(documentInstanceId, presetId, pageIds, OcrQueueTaskKind.MockPages,
                    version.Value.EngineId, AdapterKindFor(version.Value), ProviderIdFor(version.Value),
                    PriorityFor(pageIds)),
                cancellationToken),
            cancellationToken);
    }

    public async Task<Result<OcrRun>> RunPresetOnRegionAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default)
    {
        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueRegionAsync(documentInstanceId, presetId, pageId, regionBBox,
                version.Value.EngineId, AdapterKindFor(version.Value), ProviderIdFor(version.Value),
                OcrQueuePriority.InteractiveCurrentPage, cancellationToken),
            cancellationToken);
    }

    public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default)
    {
        // Region candidates are ephemeral: the engine persists no run for them, so there is no
        // status to track and queueing is not required for status correctness.
        return _engine.RecognizeRegionCandidateAsync(documentInstanceId, presetId, pageId, regionBBox,
            cancellationToken);
    }

    public async Task<Result<OcrRun>> RunPresetOnImagePageAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueImagePageAsync(documentInstanceId, presetId, pageId, imagePath,
                OcrQueuePriority.InteractiveCurrentPage, cancellationToken),
            cancellationToken);
    }

    public async Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        int dpi = 200,
        CancellationToken cancellationToken = default)
    {
        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueRenderedPdfPageAsync(documentInstanceId, presetId, pageId, dpi,
                OcrQueuePriority.InteractiveCurrentPage, cancellationToken),
            cancellationToken);
    }

    public Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return _engine.CancelRunAsync(runId, cancellationToken);
    }

    public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        return _engine.UnsetCurrentOcrAsync(documentInstanceId, cancellationToken);
    }

    public Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return _engine.HideOcrRunAsync(runId, cancellationToken);
    }

    public Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId runId,
        IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default)
    {
        return _engine.AdoptCandidateRunAsync(runId, selectedPages, cancellationToken);
    }

    public Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return _engine.GetRunAsync(runId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId,
        CancellationToken cancellationToken = default)
    {
        return _engine.ListPageResultsAsync(runId, cancellationToken);
    }

    private async Task<Result<OcrRun>> EnqueueAndAwaitAsync(
        Func<IOcrQueueScheduler, Task<Result<OcrQueueTask>>> enqueue,
        CancellationToken cancellationToken)
    {
        Result<OcrQueueTask> queued = await enqueue(Queue);
        if (queued.IsFailure)
        {
            return Result<OcrRun>.Failure(queued.ErrorCode!, queued.ErrorMessage!);
        }

        OcrQueueTaskId taskId = queued.Value.TaskId;
        TaskCompletionSource<OcrQueueTask> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<OcrQueueChangedEventArgs> handler = (_, args) =>
        {
            if (args.Task is { } task && task.TaskId == taskId && IsTerminal(task.State))
            {
                completion.TrySetResult(task);
            }
        };

        Queue.Changed += handler;
        try
        {
            await Queue.StartAsync(cancellationToken);
            Result<OcrQueueTask> current = await Queue.GetTaskAsync(taskId, cancellationToken);
            if (current.IsSuccess && IsTerminal(current.Value.State))
            {
                completion.TrySetResult(current.Value);
            }

            OcrQueueTask terminal;
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                try
                {
                    terminal = await completion.Task;
                }
                catch (OperationCanceledException)
                {
                    await Queue.CancelTaskAsync(taskId, CancellationToken.None);
                    return Result<OcrRun>.Failure(OcrFailureCode.Cancelled, "OCR run was cancelled.");
                }
            }

            return terminal.State switch
            {
                OcrQueueTaskState.Succeeded => terminal.RunId is null
                    ? Result<OcrRun>.Failure(AppErrorCodes.InvalidState,
                        "OCR queue task succeeded without a run id.")
                    : await _engine.GetRunAsync(terminal.RunId.Value, cancellationToken),
                OcrQueueTaskState.Cancelled => Result<OcrRun>.Failure(OcrFailureCode.Cancelled,
                    "OCR run was cancelled."),
                _ => Result<OcrRun>.Failure(terminal.LastErrorCode ?? "ocr_failed",
                    terminal.LastErrorMessage ?? "OCR task failed.")
            };
        }
        finally
        {
            Queue.Changed -= handler;
        }
    }

    private static bool IsTerminal(string state)
    {
        return state is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed or OcrQueueTaskState.Cancelled
            or OcrQueueTaskState.Blocked;
    }

    private static string PriorityFor(IReadOnlyList<PageId> pageIds)
    {
        return pageIds.Count <= 1
            ? OcrQueuePriority.InteractiveCurrentPage
            : OcrQueuePriority.InteractiveSelectedPages;
    }

    private static string AdapterKindFor(OcrPresetVersion version)
    {
        return version.EngineId == OcrEngineIds.MinerU ? OcrAdapterKind.CloudApi : OcrAdapterKind.LocalLibrary;
    }

    private static string? ProviderIdFor(OcrPresetVersion version)
    {
        return version.EngineId == OcrEngineIds.MinerU ? ProviderIds.MinerU : null;
    }
}
