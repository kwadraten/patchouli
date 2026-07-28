using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Patchouli.Infrastructure.Files;

public interface INativeFileAccessAdapter
{
    ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path, CancellationToken cancellationToken);
    ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path, CancellationToken cancellationToken);
}

public sealed record NativeDirectoryResolution(
    string? ResolvedPath,
    string? FailureCode = null,
    string? FailureReason = null);

public sealed record NativeFileMaterialization(
    bool IsAvailable,
    string? FailureCode = null,
    string? FailureReason = null);

public sealed class PortableNativeFileAccessAdapter : INativeFileAccessAdapter
{
    public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            DirectoryInfo info = new(path);
            FileSystemInfo? target = info.ResolveLinkTarget(true);
            return ValueTask.FromResult(
                new NativeDirectoryResolution(Path.GetFullPath(target?.FullName ?? info.FullName)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(new NativeDirectoryResolution(null, Classify(exception), exception.Message));
        }
    }

    public async ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            FileLocalityAssessment locality = FileLocalityClassifier.Assess(path);
            if (locality.Readiness == FileLocalityReadiness.CloudUnready)
            {
                try
                {
                    await WindowsCloudFileHydrator.HydrateAsync(path, cancellationToken);
                    return new NativeFileMaterialization(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (IOException exception)
                {
                    return new NativeFileMaterialization(
                        false,
                        Classify(exception),
                        exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    return new NativeFileMaterialization(
                        false,
                        Classify(exception),
                        exception.Message);
                }
            }
        }

        return new NativeFileMaterialization(true);
    }

    internal static string Classify(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "access_denied",
            DirectoryNotFoundException or FileNotFoundException => "offline",
            _ => "io_error"
        };
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsCloudFileHydrator
    {
        private const int BufferSize = 1024 * 1024;
        private const long CfEof = -1;

        public static async Task HydrateAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                // The synchronous API is isolated from the UI thread. WaitAsync lets the scan
                // stop promptly when cancelled even if a provider is still completing its request.
                await Task.Run(() => HydrateWithCloudFilesApi(path), CancellationToken.None)
                    .WaitAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException)
            {
                // Fall back to read-to-download.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to read-to-download.
            }
            catch (ExternalException)
            {
                // Fall back to read-to-download.
            }
            catch (DllNotFoundException)
            {
                // Fall back to read-to-download.
            }
            catch (EntryPointNotFoundException)
            {
                // Some third-party sync clients expose placeholder attributes but do not fully
                // support CfHydratePlaceholder. Sequential reads retain their read-to-download
                // compatibility and are cancellable.
            }

            await HydrateByReadingAsync(path, cancellationToken);
        }

        private static void HydrateWithCloudFilesApi(string path)
        {
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            int result = CfHydratePlaceholder(handle, 0, CfEof, 0, IntPtr.Zero);
            if (result < 0)
            {
                throw Marshal.GetExceptionForHR(result) ?? new ExternalException(
                    $"CfHydratePlaceholder failed with HRESULT 0x{result:X8}.",
                    result);
            }
        }

        private static async Task HydrateByReadingAsync(string path, CancellationToken cancellationToken)
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
            while (await stream.ReadAsync(buffer, cancellationToken) > 0)
            {
                // Reading all bytes asks read-to-download providers to fully hydrate the file.
            }
        }

        [DllImport("cldapi.dll")]
        private static extern int CfHydratePlaceholder(
            SafeFileHandle fileHandle,
            long startingOffset,
            long length,
            int hydrateFlags,
            IntPtr overlapped);
    }
}

public sealed class FileSearchRootAccess : IFileSearchRootAccess, IFileMaterializationService
{
    private const int MaterializationStatePollCount = 40;
    private static readonly TimeSpan MaterializationStatePollInterval = TimeSpan.FromMilliseconds(250);
    private readonly INativeFileAccessAdapter _native;
    private readonly Func<string, FileLocalityAssessment> _assess;
    private IReadOnlyList<(string Pattern, Regex Regex)> _exclusions;

