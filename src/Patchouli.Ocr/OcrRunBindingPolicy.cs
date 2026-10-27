using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

/// <summary>
/// Pure validation rules for binding a freshly created run to a queue attempt. A binding is
/// accepted only while that exact attempt is still running: a repeated identical binding is
/// idempotent, a different run for the same attempt conflicts, and a report from a superseded
/// or removed attempt is rejected so it cannot mutate the task that replaced it.
/// </summary>
internal static class OcrRunBindingPolicy
{
    /// <summary>
    /// <paramref name="UpdatedTask"/> is the task row to store (null when nothing changed);
    /// <paramref name="RecordsBinding"/> tells the caller to record the attempt→run pair even
    /// when the row does not change, so a later report naming a different run conflicts rather
    /// than silently replacing the ownership this attempt already holds.
    /// </summary>
    public sealed record Decision(Result Result, OcrQueueTask? UpdatedTask, bool RecordsBinding);

    public static Decision Evaluate(OcrQueueTask? task, int attempt, OcrRunId runId,
        OcrPresetVersionId? presetVersionId, DateTimeOffset now)
    {
        if (task is null)
        {
            return new Decision(Result.Failure(AppErrorCodes.InvalidState,
                "The OCR queue task was removed before its run could be bound."), null, false);
        }

        if (task.State != OcrQueueTaskState.Running || task.AttemptCount != attempt)
        {
            return new Decision(Result.Failure(AppErrorCodes.InvalidState,
                "The OCR run belongs to a superseded queue attempt."), null, false);
        }

        if (task.PresetVersionId is { } expectedVersion && presetVersionId is { } actualVersion &&
            expectedVersion != actualVersion)
        {
            return new Decision(Result.Failure(AppErrorCodes.Conflict,
                "The OCR run does not use this task's fixed preset version."), null, false);
        }

        if (task.RunId == runId && (task.PresetVersionId is not null || presetVersionId is null))
        {
            // Idempotent repeat (including a retained run re-reporting itself).
            return new Decision(Result.Success(), null, true);
        }

        if (task.RunId is not null && task.RunId != runId)
        {
            return new Decision(Result.Failure(AppErrorCodes.Conflict,
                "A different OCR run is already bound to this queue attempt."), null, false);
        }

        // Legacy unpinned tasks learn the actual durable version here. A later retry may pin
        // that same version without discarding already completed recognition.
        return new Decision(Result.Success(), task with
        {
            RunId = runId,
            PresetVersionId = task.PresetVersionId ?? presetVersionId,
            UpdatedAt = now
        }, true);
    }
}
