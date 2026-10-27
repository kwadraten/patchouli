using FluentAssertions;
using Patchouli.Host.Watching;

namespace Patchouli.Tests;

/// <summary>
/// Pure, deterministic coverage for <see cref="WatchLoopSchedule"/> — the debounce,
/// reconciliation and retry decisions extracted from <see cref="FileSearchRootWatcherService"/>.
/// No wall-clock time is involved: the simulated "now" advances only through delays the
/// schedule itself returns, so these tests pin the exact quiet/max/retry cadence without
/// real sleeps.
/// </summary>
public sealed class WatcherSchedulingRefactorTests
{
    private static readonly WatchScheduleOptions Options = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(20));

    private readonly WatchLoopSchedule _schedule = new(Options);

    [Fact]
    public void Default_schedule_preserves_the_production_timing_knobs()
    {
        WatchScheduleOptions.Default.QuietPeriod.Should().Be(TimeSpan.FromSeconds(2));
        WatchScheduleOptions.Default.MaxWait.Should().Be(TimeSpan.FromSeconds(15));
        WatchScheduleOptions.Default.ReconciliationInterval.Should().Be(TimeSpan.FromMinutes(10));
        WatchScheduleOptions.Default.DeferredRetryDelay.Should().Be(TimeSpan.FromSeconds(3));
        WatchScheduleOptions.Default.MinimumQuietPoll.Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void Reconciliation_is_due_once_the_interval_has_elapsed_and_when_never_run()
    {
        DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        _schedule.IsReconciliationDue(now, DateTimeOffset.MinValue)
            .Should().BeTrue("activation must reconcile files changed while the app was closed");
        _schedule.IsReconciliationDue(now, now).Should().BeFalse();
        _schedule.IsReconciliationDue(now, now - Options.ReconciliationInterval + TimeSpan.FromTicks(1))
            .Should().BeFalse("just under the interval is not yet due");
        _schedule.IsReconciliationDue(now, now - Options.ReconciliationInterval)
            .Should().BeTrue("the boundary itself counts as due");
        _schedule.IsReconciliationDue(now, now - Options.ReconciliationInterval - TimeSpan.FromTicks(1))
            .Should().BeTrue();
    }

    [Fact]
    public void Signal_wait_timeout_is_non_positive_exactly_when_reconciliation_is_due()
    {
        DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        TimeSpan[] elapsedSinceLastRescan =
        [
            TimeSpan.Zero,
            Options.ReconciliationInterval / 2,
            Options.ReconciliationInterval - TimeSpan.FromMilliseconds(1),
            Options.ReconciliationInterval,
            Options.ReconciliationInterval + TimeSpan.FromMinutes(1)
        ];
        foreach (TimeSpan elapsed in elapsedSinceLastRescan)
        {
            DateTimeOffset last = now - elapsed;
            TimeSpan timeout = _schedule.SignalWaitTimeout(now, last);
            (timeout <= TimeSpan.Zero).Should().Be(_schedule.IsReconciliationDue(now, last),
                $"elapsed {elapsed}: a non-positive timeout means 'skip the wait', i.e. due");
            if (timeout > TimeSpan.Zero)
            {
                timeout.Should().Be(Options.ReconciliationInterval - elapsed,
                    "the idle wait wakes exactly when reconciliation comes due");
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_requested_or_due_full_rescan_wins_over_the_dirty_batch(
        bool fullRequested,
        bool reconciliationDue)
    {
        _schedule.SelectWorkAfterDebounce(fullRequested, reconciliationDue)
            .Should().Be(WatchLoopWork.FullRescan,
                "the dirty set stays reserved for the rescan's own snapshot");
    }

    [Fact]
    public void Without_a_full_rescan_trigger_the_dirty_batch_runs_incrementally()
    {
        _schedule.SelectWorkAfterDebounce(false, false)
            .Should().Be(WatchLoopWork.ProcessDirtyBatch);
    }

    [Fact]
    public void Quiet_wait_is_satisfied_by_either_the_quiet_period_or_the_burst_cap()
    {
        _schedule.EvaluateQuietWait(Options.QuietPeriod, TimeSpan.Zero)
            .Should().BeNull("the boundary itself is quiet enough");
        _schedule.EvaluateQuietWait(Options.QuietPeriod + TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(4))
            .Should().BeNull("once quiet long enough, stop waiting");
        _schedule.EvaluateQuietWait(TimeSpan.Zero, Options.MaxWait)
            .Should().BeNull("a burst at the cap runs even with a brand-new event");
        _schedule.EvaluateQuietWait(TimeSpan.FromSeconds(1), Options.MaxWait + TimeSpan.FromSeconds(2))
            .Should().BeNull("a burst past the cap runs even mid-quiet");
    }

    [Fact]
    public void Quiet_wait_delays_until_quiet_or_cap_whichever_comes_first()
    {
        _schedule.EvaluateQuietWait(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
            .Should().Be(TimeSpan.FromSeconds(1), "one second of quiet remains");
        _schedule.EvaluateQuietWait(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(14.5))
            .Should().Be(TimeSpan.FromSeconds(0.5),
                "the burst cap is nearer than the remaining quiet period");
    }

    [Fact]
    public void Quiet_wait_never_polls_faster_than_the_minimum()
    {
        _schedule.EvaluateQuietWait(Options.QuietPeriod - TimeSpan.FromMilliseconds(5),
                TimeSpan.FromSeconds(4))
            .Should().Be(Options.MinimumQuietPoll,
                "a sub-poll remainder would busy-spin; it is floored at the minimum poll");
        _schedule.EvaluateQuietWait(TimeSpan.Zero, Options.MaxWait - TimeSpan.FromMilliseconds(3))
            .Should().Be(Options.MinimumQuietPoll,
                "the same floor applies to the burst-cap remainder");
    }

    [Fact]
    public void Simulated_debounce_runs_the_pass_one_quiet_period_after_the_last_event()
    {
        // Virtual-clock re-enactment of the watch loop's quiet wait: the delay the schedule
        // returns is the only way virtual time advances, and events arriving mid-delay move
        // the last-event stamp without cancelling the wait — exactly as the loop treats them.
        double[] eventTimes = [0.0, 1.0, 3.0];
        double burstStart = eventTimes[0];
        double lastEvent = eventTimes[0];
        double now = burstStart;
        int nextEvent = 1;
        int evaluations = 0;

        while (true)
        {
            evaluations++;
            TimeSpan? delay = _schedule.EvaluateQuietWait(
                TimeSpan.FromSeconds(now - lastEvent), TimeSpan.FromSeconds(now - burstStart));
            if (delay is null)
            {
                break;
            }

            double wake = now + delay.Value.TotalSeconds;
            while (nextEvent < eventTimes.Length && eventTimes[nextEvent] <= wake)
            {
                lastEvent = eventTimes[nextEvent++];
            }

            now = wake;
            evaluations.Should().BeLessThan(1000, "the schedule must terminate a bounded loop");
        }

        now.Should().BeApproximately(lastEvent + Options.QuietPeriod.TotalSeconds, 1e-9,
            "the pass runs once the stream has been quiet for the full quiet period");
    }

    [Fact]
    public void Simulated_debounce_caps_an_endless_burst_at_max_wait()
    {
        // Events keep arriving just inside the quiet period, so the stream never goes quiet;
        // the pass must still run once the burst has lasted MaxWait — a continuous stream
        // cannot postpone work forever.
        const double eventSpacing = 1.5;
        double burstStart = 0;
        double lastEvent = 0;
        double now = 0;
        int evaluations = 0;

        while (true)
        {
            evaluations++;
            TimeSpan? delay = _schedule.EvaluateQuietWait(
                TimeSpan.FromSeconds(now - lastEvent), TimeSpan.FromSeconds(now - burstStart));
            if (delay is null)
            {
                break;
            }

            double wake = now + delay.Value.TotalSeconds;
            if (lastEvent + eventSpacing <= wake)
            {
                now = lastEvent += eventSpacing;
            }
            else
            {
                now = wake;
            }

            evaluations.Should().BeLessThan(1000, "the schedule must terminate a bounded loop");
        }

        now.Should().BeApproximately(burstStart + Options.MaxWait.TotalSeconds, 1e-9,
            "the burst cap fires exactly MaxWait after the first dirty event");
    }
}
