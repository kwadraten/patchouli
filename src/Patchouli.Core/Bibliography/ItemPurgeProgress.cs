namespace Patchouli.Core.Bibliography;

/// <summary>Privacy-safe purge stage/profiling data; never contains item ids, paths or document text.</summary>
public sealed record ItemPurgeProgress(
    string Phase,
    string Message,
    bool IsCompleted = false,
    double? ElapsedMilliseconds = null,
    bool? FtsCacheRebuilt = null,
    int? SqliteErrorCode = null);