    public FileSearchRootAccess(
        INativeFileAccessAdapter? native = null,
        IEnumerable<string>? exclusionPatterns = null,
        Func<string, FileLocalityAssessment>? localityClassifier = null)
    {
        _native = native ?? new PortableNativeFileAccessAdapter();
        _assess = localityClassifier ?? FileLocalityClassifier.Assess;
        _exclusions = CompileExclusions(exclusionPatterns ?? Array.Empty<string>());
    }

    public void UpdateExclusionPatterns(IEnumerable<string> exclusionPatterns)
    {
        _exclusions = CompileExclusions(exclusionPatterns);
    }

    public async Task<Result> EnsureAvailableAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "A file path is required for materialization.");
        }

        NativeFileMaterialization materialized = await _native.MaterializeFileAsync(path, cancellationToken);
        if (!materialized.IsAvailable)
        {
            return Result.Failure(materialized.FailureCode ?? "materialization_failed",
                materialized.FailureReason ?? "The file could not be materialized.");
        }

        for (int attempt = 0; attempt < MaterializationStatePollCount; attempt++)
        {
            FileLocalityAssessment locality = _assess(path);
            if (locality.Readiness != FileLocalityReadiness.CloudUnready)
            {
                return Result.Success();
            }

            await Task.Delay(MaterializationStatePollInterval, cancellationToken);
        }

        FileLocalityAssessment finalLocality = _assess(path);
        return finalLocality.Readiness != FileLocalityReadiness.CloudUnready
            ? Result.Success()
            : Result.Failure(
                finalLocality.ReasonCode ?? FileLocalityCodes.CloudNotDownloaded,
                finalLocality.Reason ?? "Cloud file materialization did not complete.");
    }

    public FileLocalityAssessment Assess(string path)
    {
        return _assess(path);
    }

    public static bool TryValidateExclusionPatterns(IEnumerable<string> exclusionPatterns, out string? error)
    {
        try
        {
            _ = CompileExclusions(exclusionPatterns);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static IReadOnlyList<(string Pattern, Regex Regex)> CompileExclusions(
        IEnumerable<string> exclusionPatterns)
    {
        return exclusionPatterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => (pattern, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250))))
            .ToArray();
    }

    public Task<Result<SelectedFileSearchRoot>> SelectRootAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<SelectedFileSearchRoot>.Failure(
            "picker_required",
            "Directory selection must be supplied by the platform picker adapter."));
    }

    public Task<Result<ResolvedFileSearchRoot>> ReopenAsync(FileSearchRoot root,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string kind = root.AuthorizationKind ?? FileSearchRootAuthorizationKinds.None;
        if (!IsAuthorizationKindSupported(kind))
        {
            return Task.FromResult(Result<ResolvedFileSearchRoot>.Failure(
                "authorization_unsupported",
                $"Authorization kind '{kind}' requires a platform adapter."));
        }

        return Task.FromResult(Result<ResolvedFileSearchRoot>.Success(new ResolvedFileSearchRoot(
            root.RootPath, root.RootPath, "filesystem", kind)));
    }

    public Task<Result<ResolvedFileSearchRoot>> ResolveSelectedAsync(SelectedFileSearchRoot root,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAuthorizationKindSupported(root.AuthorizationKind))
        {
            return Task.FromResult(Result<ResolvedFileSearchRoot>.Failure("authorization_unsupported",
                $"Authorization kind '{root.AuthorizationKind}' requires a platform adapter."));
        }

        return Task.FromResult(Result<ResolvedFileSearchRoot>.Success(new ResolvedFileSearchRoot(root.DisplayPath,
            Path.GetFullPath(root.DisplayPath), root.ProviderIdentity, root.AuthorizationKind)));
    }

    private static bool IsAuthorizationKindSupported(string kind)
    {
        return string.Equals(kind, FileSearchRootAuthorizationKinds.None, StringComparison.Ordinal)
               || string.Equals(kind, FileSearchRootAuthorizationKinds.TccPicker, StringComparison.Ordinal);
    }

    public async Task<FileSearchRootScanResult> ScanPdfAsync(ResolvedFileSearchRoot root,
        CancellationToken cancellationToken = default)
    {
        FileSearchRootTraversalResult traversal = await TraverseAsync(root, cancellationToken);
        List<PdfCandidate> candidates = new();
        List<FileSearchRootIssue> skippedFiles = traversal.SkippedFiles.ToList();

        foreach (string path in traversal.Files.Where(path =>
                     string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return BuildScanResult(candidates, traversal.SkippedDirectories, skippedFiles,
                    traversal.ExcludedEntries, traversal.RootStatus, FileSearchRootScanStatuses.Cancelled);
            }

            try
            {
                FileLocalityAssessment locality = _assess(path);
                if (locality.Readiness == FileLocalityReadiness.CloudUnready)
                {
                    // Preserve placeholders in the queue without opening or hashing them.
                    // Importers process local/ready candidates first, then hydrate this tier.
                    candidates.Add(new PdfCandidate(
                        path,
                        Path.GetFileName(path),
                        0,
                        null,
                        null,
                        "awaiting_download",
                        FileLocalityReadiness.CloudUnready,
                        true));
                    continue;
                }

                Result materialized = await EnsureAvailableAsync(path, cancellationToken);
                if (materialized.IsFailure)
                {
                    skippedFiles.Add(new FileSearchRootIssue(path, materialized.ErrorCode ?? "materialization_failed",
                        materialized.ErrorMessage ?? "The file could not be materialized."));
                    continue;
                }

                // Re-assess after materialization (macOS iCloud may still report not ready).
                locality = _assess(path);
                if (locality.Readiness == FileLocalityReadiness.CloudUnready)
                {
                    skippedFiles.Add(new FileSearchRootIssue(
                        path,
                        locality.ReasonCode ?? FileLocalityCodes.CloudNotDownloaded,
                        locality.Reason ?? "Cloud file is not downloaded yet."));
                    continue;
                }

                FileInfo info = new(path);
                candidates.Add(
                    new PdfCandidate(
                        path,
                        info.Name,
                        info.Length,
                        info.LastWriteTimeUtc,
                        null,
                        "discovered",
                        locality.Readiness,
                        locality.IsCloudPath));
            }
            catch (OperationCanceledException)
            {
                return BuildScanResult(candidates, traversal.SkippedDirectories, skippedFiles,
                    traversal.ExcludedEntries, traversal.RootStatus, FileSearchRootScanStatuses.Cancelled);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skippedFiles.Add(new FileSearchRootIssue(path, PortableNativeFileAccessAdapter.Classify(exception),
                    exception.Message));
            }
        }

        string status = traversal.ScanStatus == FileSearchRootScanStatuses.Complete && skippedFiles.Count == 0
            ? FileSearchRootScanStatuses.Complete
            : traversal.ScanStatus == FileSearchRootScanStatuses.Failed
                ? FileSearchRootScanStatuses.Failed
                : FileSearchRootScanStatuses.Partial;
        string rootStatus = status == FileSearchRootScanStatuses.Partial &&
                            traversal.RootStatus == FileSearchRootStatuses.Available
            ? FileSearchRootStatuses.Partial
            : traversal.RootStatus;
        return BuildScanResult(candidates, traversal.SkippedDirectories, skippedFiles, traversal.ExcludedEntries,
            rootStatus, status);
    }

    public async Task<FileSearchRootTraversalResult> TraverseAsync(ResolvedFileSearchRoot root,
        CancellationToken cancellationToken = default)
    {
        List<string> files = new();
        List<FileSearchRootIssue> skippedDirectories = new();
        List<FileSearchRootIssue> skippedFiles = new();
        List<FileSearchRootExcludedEntry> excludedEntries = new();
        Stack<string> pending = new();
        HashSet<string> visited =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Push(root.ResolvedPath);
        bool started = false;

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, excludedEntries,
                    started ? FileSearchRootStatuses.Partial : FileSearchRootStatuses.Available,
                    FileSearchRootScanStatuses.Cancelled);
            }

            string displayPath = pending.Pop();
            NativeDirectoryResolution resolution;
            try
            {
                resolution = await _native.ResolveDirectoryAsync(displayPath, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, excludedEntries,
                    started ? FileSearchRootStatuses.Partial : FileSearchRootStatuses.Available,
                    FileSearchRootScanStatuses.Cancelled);
            }

            if (resolution.ResolvedPath is null)
            {
                skippedDirectories.Add(new FileSearchRootIssue(displayPath,
                    resolution.FailureCode ?? "alias_resolution_failed",
                    resolution.FailureReason ?? "The directory target could not be resolved."));
                if (!started)
                {
                    return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, excludedEntries,
                        RootStatus(resolution.FailureCode), FileSearchRootScanStatuses.Failed);
                }

                continue;
            }

            if (!visited.Add(resolution.ResolvedPath))
            {
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(resolution.ResolvedPath);
                started = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                string code = PortableNativeFileAccessAdapter.Classify(exception);
                skippedDirectories.Add(new FileSearchRootIssue(displayPath, code, exception.Message));
                if (!started)
                {
                    return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, excludedEntries,
                        RootStatus(code), FileSearchRootScanStatuses.Failed);
                }

                continue;
            }

            foreach (string entry in entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, excludedEntries,
                        FileSearchRootStatuses.Partial, FileSearchRootScanStatuses.Cancelled);
                }

                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        string relativePath = Path.GetRelativePath(root.ResolvedPath, entry)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        (string Pattern, Regex Regex) exclusion = _exclusions.FirstOrDefault(rule =>
                            rule.Regex.IsMatch(relativePath));
                        if (exclusion.Regex is not null)
                        {
                            excludedEntries.Add(new FileSearchRootExcludedEntry(entry, relativePath,
                                exclusion.Pattern));
                        }
                        else
                        {
                            pending.Push(entry);
                        }
                    }
                    else
                    {
                        string relativePath = Path.GetRelativePath(root.ResolvedPath, entry)
                            .Replace(Path.DirectorySeparatorChar, '/');
                        (string Pattern, Regex Regex) exclusion = _exclusions.FirstOrDefault(rule =>
                            rule.Regex.IsMatch(relativePath));
                        if (exclusion.Regex is not null)
                        {
                            excludedEntries.Add(new FileSearchRootExcludedEntry(entry, relativePath,
                                exclusion.Pattern));
                        }
                        else
                        {
                            files.Add(entry);
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skippedFiles.Add(new FileSearchRootIssue(entry, PortableNativeFileAccessAdapter.Classify(exception),
                        exception.Message));
                }
                catch (RegexMatchTimeoutException exception)
                {
                    skippedFiles.Add(new FileSearchRootIssue(entry, "exclusion_rule_timeout", exception.Message));
                    return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, excludedEntries,
                        started ? FileSearchRootStatuses.Partial : FileSearchRootStatuses.Available,
                        FileSearchRootScanStatuses.Failed);
                }
            }
        }

        bool partial = skippedDirectories.Count > 0 || skippedFiles.Count > 0;
        return new FileSearchRootTraversalResult(files, skippedDirectories, skippedFiles, excludedEntries,
            partial ? FileSearchRootStatuses.Partial : FileSearchRootStatuses.Available,
            partial ? FileSearchRootScanStatuses.Partial : FileSearchRootScanStatuses.Complete);
    }

    private static FileSearchRootScanResult BuildScanResult(List<PdfCandidate> candidates,
        IReadOnlyList<FileSearchRootIssue> directories, IReadOnlyList<FileSearchRootIssue> files,
        IReadOnlyList<FileSearchRootExcludedEntry> excludedEntries, string rootStatus, string scanStatus)
    {
        // Local-ready first, then hydrated cloud paths, then placeholders that the importer
        // downloads after all immediately readable candidates have finished.
        PdfCandidate[] ordered = FileLocalityClassifier
            .OrderForImport(candidates, static c => c.Readiness, static c => c.FileName)
            .ToArray();
        return new FileSearchRootScanResult(ordered, directories, files, excludedEntries, rootStatus, scanStatus);
    }

    private static string RootStatus(string? code)
    {
        return code switch
        {
            "access_denied" => FileSearchRootStatuses.AccessDenied,
            "authorization_required" => FileSearchRootStatuses.AuthorizationRequired,
            _ => FileSearchRootStatuses.Offline
        };
    }
}
