namespace Patchouli.Host.Watching;

/// <summary>Watcher cadence; the minimum poll prevents a tight loop near debounce boundaries.</summary>
internal sealed record WatchScheduleOptions(
    TimeSpan QuietPeriod,
    TimeSpan MaxWait,
    TimeSpan ReconciliationInterval,
    TimeSpan DeferredRetryDelay,
    TimeSpan MinimumQuietPoll)
{
    public static WatchScheduleOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(20));
}

/// <summary>The work selected by <see cref="WatchLoopSchedule.SelectWorkAfterDebounce"/>.</summary>
internal enum WatchLoopWork
{
    /// <summary>Full reconciliation rescan (activation, watcher error or periodic interval).</summary>
    FullRescan,

    /// <summary>Incremental import pass over the swapped-out dirty batch.</summary>
    ProcessDirtyBatch
}

/// <summary>Pure scheduling decisions over explicit wall-clock times and elapsed durations.</summary>
internal sealed class WatchLoopSchedule(WatchScheduleOptions options)
{
    public WatchScheduleOptions Options { get; } = options;

    public bool IsReconciliationDue(DateTimeOffset now, DateTimeOffset lastFullRescanUtc)
    {
        return now - lastFullRescanUtc >= Options.ReconciliationInterval;
    }

    /// <summary>
    /// Post-debounce work selection: a requested or due reconciliation rescan always wins
    /// over an incremental pass — the dirty set stays reserved for the rescan's own snapshot.
    /// </summary>
    public WatchLoopWork SelectWorkAfterDebounce(bool fullRescanRequested, bool reconciliationDue)
    {
        return fullRescanRequested || reconciliationDue
            ? WatchLoopWork.FullRescan
            : WatchLoopWork.ProcessDirtyBatch;
    }

    /// <summary>
    /// One quiet-period evaluation step. Returns <see langword="null"/> once the debounce is
    /// satisfied — no event for QuietPeriod, or the burst reached MaxWait — otherwise the
    /// delay until the next re-evaluation: the sooner of the remaining quiet time and the
    /// remaining burst cap, never below the minimum poll.
    /// </summary>
    public TimeSpan? EvaluateQuietWait(TimeSpan quietFor, TimeSpan burstFor)
    {
        if (quietFor >= Options.QuietPeriod || burstFor >= Options.MaxWait)
        {
            return null;
        }

        TimeSpan remaining = Options.QuietPeriod - quietFor;
        TimeSpan cap = Options.MaxWait - burstFor;
        if (cap < remaining)
        {
            remaining = cap;
        }

        if (remaining < Options.MinimumQuietPoll)
        {
            remaining = Options.MinimumQuietPoll;
        }

        return remaining;
    }

    /// <summary>
    /// How long a signal wait may block before reconciliation comes due. Non-positive means
    /// reconciliation is already due and the wait must be skipped entirely.
    /// </summary>
    public TimeSpan SignalWaitTimeout(DateTimeOffset now, DateTimeOffset lastFullRescanUtc)
    {
        return Options.ReconciliationInterval - (now - lastFullRescanUtc);
    }
}
