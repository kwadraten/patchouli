namespace Patchouli.Core.Files;

/// <summary>Import readiness for discovered PDF candidates.</summary>
public static class FileLocalityReadiness
{
    /// <summary>Local disk file; import first.</summary>
    public const string LocalReady = "local_ready";

    /// <summary>Cloud/sync path but data appears present on disk; import after local.</summary>
    public const string CloudReady = "cloud_ready";

    /// <summary>Placeholder / not hydrated; download after immediately readable files.</summary>
    public const string CloudUnready = "cloud_unready";
}

public static class FileLocalityCodes
{
    public const string CloudNotDownloaded = "cloud_not_downloaded";
}

public sealed record FileLocalityAssessment(
    string Readiness,
    bool IsCloudPath,
    string? ReasonCode = null,
    string? Reason = null);
