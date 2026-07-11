using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Files;

public interface INativeFileAccessAdapter
{
    ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path, CancellationToken cancellationToken);
    ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path, CancellationToken cancellationToken);
}

public sealed record NativeDirectoryResolution(string? ResolvedPath, string? FailureCode = null, string? FailureReason = null);
public sealed record NativeFileMaterialization(bool IsAvailable, string? FailureCode = null, string? FailureReason = null);

public sealed class PortableNativeFileAccessAdapter : INativeFileAccessAdapter
{
    public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var info = new DirectoryInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return ValueTask.FromResult(new NativeDirectoryResolution(Path.GetFullPath(target?.FullName ?? info.FullName)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(new NativeDirectoryResolution(null, Classify(exception), exception.Message));
        }
    }

    public ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Windows/Linux do not emulate Finder aliases, security bookmarks, or iCloud materialization.
        return ValueTask.FromResult(new NativeFileMaterialization(true));
    }

    internal static string Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access_denied",
        DirectoryNotFoundException or FileNotFoundException => "offline",
        _ => "io_error"
    };
}

public sealed class FileSearchRootAccess : IFileSearchRootAccess
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".svn", ".hg", ".vs"
    };

    private readonly INativeFileAccessAdapter _native;

    public FileSearchRootAccess(INativeFileAccessAdapter? native = null)
    {
        _native = native ?? new PortableNativeFileAccessAdapter();
    }

    public Task<Result<SelectedFileSearchRoot>> SelectRootAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result<SelectedFileSearchRoot>.Failure(
            "picker_required",
            "Directory selection must be supplied by the platform picker adapter."));

    public Task<Result<ResolvedFileSearchRoot>> ReopenAsync(FileSearchRoot root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = root.AuthorizationKind ?? FileSearchRootAuthorizationKinds.None;
        if (!string.Equals(kind, FileSearchRootAuthorizationKinds.None, StringComparison.Ordinal))
        {
            return Task.FromResult(Result<ResolvedFileSearchRoot>.Failure(
                "authorization_unsupported",
                $"Authorization kind '{kind}' requires a platform adapter."));
        }

        return Task.FromResult(Result<ResolvedFileSearchRoot>.Success(new ResolvedFileSearchRoot(
            root.RootPath, root.RootPath, "filesystem", kind)));
    }

    public async Task<FileSearchRootScanResult> ScanPdfAsync(ResolvedFileSearchRoot root, CancellationToken cancellationToken = default)
    {
        var traversal = await TraverseAsync(root, cancellationToken);
        var candidates = new List<PdfCandidate>();
        var skippedFiles = traversal.SkippedFiles.ToList();

        foreach (var path in traversal.Files.Where(path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            if (cancellationToken.IsCancellationRequested)
                return BuildScanResult(candidates, traversal.SkippedDirectories, skippedFiles, traversal.RootStatus, FileSearchRootScanStatuses.Cancelled);

            try
            {
                var materialized = await _native.MaterializeFileAsync(path, cancellationToken);
                if (!materialized.IsAvailable)
                {
                    skippedFiles.Add(new FileSearchRootIssue(path, materialized.FailureCode ?? "materialization_failed", materialized.FailureReason ?? "The file could not be materialized."));
                    continue;
                }

                var info = new FileInfo(path);
                candidates.Add(new PdfCandidate(path, info.Name, info.Length, info.LastWriteTimeUtc, null, "discovered"));
            }
            catch (OperationCanceledException)
            {
                return BuildScanResult(candidates, traversal.SkippedDirectories, skippedFiles, traversal.RootStatus, FileSearchRootScanStatuses.Cancelled);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skippedFiles.Add(new FileSearchRootIssue(path, PortableNativeFileAccessAdapter.Classify(exception), exception.Message));
            }
        }

        var status = traversal.ScanStatus == FileSearchRootScanStatuses.Complete && skippedFiles.Count == 0
            ? FileSearchRootScanStatuses.Complete
            : traversal.ScanStatus == FileSearchRootScanStatuses.Failed ? FileSearchRootScanStatuses.Failed : FileSearchRootScanStatuses.Partial;
        var rootStatus = status == FileSearchRootScanStatuses.Partial && traversal.RootStatus == FileSearchRootStatuses.Available
            ? FileSearchRootStatuses.Partial : traversal.RootStatus;
        return BuildScanResult(candidates, traversal.SkippedDirectories, skippedFiles, rootStatus, status);
    }

    public async Task<FileSearchRootTraversalResult> TraverseAsync(ResolvedFileSearchRoot root, CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        var skippedDirectories = new List<FileSearchRootIssue>();
        var skippedFiles = new List<FileSearchRootIssue>();
        var pending = new Stack<string>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Push(root.ResolvedPath);
        var started = false;

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
                return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, started ? FileSearchRootStatuses.Partial : FileSearchRootStatuses.Available, FileSearchRootScanStatuses.Cancelled);

            var displayPath = pending.Pop();
            var resolution = await _native.ResolveDirectoryAsync(displayPath, cancellationToken);
            if (resolution.ResolvedPath is null)
            {
                skippedDirectories.Add(new FileSearchRootIssue(displayPath, resolution.FailureCode ?? "alias_resolution_failed", resolution.FailureReason ?? "The directory target could not be resolved."));
                if (!started)
                    return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, RootStatus(resolution.FailureCode), FileSearchRootScanStatuses.Failed);
                continue;
            }

            if (!visited.Add(resolution.ResolvedPath))
                continue;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(resolution.ResolvedPath);
                started = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var code = PortableNativeFileAccessAdapter.Classify(exception);
                skippedDirectories.Add(new FileSearchRootIssue(displayPath, code, exception.Message));
                if (!started)
                    return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, RootStatus(code), FileSearchRootScanStatuses.Failed);
                continue;
            }

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, FileSearchRootStatuses.Partial, FileSearchRootScanStatuses.Cancelled);
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!ExcludedDirectories.Contains(Path.GetFileName(entry)))
                            pending.Push(entry);
                    }
                    else
                    {
                        files.Add(entry);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skippedFiles.Add(new FileSearchRootIssue(entry, PortableNativeFileAccessAdapter.Classify(exception), exception.Message));
                }
            }
        }

        var partial = skippedDirectories.Count > 0 || skippedFiles.Count > 0;
        return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles,
            partial ? FileSearchRootStatuses.Partial : FileSearchRootStatuses.Available,
            partial ? FileSearchRootScanStatuses.Partial : FileSearchRootScanStatuses.Complete);
    }

    private static FileSearchRootScanResult BuildScanResult(List<PdfCandidate> candidates, IReadOnlyList<FileSearchRootIssue> directories, IReadOnlyList<FileSearchRootIssue> files, string rootStatus, string scanStatus)
        => new(candidates.OrderBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase).ToArray(), directories, files, rootStatus, scanStatus);

    private static string RootStatus(string? code) => code switch
    {
        "access_denied" => FileSearchRootStatuses.AccessDenied,
        "authorization_required" => FileSearchRootStatuses.AuthorizationRequired,
        _ => FileSearchRootStatuses.Offline
    };
}
