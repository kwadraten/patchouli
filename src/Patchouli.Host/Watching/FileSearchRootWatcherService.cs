using System.Threading.Channels;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Import;
using Patchouli.UI.Diagnostics;

namespace Patchouli.Host.Watching;

/// <summary>
/// Owns the <see cref="FileSystemWatcher"/> fleet for the configured file search roots plus the
/// rescan pipeline that imports newly discovered PDFs. File events only reserve
/// work in a dirty set consumed by a single watch loop (incremental import passes, with full
/// reconciliation rescans at activation, on watcher errors and on a periodic interval);
/// events never cancel a scan or import already running. Subscribers react to
/// <see cref="RescanCompleted"/>, <see cref="RescanFailed"/> and
/// <see cref="SearchRootAvailabilityChanged"/> and marshal to the UI as needed.
/// </summary>
public sealed class FileSearchRootWatcherService : IAsyncDisposable
{
    public const string ManualTrigger = "manual";
    public const string FileWatcherTrigger = "file-watcher";

    private readonly FileSearchRootWatcherDependencies _dependencies;
    private readonly IAppLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly WatchLoopSchedule _schedule;

    private readonly Dictionary<string, FileSystemWatcher> _fileSearchRootWatchers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _fileSearchRootWatchSync = new();

    // Platform path-case semantics come from the claim coordinator's shared comparer:
    // on Linux, A.pdf and a.pdf are different files and must stay separate dirty entries.
    private readonly Dictionary<string, long> _changedPaths = new(FileOperationCoordinator.PathComparer);
    private long _fileChangeVersion;
    private readonly SemaphoreSlim _fileSearchRootRescanGate = new(1, 1);
    private long _fileSearchRootWatchGeneration;
    private bool _fileSearchRootWatchersActive;

    // Watcher scheduling is a single-consumer loop fed by a coalescing
    // signal. File events only record dirty paths and signal the loop — they never
    // cancel a pending wait's replacement or, worse, a scan/import already running.
    // The loop alone decides between an incremental import pass over the dirty set
    // and a full reconciliation rescan. The pure cadence decisions live in
    // WatchLoopSchedule; the production timing knobs are WatchScheduleOptions.Default.
    private static readonly TimeSpan StaleImportTempAge = TimeSpan.FromHours(1);
    private const int IncrementalImportConcurrency = 2;

    private readonly Channel<byte> _rescanSignal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private CancellationTokenSource? _watchLoopStop;
    private Task? _watchLoopTask;
    private long _lastFileEventTimestamp;
    private long _burstStartTimestamp;
    private int _fullRescanRequested;
    private DateTimeOffset _lastFullRescanUtc = DateTimeOffset.MinValue;
    private bool _staleImportCleanupDone;

    public FileSearchRootWatcherService(HostServices services, IAppLogger logger)
        : this(FileSearchRootWatcherDependencies.FromHostServices(services), logger)
    {
    }

    internal FileSearchRootWatcherService(
        FileSearchRootWatcherDependencies dependencies,
        IAppLogger logger,
        TimeProvider? timeProvider = null,
        WatchLoopSchedule? schedule = null)
    {
        _dependencies = dependencies;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _schedule = schedule ?? new WatchLoopSchedule(WatchScheduleOptions.Default);
    }

    /// <summary>Raised on the rescan task's thread after a successful rescan.</summary>
    public event EventHandler<FileSearchRootRescanCompleted>? RescanCompleted;

    /// <summary>Raised on the rescan task's thread when a rescan fails without a summary.</summary>
    public event EventHandler<FileSearchRootRescanFailed>? RescanFailed;

    /// <summary>Raised on the rescan task's thread whenever a root's availability is updated.</summary>
    public event EventHandler<SearchRootAvailabilityChanged>? SearchRootAvailabilityChanged;

    /// <summary>
    /// Diagnostic-only seam (test observable): raised on the watch-loop thread each time the
    /// loop enters a scheduling phase. Raising phases never changes debounce behavior —
    /// subscribers observe only and must not mutate service state.
    /// </summary>
    internal event EventHandler<WatchLoopPhase>? WatchLoopPhaseChanged;

