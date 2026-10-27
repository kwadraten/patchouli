namespace Patchouli.Ocr;

/// <summary>
/// Pause-scope rules for the queue. Pauses are stored as opaque <c>scope:target</c> keys; this
/// type owns the key format and which keys suppress dispatch of a given task.
/// </summary>
internal static class OcrQueuePausePolicy
{
    public static string Key(string scope, string? target)
    {
        return scope + ":" + (target ?? "");
    }

    /// <summary>Preset-level pause is defined as unsupported: pausing it fails explicitly.</summary>
    public static bool IsUnsupportedScope(string scope)
    {
        return scope == "preset";
    }

    public static bool IsPaused(OcrQueueTask task, IReadOnlySet<string> pauses)
    {
        return pauses.Contains(OcrPauseScope.Global + ":") ||
               pauses.Contains(OcrPauseScope.Task + ":" + task.TaskId) ||
               (pauses.Contains(OcrPauseScope.Local + ":") && task.AdapterKind != OcrAdapterKind.CloudApi) ||
               (pauses.Contains(OcrPauseScope.Cloud + ":") && task.AdapterKind == OcrAdapterKind.CloudApi) ||
               (!string.IsNullOrEmpty(task.ProviderId) &&
                pauses.Contains(OcrPauseScope.Provider + ":" + task.ProviderId));
    }
}
