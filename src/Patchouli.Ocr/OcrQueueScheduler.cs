using System.Threading.Channels;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Llm;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Ocr;

public sealed class OcrRetryPolicy : IOcrRetryPolicy
{
    private static readonly HashSet<string> Transient =
    [
        "network_timeout", "temporary_provider_error", "rate_limited", "quota_exceeded_retryable", "worker_crashed",
        OcrFailureCode.LocalOcrTimeout, MinerUProviderStatus.DownloadFailed, MinerUProviderStatus.Timeout,
        MinerUProviderStatus.UploadFailed, MinerUProviderStatus.UploadUrlFailed,
        LlmFailureCodes.Cancelled, LlmFailureCodes.Interrupted, LlmFailureCodes.InvalidModelOutput
    ];

    private static readonly HashSet<string> Manual =
    [
        "auth_failed", "model_not_found", "bad_endpoint_config", "model_path_missing", "model_path_inaccessible",
        OcrFailureCode.SourceFileMissing, OcrFailureCode.SourceFileChanged,
        OcrFailureCode.BBoxCoordinateTransformFailed, OcrFailureCode.ImageTooLargeForOcr,
        OcrFailureCode.RendererTimeout, "unsupported_file", "invalid_page_box", "missing_executable",
        LlmFailureCodes.ContextLengthExceeded, LlmFailureCodes.ContentFiltered, LlmFailureCodes.EmptyResponse
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

public sealed class OcrQueueScheduler : IOcrQueueScheduler, IAsyncDisposable
{
    private readonly IClock _clock;
    private readonly IOcrQueueTaskExecutor _executor;
    private readonly IOcrRetryPolicy _retry;
    private OcrQueueLimits _limits;
    private readonly Dictionary<OcrQueueTaskId, OcrQueueTask> _tasks = new();
    private readonly HashSet<string> _pauses = new();
    private readonly Dictionary<OcrQueueTaskId, RunningAttempt> _running = new();
    private readonly Dictionary<OcrQueueTaskId, (int Attempt, OcrRunId RunId)> _attemptBindings = new();
    private readonly Dictionary<OcrQueueTaskId, OcrTaskProgressReport> _progress = new();
    private readonly Dictionary<OcrQueueTaskId, DateTimeOffset> _finishedAt = new();
    private readonly Dictionary<OcrQueueTaskId, IActivityScope> _activityScopes = new();
    private readonly object _activityLock = new();
    private readonly object _progressLock = new();
    private readonly Dictionary<OcrQueueTaskId, DateTimeOffset> _lastProgressNotification = new();
    private readonly object _stateLock = new();

    private readonly Channel<bool> _wakeChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    private readonly Func<CancellationToken, Task<Result<LibraryId>>> _libraryIdResolver;
    private readonly Action<Exception>? _loopErrorLogger;
    private readonly IHostActivityTracker? _activityTracker;
    private readonly OcrEngineLeaseRegistry? _engineLeases;
    private readonly TimeSpan _loopInterval;
    private LibraryId? _libraryId;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private Task? _shutdownTask;
    private int _shutdownRequested;

    /// <summary>One actual worker lifetime, including cancellation callbacks and generation release.</summary>
    private sealed class RunningAttempt
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source;
        private Task? _cancellation;

        public RunningAttempt()
        {
            _source = new CancellationTokenSource();
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestCancellationAsync()
        {
            lock (_gate)
            {
                return _source is null ? Task.CompletedTask : _cancellation ??= _source.CancelAsync();
            }
        }

        public async ValueTask ReleaseCancellationAsync()
        {
            CancellationTokenSource? source;
            Task? cancellation;
            lock (_gate)
            {
                source = _source;
                _source = null;
                cancellation = _cancellation;
            }

            try
            {
                if (cancellation is not null)
                {
                    await cancellation.ConfigureAwait(false);
                }
            }
            finally
            {
                source?.Dispose();
            }
        }
    }

    public OcrQueueScheduler(LibraryId libraryId, IClock clock, IOcrQueueTaskExecutor executor,
        IOcrRetryPolicy? retry = null, OcrQueueLimits? limits = null, TimeSpan? loopInterval = null,
        Action<Exception>? loopErrorLogger = null, IHostActivityTracker? activityTracker = null,
        OcrEngineLeaseRegistry? engineLeases = null)
        : this(_ => Task.FromResult(Result<LibraryId>.Success(libraryId)), clock, executor, retry, limits,
            loopInterval, loopErrorLogger, activityTracker, engineLeases)
    {
    }

    public OcrQueueScheduler(Func<CancellationToken, Task<Result<LibraryId>>> libraryIdResolver, IClock clock,
        IOcrQueueTaskExecutor executor, IOcrRetryPolicy? retry = null, OcrQueueLimits? limits = null,
        TimeSpan? loopInterval = null, Action<Exception>? loopErrorLogger = null,
        IHostActivityTracker? activityTracker = null, OcrEngineLeaseRegistry? engineLeases = null)
    {
        _engineLeases = engineLeases;
        _libraryIdResolver = libraryIdResolver;
        _clock = clock;
        _executor = executor;
        _retry = retry ?? new OcrRetryPolicy();
        _limits = limits ?? OcrQueueLimits.Default;
        _loopInterval = loopInterval ?? TimeSpan.FromMilliseconds(500);
        _loopErrorLogger = loopErrorLogger;
        _activityTracker = activityTracker;
        _clock.Advanced += WakeScheduler;
    }

    private readonly object _lifecycleLock = new();

    public event EventHandler<OcrQueueChangedEventArgs>? Changed;

    public void WakeScheduler()
    {
        _wakeChannel.Writer.TryWrite(true);
    }

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
            new OcrQueueTaskRequest(d, p, pages, OcrQueueTaskKind.Pages, OcrEngineIds.Mock, OcrAdapterKind.Mock,
                null, priority, Source: OcrQueueSource.Test), c);
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
        c.ThrowIfCancellationRequested();
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdownRequested) != 0, this);
            if (_loopTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _loop = CancellationTokenSource.CreateLinkedTokenSource(c);
            CancellationToken loopToken = _loop.Token;
            _loopTask = Task.Run(() => RunSchedulerLoopAsync(loopToken), CancellationToken.None);
            RefreshActivityStates();
            OnChanged(null, OcrQueueChangeKind.Started);
            WakeScheduler();
            return Task.CompletedTask;
        }
    }

    public async Task StopAsync(CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        CancellationTokenSource? loop;
        Task? loopTask;
        lock (_lifecycleLock)
        {
            if (_loop is null)
            {
                return;
            }

            loop = _loop;
            loopTask = _loopTask;
            _loop = null;
            _loopTask = null;
        }

        loop.Cancel();
        WakeScheduler();
        try
        {
            if (loopTask is not null)
            {
                await loopTask.WaitAsync(c).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loop.Dispose();
            ClearActivityScopes();
            // Stop is restartable scheduling control, not host teardown. Queued tasks retain
            // their accepted generations; DisposeAsync owns permanent cancellation/draining.
            OnChanged(null, OcrQueueChangeKind.Stopped);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            Interlocked.Exchange(ref _shutdownRequested, 1);
            return new ValueTask(_shutdownTask ??= ShutdownCoreAsync());
        }
    }

    private async Task ShutdownCoreAsync()
    {
        _clock.Advanced -= WakeScheduler;
        await StopAsync().ConfigureAwait(false);
        RunningAttempt[] running;
        lock (_stateLock)
        {
            running = _running.Values.ToArray();
            foreach (OcrQueueTask queued in _tasks.Values.Where(task =>
                         !_running.ContainsKey(task.TaskId) && task.State is
                             OcrQueueTaskState.Queued or OcrQueueTaskState.Paused).ToArray())
            {
                _tasks[queued.TaskId] = queued with
                {
                    State = OcrQueueTaskState.Cancelled,
                    UpdatedAt = _clock.UtcNow
                };
            }
        }

        try
        {
            await Task.WhenAll(running.Select(attempt => attempt.RequestCancellationAsync()))
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Executor completion includes working-tree import, adoption and indexing, not
                // merely the extension callback. Nothing may outlive the host's services.
                await Task.WhenAll(running.Select(attempt => attempt.Finished.Task)).ConfigureAwait(false);
            }
            finally
            {
                if (_engineLeases is not null)
                {
                    await _engineLeases.DisposeAsync().ConfigureAwait(false);
                }

                ClearActivityScopes();
            }
        }
    }

    private async Task RunSchedulerLoopAsync(CancellationToken loopToken)
    {
        while (!loopToken.IsCancellationRequested)
        {
            while (_wakeChannel.Reader.TryRead(out _))
            {
            }

            try
            {
                await RunOneSchedulingTickAsync(loopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                try
                {
                    _loopErrorLogger?.Invoke(ex);
                }
                catch (Exception reporterException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"OCR scheduler error reporter failed: {reporterException}");
                }
            }

            TimeSpan? delayToWait = null;
            lock (_stateLock)
            {
                DateTimeOffset now = _clock.UtcNow;
                DateTimeOffset? nearest = null;

                foreach (OcrQueueTask task in _tasks.Values)
                {
                    if (task.State == OcrQueueTaskState.Queued && !Paused(task))
                    {
                        if (task.ScheduledAfter.HasValue && task.ScheduledAfter.Value > now)
                        {
                            if (nearest == null || task.ScheduledAfter.Value < nearest.Value)
                            {
                                nearest = task.ScheduledAfter.Value;
                            }
                        }
                    }
                }

                if (nearest.HasValue)
                {
                    TimeSpan diff = nearest.Value - now;
                    delayToWait = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
                }
            }

            if (delayToWait == TimeSpan.Zero)
            {
                continue;
            }

            try
            {
                if (delayToWait.HasValue)
                {
                    using CancellationTokenSource timeoutCts =
                        CancellationTokenSource.CreateLinkedTokenSource(loopToken);
                    timeoutCts.CancelAfter(delayToWait.Value);
                    try
                    {
                        await _wakeChannel.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!loopToken.IsCancellationRequested)
                    {
                        // Nearest retry deadline reached
                    }
                }
                else
                {
                    await _wakeChannel.Reader.WaitToReadAsync(loopToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<Result<OcrQueueTask>> EnqueueAsync(OcrQueueTaskRequest request, CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState, "The OCR queue is shutting down.");
        }

        // Snapshot the accepted page selection before any validation or await: the caller owns
        // request.PageIds and may mutate it while library-id resolution is in flight. The task
        // exposes a read-only view so queued work can never observe caller-side edits.
        IReadOnlyList<PageId> acceptedPages =
            Array.AsReadOnly(request.PageIds?.ToArray() ?? []);
        if (acceptedPages.Count == 0 || string.IsNullOrWhiteSpace(request.TaskKind) ||
            string.IsNullOrWhiteSpace(request.EngineId) || string.IsNullOrWhiteSpace(request.AdapterKind))
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.ValidationFailed,
                "OCR queue task requires pages, kind, engine, and adapter kind.");
        }

        Result<LibraryId>? resolvedLibraryId = null;
        if (_libraryId is null)
        {
            resolvedLibraryId = await _libraryIdResolver(c).ConfigureAwait(false);
            if (resolvedLibraryId.IsFailure)
            {
                return Result<OcrQueueTask>.Failure(resolvedLibraryId.ErrorCode!, resolvedLibraryId.ErrorMessage!);
            }
        }

        OcrQueueTask task;
        lock (_stateLock)
        {
            _libraryId ??= resolvedLibraryId!.Value;
            DateTimeOffset now = _clock.UtcNow;
            task = new OcrQueueTask(OcrQueueTaskId.New(), _libraryId.Value, request.DocumentInstanceId,
                request.PresetId,
                acceptedPages, request.TaskKind, request.EngineId, request.AdapterKind, request.ProviderId,
                request.Priority, now, now, OcrQueueTaskState.Queued, 0, Math.Max(1, request.MaxAttempts), null, null,
                null, null, RegionBBox: request.RegionBBox,
                CommitOnCompletion: request.CommitOnCompletion, PresetVersionId: request.PresetVersionId,
                Source: request.Source);
        }

        // Acceptance pins the generation the task will run: the pinned image engine and the
        // selected text provider are frozen here, before the task becomes visible, so a reload
        // or config change while it waits cannot change what this task executes. A failed pin
        // fails acceptance — there is no fallback to whatever is current later — but the
        // rejection still leaves a durable failed row so the request does not vanish silently.
        if (_engineLeases is not null)
        {
            Result<IOcrEngineLease?> pinned;
            try
            {
                pinned = await _engineLeases.AcquireAsync(task, c).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (c.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                  "ocr.scheduler-acceptance"))
            {
                // Extension activation failures surface as thrown exceptions (e.g. a failed
                // trusted-source check), not Results. Never surface the raw message: it can
                // carry local paths or configuration material; the stable code and the
                // recorded row carry the diagnosis.
                pinned = Result<IOcrEngineLease?>.Failure("engine_generation_pin_failed",
                    "The OCR engine generation could not be prepared.");
            }

            if (pinned.IsFailure)
            {
                RecordRejectedTask(task, pinned.ErrorCode!, pinned.ErrorMessage!);
                return Result<OcrQueueTask>.Failure(pinned.ErrorCode!, pinned.ErrorMessage!);
            }

            if (pinned.Value is { } lease)
            {
                task = task with
                {
                    AdapterKind = lease.Definition.AdapterKind,
                    ProviderId = lease.ProviderId ?? task.ProviderId
                };
            }
        }

        bool accepted;
        lock (_stateLock)
        {
            accepted = Volatile.Read(ref _shutdownRequested) == 0 && !c.IsCancellationRequested;
            if (accepted)
            {
                _tasks[task.TaskId] = task;
            }
        }

        if (!accepted)
        {
            if (_engineLeases is not null)
            {
                await _engineLeases.ReleaseAsync(task.TaskId).ConfigureAwait(false);
            }

            c.ThrowIfCancellationRequested();
            return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState, "The OCR queue is shutting down.");
        }

        RefreshActivityStates();
        WakeScheduler();
        OnChanged(task, OcrQueueChangeKind.Enqueued);
        return Result<OcrQueueTask>.Success(task);
    }

    /// <summary>
    ///     A request rejected at acceptance (generation pin failure) still leaves a durable
    ///     failed row: the caller's transient status message disappears, but the queue must
    ///     keep why nothing ran. The row is terminal — retrying goes through the normal
    ///     failed-task retry path and re-pins a generation.
    /// </summary>
    private void RecordRejectedTask(OcrQueueTask task, string errorCode, string errorMessage)
    {
        OcrQueueTask rejected = task with
        {
            State = OcrQueueTaskState.Failed,
            LastErrorCode = errorCode,
            LastErrorMessage = errorMessage,
            UpdatedAt = _clock.UtcNow
        };
        lock (_stateLock)
        {
            _tasks[rejected.TaskId] = rejected;
            _finishedAt[rejected.TaskId] = rejected.UpdatedAt;
        }

        RefreshActivityStates();
        OnChanged(rejected, OcrQueueChangeKind.Enqueued);
    }

    public Task RunOneSchedulingTickAsync(CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        List<(OcrQueueTask Task, RunningAttempt Attempt)> claimed = [];
        lock (_stateLock)
        {
            if (Volatile.Read(ref _shutdownRequested) != 0)
            {
                return Task.CompletedTask;
            }

            OcrQueueTask[] eligible = _tasks.Values
                .Where(t => t.State == OcrQueueTaskState.Queued &&
                            !_running.ContainsKey(t.TaskId) &&
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
                    State = OcrQueueTaskState.Running,
                    UpdatedAt = _clock.UtcNow,
                    RunId = candidate.RunId,
                    CompletedPageCount = candidate.CompletedPageCount,
                    FailedPageCount = 0
                };
                _tasks[task.TaskId] = task;
                lock (_progressLock)
                {
                    // A fresh claim owns its progress stream: drop the previous attempt's last
                    // report so readers cannot see stale progress before this attempt reports.
                    _progress.Remove(task.TaskId);
                    _lastProgressNotification.Remove(task.TaskId);
                }

                RunningAttempt attempt = new();
                _running[task.TaskId] = attempt;
                claimed.Add((task, attempt));
            }
        }

        RefreshActivityStates();
        foreach ((OcrQueueTask task, RunningAttempt attempt) in claimed)
        {
            OnChanged(task, OcrQueueChangeKind.Updated);
            _ = Task.Run(() => ExecuteAndCompleteAsync(task, attempt), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task WaitForIdleAsync(CancellationToken c = default)
    {
        while (true)
        {
            c.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                DateTimeOffset now = _clock.UtcNow;
                bool idle = _running.Count == 0 && _tasks.Values.All(t =>
                    t.State != OcrQueueTaskState.Running &&
                    (t.State != OcrQueueTaskState.Queued ||
                     (t.ScheduledAfter is not null && t.ScheduledAfter > now) ||
                     Paused(t)));
                if (idle)
                {
                    return;
                }
            }

            await Task.Delay(20, c).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAndCompleteAsync(OcrQueueTask task, RunningAttempt attempt)
    {
        try
        {
            await ExecuteAttemptCoreAsync(task, attempt.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "ocr.scheduler-worker-completion"))
        {
            lock (_stateLock)
            {
                if (_tasks.TryGetValue(task.TaskId, out OcrQueueTask? live) &&
                    live.State == OcrQueueTaskState.Running && live.AttemptCount == task.AttemptCount)
                {
                    _tasks[task.TaskId] = live with
                    {
                        State = OcrQueueTaskState.Failed,
                        LastErrorCode = "worker_cleanup_failed",
                        LastErrorMessage = "The OCR worker could not finish releasing its resources.",
                        UpdatedAt = _clock.UtcNow
                    };
                }
            }
        }
        finally
        {
            try
            {
                await attempt.ReleaseCancellationAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                  "ocr.scheduler-cancellation-callback"))
            {
                // Callback failures are observed without releasing the worker's drain early.
                System.Diagnostics.Debug.WriteLine($"OCR cancellation callback failed: {exception.GetType().Name}");
            }
            finally
            {
                lock (_stateLock)
                {
                    if (_running.TryGetValue(task.TaskId, out RunningAttempt? current) &&
                        ReferenceEquals(current, attempt))
                    {
                        _running.Remove(task.TaskId);
                    }
                }

                attempt.Finished.TrySetResult();
                WakeScheduler();
            }
        }
    }

    private async Task ExecuteAttemptCoreAsync(OcrQueueTask task, CancellationToken cancellationToken)
    {
        // Typed per-attempt context: the created run binds to this task id and attempt
        // (AttemptCount at claim time) so callbacks from superseded invocations are rejected.
        OcrRunExecutionContext execution = new(task.TaskId, task.AttemptCount,
            (runId, versionId) => BindAttemptRun(task.TaskId, task.AttemptCount, runId, versionId));
        OcrQueueExecutionResult result;
        try
        {
            result = await _executor.ExecuteAsync(task, execution, cancellationToken,
                    new TaskProgressSink(this, task.TaskId, task.AttemptCount))
                .ConfigureAwait(false);
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
            catch (Exception reporterException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"OCR worker error reporter failed: {reporterException}");
            }

            // Never surface the raw exception message here: it can contain local paths or
            // credential material. The stable code carries the diagnosis for public projections.
            result = new OcrQueueExecutionResult(false, false, "worker_crashed",
                "The OCR worker stopped unexpectedly.");
        }

        OcrQueueTask? updated = null;
        lock (_stateLock)
        {
            OcrRunId? boundRunId = _attemptBindings.TryGetValue(task.TaskId,
                out (int Attempt, OcrRunId RunId) binding) && binding.Attempt == task.AttemptCount
                ? binding.RunId
                : null;
            _attemptBindings.Remove(task.TaskId);

            // Finalization must still belong to the live claimed attempt: a task removed or
            // superseded while the worker unwound must not be resurrected or overwritten.
            if (_tasks.TryGetValue(task.TaskId, out OcrQueueTask? live) &&
                live.State == OcrQueueTaskState.Running && live.AttemptCount == task.AttemptCount)
            {
                DateTimeOffset now = _clock.UtcNow;
                // The run bound through the execution context (or explicitly retained on the
                // claim) owns this attempt; a worker result naming a different run is a
                // contradiction that fails the attempt, not a change of ownership.
                OcrRunId? effectiveRunId = boundRunId ?? live.RunId;
                if (effectiveRunId is { } owned && result.RunId is { } reportedRunId &&
                    reportedRunId != owned)
                {
                    result = new OcrQueueExecutionResult(false, false, "run_identity_mismatch",
                        $"The OCR worker reported run {reportedRunId} but this attempt is bound to run {owned}.",
                        owned, result.CompletedPageCount, result.FailedPageCount);
                }
                else
                {
                    effectiveRunId ??= result.RunId;
                }

                if (result.Cancelled)
                {
                    updated = live with
                    {
                        State = OcrQueueTaskState.Cancelled,
                        RunId = effectiveRunId,
                        CompletedPageCount = result.CompletedPageCount > 0
                            ? result.CompletedPageCount
                            : task.CompletedPageCount,
                        FailedPageCount = result.FailedPageCount,
                        UpdatedAt = now
                    };
                }
                else if (result.Succeeded)
                {
                    updated = live with
                    {
                        State = OcrQueueTaskState.Succeeded,
                        RunId = effectiveRunId,
                        CompletedPageCount = result.CompletedPageCount > 0
                            ? result.CompletedPageCount
                            : task.CompletedPageCount,
                        FailedPageCount = result.FailedPageCount,
                        UpdatedAt = now
                    };
                }
                else if (_retry.ShouldRetry(task, result.ErrorCode))
                {
                    int completed = result.CompletedPageCount > 0
                        ? result.CompletedPageCount
                        : task.CompletedPageCount;
                    bool retainRecognition = effectiveRunId is not null && result.FailedPageCount == 0 &&
                                             completed >= task.PageIds.Count;
                    updated = live with
                    {
                        State = OcrQueueTaskState.Queued,
                        AttemptCount = task.AttemptCount + 1,
                        ScheduledAfter = now + _retry.GetNextDelay(task.AttemptCount + 1),
                        // Recognition retries bind a new run. Post-recognition adoption/index
                        // retries retain completed recognition, which the executor revalidates.
                        RunId = retainRecognition ? effectiveRunId : null,
                        CompletedPageCount = retainRecognition ? completed : 0,
                        FailedPageCount = 0,
                        LastErrorCode = result.ErrorCode,
                        LastErrorMessage = result.ErrorMessage,
                        UpdatedAt = now
                    };
                    lock (_progressLock)
                    {
                        _progress.Remove(task.TaskId);
                        _lastProgressNotification.Remove(task.TaskId);
                    }
                }
                else
                {
                    updated = live with
                    {
                        State = _retry.Classify(result.ErrorCode) == OcrRetryClassification.ManualRepairRequired
                            ? OcrQueueTaskState.Blocked
                            : OcrQueueTaskState.Failed,
                        RunId = effectiveRunId,
                        CompletedPageCount = result.CompletedPageCount > 0
                            ? result.CompletedPageCount
                            : task.CompletedPageCount,
                        FailedPageCount = result.FailedPageCount,
                        LastErrorCode = result.ErrorCode,
                        LastErrorMessage = result.ErrorMessage,
                        UpdatedAt = now
                    };
                }

                _tasks[task.TaskId] = updated;
            }
        }

        // Terminal (or dropped) task: the acceptance-time generation pin ends here. A requeued
        // attempt of the same task keeps its pin — retries re-run the same selection.
        if (_engineLeases is not null &&
            (updated is null || updated.State is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
                or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked))
        {
            await _engineLeases.ReleaseAsync(task.TaskId);
        }

        if (updated is null)
        {
            RefreshActivityStates();
            WakeScheduler();
            return;
        }

        if (updated.State is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
            or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked)
        {
            lock (_progressLock)
            {
                _finishedAt[task.TaskId] = updated.UpdatedAt;
                _lastProgressNotification.Remove(task.TaskId);
            }
        }

        RefreshActivityStates();
        WakeScheduler();
        OnChanged(updated, OcrQueueChangeKind.Updated);
    }

    /// <summary>
    /// Typed run-binding callback used by <see cref="OcrRunExecutionContext"/>. It binds a newly
    /// created run to the live task only while that exact attempt is still running: a repeated
    /// identical binding is idempotent, a different run for the same attempt conflicts, and a
    /// report from a superseded or removed attempt is rejected so it cannot mutate the task that
    /// replaced it. State is mutated under <see cref="_stateLock"/> but the change notification is
    /// raised after the lock is released.
    /// </summary>
    private Result BindAttemptRun(OcrQueueTaskId taskId, int attempt, OcrRunId runId,
        OcrPresetVersionId? presetVersionId)
    {
        OcrQueueTask updated;
        lock (_stateLock)
        {
            if (!_tasks.TryGetValue(taskId, out OcrQueueTask? task))
            {
                return Result.Failure(AppErrorCodes.InvalidState,
                    "The OCR queue task was removed before its run could be bound.");
            }

            if (task.State != OcrQueueTaskState.Running || task.AttemptCount != attempt)
            {
                return Result.Failure(AppErrorCodes.InvalidState,
                    "The OCR run belongs to a superseded queue attempt.");
            }

            if (task.PresetVersionId is { } expectedVersion && presetVersionId is { } actualVersion &&
                expectedVersion != actualVersion)
            {
                return Result.Failure(AppErrorCodes.Conflict,
                    "The OCR run does not use this task's fixed preset version.");
            }

            if (task.RunId == runId && (task.PresetVersionId is not null || presetVersionId is null))
            {
                // Idempotent repeat (including a retained run re-reporting itself): still record
                // the binding so a later report naming a different run conflicts rather than
                // silently replacing the ownership this attempt already holds.
                _attemptBindings[taskId] = (attempt, runId);
                return Result.Success();
            }

            if (task.RunId is not null && task.RunId != runId)
            {
                return Result.Failure(AppErrorCodes.Conflict,
                    "A different OCR run is already bound to this queue attempt.");
            }

            _attemptBindings[taskId] = (attempt, runId);
            updated = task with
            {
                RunId = runId,
                // Legacy unpinned tasks learn the actual durable version here. A later retry
                // may pin that same version without discarding already completed recognition.
                PresetVersionId = task.PresetVersionId ?? presetVersionId,
                UpdatedAt = _clock.UtcNow
            };
            _tasks[taskId] = updated;
        }

        OnChanged(updated, OcrQueueChangeKind.Updated);
        return Result.Success();
    }

    private void OnProgress(OcrTaskProgressReport report)
    {
        // Validation and storage run under one state→progress lock scope so a requeue or clear
        // cannot interleave between them. Reports are attempt-owned: unknown, removed,
        // non-running, or foreign-attempt reports are dropped entirely, and the change
        // notification is published only after both locks are released.
        bool isTerminal = report.Fraction is not null && report.Fraction.Value >= 1.0;
        lock (_stateLock)
        {
            if (!_tasks.TryGetValue(report.TaskId, out OcrQueueTask? current) ||
                current.State != OcrQueueTaskState.Running ||
                report.Attempt != current.AttemptCount)
            {
                return;
            }

            lock (_progressLock)
            {
                _progress[report.TaskId] = report;
                DateTimeOffset now = _clock.UtcNow;
                if (!isTerminal &&
                    _lastProgressNotification.TryGetValue(report.TaskId, out DateTimeOffset last) &&
                    (now - last).TotalMilliseconds < 200)
                {
                    return;
                }

                _lastProgressNotification[report.TaskId] = now;
            }
        }

        OnChanged(null, OcrQueueChangeKind.Progress);
    }

    /// <summary>
    ///     Progress sink bound to one claimed task attempt. Reports stamped with a foreign task id
    ///     or attempt are rejected outright; reports omitting an attempt stamp (legacy callers)
    ///     are stamped with the bound attempt before the scheduler revalidates them.
    /// </summary>
    private sealed class TaskProgressSink(
        OcrQueueScheduler scheduler,
        OcrQueueTaskId taskId,
        int attempt)
        : IProgress<OcrTaskProgressReport>
    {
        public void Report(OcrTaskProgressReport value)
        {
            if (value.TaskId != taskId || (value.Attempt is { } stamped && stamped != attempt))
            {
                return;
            }

            scheduler.OnProgress(new OcrTaskProgressReport(taskId, value.Stage, value.Fraction,
                value.Detail, attempt));
        }
    }

    public Task<Result> PauseAsync(string scope, string? target = null, CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        if (scope == "preset")
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Preset-level pause is not supported."));
        }

        lock (_stateLock)
        {
            _pauses.Add(scope + ":" + (target ?? ""));
        }

        RefreshActivityStates();
        WakeScheduler();
        OnChanged(null, OcrQueueChangeKind.Updated);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> ResumeAsync(string scope, string? target = null, CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            _pauses.Remove(scope + ":" + (target ?? ""));
        }

        RefreshActivityStates();
        WakeScheduler();
        OnChanged(null, OcrQueueChangeKind.Updated);
        return Task.FromResult(Result.Success());
    }

    public async Task<Result> CancelTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        OcrQueueTask? updated = null;
        RunningAttempt? toCancel = null;
        lock (_stateLock)
        {
            if (!_tasks.TryGetValue(id, out OcrQueueTask? task))
            {
                return Result.Failure(AppErrorCodes.NotFound, "Queue task was not found.");
            }

            if (task.State is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
                or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked)
            {
                return Result.Failure(AppErrorCodes.InvalidState,
                    "Terminal OCR tasks cannot be cancelled.");
            }

            if (_running.TryGetValue(id, out toCancel))
            {
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

        if (toCancel is not null)
        {
            await toCancel.RequestCancellationAsync().ConfigureAwait(false);
        }

        // A queued task cancelled before a claim never executes: release the pin immediately.
        // A running task releases in ExecuteAndCompleteAsync after its worker drains.
        if (updated is not null && _engineLeases is not null)
        {
            await _engineLeases.ReleaseAsync(id);
        }

        RefreshActivityStates();
        WakeScheduler();
        if (updated is not null)
        {
            OnChanged(updated, OcrQueueChangeKind.Updated);
        }

        return Result.Success();
    }

    public Task<Result<OcrQueueTask>> RetryTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
    {
        return RetryTaskCoreAsync(id, null, c);
    }

    public Task<Result<OcrQueueTask>> RetryTaskWithVersionAsync(OcrQueueTaskId id,
        OcrPresetVersionId presetVersionId, CancellationToken c = default)
    {
        return RetryTaskCoreAsync(id, presetVersionId, c);
    }

    private async Task<Result<OcrQueueTask>> RetryTaskCoreAsync(OcrQueueTaskId id,
        OcrPresetVersionId? presetVersionId, CancellationToken c)
    {
        c.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState, "The OCR queue is shutting down.");
        }

        OcrQueueTask retry;
        lock (_stateLock)
        {
            if (!_tasks.TryGetValue(id, out OcrQueueTask? task))
            {
                return Result<OcrQueueTask>.Failure(AppErrorCodes.NotFound, "Queue task was not found.");
            }

            if (task.State is not (OcrQueueTaskState.Failed or OcrQueueTaskState.Blocked
                or OcrQueueTaskState.Cancelled))
            {
                return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState,
                    "Only failed, blocked, or cancelled OCR tasks can be retried.");
            }

            DateTimeOffset now = _clock.UtcNow;
            // A task reaches the Cancelled state only after its worker has quiesced (queued tasks
            // flip immediately; running tasks flip in ExecuteAndCompleteAsync under _stateLock),
            // so retrying here cannot race the old worker. A completed run is retained only when
            // the retry keeps the same preset version (an explicit different version must
            // re-recognize) and the bound run already covered every requested page — the executor
            // revalidates coverage against the actual page results.
            bool versionMatches = presetVersionId is null || presetVersionId == task.PresetVersionId;
            bool preserveRunId = task.RunId is not null && versionMatches &&
                                 task.FailedPageCount == 0 &&
                                 task.CompletedPageCount >= task.PageIds.Count;
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
                RunId = preserveRunId ? task.RunId : null,
                CompletedPageCount = preserveRunId ? task.CompletedPageCount : 0,
                FailedPageCount = 0,
                PresetVersionId = presetVersionId ?? task.PresetVersionId,
                Source = OcrQueueSource.Retry
            };
        }

        // Recognition retries are new acceptances and pin a generation before becoming visible.
        // Adoption/index-only retries reuse durable recognition, requiring no source generation,
        // weights or credentials that might no longer be available.
        if (_engineLeases is not null && retry.RunId is null)
        {
            Result<IOcrEngineLease?> pinned;
            try
            {
                pinned = await _engineLeases.AcquireAsync(retry, c).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (c.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                  "ocr.scheduler-acceptance"))
            {
                pinned = Result<IOcrEngineLease?>.Failure("engine_generation_pin_failed",
                    "The OCR engine generation could not be prepared.");
            }

            if (pinned.IsFailure)
            {
                RecordRejectedTask(retry, pinned.ErrorCode!, pinned.ErrorMessage!);
                return Result<OcrQueueTask>.Failure(pinned.ErrorCode!, pinned.ErrorMessage!);
            }

            if (pinned.Value is { } lease)
            {
                retry = retry with
                {
                    AdapterKind = lease.Definition.AdapterKind,
                    ProviderId = lease.ProviderId ?? retry.ProviderId
                };
            }
        }

        bool accepted;
        lock (_stateLock)
        {
            accepted = Volatile.Read(ref _shutdownRequested) == 0 && !c.IsCancellationRequested;
            if (accepted)
            {
                _tasks[retry.TaskId] = retry;
            }
        }

        if (!accepted)
        {
            if (_engineLeases is not null)
            {
                await _engineLeases.ReleaseAsync(retry.TaskId).ConfigureAwait(false);
            }

            c.ThrowIfCancellationRequested();
            return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState, "The OCR queue is shutting down.");
        }

        RefreshActivityStates();
        WakeScheduler();
        OnChanged(retry, OcrQueueChangeKind.Enqueued);
        return Result<OcrQueueTask>.Success(retry);
    }

    public Task<Result<OcrQueueTask>> GetTaskAsync(OcrQueueTaskId id, CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            return Task.FromResult(_tasks.TryGetValue(id, out OcrQueueTask? task)
                ? Result<OcrQueueTask>.Success(task)
                : Result<OcrQueueTask>.Failure(AppErrorCodes.NotFound, "Queue task was not found."));
        }
    }

    public Task<Result<IReadOnlyList<OcrQueueTask>>> ListTasksAsync(OcrQueueTaskFilter f,
        CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            OcrQueueTask[] tasks = _tasks.Values.Where(t =>
                    (f.State is null || t.State == f.State) && (f.EngineId is null || t.EngineId == f.EngineId) &&
                    (f.ProviderId is null || t.ProviderId == f.ProviderId) &&
                    (f.IncludeCompleted ||
                     t.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running or OcrQueueTaskState.Paused))
                .OrderByDescending(Score)
                .ToArray();
            return Task.FromResult(Result<IReadOnlyList<OcrQueueTask>>.Success(tasks));
        }
    }

    public Task<Result<OcrQueueStatus>> GetQueueStatusAsync(CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        lock (_stateLock)
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
            return Task.FromResult(Result<OcrQueueStatus>.Success(status));
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
        lock (_stateLock)
        {
            removed = _tasks.Values
                .Where(t => t.State is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
                    or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked)
                .Select(t => t.TaskId)
                .ToArray();
            foreach (OcrQueueTaskId id in removed)
            {
                _tasks.Remove(id);
                _attemptBindings.Remove(id);
            }
        }

        if (removed.Length == 0)
        {
            return;
        }

        // Removal is a release point for any pin still held (terminal tasks normally released
        // already; this covers dropped or stale entries deterministically).
        if (_engineLeases is not null)
        {
            foreach (OcrQueueTaskId id in removed)
            {
                _ = ReleaseLeaseQuietAsync(id);
            }
        }

        lock (_progressLock)
        {
            foreach (OcrQueueTaskId id in removed)
            {
                _progress.Remove(id);
                _finishedAt.Remove(id);
                _lastProgressNotification.Remove(id);
            }
        }

        RefreshActivityStates();
        WakeScheduler();
        OnChanged(null, OcrQueueChangeKind.Updated);
    }

    public void UpdateLimits(OcrQueueLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        lock (_stateLock)
        {
            _limits = limits;
        }

        WakeScheduler();
        OnChanged(null, OcrQueueChangeKind.Updated);
    }

    public void SetLimits(OcrQueueLimits limits)
    {
        UpdateLimits(limits);
    }

    /// <summary>
    /// Fire-and-forget release for synchronous removal paths. Lease disposal faults are logged
    /// through the loop-error logger instead of surfacing as unobserved task exceptions.
    /// </summary>
    private async Task ReleaseLeaseQuietAsync(OcrQueueTaskId id)
    {
        try
        {
            await _engineLeases!.ReleaseAsync(id);
        }
        catch (Exception ex)
        {
            _loopErrorLogger?.Invoke(ex);
        }
    }

    private bool Paused(OcrQueueTask t)
    {
        return _pauses.Contains("global:") || _pauses.Contains("task:" + t.TaskId) ||
               (_pauses.Contains("local:") && t.AdapterKind != OcrAdapterKind.CloudApi) ||
               (_pauses.Contains("cloud:") && t.AdapterKind == OcrAdapterKind.CloudApi) ||
               (!string.IsNullOrEmpty(t.ProviderId) && _pauses.Contains("provider:" + t.ProviderId));
    }

    private void RefreshActivityStates()
    {
        if (_activityTracker is null)
        {
            return;
        }

        TaskActivityState[] desired;
        lock (_stateLock)
        {
            DateTimeOffset now = _clock.UtcNow;
            HashSet<OcrQueueTaskId> retriedTaskIds = _tasks.Values
                .Where(task => task.RetryOfTaskId is not null &&
                               task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running)
                .Select(task => task.RetryOfTaskId!.Value)
                .ToHashSet();
            desired = _tasks.Values
                .Where(task => task.State is OcrQueueTaskState.Queued or OcrQueueTaskState.Running ||
                               (task.State == OcrQueueTaskState.Blocked && !retriedTaskIds.Contains(task.TaskId)))
                .Select(task => CreateActivityState(task, now))
                .ToArray();
        }

        lock (_activityLock)
        {
            HashSet<OcrQueueTaskId> desiredIds = desired.Select(state => state.TaskId).ToHashSet();
            foreach (OcrQueueTaskId obsoleteId in _activityScopes.Keys.Where(id => !desiredIds.Contains(id)).ToArray())
            {
                _activityScopes.Remove(obsoleteId, out IActivityScope? obsoleteScope);
                obsoleteScope?.Dispose();
            }

            foreach (TaskActivityState state in desired)
            {
                if (!_activityScopes.TryGetValue(state.TaskId, out IActivityScope? scope))
                {
                    scope = _activityTracker.BeginScope(
                        "OCR 队列",
                        HostActivityKind.Ocr,
                        state.Detail,
                        $"ocr:{state.TaskId}");
                    _activityScopes[state.TaskId] = scope;
                }

                scope.UpdateDetail(state.Detail);
                scope.SetPaused(state.IsPaused, state.PauseReason);
                scope.SetWaitingRetry(state.IsWaitingRetry, state.RetryReason);
            }
        }
    }

    private TaskActivityState CreateActivityState(OcrQueueTask task, DateTimeOffset now)
    {
        if (task.State == OcrQueueTaskState.Blocked)
        {
            string reason = string.IsNullOrWhiteSpace(task.LastErrorMessage)
                ? "需要人工处理后重试"
                : $"需要人工处理：{task.LastErrorMessage}";
            return new TaskActivityState(task.TaskId, "等待人工处理", true, reason, false, null);
        }

        bool isPaused = task.State == OcrQueueTaskState.Queued && Paused(task);
        bool isWaitingRetry = task.State == OcrQueueTaskState.Queued && !isPaused &&
                              task.ScheduledAfter is { } scheduledAfter && scheduledAfter > now;
        string detail = task.State == OcrQueueTaskState.Running
            ? "正在执行"
            : isWaitingRetry
                ? "等待延迟重试"
                : isPaused
                    ? "已暂停"
                    : "等待调度";
        string? retryReason = isWaitingRetry
            ? $"将在 {task.ScheduledAfter!.Value:HH:mm:ss} 重试"
            : null;
        return new TaskActivityState(task.TaskId, detail, isPaused, isPaused ? "OCR 队列已暂停" : null,
            isWaitingRetry, retryReason);
    }

    private void ClearActivityScopes()
    {
        lock (_activityLock)
        {
            foreach (IActivityScope scope in _activityScopes.Values)
            {
                scope.Dispose();
            }

            _activityScopes.Clear();
        }
    }

    private sealed record TaskActivityState(
        OcrQueueTaskId TaskId,
        string Detail,
        bool IsPaused,
        string? PauseReason,
        bool IsWaitingRetry,
        string? RetryReason);

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
