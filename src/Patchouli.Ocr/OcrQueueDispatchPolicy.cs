using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

/// <summary>
/// Pure dispatch rules for the OCR queue: priority ranks, aging score, eligibility, per-tick
/// claim ordering, concurrency limits, the deferred-wake delay and the idle predicate. All
/// decisions are functions of the task snapshots passed in; the scheduler owns the mutable
/// dictionaries these snapshots come from.
/// </summary>
internal static class OcrQueueDispatchPolicy
{
    /// <summary>Unknown and maintenance priorities rank lowest; never throws on new strings.</summary>
    public static int PriorityRank(string priority)
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

    /// <summary>Priority dominates; age adds at most 999 (one point per 10 queued minutes).</summary>
    public static int Score(OcrQueueTask task, DateTimeOffset now)
    {
        return PriorityRank(task.Priority) * 1000 +
               (int)Math.Min(999, (now - task.CreatedAt).TotalMinutes / 10);
    }

    public static bool IsDispatchEligible(OcrQueueTask task, bool alreadyRunning, DateTimeOffset now,
        IReadOnlySet<string> pauses)
    {
        return task.State == OcrQueueTaskState.Queued && !alreadyRunning &&
               (task.ScheduledAfter is null || task.ScheduledAfter <= now) &&
               !OcrQueuePausePolicy.IsPaused(task, pauses);
    }

    /// <summary>Eligible queued tasks in claim order: highest score, then oldest first.</summary>
    public static OcrQueueTask[] SelectDispatchCandidates(IEnumerable<OcrQueueTask> tasks,
        IReadOnlyCollection<OcrQueueTaskId> runningTaskIds, DateTimeOffset now,
        IReadOnlySet<string> pauses)
    {
        return tasks
            .Where(t => IsDispatchEligible(t, runningTaskIds.Contains(t.TaskId), now, pauses))
            .OrderByDescending(t => Score(t, now))
            .ThenBy(t => t.CreatedAt)
            .ToArray();
    }

    /// <summary>
    /// Concurrency gate evaluated against the task list as it stands at the moment of the call:
    /// tasks claimed earlier in the same tick already count as running and narrow what follows.
    /// </summary>
    public static bool CanRunNow(OcrQueueTask candidate, IEnumerable<OcrQueueTask> tasks,
        OcrQueueLimits limits)
    {
        OcrQueueTask[] running = tasks.Where(x => x.State == OcrQueueTaskState.Running).ToArray();
        if (running.Length >= limits.GlobalMaxConcurrent)
        {
            return false;
        }

        bool local = candidate.AdapterKind != OcrAdapterKind.CloudApi;
        if (local && running.Count(x => x.AdapterKind != OcrAdapterKind.CloudApi) >= limits.LocalMaxConcurrent)
        {
            return false;
        }

        if (!local && running.Count(x => x.AdapterKind == OcrAdapterKind.CloudApi) >= limits.CloudMaxConcurrent)
        {
            return false;
        }

        if (running.Count(x => x.EngineId == candidate.EngineId) >= limits.PerEngineMaxConcurrent)
        {
            return false;
        }

        if (candidate.ProviderId is not null &&
            running.Count(x => x.ProviderId == candidate.ProviderId) >= limits.PerProviderMaxConcurrent)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Time until the nearest deferred queued task becomes dispatchable, or null when no queued
    /// unpaused task is scheduled in the future.
    /// </summary>
    public static TimeSpan? NextDeferredWakeDelay(IEnumerable<OcrQueueTask> tasks, DateTimeOffset now,
        IReadOnlySet<string> pauses)
    {
        DateTimeOffset? nearest = null;
        foreach (OcrQueueTask task in tasks)
        {
            if (task.State == OcrQueueTaskState.Queued && !OcrQueuePausePolicy.IsPaused(task, pauses) &&
                task.ScheduledAfter.HasValue && task.ScheduledAfter.Value > now)
            {
                if (nearest == null || task.ScheduledAfter.Value < nearest.Value)
                {
                    nearest = task.ScheduledAfter.Value;
                }
            }
        }

        if (!nearest.HasValue)
        {
            return null;
        }

        TimeSpan diff = nearest.Value - now;
        return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
    }

    /// <summary>
    /// Idle means no in-flight attempts and nothing dispatchable: queued work that is deferred or
    /// paused does not block idleness.
    /// </summary>
    public static bool IsIdle(IEnumerable<OcrQueueTask> tasks, int runningAttemptCount, DateTimeOffset now,
        IReadOnlySet<string> pauses)
    {
        return runningAttemptCount == 0 && tasks.All(t =>
            t.State != OcrQueueTaskState.Running &&
            (t.State != OcrQueueTaskState.Queued ||
             (t.ScheduledAfter is not null && t.ScheduledAfter > now) ||
             OcrQueuePausePolicy.IsPaused(t, pauses)));
    }
}