    /// <summary>
    /// Scheduling phases reported by <see cref="WatchLoopPhaseChanged"/> — the values describe
    /// where the watch loop currently spends wall-clock time.
    /// </summary>
    internal enum WatchLoopPhase
    {
        /// <summary>Idle; blocked on the coalescing signal channel or the reconciliation interval.</summary>
        WaitingForSignal,

        /// <summary>Debounce quiet-period wait after a reservation was observed.</summary>
        Debouncing,

        /// <summary>Running a full reconciliation rescan (activation, watcher error or periodic).</summary>
        FullRescan,

        /// <summary>Running an incremental import pass over the swapped-out dirty batch.</summary>
        IncrementalImport
    }

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
            if (_watchLoopTask is null || _watchLoopTask.IsCompleted)
            {
                // Issue #30: every (re)activation starts with one full reconciliation
                // rescan so files changed while the app was closed are picked up.
                Interlocked.Exchange(ref _fullRescanRequested, 1);
                _lastFullRescanUtc = DateTimeOffset.MinValue;
                _watchLoopStop = new CancellationTokenSource();
                CancellationToken stopToken = _watchLoopStop.Token;
                _watchLoopTask = Task.Run(() => RunWatchLoopAsync(stopToken));
            }
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
                    NoteFileEvent($"{e.ChangeType}: {e.FullPath}", watcherGeneration, e.FullPath);
                RenamedEventHandler renamed = (_, e) =>
                    NoteFileEvent($"Renamed: {e.OldFullPath} -> {e.FullPath}", watcherGeneration,
                        e.OldFullPath, e.FullPath);
                ErrorEventHandler error = (_, e) =>
                    NoteWatcherError($"Error: {e.GetException()?.Message ?? "unknown"}", watcherGeneration);
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

