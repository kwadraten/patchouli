using Patchouli.Core.Diagnostics;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Files;
using Patchouli.UI.Diagnostics;

namespace Patchouli.Host.Watching;

/// <summary>
/// Owns the <see cref="FileSystemWatcher"/> fleet for the configured file search roots plus the
/// debounced rescan pipeline that imports newly discovered PDFs. Extracted from
/// <c>MainWindowViewModel</c>; the service never touches the UI thread — subscribers react to
/// <see cref="RescanCompleted"/>, <see cref="RescanFailed"/> and
/// <see cref="SearchRootAvailabilityChanged"/> and marshal to the UI as needed.
/// </summary>
public sealed class FileSearchRootWatcherService : IAsyncDisposable
{
    public const string ManualTrigger = "manual";
    public const string FileWatcherTrigger = "file-watcher";

    private readonly HostServices _services;
    private readonly IAppLogger _logger;

    private readonly Dictionary<string, FileSystemWatcher> _fileSearchRootWatchers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _fileSearchRootWatchSync = new();
    private readonly SemaphoreSlim _fileSearchRootRescanGate = new(1, 1);
    private CancellationTokenSource? _fileSearchRootWatchDebounce;
    private Task? _fileSearchRootWatchDebounceTask;
    private long _fileSearchRootWatchGeneration;
    private bool _fileSearchRootWatchersActive;

