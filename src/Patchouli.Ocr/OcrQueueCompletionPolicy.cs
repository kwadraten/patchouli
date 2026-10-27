using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

/// <summary>
/// Pure settlement rules for one finished worker attempt. Given the live task row, the claimed
/// attempt snapshot, the worker's result and the run bound through the execution context, it
/// produces the next task state: succeeded, cancelled, requeued with a backoff, or terminally
/// failed/blocked. The caller must first verify <see cref="OcrQueueLifecyclePolicy.IsLiveClaim"/>
/// so a removed or superseded attempt cannot mutate its replacement.
/// </summary>
internal static class OcrQueueCompletionPolicy
{
    public static OcrQueueTask SettleAttempt(OcrQueueTask live, OcrQueueTask claimed,
        OcrQueueExecutionResult result, OcrRunId? boundRunId, IOcrRetryPolicy retry, DateTimeOffset now)
    {
        // The run bound through the execution context (or explicitly retained on the claim)
        // owns this attempt; a worker result naming a different run is a contradiction that
        // fails the attempt, not a change of ownership.
        OcrRunId? effectiveRunId = boundRunId ?? live.RunId;
        if (effectiveRunId is { } owned && result.RunId is { } reportedRunId && reportedRunId != owned)
        {
            result = new OcrQueueExecutionResult(false, false, "run_identity_mismatch",
                $"The OCR worker reported run {reportedRunId} but this attempt is bound to run {owned}.",
                owned, result.CompletedPageCount, result.FailedPageCount);
        }
        else
        {
            effectiveRunId ??= result.RunId;
        }

        if (result.Cancelled)
        {
            return live with
            {
                State = OcrQueueTaskState.Cancelled,
                RunId = effectiveRunId,
                CompletedPageCount = result.CompletedPageCount > 0
                    ? result.CompletedPageCount
                    : claimed.CompletedPageCount,
                FailedPageCount = result.FailedPageCount,
                UpdatedAt = now
            };
        }

        if (result.Succeeded)
        {
            return live with
            {
                State = OcrQueueTaskState.Succeeded,
                RunId = effectiveRunId,
                CompletedPageCount = result.CompletedPageCount > 0
                    ? result.CompletedPageCount
                    : claimed.CompletedPageCount,
                FailedPageCount = result.FailedPageCount,
                UpdatedAt = now
            };
        }

        if (retry.ShouldRetry(claimed, result.ErrorCode))
        {
            int completed = result.CompletedPageCount > 0
                ? result.CompletedPageCount
                : claimed.CompletedPageCount;
            bool retainRecognition = effectiveRunId is not null && result.FailedPageCount == 0 &&
                                     completed >= claimed.PageIds.Count;
            return live with
            {
                State = OcrQueueTaskState.Queued,
                AttemptCount = claimed.AttemptCount + 1,
                ScheduledAfter = now + retry.GetNextDelay(claimed.AttemptCount + 1),
                // Recognition retries bind a new run. Post-recognition adoption/index retries
                // retain completed recognition, which the executor revalidates.
                RunId = retainRecognition ? effectiveRunId : null,
                CompletedPageCount = retainRecognition ? completed : 0,
                FailedPageCount = 0,
                LastErrorCode = result.ErrorCode,
                LastErrorMessage = result.ErrorMessage,
                UpdatedAt = now
            };
        }

        return live with
        {
            State = retry.Classify(result.ErrorCode) == OcrRetryClassification.ManualRepairRequired
                ? OcrQueueTaskState.Blocked
                : OcrQueueTaskState.Failed,
            RunId = effectiveRunId,
            CompletedPageCount = result.CompletedPageCount > 0
                ? result.CompletedPageCount
                : claimed.CompletedPageCount,
            FailedPageCount = result.FailedPageCount,
            LastErrorCode = result.ErrorCode,
            LastErrorMessage = result.ErrorMessage,
            UpdatedAt = now
        };
    }

    /// <summary>The acceptance-time generation pin ends when the attempt settles terminally or
    /// the row is gone; a requeued attempt keeps its pin so retries re-run the same selection.</summary>
    public static bool ReleasesGenerationPin(OcrQueueTask? updated)
    {
        return updated is null || OcrQueueLifecyclePolicy.IsTerminal(updated.State);
    }
}
