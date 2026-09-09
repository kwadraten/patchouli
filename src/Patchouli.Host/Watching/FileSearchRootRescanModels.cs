using Patchouli.Core.Ids;

namespace Patchouli.Host.Watching;

public sealed record FileSearchRootRescanSummary(
    int ScannedPdfCount,
    int ImportedPdfCount,
    int SkippedKnownPdfCount,
    int FailedPdfCount,
    int PartialRootCount = 0,
    int UnavailableRootCount = 0,
    int SkippedDirectoryCount = 0,
    int SkippedFileCount = 0)
{
    public bool HasWarnings => FailedPdfCount > 0 || PartialRootCount > 0 || UnavailableRootCount > 0 ||
                               SkippedDirectoryCount > 0 || SkippedFileCount > 0;
}

/// <summary>Raised when a rescan finishes successfully. <see cref="Trigger"/> distinguishes manual
/// rescans ("manual") from debounced watcher rescans ("file-watcher").</summary>
public sealed record FileSearchRootRescanCompleted(
    FileSearchRootRescanSummary Summary,
    string Trigger,
    string Message);

/// <summary>Raised when a rescan fails before producing a summary.</summary>
public sealed record FileSearchRootRescanFailed(
    string Trigger,
    string? ErrorCode,
    string Message);

/// <summary>Raised whenever a rescan updates the availability flag of a file search root.</summary>
public sealed record SearchRootAvailabilityChanged(
    FileSearchRootId RootId,
    string RootPath,
    bool IsAvailable);
