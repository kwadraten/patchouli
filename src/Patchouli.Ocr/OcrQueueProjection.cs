namespace Patchouli.Ocr;

/// <summary>
/// Pure read-side projections of the queue: the task-list filter/ordering and the status
/// snapshot consumed by <see cref="IOcrQueueScheduler.ListTasksAsync"/> and
/// <see cref="IOcrQueueScheduler.GetQueueStatusAsync"/>.
/// </summary>
internal static class OcrQueueProjection
{
    public static bool MatchesFilter(OcrQueueTask task, OcrQueueTaskFilter filter)
    {
        return (filter.State is null || task.State == filter.State) &&
               (filter.EngineId is null || task.EngineId == filter.EngineId) &&
               (filter.ProviderId is null || task.ProviderId == filter.ProviderId) &&
               (filter.IncludeCompleted || OcrQueueLifecyclePolicy.IsUnfinished(task.State));
    }

    public static OcrQueueTask[] SelectForListing(IEnumerable<OcrQueueTask> tasks,
        OcrQueueTaskFilter filter, DateTimeOffset now)
    {
        return tasks.Where(t => MatchesFilter(t, filter))
            .OrderByDescending(t => OcrQueueDispatchPolicy.Score(t, now))
            .ToArray();
    }

    public static OcrQueueStatus ProjectStatus(bool isRunning, IEnumerable<OcrQueueTask> tasks,
        IReadOnlyList<string> pausedScopes, OcrQueueLimits limits)
    {
        OcrQueueTask[] snapshot = tasks as OcrQueueTask[] ?? tasks.ToArray();

        int Count(string state)
        {
            return snapshot.Count(t => t.State == state);
        }

        OcrQueueTask[] running = snapshot.Where(t => t.State == OcrQueueTaskState.Running).ToArray();
        return new OcrQueueStatus(isRunning, Count(OcrQueueTaskState.Queued), Count(OcrQueueTaskState.Running),
            Count(OcrQueueTaskState.Succeeded), Count(OcrQueueTaskState.Failed),
            Count(OcrQueueTaskState.Cancelled), Count(OcrQueueTaskState.Blocked), pausedScopes, limits,
            running.GroupBy(t => t.EngineId).ToDictionary(g => g.Key, g => g.Count()),
            running.Where(t => t.ProviderId is not null).GroupBy(t => t.ProviderId!)
                .ToDictionary(g => g.Key, g => g.Count()));
    }
}
