using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfDiscoveryService
{
    private readonly IFileSearchRootAccess _rootAccess;

    public PdfDiscoveryService(IFileSearchRootAccess? rootAccess = null)
    {
        _rootAccess = rootAccess ?? new FileSearchRootAccess();
    }

    public Task<Result> EnsureAvailableAsync(string path, CancellationToken cancellationToken = default)
    {
        return _rootAccess.EnsureAvailableAsync(path, cancellationToken);
    }

    public FileLocalityAssessment Assess(string path)
    {
        return _rootAccess is FileSearchRootAccess access
            ? access.Assess(path)
            : FileLocalityClassifier.Assess(path);
    }

    public async Task<PdfScanResult> ScanDirectoryAsync(SelectedFileSearchRoot selectedRoot,
        CancellationToken cancellationToken = default)
    {
        Result<ResolvedFileSearchRoot> reopened;
        try
        {
            reopened = await _rootAccess.ResolveSelectedAsync(selectedRoot, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PdfScanResult([], 0, selectedRoot.DisplayPath, [], [], [],
                FileSearchRootStatuses.Available, FileSearchRootScanStatuses.Cancelled);
        }

        if (reopened.IsFailure)
        {
            return new PdfScanResult([], 0, selectedRoot.DisplayPath, [],
                [new FileSearchRootIssue(selectedRoot.DisplayPath, reopened.ErrorCode!, reopened.ErrorMessage!)], [],
                FileSearchRootStatuses.AuthorizationRequired, FileSearchRootScanStatuses.Failed);
        }

        ResolvedFileSearchRoot resolved = reopened.Value;
        try
        {
            FileSearchRootScanResult result = await _rootAccess.ScanPdfAsync(resolved, cancellationToken);
            return new PdfScanResult(result.Candidates, result.Candidates.Count, selectedRoot.DisplayPath,
                result.SkippedDirectories, result.SkippedFiles, result.ExcludedEntries, result.RootStatus,
                result.ScanStatus);
        }
        finally
        {
            resolved.AccessLease?.Dispose();
        }
    }
}
