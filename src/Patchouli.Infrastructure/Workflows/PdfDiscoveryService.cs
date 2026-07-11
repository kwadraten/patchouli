using Patchouli.Core.Import;
using Patchouli.Core.Files;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfDiscoveryService
{
    private readonly IFileSearchRootAccess _rootAccess;

    public PdfDiscoveryService(IFileSearchRootAccess? rootAccess = null)
    {
        _rootAccess = rootAccess ?? new FileSearchRootAccess();
    }

    public Task<PdfScanResult> ScanDirectoryAsync(string scanRoot, CancellationToken cancellationToken = default)
        => ScanAsync(scanRoot, cancellationToken);

    private async Task<PdfScanResult> ScanAsync(string scanRoot, CancellationToken cancellationToken)
    {
        var resolved = new ResolvedFileSearchRoot(scanRoot, Path.GetFullPath(scanRoot), "filesystem", FileSearchRootAuthorizationKinds.None);
        var result = await _rootAccess.ScanPdfAsync(resolved, cancellationToken);
        return new PdfScanResult(result.Candidates, result.Candidates.Count, scanRoot, result.SkippedDirectories, result.SkippedFiles, result.RootStatus, result.ScanStatus);
    }
}
