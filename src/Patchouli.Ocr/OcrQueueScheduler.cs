using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Ocr;

public sealed class OcrRetryPolicy : IOcrRetryPolicy
{
    private static readonly HashSet<string> Transient =
    [
        "network_timeout", "temporary_provider_error", "rate_limited", "quota_exceeded_retryable", "worker_crashed",
        OcrFailureCode.LocalOcrTimeout, MinerUProviderStatus.DownloadFailed, MinerUProviderStatus.Timeout,
        MinerUProviderStatus.UploadFailed, MinerUProviderStatus.UploadUrlFailed
    ];

    private static readonly HashSet<string> Manual =
    [
        "auth_failed", "model_not_found", "bad_endpoint_config", "model_path_missing", "model_path_inaccessible",
        OcrFailureCode.SourceFileMissing, OcrFailureCode.SourceFileChanged,
        OcrFailureCode.BBoxCoordinateTransformFailed, OcrFailureCode.ImageTooLargeForOcr,
        OcrFailureCode.RendererTimeout, "unsupported_file", "invalid_page_box", "missing_executable"
    ];

    public string Classify(string? code)
    {
        return Manual.Contains(code ?? "") ? OcrRetryClassification.ManualRepairRequired :
            Transient.Contains(code ?? "") ? OcrRetryClassification.TransientRetryable :
            OcrRetryClassification.NonRetryable;
    }

    public bool ShouldRetry(OcrQueueTask task, string? code)
    {
        return Classify(code) == OcrRetryClassification.TransientRetryable && (code == OcrFailureCode.LocalOcrTimeout
            ? task.AttemptCount < 1
            : task.AttemptCount < task.MaxAttempts);
    }

    public TimeSpan GetNextDelay(int attempt)
    {
        return attempt switch
        {
            1 => TimeSpan.FromSeconds(5), 2 => TimeSpan.FromSeconds(30), _ => TimeSpan.FromMinutes(2)
        };
    }
}

public sealed class OcrQueueScheduler : IOcrQueueScheduler
{
    private readonly IClock _clock;
    private readonly IOcrQueueTaskExecutor _executor;
    private readonly IOcrRetryPolicy _retry;
    private readonly OcrQueueLimits _limits;
    private readonly Dictionary<OcrQueueTaskId, OcrQueueTask> _tasks = new();
    private readonly HashSet<string> _pauses = new();
    private readonly Dictionary<OcrQueueTaskId, CancellationTokenSource> _running = new();
    private readonly Dictionary<OcrQueueTaskId, OcrTaskProgressReport> _progress = new();
    private readonly Dictionary<OcrQueueTaskId, DateTimeOffset> _finishedAt = new();
    private readonly object _progressLock = new();
    private DateTime _lastProgressNotification;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<Result<LibraryId>>> _libraryIdResolver;
    private readonly Action<Exception>? _loopErrorLogger;
    private readonly TimeSpan _loopInterval;
    private LibraryId? _libraryId;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;

    public OcrQueueScheduler(LibraryId libraryId, IClock clock, IOcrQueueTaskExecutor executor,
        IOcrRetryPolicy? retry = null, OcrQueueLimits? limits = null, TimeSpan? loopInterval = null,
        Action<Exception>? loopErrorLogger = null)
        : this(_ => Task.FromResult(Result<LibraryId>.Success(libraryId)), clock, executor, retry, limits,
            loopInterval, loopErrorLogger)
    {
    }

    public OcrQueueScheduler(Func<CancellationToken, Task<Result<LibraryId>>> libraryIdResolver, IClock clock,
        IOcrQueueTaskExecutor executor, IOcrRetryPolicy? retry = null, OcrQueueLimits? limits = null,
        TimeSpan? loopInterval = null, Action<Exception>? loopErrorLogger = null)
    {
        _libraryIdResolver = libraryIdResolver;
        _clock = clock;
        _executor = executor;
        _retry = retry ?? new OcrRetryPolicy();
        _limits = limits ?? OcrQueueLimits.Default;
        _loopInterval = loopInterval ?? TimeSpan.FromMilliseconds(500);
        _loopErrorLogger = loopErrorLogger;
    }

