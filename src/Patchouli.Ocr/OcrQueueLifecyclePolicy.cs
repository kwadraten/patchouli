using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

/// <summary>
/// Pure lifecycle rules for queue tasks: state classification, attempt ownership, and the
/// record projections for acceptance, claiming, cancellation, rejection and manual retry.
/// The scheduler applies the returned records to its dictionaries; these functions never
/// mutate state themselves.
/// </summary>
internal static class OcrQueueLifecyclePolicy
{
    /// <summary>Settled states: nothing more will run for the task and cancellation is refused.</summary>
    public static bool IsTerminal(string state)
    {
        return state is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
            or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked;
    }

    /// <summary>States a task list still shows when completed items are filtered out.</summary>
    public static bool IsUnfinished(string state)
    {
        return state is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused;
    }

    /// <summary>Queued or running work; used by projections that treat blocked tasks separately.</summary>
    public static bool IsInFlight(string state)
    {
        return state is OcrQueueTaskState.Queued or OcrQueueTaskState.Running;
    }

    public static bool IsRetryableState(string state)
    {
        return state is OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked or OcrQueueTaskState.Cancelled;
    }

    /// <summary>
    /// Finalization belongs to the live claimed attempt: a task removed or superseded while its
    /// worker unwound must not be resurrected or overwritten.
    /// </summary>
    public static bool IsLiveClaim(OcrQueueTask live, int attempt)
    {
        return live.State == OcrQueueTaskState.Running && live.AttemptCount == attempt;
    }

    /// <summary>Queued or paused tasks without a running attempt are cancelled on shutdown.</summary>
    public static bool IsShutdownCancellable(OcrQueueTask task, bool isRunning)
    {
        return !isRunning && task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Paused;
    }

    public static bool IsAcceptableRequest(OcrQueueTaskRequest request, int pageCount)
    {
        return pageCount > 0 && !string.IsNullOrWhiteSpace(request.TaskKind) &&
               !string.IsNullOrWhiteSpace(request.EngineId) && !string.IsNullOrWhiteSpace(request.AdapterKind);
    }

    public static OcrQueueTask CreateAccepted(OcrQueueTaskRequest request, OcrQueueTaskId taskId,
        LibraryId libraryId, IReadOnlyList<PageId> pages, DateTimeOffset now)
    {
        return new OcrQueueTask(taskId, libraryId, request.DocumentInstanceId, request.PresetId,
            pages, request.TaskKind, request.EngineId, request.AdapterKind, request.ProviderId,
            request.Priority, now, now, OcrQueueTaskState.Queued, 0, Math.Max(1, request.MaxAttempts), null, null,
            null, null, RegionBBox: request.RegionBBox,
            CommitOnCompletion: request.CommitOnCompletion, PresetVersionId: request.PresetVersionId,
            Source: request.Source);
    }

    /// <summary>A claim keeps the retained run and completed-page count; failure tallies reset.</summary>
    public static OcrQueueTask Claim(OcrQueueTask candidate, DateTimeOffset now)
    {
        return candidate with
        {
            State = OcrQueueTaskState.Running,
            UpdatedAt = now,
            FailedPageCount = 0
        };
    }

    public static OcrQueueTask CancelWaiting(OcrQueueTask task, DateTimeOffset now)
    {
        return task with { State = OcrQueueTaskState.Cancelled, UpdatedAt = now };
    }

    /// <summary>
    /// A request rejected at acceptance (generation pin failure) still leaves a durable failed
    /// row: the caller's transient status message disappears, but the queue must keep why
    /// nothing ran. The row is terminal — retrying goes through the normal failed-task retry
    /// path and re-pins a generation.
    /// </summary>
    public static OcrQueueTask RejectAtAcceptance(OcrQueueTask task, string errorCode, string errorMessage,
        DateTimeOffset now)
    {
        return task with
        {
            State = OcrQueueTaskState.Failed,
            LastErrorCode = errorCode,
            LastErrorMessage = errorMessage,
            UpdatedAt = now
        };
    }

    public static OcrQueueTask FailWorkerCleanup(OcrQueueTask live, DateTimeOffset now)
    {
        return live with
        {
            State = OcrQueueTaskState.Failed,
            LastErrorCode = "worker_cleanup_failed",
            LastErrorMessage = "The OCR worker could not finish releasing its resources.",
            UpdatedAt = now
        };
    }

    /// <summary>
    /// A task reaches the Cancelled state only after its worker has quiesced (queued tasks flip
    /// immediately; running tasks flip when the attempt settles under the state lock), so
    /// retrying cannot race the old worker. A completed run is retained only when the retry
    /// keeps the same preset version (an explicit different version must re-recognize) and the
    /// bound run already covered every requested page — the executor revalidates coverage
    /// against the actual page results.
    /// </summary>
    public static OcrQueueTask CreateRetry(OcrQueueTask task, OcrPresetVersionId? presetVersionId,
        OcrQueueTaskId newTaskId, DateTimeOffset now)
    {
        bool versionMatches = presetVersionId is null || presetVersionId == task.PresetVersionId;
        bool preserveRunId = task.RunId is not null && versionMatches &&
                             task.FailedPageCount == 0 &&
                             task.CompletedPageCount >= task.PageIds.Count;
        return task with
        {
            TaskId = newTaskId,
            Priority = OcrQueuePriority.BackgroundRetry,
            CreatedAt = now,
            UpdatedAt = now,
            State = OcrQueueTaskState.Queued,
            AttemptCount = 0,
            RetryOfTaskId = task.TaskId,
            LastErrorCode = null,
            LastErrorMessage = null,
            ScheduledAfter = null,
            RunId = preserveRunId ? task.RunId : null,
            CompletedPageCount = preserveRunId ? task.CompletedPageCount : 0,
            FailedPageCount = 0,
            PresetVersionId = presetVersionId ?? task.PresetVersionId,
            Source = OcrQueueSource.Retry
        };
    }
}