    public FileSearchRootWatcherService(HostServices services, IAppLogger logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Raised on the rescan task's thread after a successful rescan.</summary>
    public event EventHandler<FileSearchRootRescanCompleted>? RescanCompleted;

    /// <summary>Raised on the rescan task's thread when a rescan fails without a summary.</summary>
    public event EventHandler<FileSearchRootRescanFailed>? RescanFailed;

    /// <summary>Raised on the rescan task's thread whenever a root's availability is updated.</summary>
    public event EventHandler<SearchRootAvailabilityChanged>? SearchRootAvailabilityChanged;

    /// <summary>
    /// Reconciles the watcher fleet with the given available roots: drops watchers for removed or
    /// unavailable roots and creates recursive <c>*.pdf</c> watchers for new ones.
    /// </summary>
    public void RefreshWatchers(IReadOnlyList<FileSearchRoot> roots)
    {
        long watcherGeneration;
        lock (_fileSearchRootWatchSync)
        {
            _fileSearchRootWatchersActive = true;
            watcherGeneration = _fileSearchRootWatchGeneration;
        }

        HashSet<string> wanted = roots.Where(root => root.IsAvailable && Directory.Exists(root.RootPath))
            .Select(root => Path.GetFullPath(root.RootPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in _fileSearchRootWatchers.Keys.Where(path => !wanted.Contains(path)).ToArray())
        {
            _fileSearchRootWatchers[path].Dispose();
            _fileSearchRootWatchers.Remove(path);
        }

        foreach (string path in wanted)
        {
            if (_fileSearchRootWatchers.ContainsKey(path))
            {
                continue;
            }

            try
            {
                FileSystemWatcher watcher = new(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    Filter = "*.pdf",
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler changed = (_, e) =>
                    ScheduleFileSearchRootRescan($"{e.ChangeType}: {e.FullPath}", watcherGeneration);
                RenamedEventHandler renamed = (_, e) =>
                    ScheduleFileSearchRootRescan($"Renamed: {e.OldFullPath} -> {e.FullPath}", watcherGeneration);
                ErrorEventHandler error = (_, e) =>
                    ScheduleFileSearchRootRescan($"Error: {e.GetException()?.Message ?? "unknown"}", watcherGeneration);
                watcher.Created += changed;
                watcher.Changed += changed;
                watcher.Deleted += changed;
                watcher.Renamed += renamed;
                watcher.Error += error;
                _fileSearchRootWatchers[path] = watcher;
            }
            catch (Exception exception)
            {
                UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "create-watcher");
            }
        }
    }

    /// <summary>
    /// Stops all watchers and cancels any pending debounced rescan. Called on database switch and
    /// shutdown; watchers are restarted via <see cref="RefreshWatchers"/>.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? debounce;
        Task? debounceTask;
        lock (_fileSearchRootWatchSync)
        {
            _fileSearchRootWatchersActive = false;
            _fileSearchRootWatchGeneration++;
            debounce = _fileSearchRootWatchDebounce;
            debounceTask = _fileSearchRootWatchDebounceTask;
            _fileSearchRootWatchDebounce = null;
            _fileSearchRootWatchDebounceTask = null;
        }

        debounce?.Cancel();
        foreach (FileSystemWatcher watcher in _fileSearchRootWatchers.Values)
        {
            watcher.Dispose();
        }

        _fileSearchRootWatchers.Clear();
        try
        {
            if (debounceTask is not null)
            {
                await debounceTask;
            }
        }
        catch (OperationCanceledException) when (debounce?.IsCancellationRequested == true)
        {
            // Expected when a database switch or application shutdown cancels a pending watcher rescan.
        }
        finally
        {
            debounce?.Dispose();
        }
    }

    /// <summary>
    /// Scans all file search roots and imports newly discovered PDFs, serialized through an internal
    /// gate. Progress is reported through <paramref name="progress"/> and a
    /// <c>BlockingOperationTypes.FileSearchRootScan</c> blocking-operation record. Caller is
    /// responsible for any modal UI; this method never shows dialogs.
    /// </summary>
    public async Task<Result<FileSearchRootRescanSummary>> RescanFileSearchRootsAsync(
        string completionMessage = "文件重新扫描完成。",
        CancellationToken cancellationToken = default,
        Action<int?, int?, string, string?>? progress = null,
        string trigger = ManualTrigger)
    {
        await _fileSearchRootRescanGate.WaitAsync(cancellationToken);
        try
        {
            Result<FileSearchRootRescanSummary> result = await Task.Run(
                () => RescanFileSearchRootsCoreAsync(completionMessage, cancellationToken, progress, trigger),
                cancellationToken);
            ApplyFileSearchRootRescanResult(result, completionMessage, trigger);
            return result;
        }
        finally
        {
            _fileSearchRootRescanGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task<Result<FileSearchRootRescanSummary>> RescanFileSearchRootsCoreAsync(
        string completionMessage,
        CancellationToken cancellationToken,
        Action<int?, int?, string, string?>? progress,
        string trigger)
    {
        BlockingOperationId? operationId = null;
        try
        {
            Result<IReadOnlyList<FileSearchRoot>> roots =
                await _services.FileResolution.ListSearchRootsAsync(cancellationToken);
            if (roots.IsFailure)
            {
                return Result<FileSearchRootRescanSummary>.Failure(roots.ErrorCode!, roots.ErrorMessage!);
            }

            await LogOperationAsync("file-scan",
                $"Rescan started (trigger={trigger}): {roots.Value.Count} file search root(s).");

            Result<BlockingOperation> started = await _services.BlockingOperations.StartAsync(
                BlockingOperationTypes.FileSearchRootScan,
                BlockingOperationScopeTypes.FileSearchRoot,
                "all",
                true,
                "正在重新扫描文件搜索根。",
                0,
                roots.Value.Count,
                ["等待扫描完成", "检查离线文件搜索根"],
                cancellationToken);
            if (started.IsSuccess)
            {
                operationId = started.Value.OperationId;
            }

            HashSet<string> knownPaths = await LoadKnownFilePathsAsync(cancellationToken);
            int processedRoots = 0;
            int scanned = 0;
            int imported = 0;
            int skipped = 0;
            int failed = 0;
            int partialRoots = 0;
            int unavailableRoots = 0;
            int skippedDirectories = 0;
            int skippedFiles = 0;
            progress?.Invoke(0, roots.Value.Count, "正在扫描文件搜索根。", $"已找到 {roots.Value.Count} 个文件搜索根。");

            foreach (FileSearchRoot root in roots.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(processedRoots, roots.Value.Count, $"正在扫描：{root.RootPath}", null);
                Result<ResolvedFileSearchRoot> reopened =
                    await _services.FileSearchRootAccess.ReopenAsync(root, cancellationToken);
                if (reopened.IsFailure)
                {
                    unavailableRoots++;
                    await _services.FileResolution.SetSearchRootAvailabilityAsync(root.RootId, false,
                        cancellationToken);
                    SearchRootAvailabilityChanged?.Invoke(this,
                        new SearchRootAvailabilityChanged(root.RootId, root.RootPath, false));
                    processedRoots++;
                    progress?.Invoke(processedRoots, roots.Value.Count, "文件搜索根不可用，已跳过。",
                        reopened.ErrorMessage ?? root.RootPath);
                    continue;
                }

                using IDisposable? resolvedRoot = reopened.Value.AccessLease;
                FileSearchRootScanResult scan =
                    await _services.FileSearchRootAccess.ScanPdfAsync(reopened.Value, cancellationToken);
                await LogOperationAsync("file-scan",
                    $"Root scan finished (trigger={trigger}): {root.RootPath} status={scan.ScanStatus}, " +
                    $"candidates={scan.Candidates.Count}, skippedDirectories={scan.SkippedDirectories.Count}, " +
                    $"skippedFiles={scan.SkippedFiles.Count}.");
                foreach (FileSearchRootIssue issue in scan.SkippedDirectories.Concat(scan.SkippedFiles))
                {
                    await LogOperationAsync("file-scan",
                        $"Skipped (trigger={trigger}): [{issue.Code}] {issue.Path} - {issue.Reason}");
                }

                bool available = scan.ScanStatus == FileSearchRootScanStatuses.Complete &&
                                 scan.RootStatus == FileSearchRootStatuses.Available;
                await _services.FileResolution.SetSearchRootAvailabilityAsync(root.RootId, available,
                    cancellationToken);
                SearchRootAvailabilityChanged?.Invoke(this,
                    new SearchRootAvailabilityChanged(root.RootId, root.RootPath, available));
                if (scan.ScanStatus == FileSearchRootScanStatuses.Partial)
                {
                    partialRoots++;
                }

                if (scan.ScanStatus == FileSearchRootScanStatuses.Failed)
                {
                    unavailableRoots++;
                }

                skippedDirectories += scan.SkippedDirectories.Count;
                skippedFiles += scan.SkippedFiles.Count;
                if (operationId is not null)
                {
                    foreach (IGrouping<string, FileSearchRootExcludedEntry> group in scan.ExcludedEntries.GroupBy(
                                 entry => entry.Rule, StringComparer.Ordinal))
                    {
                        await _services.BlockingOperations.AddLogEntryAsync(operationId.Value, "info",
                            $"Excluded {group.Count()} path(s) by scan rule.", group.Key,
                            BlockingOperationScopeTypes.FileSearchRoot, root.RootId.ToString(), cancellationToken);
                    }
                }

                if (scan.ScanStatus is FileSearchRootScanStatuses.Failed or FileSearchRootScanStatuses.Cancelled)
                {
                    processedRoots++;
                    progress?.Invoke(processedRoots, roots.Value.Count,
                        $"文件搜索根扫描未完成：{scan.ScanStatus}", root.RootPath);
                    continue;
                }

                // A partial scan (e.g. a directory timed out) still
                // imports the candidates that were discovered; the next rescan picks up the rest.
                if (scan.ScanStatus == FileSearchRootScanStatuses.Partial)
                {
                    progress?.Invoke(processedRoots, roots.Value.Count,
                        "文件搜索根扫描不完整，仍导入已发现的 PDF。", root.RootPath);
                }

                scanned += scan.Candidates.Count;
                // Local-ready first, then hydrated cloud files, then placeholders. The last tier
                // is hydrated only after all immediately readable files have been imported.
                List<PdfCandidate> importQueue = FileLocalityClassifier
                    .OrderForImport(scan.Candidates, static c => c.Readiness, static c => c.FileName)
                    .ToList();
                int importIndex = 0;
                foreach (PdfCandidate candidate in importQueue)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    importIndex++;
                    string normalizedPath = Path.GetFullPath(candidate.Path);
                    if (knownPaths.Contains(normalizedPath))
                    {
                        skipped++;
                        continue;
                    }

                    FileLocalityAssessment locality = _services.FileSearchRootAccess.Assess(normalizedPath);
                    if (locality.Readiness == FileLocalityReadiness.CloudUnready)
                    {
                        progress?.Invoke(processedRoots, roots.Value.Count,
                            $"正在下载云端文件：{candidate.FileName}", $"下载 → {normalizedPath}");
                        await LogOperationAsync("file-scan",
                            $"Hydrating cloud file (trigger={trigger}): {normalizedPath}");
                        Result materialized =
                            await _services.FileSearchRootAccess.EnsureAvailableAsync(normalizedPath,
                                cancellationToken);
                        locality = _services.FileSearchRootAccess.Assess(normalizedPath);
                        if (materialized.IsFailure)
                        {
                            failed++;
                            string error = materialized.ErrorMessage ?? locality.Reason ?? "云端文件下载未完成。";
                            progress?.Invoke(processedRoots, roots.Value.Count,
                                $"云端文件下载失败：{candidate.FileName}", error);
                            await LogOperationAsync("file-scan",
                                $"Cloud hydration failed (trigger={trigger}): {normalizedPath} - {error}");
                            continue;
                        }
                    }

                    string tier = locality.Readiness == FileLocalityReadiness.LocalReady ? "local" : "cloud";
                    progress?.Invoke(processedRoots, roots.Value.Count,
                        $"正在导入 ({importIndex}/{importQueue.Count}, {tier})：{candidate.FileName}",
                        $"导入 ({importIndex}/{importQueue.Count}) → {normalizedPath}");
                    await LogOperationAsync("file-scan",
                        $"Importing (trigger={trigger}, {importIndex}/{importQueue.Count}, tier={tier}): {normalizedPath}");

                    PdfImportResult importedPdf =
                        await _services.PdfImport.ImportPdfAsync(new PdfImportRequest(normalizedPath, null, null, null),
                            cancellationToken);
                    if (importedPdf.Success)
                    {
                        imported++;
                        knownPaths.Add(normalizedPath);
                        progress?.Invoke(processedRoots, roots.Value.Count, $"已导入：{candidate.FileName}",
                            $"导入完成 → {normalizedPath}");
                    }
                    else
                    {
                        failed++;
                        progress?.Invoke(processedRoots, roots.Value.Count, $"导入失败：{candidate.FileName}",
                            importedPdf.ErrorMessage);
                        await LogOperationAsync("file-scan",
                            $"Import failed (trigger={trigger}): {normalizedPath} - {importedPdf.ErrorMessage}");
                    }
                }

                processedRoots++;
                if (operationId is not null)
                {
                    await _services.BlockingOperations.UpdateProgressAsync(
                        operationId.Value,
                        processedRoots,
                        progressLabel: $"已处理 {processedRoots}/{roots.Value.Count} 个文件搜索根，已扫描 {scanned} 个 PDF。",
                        cancellationToken: cancellationToken);
                }

                progress?.Invoke(processedRoots, roots.Value.Count, $"已处理 {processedRoots}/{roots.Value.Count} 个文件搜索根。",
                    null);
            }

            FileSearchRootRescanSummary summary = new(scanned, imported, skipped, failed, partialRoots,
                unavailableRoots, skippedDirectories, skippedFiles);
            string message = BuildFileSearchRootRescanMessage(summary, completionMessage);
            progress?.Invoke(roots.Value.Count, roots.Value.Count, "文件重新扫描完成。", message);
            await LogOperationAsync("file-scan",
                $"Rescan finished (trigger={trigger}): scanned={scanned}, imported={imported}, " +
                $"known={skipped}, failed={failed}, partialRoots={partialRoots}, " +
                $"unavailableRoots={unavailableRoots}.");
            if (operationId is not null)
            {
                await _services.BlockingOperations.CompleteAsync(operationId.Value, message, Array.Empty<string>(),
                    cancellationToken);
            }

            return Result<FileSearchRootRescanSummary>.Success(summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await LogOperationAsync("file-scan", $"Rescan cancelled (trigger={trigger}).");
            if (operationId is not null)
            {
                await _services.BlockingOperations.CancelAsync(
                    operationId.Value,
                    "文件重新扫描已取消。",
                    ["可稍后重新扫描"],
                    CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            await LogOperationAsync("file-scan", $"Rescan failed (trigger={trigger}): {ex.Message}");
            string message = $"文件重新扫描失败：{ex.Message}";
            if (operationId is not null)
            {
                await _services.BlockingOperations.FailAsync(operationId.Value, AppErrorCodes.InvalidState, message,
                    "文件重新扫描失败。", ["检查文件搜索根权限", "重新扫描"], CancellationToken.None);
            }

            return Result<FileSearchRootRescanSummary>.Failure(AppErrorCodes.InvalidState, message);
        }
    }

    private void ApplyFileSearchRootRescanResult(
        Result<FileSearchRootRescanSummary> result,
        string completionMessage,
        string trigger)
    {
        if (result.IsFailure)
        {
            RescanFailed?.Invoke(this,
                new FileSearchRootRescanFailed(trigger, result.ErrorCode, result.ErrorMessage ?? "文件重新扫描失败。"));
            return;
        }

        RescanCompleted?.Invoke(this,
            new FileSearchRootRescanCompleted(result.Value, trigger,
                BuildFileSearchRootRescanMessage(result.Value, completionMessage)));
    }

    private static string BuildFileSearchRootRescanMessage(
        FileSearchRootRescanSummary summary,
        string completionMessage)
    {
        return
            $"{completionMessage} 扫描 {summary.ScannedPdfCount} 个 PDF，新增 {summary.ImportedPdfCount} 个，已存在 {summary.SkippedKnownPdfCount} 个，失败 {summary.FailedPdfCount} 个；部分扫描 {summary.PartialRootCount} 个，不可用 {summary.UnavailableRootCount} 个，跳过目录 {summary.SkippedDirectoryCount} 个、文件 {summary.SkippedFileCount} 个。";
    }

    private async Task<HashSet<string>> LoadKnownFilePathsAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<string>> paths = await _services.Files.ListOriginalPathsAsync(cancellationToken);
        if (paths.IsFailure)
        {
            throw new InvalidOperationException(paths.ErrorMessage);
        }

        return paths.Value.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ScheduleFileSearchRootRescan(string changeDescription, long watcherGeneration)
    {
        try
        {
            CancellationTokenSource current = new();
            CancellationTokenSource? previous;
            Task? previousTask;
            Task currentTask;
            lock (_fileSearchRootWatchSync)
            {
                if (!_fileSearchRootWatchersActive || watcherGeneration != _fileSearchRootWatchGeneration)
                {
                    current.Dispose();
                    return;
                }

                previous = _fileSearchRootWatchDebounce;
                previousTask = _fileSearchRootWatchDebounceTask;
                currentTask = DebounceFileSearchRootRescanAsync(current.Token);
                _fileSearchRootWatchDebounce = current;
                _fileSearchRootWatchDebounceTask = currentTask;
            }

            _ = LogOperationAsync(FileWatcherTrigger, $"Change detected: {changeDescription}; rescan scheduled.");
            previous?.Cancel();
            if (previous is not null)
            {
                if (previousTask is null)
                {
                    previous.Dispose();
                }
                else
                {
                    DisposeCancellationSourceAfterTask(previous, previousTask);
                }
            }

            ObserveDebouncedRescan(currentTask, current.Token);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "schedule-rescan");
        }
    }

    private static void DisposeCancellationSourceAfterTask(CancellationTokenSource source, Task task)
    {
        _ = task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            source,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveDebouncedRescan(Task task, CancellationToken cancellationToken)
    {
        _ = task.ContinueWith(
            (completed, state) =>
            {
                CancellationToken token = (CancellationToken)state!;
                if (completed.IsCanceled && token.IsCancellationRequested)
                {
                    return;
                }

                if (completed.IsFaulted && completed.Exception is not null)
                {
                    UnexpectedExceptions.Sink.Report(completed.Exception.GetBaseException(), FileWatcherTrigger,
                        "debounced-rescan");
                }
            },
            cancellationToken,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DebounceFileSearchRootRescanAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await RescanFileSearchRootsAsync("文件变化后自动重新扫描完成。", cancellationToken,
            trigger: FileWatcherTrigger);
    }

    private async Task LogOperationAsync(string operation, string message)
    {
        try
        {
            await _logger.LogAsync(operation, message);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "operation-log", operation);
        }
    }
}
