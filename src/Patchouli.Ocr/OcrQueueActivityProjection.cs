using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

internal sealed record OcrTaskActivityState(
    OcrQueueTaskId TaskId,
    string Detail,
    bool IsPaused,
    string? PauseReason,
    bool IsWaitingRetry,
    string? RetryReason);

/// <summary>
/// Pure projection from queue tasks to host-activity states: which tasks surface as activity
/// items and how each one is described (ready, paused, waiting on a deferred retry, or blocked
/// awaiting manual repair).
/// </summary>
internal static class OcrQueueActivityProjection
{
    /// <summary>
    /// In-flight tasks always surface. A blocked task surfaces only while no queued/running
    /// retry of it exists — once a manual retry is accepted, the retried copy carries the
    /// activity and the blocked original drops out.
    /// </summary>
    public static OcrQueueTask[] SelectVisible(IEnumerable<OcrQueueTask> tasks)
    {
        OcrQueueTask[] snapshot = tasks as OcrQueueTask[] ?? tasks.ToArray();
        HashSet<OcrQueueTaskId> retriedTaskIds = snapshot
            .Where(task => task.RetryOfTaskId is not null && OcrQueueLifecyclePolicy.IsInFlight(task.State))
            .Select(task => task.RetryOfTaskId!.Value)
            .ToHashSet();
        return snapshot
            .Where(task => OcrQueueLifecyclePolicy.IsInFlight(task.State) ||
                           (task.State == OcrQueueTaskState.Blocked && !retriedTaskIds.Contains(task.TaskId)))
            .ToArray();
    }

    public static OcrTaskActivityState Describe(OcrQueueTask task, DateTimeOffset now,
        IReadOnlySet<string> pauses)
    {
        if (task.State == OcrQueueTaskState.Blocked)
        {
            string reason = string.IsNullOrWhiteSpace(task.LastErrorMessage)
                ? "需要人工处理后重试"
                : $"需要人工处理：{task.LastErrorMessage}";
            return new OcrTaskActivityState(task.TaskId, "等待人工处理", true, reason, false, null);
        }

        bool isPaused = task.State == OcrQueueTaskState.Queued && OcrQueuePausePolicy.IsPaused(task, pauses);
        bool isWaitingRetry = task.State == OcrQueueTaskState.Queued && !isPaused &&
                              task.ScheduledAfter is { } scheduledAfter && scheduledAfter > now;
        string detail = task.State == OcrQueueTaskState.Running
            ? "正在执行"
            : isWaitingRetry
                ? "等待延迟重试"
                : isPaused
                    ? "已暂停"
                    : "等待调度";
        string? retryReason = isWaitingRetry
            ? $"将在 {task.ScheduledAfter!.Value:HH:mm:ss} 重试"
            : null;
        return new OcrTaskActivityState(task.TaskId, detail, isPaused, isPaused ? "OCR 队列已暂停" : null,
            isWaitingRetry, retryReason);
    }
}
