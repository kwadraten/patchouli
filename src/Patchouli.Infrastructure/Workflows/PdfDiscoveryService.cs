using Patchouli.Core.Diagnostics;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfDiscoveryService
{
    private readonly IFileSearchRootAccess _rootAccess;
    private readonly IHostActivityTracker? _activityTracker;
    private readonly CancellationToken _hostLifetime;

    public PdfDiscoveryService(IFileSearchRootAccess? rootAccess = null, IHostActivityTracker? activityTracker = null,
        CancellationToken hostLifetime = default)
    {
        _rootAccess = rootAccess ?? new FileSearchRootAccess();
        _activityTracker = activityTracker;
        _hostLifetime = hostLifetime;
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
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetime);
        cancellationToken = linkedCancellation.Token;
        using IActivityScope? activity = _activityTracker?.BeginScope(
            "扫描 PDF",
            HostActivityKind.Scanning,
            selectedRoot.DisplayPath);
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
            if (cancellationToken.IsCancellationRequested)
            {
                return new PdfScanResult([], 0, selectedRoot.DisplayPath, [], [], [],
                    result.RootStatus, FileSearchRootScanStatuses.Cancelled);
            }

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
