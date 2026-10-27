using Patchouli.Core.Diagnostics;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Operations;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Host.Watching;

/// <summary>
/// Watcher dependencies. The path-claim registry must be shared with explicit imports.
/// Locality assessment is separate because it is not part of IFileSearchRootAccess.
/// </summary>
internal sealed record FileSearchRootWatcherDependencies(
    Func<CancellationToken, Task<HashSet<string>>> LoadKnownFilePaths,
    IFileResolutionService FileResolution,
    IBlockingOperationService BlockingOperations,
    IFileSearchRootAccess FileSearchRootAccess,
    Func<string, FileLocalityAssessment> AssessFileLocality,
    IFileOperationCoordinator FileOperations,
    IPdfImportWorkflow PdfImport,
    IHostActivityTracker? ActivityTracker,
    CancellationToken LifetimeToken)
{
    public static FileSearchRootWatcherDependencies FromHostServices(HostServices services)
    {
        return new FileSearchRootWatcherDependencies(
            new UnchangedFilePathLookup(services.ConnectionFactory).LoadAsync,
            services.FileResolution,
            services.BlockingOperations,
            services.FileSearchRootAccess,
            services.FileSearchRootAccess.Assess,
            services.FileOperations,
            services.PdfImport,
            services.ActivityTracker,
            services.LifetimeToken);
    }
}