    public event EventHandler<OcrQueueChangedEventArgs>? Changed;

    public Task<Result<OcrQueueTask>> EnqueueDocumentAsync(DocumentInstanceId d, OcrPresetId p,
        IReadOnlyList<PageId> pages, string engineId, string adapterKind, string? providerId, string priority,
        CancellationToken c = default)
    {
        return EnqueueAsync(
            new OcrQueueTaskRequest(d, p, pages, OcrQueueTaskKind.Document, engineId, adapterKind, providerId,
                priority),
            c);
    }

    public Task<Result<OcrQueueTask>> EnqueueMockPagesAsync(DocumentInstanceId d, OcrPresetId p,
        IReadOnlyList<PageId> pages, string priority, CancellationToken c = default)
    {
        return EnqueueAsync(
            new OcrQueueTaskRequest(d, p, pages, OcrQueueTaskKind.MockPages, OcrEngineIds.Mock, OcrAdapterKind.Mock,
                null, priority), c);
    }

    public Task<Result<OcrQueueTask>> EnqueueImagePageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
        string path, string priority, CancellationToken c = default)
    {
        return EnqueueAsync(
            new OcrQueueTaskRequest(d, p, [page], OcrQueueTaskKind.ImagePage, OcrEngineIds.LocalPlaceholder,
                OcrAdapterKind.LocalProcess,
                null, priority, path), c);
    }

    public Task<Result<OcrQueueTask>> EnqueueRenderedPdfPageAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
        int dpi, string priority, CancellationToken c = default)
    {
        return EnqueueAsync(
            new OcrQueueTaskRequest(d, p, [page], OcrQueueTaskKind.RenderedPdfPage, OcrEngineIds.LocalPlaceholder,
                OcrAdapterKind.LocalProcess, null, priority, null, dpi), c);
    }

    public Task<Result<OcrQueueTask>> EnqueueRegionAsync(DocumentInstanceId d, OcrPresetId p, PageId page,
        NormalizedBBox regionBBox, string engineId, string adapterKind, string? providerId, string priority,
        CancellationToken c = default)
    {
        return EnqueueAsync(
            new OcrQueueTaskRequest(d, p, [page], OcrQueueTaskKind.Region, engineId, adapterKind, providerId,
                priority, RegionBBox: regionBBox, CommitOnCompletion: false), c);
    }

    public Task StartAsync(CancellationToken c = default)
    {
        if (_loopTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _loop = CancellationTokenSource.CreateLinkedTokenSource(c);
        _loopTask = Task.Run(async () =>
        {
            while (!_loop.IsCancellationRequested)
            {
                try
                {
                    await RunOneSchedulingTickAsync(_loop.Token);
                }
                catch (OperationCanceledException) when (_loop.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    try
                    {
                        _loopErrorLogger?.Invoke(ex);
                    }
                    // A failing diagnostic callback must not terminate the scheduler loop.
                    // ReSharper disable once EmptyGeneralCatchClause
                    catch
                    {
                    }
                }

                try
                {
                    await Task.Delay(_loopInterval, _loop.Token);
                }
                catch (OperationCanceledException) when (_loop.IsCancellationRequested)
                {
                }
            }
        }, CancellationToken.None);
        OnChanged(null, OcrQueueChangeKind.Started);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken c = default)
    {
        if (_loop is null)
        {
            return;
        }

        _loop.Cancel();
        try
        {
            if (_loopTask is not null)
            {
                await _loopTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loop.Dispose();
            _loop = null;
            _loopTask = null;
            OnChanged(null, OcrQueueChangeKind.Stopped);
        }
    }

    public async Task<Result<OcrQueueTask>> EnqueueAsync(OcrQueueTaskRequest request, CancellationToken c = default)
    {
        if (request.PageIds.Count == 0 || string.IsNullOrWhiteSpace(request.TaskKind) ||
            string.IsNullOrWhiteSpace(request.EngineId) || string.IsNullOrWhiteSpace(request.AdapterKind))
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.ValidationFailed,
                "OCR queue task requires pages, kind, engine, and adapter kind.");
        }

        Result<LibraryId>? resolvedLibraryId = null;
        if (_libraryId is null)
        {
            resolvedLibraryId = await _libraryIdResolver(c);
            if (resolvedLibraryId.IsFailure)
            {
                return Result<OcrQueueTask>.Failure(resolvedLibraryId.ErrorCode!, resolvedLibraryId.ErrorMessage!);
            }
        }

        OcrQueueTask task;
        await _gate.WaitAsync(c);
        try
        {
            _libraryId ??= resolvedLibraryId!.Value;
            DateTimeOffset now = _clock.UtcNow;
            task = new OcrQueueTask(OcrQueueTaskId.New(), _libraryId.Value, request.DocumentInstanceId,
                request.PresetId,
                request.PageIds, request.TaskKind, request.EngineId, request.AdapterKind, request.ProviderId,
                request.Priority, now, now, OcrQueueTaskState.Queued, 0, Math.Max(1, request.MaxAttempts), null, null,
                null, null, request.ImagePath, request.Dpi, RegionBBox: request.RegionBBox,
                CommitOnCompletion: request.CommitOnCompletion);
            _tasks[task.TaskId] = task;
        }
        finally
        {
            _gate.Release();
        }

        OnChanged(task, OcrQueueChangeKind.Enqueued);
        return Result<OcrQueueTask>.Success(task);
    }

    public async Task RunOneSchedulingTickAsync(CancellationToken c = default)
    {
        List<(OcrQueueTask Task, CancellationTokenSource Cts)> claimed = [];
        await _gate.WaitAsync(c);
        try
        {
            OcrQueueTask[] eligible = _tasks.Values
                .Where(t => t.State == OcrQueueTaskState.Queued &&
                            (t.ScheduledAfter is null || t.ScheduledAfter <= _clock.UtcNow) && !Paused(t))
                .OrderByDescending(Score)
                .ThenBy(t => t.CreatedAt)
                .ToArray();
            foreach (OcrQueueTask candidate in eligible)
            {
                if (!CanRun(candidate))
                {
                    continue;
                }

                OcrQueueTask task = candidate with
                {
                    State = OcrQueueTaskState.Running, UpdatedAt = _clock.UtcNow, CompletedPageCount = 0,
                    FailedPageCount = 0
                };
                _tasks[task.TaskId] = task;
                CancellationTokenSource cts = new();
                _running[task.TaskId] = cts;
                claimed.Add((task, cts));
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach ((OcrQueueTask task, CancellationTokenSource cts) in claimed)
        {
            OnChanged(task, OcrQueueChangeKind.Updated);
            _ = Task.Run(() => ExecuteAndCompleteAsync(task, cts), CancellationToken.None);
        }
    }

    public async Task WaitForIdleAsync(CancellationToken c = default)
    {
        while (true)
        {
            await _gate.WaitAsync(c);
            try
            {
                DateTimeOffset now = _clock.UtcNow;
                bool idle = _tasks.Values.All(t =>
                    t.State != OcrQueueTaskState.Running &&
                    (t.State != OcrQueueTaskState.Queued ||
                     (t.ScheduledAfter is not null && t.ScheduledAfter > now)));
                if (idle)
                {
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(20, c);
        }
    }

    private async Task ExecuteAndCompleteAsync(OcrQueueTask task, CancellationTokenSource cts)
    {
        OcrQueueExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(task, cts.Token, new TaskProgressSink(this));
        }
        catch (OperationCanceledException)
        {
            result = new OcrQueueExecutionResult(false, true);
        }
        catch (Exception ex)
        {
            try
            {
                _loopErrorLogger?.Invoke(ex);
            }
            // A failing diagnostic callback must not prevent worker state recovery.
            // ReSharper disable once EmptyGeneralCatchClause
            catch
            {
            }

            result = new OcrQueueExecutionResult(false, false, "worker_crashed", ex.Message);
        }

        OcrQueueTask updated;
        await _gate.WaitAsync();
        try
        {
            _running.Remove(task.TaskId);
            DateTimeOffset now = _clock.UtcNow;
            if (result.Cancelled)
            {
                updated = task with
                {
                    State = OcrQueueTaskState.Cancelled, RunId = result.RunId,
                    CompletedPageCount = result.CompletedPageCount, FailedPageCount = result.FailedPageCount,
                    UpdatedAt = now
                };
            }
            else if (result.Succeeded)
            {
                updated = task with
                {
                    State = OcrQueueTaskState.Succeeded, RunId = result.RunId,
                    CompletedPageCount = result.CompletedPageCount, FailedPageCount = result.FailedPageCount,
                    UpdatedAt = now
                };
            }
            else if (_retry.ShouldRetry(task, result.ErrorCode))
            {
                updated = task with
                {
                    State = OcrQueueTaskState.Queued, AttemptCount = task.AttemptCount + 1,
                    ScheduledAfter = now + _retry.GetNextDelay(task.AttemptCount + 1), RunId = result.RunId,
                    CompletedPageCount = result.CompletedPageCount, FailedPageCount = result.FailedPageCount,
                    LastErrorCode = result.ErrorCode, LastErrorMessage = result.ErrorMessage, UpdatedAt = now
                };
            }
            else
            {
                updated = task with
                {
                    State = _retry.Classify(result.ErrorCode) == OcrRetryClassification.ManualRepairRequired
                        ? OcrQueueTaskState.Blocked
                        : OcrQueueTaskState.Failed,
                    RunId = result.RunId,
                    CompletedPageCount = result.CompletedPageCount, FailedPageCount = result.FailedPageCount,
                    LastErrorCode = result.ErrorCode, LastErrorMessage = result.ErrorMessage, UpdatedAt = now
                };
            }

            _tasks[task.TaskId] = updated;
        }
        finally
        {
            cts.Dispose();
            _gate.Release();
        }

        if (updated.State is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
            or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked)
        {
            lock (_progressLock)
            {
                _finishedAt[task.TaskId] = updated.UpdatedAt;
            }
        }

        OnChanged(updated, OcrQueueChangeKind.Updated);
    }

    private void OnProgress(OcrTaskProgressReport report)
    {
        lock (_progressLock)
        {
            _progress[report.TaskId] = report;
            // Byte-level download reports can arrive at a very high rate; the snapshot above is
            // always current, but change notifications are throttled to keep UI refreshes cheap.
            DateTime now = DateTime.UtcNow;
            if ((now - _lastProgressNotification).TotalMilliseconds < 200)
            {
                return;
            }

            _lastProgressNotification = now;
        }

        // A null task keeps state-machine side effects (shell status, retry watchers) out of
        // pure progress notifications; listeners re-query snapshots via GetTaskProgress.
        OnChanged(null, OcrQueueChangeKind.Progress);
    }

    private sealed class TaskProgressSink(OcrQueueScheduler scheduler)
        : IProgress<OcrTaskProgressReport>
    {
        public void Report(OcrTaskProgressReport value)
        {
            scheduler.OnProgress(value);
        }
    }

    public async Task<Result> PauseAsync(string scope, string? target = null, CancellationToken c = default)
    {
        if (scope == "preset")
        {
            return Result.Failure(AppErrorCodes.UnsupportedOperation, "Preset-level pause is not supported.");
        }

        await _gate.WaitAsync(c);
        try
        {
            _pauses.Add(scope + ":" + (target ?? ""));
        }
        finally
        {
            _gate.Release();
        }

        OnChanged(null, OcrQueueChangeKind.Updated);
        return Result.Success();
    }

    public async Task<Result> ResumeAsync(string scope, string? target = null, CancellationToken c = default)
    {
        await _gate.WaitAsync(c);
        try
        {
            _pauses.Remove(scope + ":" + (target ?? ""));
        }
        finally
        {
            _gate.Release();
        }

        OnChanged(null, OcrQueueChangeKind.Updated);
        return Result.Success();
    }

    public async Task<Result> CancelTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
    {
        OcrQueueTask? updated = null;
        await _gate.WaitAsync(c);
        try
        {
            if (!_tasks.TryGetValue(id, out OcrQueueTask? task))
            {
                return Result.Failure(AppErrorCodes.NotFound, "Queue task was not found.");
            }

            if (task.State is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
                or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked)
            {
                return Result.Failure(AppErrorCodes.InvalidState, "Terminal OCR tasks cannot be cancelled.");
            }

            if (_running.TryGetValue(id, out CancellationTokenSource? cts))
            {
                cts.Cancel();
            }
            else
            {
                updated = task with { State = OcrQueueTaskState.Cancelled, UpdatedAt = _clock.UtcNow };
                _tasks[id] = updated;
                lock (_progressLock)
                {
                    _finishedAt[id] = updated.UpdatedAt;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (updated is not null)
        {
            OnChanged(updated, OcrQueueChangeKind.Updated);
        }

        return Result.Success();
    }

    public async Task<Result<OcrQueueTask>> RetryTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
    {
        OcrQueueTask retry;
        await _gate.WaitAsync(c);
        try
        {
            if (!_tasks.TryGetValue(id, out OcrQueueTask? task))
            {
                return Result<OcrQueueTask>.Failure(AppErrorCodes.NotFound, "Queue task was not found.");
            }

            if (task.State is not (OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked))
            {
                return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState,
                    "Only failed or blocked OCR tasks can be retried.");
            }

            DateTimeOffset now = _clock.UtcNow;
            retry = task with
            {
                TaskId = OcrQueueTaskId.New(),
                Priority = OcrQueuePriority.BackgroundRetry,
                CreatedAt = now,
                UpdatedAt = now,
                State = OcrQueueTaskState.Queued,
                AttemptCount = 0,
                RetryOfTaskId = task.TaskId,
                LastErrorCode = null,
                LastErrorMessage = null,
                ScheduledAfter = null,
                RunId = null,
                CompletedPageCount = 0,
                FailedPageCount = 0
            };
            _tasks[retry.TaskId] = retry;
        }
        finally
        {
            _gate.Release();
        }

        OnChanged(retry, OcrQueueChangeKind.Enqueued);
        return Result<OcrQueueTask>.Success(retry);
    }

    public async Task<Result<OcrQueueTask>> GetTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
    {
        await _gate.WaitAsync(c).ConfigureAwait(false);
        try
        {
            return _tasks.TryGetValue(id, out OcrQueueTask? task)
                ? Result<OcrQueueTask>.Success(task)
                : Result<OcrQueueTask>.Failure(AppErrorCodes.NotFound, "Queue task was not found.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<IReadOnlyList<OcrQueueTask>>> ListTasksAsync(OcrQueueTaskFilter f,
        CancellationToken c = default)
    {
        await _gate.WaitAsync(c).ConfigureAwait(false);
        try
        {
            OcrQueueTask[] tasks = _tasks.Values.Where(t =>
                    (f.State is null || t.State == f.State) && (f.EngineId is null || t.EngineId == f.EngineId) &&
                    (f.ProviderId is null || t.ProviderId == f.ProviderId) &&
                    (f.IncludeCompleted ||
                     t.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused))
                .OrderByDescending(Score)
                .ToArray();
            return Result<IReadOnlyList<OcrQueueTask>>.Success(tasks);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<OcrQueueStatus>> GetQueueStatusAsync(CancellationToken c = default)
    {
        await _gate.WaitAsync(c).ConfigureAwait(false);
        try
        {
            int Count(string state)
            {
                return _tasks.Values.Count(t => t.State == state);
            }

            OcrQueueTask[] running = _tasks.Values.Where(t => t.State == OcrQueueTaskState.Running).ToArray();
            OcrQueueStatus status = new(_loopTask is { IsCompleted: false }, Count(OcrQueueTaskState.Queued),
                Count(OcrQueueTaskState.Running), Count(OcrQueueTaskState.Succeeded), Count(OcrQueueTaskState.Failed),
                Count(OcrQueueTaskState.Cancelled), Count(OcrQueueTaskState.Blocked), _pauses.ToArray(), _limits,
                running.GroupBy(t => t.EngineId).ToDictionary(g => g.Key, g => g.Count()),
                running.Where(t => t.ProviderId is not null).GroupBy(t => t.ProviderId!)
                    .ToDictionary(g => g.Key, g => g.Count()));
            return Result<OcrQueueStatus>.Success(status);
        }
        finally
        {
            _gate.Release();
        }
    }

    public OcrTaskProgressReport? GetTaskProgress(OcrQueueTaskId taskId)
    {
        lock (_progressLock)
        {
            return _progress.GetValueOrDefault(taskId);
        }
    }

    public DateTimeOffset? GetTaskFinishedAt(OcrQueueTaskId taskId)
    {
        lock (_progressLock)
        {
            return _finishedAt.TryGetValue(taskId, out DateTimeOffset finishedAt) ? finishedAt : null;
        }
    }

    public void ClearFinishedTasks()
    {
        OcrQueueTaskId[] removed;
        _gate.Wait();
        try
        {
            removed = _tasks.Values
                .Where(t => t.State is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
                    or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked)
                .Select(t => t.TaskId)
                .ToArray();
            foreach (OcrQueueTaskId id in removed)
            {
                _tasks.Remove(id);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (removed.Length == 0)
        {
            return;
        }

        lock (_progressLock)
        {
            foreach (OcrQueueTaskId id in removed)
            {
                _progress.Remove(id);
                _finishedAt.Remove(id);
            }
        }

        OnChanged(null, OcrQueueChangeKind.Updated);
    }

    private bool Paused(OcrQueueTask t)
    {
        return _pauses.Contains("global:") || _pauses.Contains("task:" + t.TaskId) ||
               (_pauses.Contains("local:") && t.AdapterKind != OcrAdapterKind.CloudApi) ||
               (_pauses.Contains("cloud:") && t.AdapterKind == OcrAdapterKind.CloudApi) ||
               (!string.IsNullOrEmpty(t.ProviderId) && _pauses.Contains("provider:" + t.ProviderId));
    }

    private bool CanRun(OcrQueueTask t)
    {
        OcrQueueTask[] running = _tasks.Values.Where(x => x.State == OcrQueueTaskState.Running).ToArray();
        if (running.Length >= _limits.GlobalMaxConcurrent)
        {
            return false;
        }

        bool local = t.AdapterKind != OcrAdapterKind.CloudApi;
        if (local && running.Count(x => x.AdapterKind != OcrAdapterKind.CloudApi) >= _limits.LocalMaxConcurrent)
        {
            return false;
        }

        if (!local && running.Count(x => x.AdapterKind == OcrAdapterKind.CloudApi) >= _limits.CloudMaxConcurrent)
        {
            return false;
        }

        if (running.Count(x => x.EngineId == t.EngineId) >= _limits.PerEngineMaxConcurrent)
        {
            return false;
        }

        if (t.ProviderId is not null &&
            running.Count(x => x.ProviderId == t.ProviderId) >= _limits.PerProviderMaxConcurrent)
        {
            return false;
        }

        return true;
    }

    private int Score(OcrQueueTask t)
    {
        return Priority(t.Priority) * 1000 + (int)Math.Min(999, (_clock.UtcNow - t.CreatedAt).TotalMinutes / 10);
    }

    private static int Priority(string p)
    {
        return p switch
        {
            OcrQueuePriority.InteractiveCurrentPage => 6, OcrQueuePriority.InteractiveSelectedPages => 5,
            OcrQueuePriority.UserStartedDocument => 4, OcrQueuePriority.BackgroundRetry => 3,
            OcrQueuePriority.BatchCollection => 2, _ => 1
        };
    }

    private void OnChanged(OcrQueueTask? task, string changeKind)
    {
        Changed?.Invoke(this, new OcrQueueChangedEventArgs(task, changeKind));
    }
}
