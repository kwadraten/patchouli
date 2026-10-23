using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

/// <summary>
/// Pure OCR queue scheduling decisions: pause matching, dispatch eligibility and capacity,
/// priority scoring, idle detection, and the host-activity projection. The scheduler owns
/// state and locking; every decision that maps queue state to an outcome lives here so it
/// can be tested without timers, executors, or activity trackers.
/// </summary>
internal static class OcrQueueSchedulingRules
{
    internal static bool IsPaused(IReadOnlySet<string> pauses, OcrQueueTask task)
    {
        return pauses.Contains("global:") || pauses.Contains("task:" + task.TaskId) ||
               (pauses.Contains("local:") && task.AdapterKind != OcrAdapterKind.CloudApi) ||
               (pauses.Contains("cloud:") && task.AdapterKind == OcrAdapterKind.CloudApi) ||
               (!string.IsNullOrEmpty(task.ProviderId) &&
                pauses.Contains("provider:" + task.ProviderId));
    }

    /// <summary>A queued task that is past its scheduled delay and not paused can dispatch.</summary>
    internal static bool IsDispatchEligible(OcrQueueTask task, IReadOnlySet<string> pauses, DateTimeOffset now)
    {
        return task.State == OcrQueueTaskState.Queued &&
               (task.ScheduledAfter is null || task.ScheduledAfter <= now) &&
               !IsPaused(pauses, task);
    }

    internal static bool HasDispatchCapacity(
        OcrQueueTask task,
        IReadOnlyList<OcrQueueTask> running,
        OcrQueueLimits limits)
    {
        if (running.Count >= limits.GlobalMaxConcurrent)
        {
            return false;
        }

        bool local = task.AdapterKind != OcrAdapterKind.CloudApi;
        if (local && running.Count(x => x.AdapterKind != OcrAdapterKind.CloudApi) >=
            limits.LocalMaxConcurrent)
        {
            return false;
        }

        if (!local && running.Count(x => x.AdapterKind == OcrAdapterKind.CloudApi) >=
            limits.CloudMaxConcurrent)
        {
            return false;
        }

        if (running.Count(x => x.EngineId == task.EngineId) >= limits.PerEngineMaxConcurrent)
        {
            return false;
        }

        if (task.ProviderId is not null &&
            running.Count(x => x.ProviderId == task.ProviderId) >= limits.PerProviderMaxConcurrent)
        {
            return false;
        }

        return true;
    }

    /// <summary>The queue is idle for this task when it will not dispatch on its own.</summary>
    internal static bool IsIdle(OcrQueueTask task, IReadOnlySet<string> pauses, DateTimeOffset now)
    {
        return task.State != OcrQueueTaskState.Running &&
               (task.State != OcrQueueTaskState.Queued ||
                (task.ScheduledAfter is not null && task.ScheduledAfter > now) ||
                IsPaused(pauses, task));
    }

    internal static int DispatchScore(OcrQueueTask task, DateTimeOffset now)
    {
        return PriorityWeight(task.Priority) * 1000 +
               (int)Math.Min(999, (now - task.CreatedAt).TotalMinutes / 10);
    }

    internal static int PriorityWeight(string priority)
    {
        return priority switch
        {
            OcrQueuePriority.InteractiveCurrentPage => 6,
            OcrQueuePriority.InteractiveSelectedPages => 5,
            OcrQueuePriority.UserStartedDocument => 4,
            OcrQueuePriority.BackgroundRetry => 3,
            OcrQueuePriority.BatchCollection => 2,
            _ => 1
        };
    }

    /// <summary>
    /// The activity states the tracker should currently show: queued and running tasks, plus
    /// blocked tasks that have not been superseded by a queued or running retry.
    /// </summary>
    internal static OcrQueueActivityState[] ActiveStates(
        IEnumerable<OcrQueueTask> tasks,
        IReadOnlySet<string> pauses,
        DateTimeOffset now)
    {
        OcrQueueTask[] snapshot = tasks as OcrQueueTask[] ?? tasks.ToArray();
        HashSet<OcrQueueTaskId> retriedTaskIds = snapshot
            .Where(task => task.RetryOfTaskId is not null &&
                           task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running)
            .Select(task => task.RetryOfTaskId!.Value)
            .ToHashSet();
        return snapshot
            .Where(task => task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running ||
                           (task.State == OcrQueueTaskState.Blocked && !retriedTaskIds.Contains(task.TaskId)))
            .Select(task => Describe(task, pauses, now))
            .ToArray();
    }

    internal static OcrQueueActivityState Describe(
        OcrQueueTask task,
        IReadOnlySet<string> pauses,
        DateTimeOffset now)
    {
        if (task.State == OcrQueueTaskState.Blocked)
        {
            string reason = string.IsNullOrWhiteSpace(task.LastErrorMessage)
                ? "需要人工处理后重试"
                : $"需要人工处理：{task.LastErrorMessage}";
            return new OcrQueueActivityState(task.TaskId, "等待人工处理", true, reason, false, null);
        }

        bool isPaused = task.State == OcrQueueTaskState.Queued && IsPaused(pauses, task);
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
        return new OcrQueueActivityState(task.TaskId, detail, isPaused,
            isPaused ? "OCR 队列已暂停" : null, isWaitingRetry, retryReason);
    }
}

internal sealed record OcrQueueActivityState(
    OcrQueueTaskId TaskId,
    string Detail,
    bool IsPaused,
    string? PauseReason,
    bool IsWaitingRetry,
    string? RetryReason);
