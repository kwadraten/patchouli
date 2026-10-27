using System.Diagnostics;
using Avalonia.Threading;

namespace Patchouli.Performance;

public sealed record UiHeartbeatMeasurement(double MaximumGapMs, int Samples);

/// <summary>AC3's shared 100 ms heartbeat, including the leading/trailing gaps and zero-tick stalls.</summary>
public static class UiHeartbeatProbe
{
    public static async Task<UiHeartbeatMeasurement> MeasureAsync(Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        List<long> ticks = [Stopwatch.GetTimestamp()];
        DispatcherTimer timer = new(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(UiPerfProbe.HeartbeatTargetMs)
        };
        timer.Tick += (_, _) => ticks.Add(Stopwatch.GetTimestamp());
        timer.Start();
        try
        {
            await operation();
        }
        finally
        {
            timer.Stop();
            ticks.Add(Stopwatch.GetTimestamp());
        }

        double maximum = 0;
        for (int index = 1; index < ticks.Count; index++)
        {
            maximum = Math.Max(maximum, Stopwatch.GetElapsedTime(ticks[index - 1], ticks[index]).TotalMilliseconds);
        }

        return new UiHeartbeatMeasurement(maximum, ticks.Count - 2);
    }
}
