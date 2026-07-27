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
                priority, RegionBBox: regionBBox, AdoptOnCompletion: false), c);
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

        OcrQueueTask task;
        await _gate.WaitAsync(c);
        try
        {
            if (_libraryId is null)
            {
                Result<LibraryId> libraryId = await _libraryIdResolver(c);
                if (libraryId.IsFailure)
                {
                    return Result<OcrQueueTask>.Failure(libraryId.ErrorCode!, libraryId.ErrorMessage!);
                }

                _libraryId = libraryId.Value;
            }

            DateTimeOffset now = _clock.UtcNow;
            task = new OcrQueueTask(OcrQueueTaskId.New(), _libraryId.Value, request.DocumentInstanceId,
                request.PresetId,
                request.PageIds, request.TaskKind, request.EngineId, request.AdapterKind, request.ProviderId,
                request.Priority, now, now, OcrQueueTaskState.Queued, 0, Math.Max(1, request.MaxAttempts), null, null,
                null, null, request.ImagePath, request.Dpi, RegionBBox: request.RegionBBox,
                AdoptOnCompletion: request.AdoptOnCompletion);
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
            result = await _executor.ExecuteAsync(task, cts.Token);
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

        OnChanged(updated, OcrQueueChangeKind.Updated);
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

            if (_running.TryGetValue(id, out CancellationTokenSource? cts))
            {
                cts.Cancel();
            }
            else
            {
                updated = task with { State = OcrQueueTaskState.Cancelled, UpdatedAt = _clock.UtcNow };
                _tasks[id] = updated;
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

    public Task<Result<OcrQueueTask>> GetTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
    {
        return Task.FromResult(_tasks.TryGetValue(id, out OcrQueueTask? t)
            ? Result<OcrQueueTask>.Success(t)
            : Result<OcrQueueTask>.Failure(AppErrorCodes.NotFound, "Queue task was not found."));
    }

    public Task<Result<IReadOnlyList<OcrQueueTask>>> ListTasksAsync(OcrQueueTaskFilter f, CancellationToken c = default)
    {
        return Task.FromResult(Result<IReadOnlyList<OcrQueueTask>>.Success(_tasks.Values.Where(t =>
                (f.State is null || t.State == f.State) && (f.EngineId is null || t.EngineId == f.EngineId) &&
                (f.ProviderId is null || t.ProviderId == f.ProviderId) &&
                (f.IncludeCompleted ||
                 t.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused))
            .OrderByDescending(Score).ToArray()));
    }

    public Task<Result<OcrQueueStatus>> GetQueueStatusAsync(CancellationToken c = default)
    {
        int Count(string state)
        {
            return _tasks.Values.Count(t => t.State == state);
        }

        OcrQueueTask[] running = _tasks.Values.Where(t => t.State == OcrQueueTaskState.Running).ToArray();
        return Task.FromResult(Result<OcrQueueStatus>.Success(new OcrQueueStatus(_loopTask is { IsCompleted: false },
            Count(OcrQueueTaskState.Queued), Count(OcrQueueTaskState.Running), Count(OcrQueueTaskState.Succeeded),
            Count(OcrQueueTaskState.Failed), Count(OcrQueueTaskState.Cancelled), Count(OcrQueueTaskState.Blocked),
            _pauses.ToArray(), _limits, running.GroupBy(t => t.EngineId).ToDictionary(g => g.Key, g => g.Count()),
            running.Where(t => t.ProviderId is not null).GroupBy(t => t.ProviderId!)
                .ToDictionary(g => g.Key, g => g.Count()))));
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
