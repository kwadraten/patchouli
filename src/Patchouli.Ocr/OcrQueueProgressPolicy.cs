namespace Patchouli.Ocr;

/// <summary>
/// Pure progress-notification rules: which reports belong to the live attempt and when a
/// stored report may raise a change notification (non-terminal reports are throttled per
/// task; terminal reports always publish).
/// </summary>
internal static class OcrQueueProgressPolicy
{
    public const double MinNotificationIntervalMilliseconds = 200;

    /// <summary>Reports are attempt-owned: only the running task's current attempt counts.</summary>
    public static bool Accepts(OcrQueueTask live, OcrTaskProgressReport report)
    {
        return live.State == OcrQueueTaskState.Running && report.Attempt == live.AttemptCount;
    }

    public static bool IsTerminal(OcrTaskProgressReport report)
    {
        return report.Fraction is not null && report.Fraction.Value >= 1.0;
    }

    public static bool ShouldNotify(bool terminalReport, DateTimeOffset? lastNotified, DateTimeOffset now)
    {
        return terminalReport || lastNotified is null ||
               (now - lastNotified.Value).TotalMilliseconds >= MinNotificationIntervalMilliseconds;
    }
}
