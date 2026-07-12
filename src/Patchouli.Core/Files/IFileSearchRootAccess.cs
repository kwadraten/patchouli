using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

public interface IFileSearchRootAccess
{
    Task<Result<SelectedFileSearchRoot>> SelectRootAsync(CancellationToken cancellationToken = default);

    Task<Result<ResolvedFileSearchRoot>>
        ReopenAsync(FileSearchRoot root, CancellationToken cancellationToken = default);

    Task<Result<ResolvedFileSearchRoot>> ResolveSelectedAsync(SelectedFileSearchRoot root,
        CancellationToken cancellationToken = default);

    Task<FileSearchRootScanResult> ScanPdfAsync(ResolvedFileSearchRoot root,
        CancellationToken cancellationToken = default);

    Task<FileSearchRootTraversalResult> TraverseAsync(ResolvedFileSearchRoot root,
        CancellationToken cancellationToken = default);
}

public sealed record FileSearchRootTraversalResult(
    IReadOnlyList<string> Files,
    IReadOnlyList<FileSearchRootIssue> SkippedDirectories,
    IReadOnlyList<FileSearchRootIssue> SkippedFiles,
    IReadOnlyList<FileSearchRootExcludedEntry> ExcludedEntries,
    string RootStatus,
    string ScanStatus);