        if (!_staleImportCleanupDone)
        {
            _staleImportCleanupDone = true;
            _ = CleanupStaleImportTempFilesAsync(wanted.ToArray());
        }
    }

    /// <summary>
    /// Stops all watchers and the watch loop. Called on database switch and
    /// shutdown; watchers are restarted via <see cref="RefreshWatchers"/>.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? loopStop;
        Task? loopTask;
        lock (_fileSearchRootWatchSync)
        {
            _fileSearchRootWatchersActive = false;
            _fileSearchRootWatchGeneration++;
            loopStop = _watchLoopStop;
            loopTask = _watchLoopTask;
            _watchLoopStop = null;
            _watchLoopTask = null;
        }

        loopStop?.Cancel();
        foreach (FileSystemWatcher watcher in _fileSearchRootWatchers.Values)
        {
            watcher.Dispose();
        }

        _fileSearchRootWatchers.Clear();
        try
        {
            if (loopTask is not null)
            {
                await loopTask;
            }
        }
        catch (OperationCanceledException) when (loopStop?.IsCancellationRequested == true)
        {
            // Expected when a database switch or application shutdown stops the watch loop.
        }
        finally
        {
            loopStop?.Dispose();
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
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _dependencies.LifetimeToken);
        cancellationToken = linkedCancellation.Token;
        await _fileSearchRootRescanGate.WaitAsync(cancellationToken);
        try
        {
            using IActivityScope? activity = _dependencies.ActivityTracker?.BeginScope(
                "扫描文件搜索根",
                HostActivityKind.Scanning,
                trigger == FileWatcherTrigger ? "文件变化触发" : "手动扫描");
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
                await _dependencies.FileResolution.ListSearchRootsAsync(cancellationToken);
            if (roots.IsFailure)
            {
                return Result<FileSearchRootRescanSummary>.Failure(roots.ErrorCode!, roots.ErrorMessage!);
            }

            await LogOperationAsync("file-scan",
                $"Rescan started (trigger={trigger}): {roots.Value.Count} file search root(s).");

            Result<BlockingOperation> started = await _dependencies.BlockingOperations.StartAsync(
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

            HashSet<string> knownPaths = await _dependencies.LoadKnownFilePaths(cancellationToken);
            Dictionary<string, long> changedPaths;
            lock (_fileSearchRootWatchSync)
            {
                changedPaths = new Dictionary<string, long>(_changedPaths, FileOperationCoordinator.PathComparer);
            }

            knownPaths.ExceptWith(changedPaths.Keys);
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
                    await _dependencies.FileSearchRootAccess.ReopenAsync(root, cancellationToken);
                if (reopened.IsFailure)
                {
                    unavailableRoots++;
                    await _dependencies.FileResolution.SetSearchRootAvailabilityAsync(root.RootId, false,
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
                    await _dependencies.FileSearchRootAccess.ScanPdfAsync(reopened.Value, cancellationToken);
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
                await _dependencies.FileResolution.SetSearchRootAvailabilityAsync(root.RootId, available,
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
                        await _dependencies.BlockingOperations.AddLogEntryAsync(operationId.Value, "info",
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

                    FileLocalityAssessment locality = _dependencies.AssessFileLocality(normalizedPath);
                    if (locality.Readiness == FileLocalityReadiness.CloudUnready)
                    {
                        progress?.Invoke(processedRoots, roots.Value.Count,
                            $"正在下载云端文件：{candidate.FileName}", $"下载 → {normalizedPath}");
                        await LogOperationAsync("file-scan",
                            $"Hydrating cloud file (trigger={trigger}): {normalizedPath}");
                        Result materialized =
                            await _dependencies.FileSearchRootAccess.EnsureAvailableAsync(normalizedPath,
                                cancellationToken);
                        locality = _dependencies.AssessFileLocality(normalizedPath);
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

                    // Issue #30: never import a path another importer currently owns
                    // (an in-flight add). Defer it into the watch loop's dirty set for
                    // re-evaluation instead of racing the owner's commit or rollback.
                    IFileOperationLease? claim = _dependencies.FileOperations.TryClaim([normalizedPath]);
                    if (claim is null)
                    {
                        skipped++;
                        RequeueDirtyPath(normalizedPath);
                        await LogOperationAsync("file-scan",
                            $"Import deferred, path claimed by another import (trigger={trigger}): {normalizedPath}");
                        continue;
                    }

                    PdfImportResult importedPdf;
                    try
                    {
                        importedPdf =
                            await _dependencies.PdfImport.ImportPdfAsync(
                                new PdfImportRequest(normalizedPath, null, null, null),
                                cancellationToken);
                    }
                    finally
                    {
                        await claim.DisposeAsync();
                    }

                    if (importedPdf.Success)
                    {
                        if (importedPdf.IsDuplicate)
                        {
                            skipped++;
                        }
                        else
                        {
                            imported++;
                        }

                        knownPaths.Add(normalizedPath);
                        progress?.Invoke(processedRoots, roots.Value.Count,
                            importedPdf.IsDuplicate ? $"文件已存在，已跳过：{candidate.FileName}" : $"已导入：{candidate.FileName}",
                            $"处理完成 → {normalizedPath}");
                        if (importedPdf.FailedPageCount > 0)
                        {
                            string firstPageFailure = importedPdf.PageFailures?.FirstOrDefault()?.ErrorMessage
                                                      ?? "unknown";
                            await LogOperationAsync("file-scan",
                                $"Import finished with {importedPdf.FailedPageCount} failed page placeholder(s) " +
                                $"(trigger={trigger}): {normalizedPath} - first failure: {firstPageFailure}");
                        }
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
                    await _dependencies.BlockingOperations.UpdateProgressAsync(
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
            if (!summary.HasWarnings)
            {
                lock (_fileSearchRootWatchSync)
                {
                    foreach ((string path, long version) in changedPaths)
                    {
                        if (_changedPaths.TryGetValue(path, out long current) && current == version)
                        {
                            _changedPaths.Remove(path);
                        }
                    }
                }
            }

            string message = BuildFileSearchRootRescanMessage(summary, completionMessage);
            progress?.Invoke(roots.Value.Count, roots.Value.Count, "文件重新扫描完成。", message);
            await LogOperationAsync("file-scan",
                $"Rescan finished (trigger={trigger}): scanned={scanned}, imported={imported}, " +
                $"known={skipped}, failed={failed}, partialRoots={partialRoots}, " +
                $"unavailableRoots={unavailableRoots}.");
            if (operationId is not null)
            {
                await _dependencies.BlockingOperations.CompleteAsync(operationId.Value, message, Array.Empty<string>(),
                    cancellationToken);
            }

            return Result<FileSearchRootRescanSummary>.Success(summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await LogOperationAsync("file-scan", $"Rescan cancelled (trigger={trigger}).");
            if (operationId is not null)
            {
                await _dependencies.BlockingOperations.CancelAsync(
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
                await _dependencies.BlockingOperations.FailAsync(operationId.Value, AppErrorCodes.InvalidState, message,
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

    /// <summary>
    /// Issue #30: file events only make a reservation — record the dirty paths, stamp
    /// the event time and signal the watch loop. Nothing here cancels anything; a scan
    /// or import already running is never interrupted by later events.
    /// </summary>
    private void NoteFileEvent(string changeDescription, long watcherGeneration, params string[] changedPaths)
    {
        try
        {
            lock (_fileSearchRootWatchSync)
            {
                if (!_fileSearchRootWatchersActive || watcherGeneration != _fileSearchRootWatchGeneration)
                {
                    return;
                }

                if (_changedPaths.Count == 0)
                {
                    Volatile.Write(ref _burstStartTimestamp, _timeProvider.GetTimestamp());
                }

                foreach (string path in changedPaths)
                {
                    _changedPaths[path] = ++_fileChangeVersion;
                }
            }

            Volatile.Write(ref _lastFileEventTimestamp, _timeProvider.GetTimestamp());
            _rescanSignal.Writer.TryWrite(0);
            _ = LogOperationAsync(FileWatcherTrigger, $"Change detected: {changeDescription}; rescan scheduled.");
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "note-file-event");
        }
    }

    /// <summary>
    /// A watcher error (e.g. internal buffer overflow) means events may have been lost:
    /// reserve one full reconciliation rescan instead of an incremental pass.
    /// </summary>
    private void NoteWatcherError(string errorDescription, long watcherGeneration)
    {
        try
        {
            lock (_fileSearchRootWatchSync)
            {
                if (!_fileSearchRootWatchersActive || watcherGeneration != _fileSearchRootWatchGeneration)
                {
                    return;
                }
            }

            Interlocked.Exchange(ref _fullRescanRequested, 1);
            _rescanSignal.Writer.TryWrite(0);
            _ = LogOperationAsync(FileWatcherTrigger, $"Watcher error: {errorDescription}; full rescan scheduled.");
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "note-watcher-error");
        }
    }

    private bool HasDirtyPaths
    {
        get
        {
            lock (_fileSearchRootWatchSync)
            {
                return _changedPaths.Count > 0;
            }
        }
    }

    private List<string> SwapDirtyPaths()
    {
        lock (_fileSearchRootWatchSync)
        {
            List<string> batch = _changedPaths.Keys.ToList();
            _changedPaths.Clear();
            return batch;
        }
    }

    private void RequeueDirtyPath(string normalizedPath)
    {
        lock (_fileSearchRootWatchSync)
        {
            if (_changedPaths.Count == 0)
            {
                Volatile.Write(ref _burstStartTimestamp, _timeProvider.GetTimestamp());
            }

            _changedPaths[normalizedPath] = ++_fileChangeVersion;
        }
    }

    /// <summary>
    /// The one and only consumer of watcher reservations. Each iteration either runs a
    /// full reconciliation rescan (activation, watcher error, or the periodic interval)
    /// or an incremental import pass over the swapped-out dirty set. Execution is bound
    /// only to the service stop token — file events arriving mid-pass simply dirty the
    /// next batch.
    /// </summary>
    private async Task RunWatchLoopAsync(CancellationToken stopToken)
    {
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                try
                {
                    await RunWatchIterationAsync(stopToken);
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // One failed pass (e.g. the database was briefly unavailable) must
                    // not kill the loop; the next signal or reconciliation retries.
                    UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "watch-loop");
                    await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stopToken);
                }
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Service stop: exit quietly.
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "watch-loop");
        }
    }

    private async Task RunWatchIterationAsync(CancellationToken stopToken)
    {
        while (_rescanSignal.Reader.TryRead(out _))
        {
            // Drain coalesced signals; the dirty set and flags are the real state.
        }

        bool fullRequested = Interlocked.Exchange(ref _fullRescanRequested, 0) == 1;
        bool reconciliationDue =
            _schedule.IsReconciliationDue(_timeProvider.GetUtcNow(), _lastFullRescanUtc);
        if (!fullRequested && !reconciliationDue && !HasDirtyPaths)
        {
            ReportWatchLoopPhase(WatchLoopPhase.WaitingForSignal);
            await WaitForSignalAsync(stopToken);
            return;
        }

        ReportWatchLoopPhase(WatchLoopPhase.Debouncing);
        await WaitQuietPeriodAsync(stopToken);
        if (stopToken.IsCancellationRequested)
        {
            return;
        }

        if (_schedule.SelectWorkAfterDebounce(fullRequested, reconciliationDue) == WatchLoopWork.FullRescan)
        {
            _lastFullRescanUtc = _timeProvider.GetUtcNow();
            ReportWatchLoopPhase(WatchLoopPhase.FullRescan);
            await RescanFileSearchRootsAsync("文件变化后自动重新扫描完成。", stopToken,
                trigger: FileWatcherTrigger);
            return;
        }

        List<string> batch = SwapDirtyPaths();
        if (batch.Count == 0)
        {
            return;
        }

        ReportWatchLoopPhase(WatchLoopPhase.IncrementalImport);
        int deferred = await RunIncrementalImportPassAsync(batch, stopToken);
        if (deferred > 0 && !stopToken.IsCancellationRequested)
        {
            // Deferred paths were re-queued; retry on a bounded cadence rather
            // than spinning while their claim holder finishes.
            await Task.Delay(_schedule.Options.DeferredRetryDelay, _timeProvider, stopToken);
            if (HasDirtyPaths)
            {
                _rescanSignal.Writer.TryWrite(0);
            }
        }
    }

    /// <summary>
    /// Sleeps until the next reservation signal, but wakes when the periodic
    /// reconciliation comes due so an idle watcher still reconciles on schedule.
    /// </summary>
    private async Task WaitForSignalAsync(CancellationToken stopToken)
    {
        TimeSpan untilDue = _schedule.SignalWaitTimeout(_timeProvider.GetUtcNow(), _lastFullRescanUtc);
        if (untilDue <= TimeSpan.Zero)
        {
            return;
        }

        using CancellationTokenSource deadline = new(untilDue, _timeProvider);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(stopToken, deadline.Token);
        try
        {
            await _rescanSignal.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
        {
            // Reconciliation interval elapsed without any file event.
        }
    }

    /// <summary>
    /// Debounce as quiet-period re-evaluation: wait until no file event has arrived for
    /// <see cref="WatchScheduleOptions.QuietPeriod"/>, re-checking the remaining time whenever
    /// the delay elapses. The delay is bound only to the stop token — new events extend the
    /// wait by moving the last-event timestamp, never by cancelling it — and
    /// <see cref="WatchScheduleOptions.MaxWait"/> caps how long a continuous event stream can
    /// postpone work.
    /// </summary>
    private async Task WaitQuietPeriodAsync(CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            TimeSpan quietFor =
                _timeProvider.GetElapsedTime(Volatile.Read(ref _lastFileEventTimestamp));
            TimeSpan burstFor =
                _timeProvider.GetElapsedTime(Volatile.Read(ref _burstStartTimestamp));
            TimeSpan? delay = _schedule.EvaluateQuietWait(quietFor, burstFor);
            if (delay is null)
            {
                return;
            }

            await Task.Delay(delay.Value, _timeProvider, stopToken);
        }
    }

    /// <summary>
    /// Raises <see cref="WatchLoopPhaseChanged"/>. A misbehaving subscriber must not kill
    /// the single-consumer watch loop, so handler failures are reported and swallowed.
    /// </summary>
    private void ReportWatchLoopPhase(WatchLoopPhase phase)
    {
        try
        {
            WatchLoopPhaseChanged?.Invoke(this, phase);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "watch-loop-phase");
        }
    }

    private enum IncrementalImportOutcome
    {
        Imported,
        Skipped,
        Failed,
        Deferred
    }

    /// <summary>
    /// Incremental pass over one dirty batch: imports only the reserved paths (bounded
    /// concurrency), serialized with full rescans through the same gate. Returns how
    /// many paths were deferred back into the dirty set.
    /// </summary>
    private async Task<int> RunIncrementalImportPassAsync(
        IReadOnlyList<string> batch,
        CancellationToken stopToken)
    {
        await _fileSearchRootRescanGate.WaitAsync(stopToken);
        try
        {
            using IActivityScope? activity = _dependencies.ActivityTracker?.BeginScope(
                "扫描文件搜索根",
                HostActivityKind.Scanning,
                "文件变化触发");
            int imported = 0;
            int skipped = 0;
            int failed = 0;
            int deferred = 0;
            using SemaphoreSlim concurrency = new(IncrementalImportConcurrency);
            Task[] passes = batch.Select(async path =>
            {
                await concurrency.WaitAsync(stopToken);
                try
                {
                    IncrementalImportOutcome outcome;
                    try
                    {
                        outcome = await ImportDirtyPathAsync(path, stopToken);
                    }
                    catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "incremental-import");
                        outcome = IncrementalImportOutcome.Failed;
                    }

                    switch (outcome)
                    {
                        case IncrementalImportOutcome.Imported:
                            Interlocked.Increment(ref imported);
                            break;
                        case IncrementalImportOutcome.Failed:
                            Interlocked.Increment(ref failed);
                            break;
                        case IncrementalImportOutcome.Deferred:
                            Interlocked.Increment(ref deferred);
                            break;
                        default:
                            Interlocked.Increment(ref skipped);
                            break;
                    }
                }
                finally
                {
                    concurrency.Release();
                }
            }).ToArray();
            await Task.WhenAll(passes);

            await LogOperationAsync("file-scan",
                $"Incremental pass finished (trigger={FileWatcherTrigger}): candidates={batch.Count}, " +
                $"imported={imported}, skipped={skipped}, failed={failed}, deferred={deferred}.");
            // A deduplicated import can still update a renamed file's known location. Every
            // completed pass must publish its result, not only passes creating a new asset.
            FileSearchRootRescanSummary summary = new(batch.Count, imported, skipped, failed, 0, 0, 0, 0);
            ApplyFileSearchRootRescanResult(
                Result<FileSearchRootRescanSummary>.Success(summary),
                "文件变化后自动重新扫描完成。", FileWatcherTrigger);

            return deferred;
        }
        finally
        {
            _fileSearchRootRescanGate.Release();
        }
    }

    private async Task<IncrementalImportOutcome> ImportDirtyPathAsync(
        string path,
        CancellationToken stopToken)
    {
        string normalizedPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(normalizedPath), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(normalizedPath))
        {
            // Deleted, renamed away, or not a PDF (rename sources, ".importing" temps):
            // nothing to import here; full reconciliation owns database-side staleness.
            return IncrementalImportOutcome.Skipped;
        }

        // A reserved dirty path is evidence of a change even when size and mtime match.
        // Re-fingerprint this bounded dirty batch; only reconciliation scans may use the
        // unchanged-path shortcut. The import pipeline still deduplicates identical content.
        FileLocalityAssessment locality = _dependencies.AssessFileLocality(normalizedPath);
        if (locality.Readiness != FileLocalityReadiness.CloudUnready && !CanOpenForRead(normalizedPath))
        {
            // Still being written by an external copy: defer quietly instead of
            // importing a partial file or logging a spurious failure.
            RequeueDirtyPath(normalizedPath);
            return IncrementalImportOutcome.Deferred;
        }

        IFileOperationLease? lease = _dependencies.FileOperations.TryClaim([normalizedPath]);
        if (lease is null)
        {
            // An explicit import owns this path right now; re-evaluate after it
            // finishes instead of importing the same file a second time.
            RequeueDirtyPath(normalizedPath);
            return IncrementalImportOutcome.Deferred;
        }

        await using (lease)
        {
            if (locality.Readiness == FileLocalityReadiness.CloudUnready)
            {
                await LogOperationAsync("file-scan",
                    $"Hydrating cloud file (trigger={FileWatcherTrigger}): {normalizedPath}");
                Result materialized =
                    await _dependencies.FileSearchRootAccess.EnsureAvailableAsync(normalizedPath, stopToken);
                locality = _dependencies.AssessFileLocality(normalizedPath);
                if (materialized.IsFailure)
                {
                    await LogOperationAsync("file-scan",
                        $"Cloud hydration failed (trigger={FileWatcherTrigger}): {normalizedPath} - " +
                        $"{materialized.ErrorMessage ?? locality.Reason ?? "云端文件下载未完成。"}");
                    return IncrementalImportOutcome.Failed;
                }
            }

            await LogOperationAsync("file-scan",
                $"Importing (trigger={FileWatcherTrigger}): {normalizedPath}");
            PdfImportResult importedPdf =
                await _dependencies.PdfImport.ImportPdfAsync(new PdfImportRequest(normalizedPath, null, null, null),
                    stopToken);
            if (!importedPdf.Success)
            {
                await LogOperationAsync("file-scan",
                    $"Import failed (trigger={FileWatcherTrigger}): {normalizedPath} - {importedPdf.ErrorMessage}");
                return IncrementalImportOutcome.Failed;
            }

            if (importedPdf.FailedPageCount > 0)
            {
                string firstPageFailure = importedPdf.PageFailures?.FirstOrDefault()?.ErrorMessage ?? "unknown";
                await LogOperationAsync("file-scan",
                    $"Import finished with {importedPdf.FailedPageCount} failed page placeholder(s) " +
                    $"(trigger={FileWatcherTrigger}): {normalizedPath} - first failure: {firstPageFailure}");
            }

            return importedPdf.IsDuplicate
                ? IncrementalImportOutcome.Skipped
                : IncrementalImportOutcome.Imported;
        }
    }

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Issue #30: removes stale ".importing" temp files left in the search roots by a
    /// process that died in the middle of a cross-volume copy. Runs once, on the first
    /// watcher activation; a temp file is removed only when it matches the generated
    /// naming pattern, is not currently claimed, and its last write is over an hour
    /// old — an in-flight copy is never touched.
    /// </summary>
    private async Task CleanupStaleImportTempFilesAsync(IReadOnlyList<string> rootPaths)
    {
        try
        {
            foreach (string rootPath in rootPaths)
            {
                List<string> stale = await Task.Run(() =>
                {
                    List<string> found = [];
                    try
                    {
                        foreach (string candidate in Directory.EnumerateFiles(rootPath, "*.importing",
                                     SearchOption.AllDirectories))
                        {
                            if (IsImportTempFileName(Path.GetFileName(candidate)) &&
                                !_dependencies.FileOperations.IsClaimed(candidate) &&
                                File.GetLastWriteTimeUtc(candidate) <
                                _timeProvider.GetUtcNow().UtcDateTime - StaleImportTempAge)
                            {
                                found.Add(candidate);
                            }
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger,
                            "cleanup-stale-import-temp");
                    }

                    return found;
                });

                foreach (string path in stale)
                {
                    try
                    {
                        File.Delete(path);
                        await LogOperationAsync("file-scan", $"Removed stale import temp file: {path}");
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger,
                            "cleanup-stale-import-temp");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, FileWatcherTrigger, "cleanup-stale-import-temp");
        }
    }

    private static bool IsImportTempFileName(string fileName)
    {
        // Generated names look like ".{original file name}.{32 hex guid}.importing".
        const string suffix = ".importing";
        if (!fileName.StartsWith('.') ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string withoutSuffix = fileName[..^suffix.Length];
        int lastDot = withoutSuffix.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        string guidPart = withoutSuffix[(lastDot + 1)..];
        return guidPart.Length == 32 && guidPart.All(Uri.IsHexDigit);
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
