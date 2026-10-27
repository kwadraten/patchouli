using System.Threading.Channels;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;

namespace Patchouli.Ocr;

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

    public Task<Result<OcrQueueTask>> EnqueueTestPagesAsync(DocumentInstanceId d, OcrPresetId p,
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
                         OcrQueueLifecyclePolicy.IsShutdownCancellable(task,
                             _running.ContainsKey(task.TaskId))).ToArray())
            {
                _tasks[queued.TaskId] = OcrQueueLifecyclePolicy.CancelWaiting(queued, _clock.UtcNow);
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

            TimeSpan? delayToWait;
            lock (_stateLock)
            {
                delayToWait = OcrQueueDispatchPolicy.NextDeferredWakeDelay(_tasks.Values, _clock.UtcNow, _pauses);
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
        if (!OcrQueueLifecyclePolicy.IsAcceptableRequest(request, acceptedPages.Count))
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
            task = OcrQueueLifecyclePolicy.CreateAccepted(request, OcrQueueTaskId.New(), _libraryId.Value,
                acceptedPages, _clock.UtcNow);
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

    private void RecordRejectedTask(OcrQueueTask task, string errorCode, string errorMessage)
    {
        OcrQueueTask rejected =
            OcrQueueLifecyclePolicy.RejectAtAcceptance(task, errorCode, errorMessage, _clock.UtcNow);
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

            OcrQueueTask[] eligible = OcrQueueDispatchPolicy.SelectDispatchCandidates(
                _tasks.Values, _running.Keys, _clock.UtcNow, _pauses);
            foreach (OcrQueueTask candidate in eligible)
            {
                if (!OcrQueueDispatchPolicy.CanRunNow(candidate, _tasks.Values, _limits))
                {
                    continue;
                }

                OcrQueueTask task = OcrQueueLifecyclePolicy.Claim(candidate, _clock.UtcNow);
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
                if (OcrQueueDispatchPolicy.IsIdle(_tasks.Values, _running.Count, _clock.UtcNow, _pauses))
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
                    OcrQueueLifecyclePolicy.IsLiveClaim(live, task.AttemptCount))
                {
                    _tasks[task.TaskId] = OcrQueueLifecyclePolicy.FailWorkerCleanup(live, _clock.UtcNow);
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

            if (_tasks.TryGetValue(task.TaskId, out OcrQueueTask? live) &&
                OcrQueueLifecyclePolicy.IsLiveClaim(live, task.AttemptCount))
            {
                updated = OcrQueueCompletionPolicy.SettleAttempt(live, task, result, boundRunId, _retry,
                    _clock.UtcNow);
                _tasks[task.TaskId] = updated;
                if (updated.State == OcrQueueTaskState.Queued)
                {
                    lock (_progressLock)
                    {
                        // A requeued attempt owns the next progress stream: drop this attempt's
                        // last report so readers cannot see stale progress before it reports.
                        _progress.Remove(task.TaskId);
                        _lastProgressNotification.Remove(task.TaskId);
                    }
                }
            }
        }

        if (_engineLeases is not null && OcrQueueCompletionPolicy.ReleasesGenerationPin(updated))
        {
            await _engineLeases.ReleaseAsync(task.TaskId);
        }

        if (updated is null)
        {
            RefreshActivityStates();
            WakeScheduler();
            return;
        }

        if (OcrQueueLifecyclePolicy.IsTerminal(updated.State))
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
    /// Typed run-binding callback used by <see cref="OcrRunExecutionContext"/>: the decision is
    /// evaluated by <see cref="OcrRunBindingPolicy"/> under <see cref="_stateLock"/>; the change
    /// notification is raised after the lock is released.
    /// </summary>
    private Result BindAttemptRun(OcrQueueTaskId taskId, int attempt, OcrRunId runId,
        OcrPresetVersionId? presetVersionId)
    {
        OcrRunBindingPolicy.Decision decision;
        lock (_stateLock)
        {
            _tasks.TryGetValue(taskId, out OcrQueueTask? task);
            decision = OcrRunBindingPolicy.Evaluate(task, attempt, runId, presetVersionId, _clock.UtcNow);
            if (decision.RecordsBinding)
            {
                _attemptBindings[taskId] = (attempt, runId);
            }

            if (decision.UpdatedTask is { } updated)
            {
                _tasks[taskId] = updated;
            }
        }

        if (decision.UpdatedTask is { } changed)
        {
            OnChanged(changed, OcrQueueChangeKind.Updated);
        }

        return decision.Result;
    }

    private void OnProgress(OcrTaskProgressReport report)
    {
        // Validation and storage run under one state→progress lock scope so a requeue or clear
        // cannot interleave between them. Reports are attempt-owned: unknown, removed,
        // non-running, or foreign-attempt reports are dropped entirely, and the change
        // notification is published only after both locks are released.
        bool isTerminal = OcrQueueProgressPolicy.IsTerminal(report);
        lock (_stateLock)
        {
            if (!_tasks.TryGetValue(report.TaskId, out OcrQueueTask? current) ||
                !OcrQueueProgressPolicy.Accepts(current, report))
            {
                return;
            }

            lock (_progressLock)
            {
                _progress[report.TaskId] = report;
                DateTimeOffset now = _clock.UtcNow;
                if (!OcrQueueProgressPolicy.ShouldNotify(isTerminal,
                        _lastProgressNotification.TryGetValue(report.TaskId, out DateTimeOffset last) ? last : null,
                        now))
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
        if (OcrQueuePausePolicy.IsUnsupportedScope(scope))
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Preset-level pause is not supported."));
        }

        lock (_stateLock)
        {
            _pauses.Add(OcrQueuePausePolicy.Key(scope, target));
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
            _pauses.Remove(OcrQueuePausePolicy.Key(scope, target));
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

            if (OcrQueueLifecyclePolicy.IsTerminal(task.State))
            {
                return Result.Failure(AppErrorCodes.InvalidState,
                    "Terminal OCR tasks cannot be cancelled.");
            }

            if (!_running.TryGetValue(id, out toCancel))
            {
                updated = OcrQueueLifecyclePolicy.CancelWaiting(task, _clock.UtcNow);
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

            if (!OcrQueueLifecyclePolicy.IsRetryableState(task.State))
            {
                return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidState,
                    "Only failed, blocked, or cancelled OCR tasks can be retried.");
            }

            retry = OcrQueueLifecyclePolicy.CreateRetry(task, presetVersionId, OcrQueueTaskId.New(),
                _clock.UtcNow);
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
            OcrQueueTask[] tasks =
                OcrQueueProjection.SelectForListing(_tasks.Values, f, _clock.UtcNow);
            return Task.FromResult(Result<IReadOnlyList<OcrQueueTask>>.Success(tasks));
        }
    }

    public Task<Result<OcrQueueStatus>> GetQueueStatusAsync(CancellationToken c = default)
    {
        c.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            OcrQueueStatus status = OcrQueueProjection.ProjectStatus(_loopTask is { IsCompleted: false },
                _tasks.Values, _pauses.ToArray(), _limits);
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
                .Where(t => OcrQueueLifecyclePolicy.IsTerminal(t.State))
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

    private void RefreshActivityStates()
    {
        if (_activityTracker is null)
        {
            return;
        }

        OcrTaskActivityState[] desired;
        lock (_stateLock)
        {
            DateTimeOffset now = _clock.UtcNow;
            desired = OcrQueueActivityProjection.SelectVisible(_tasks.Values)
                .Select(task => OcrQueueActivityProjection.Describe(task, now, _pauses))
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

            foreach (OcrTaskActivityState state in desired)
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

    private void OnChanged(OcrQueueTask? task, string changeKind)
    {
        Changed?.Invoke(this, new OcrQueueChangedEventArgs(task, changeKind));
    }
}
