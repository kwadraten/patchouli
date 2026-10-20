using System.Collections.Concurrent;
using Patchouli.Core.Credentials;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Infrastructure.Translation;
using Patchouli.Llm;

namespace Patchouli.Host.Translation;

/// <summary>
/// Host-side entry point for the direct translation engine. It resolves the requested pages to their
/// one-based physical indexes, captures the persisted translation settings for the run, starts one
/// background <see cref="TranslationRunEngine"/> execution and exposes volatile progress, cancellation
/// and the terminal per-page outcomes. The run registry is intentionally in-memory only: runs are not
/// persisted, never enter a snapshot and disappear with the host process.
/// </summary>
/// <remarks>
/// Multiple runs may target the same document at once; every run owns its own cancellation token and
/// progress buffer. Completed runs stay readable until the host restarts so a client can still fetch
/// their final status after <c>running</c> has advanced.
/// </remarks>
public sealed class TranslationRunService : ITranslationRunService, ITranslationTaskControls, IAsyncDisposable
{
    private readonly IPageService _pages;
    private readonly TranslationRunEngine _engine;
    private readonly Func<LlmAppSettings> _settings;
    private readonly CancellationToken _hostLifetime;
    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
    private readonly object _startGate = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private readonly Dictionary<string, (string Fingerprint, TranslationRunStatus Receipt)> _requests =
        new(StringComparer.Ordinal);

    private long _runSequence;
    private bool _disposed;
    private bool _queueRunning = true;
    private bool _globallyPaused;

    /// <summary>Creates the service over the page catalog, document reads, translation writes and LLM settings.</summary>
    /// <param name="pages">Resolves the document pages and validates that the document exists.</param>
    /// <param name="documents">Reads each page's current committed revision and boxes.</param>
    /// <param name="pageTranslations">Reads page translation state and commits box payloads.</param>
    /// <param name="settings">Reads the live LLM settings; called for every started run.</param>
    /// <param name="credentials">Supplies provider credentials to the default model invoker.</param>
    /// <param name="timeProvider">Clock used for retry backoff and request throttling; defaults to the system clock.</param>
    /// <param name="hostLifetime">Cancels every in-flight run when the host shuts down.</param>
    public TranslationRunService(
        IPageService pages,
        IDocumentTreeService documents,
        IPageTranslationService pageTranslations,
        Func<LlmAppSettings> settings,
        ICredentialStore credentials,
        TimeProvider? timeProvider = null,
        CancellationToken hostLifetime = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(pageTranslations);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        _pages = pages;
        _settings = settings;
        _hostLifetime = hostLifetime;
        _engine = new TranslationRunEngine(
            documents,
            pageTranslations,
            TranslationDefaults.CreateRegistry(settings, credentials),
            timeProvider);
    }

    /// <inheritdoc />
    public async Task<Result<TranslationRunStatus>> StartAsync(
        TranslationRunStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? id = request.RequestId?.Trim();
        if (id is not null && (id.Length == 0 || id.Length > 256))
        {
            return Result<TranslationRunStatus>.Failure(AppErrorCodes.InvalidArgument,
                "request_id must contain 1 through 256 characters.");
        }

        string fingerprint = System.Text.Json.JsonSerializer.Serialize(new
        {
            endpoint = "runs/translation/control", action = "start", document = request.DocumentInstanceId.ToString(),
            pages = request.PageIndexes.Distinct().Order().ToArray(),
            language = string.IsNullOrWhiteSpace(request.TargetLanguage) ? null : request.TargetLanguage.Trim(),
            force = request.ForceRetranslate
        });
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (id is not null &&
                _requests.TryGetValue(id, out (string Fingerprint, TranslationRunStatus Receipt) previous))
            {
                return previous.Fingerprint == fingerprint
                    ? Result<TranslationRunStatus>.Success(previous.Receipt with { Reused = true })
                    : Result<TranslationRunStatus>.Failure(AppErrorCodes.Conflict,
                        "request_id was already used for a different translation message.");
            }

            Result<TranslationRunStatus> accepted =
                await StartCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (accepted.IsSuccess && id is not null)
            {
                _requests[id] = (fingerprint, accepted.Value);
            }

            return accepted;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<Result<TranslationRunStatus>> StartCoreAsync(
        TranslationRunStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Result<IReadOnlyList<Page>> pages = await _pages.ListPagesAsync(request.DocumentInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (pages.IsFailure)
        {
            return Result<TranslationRunStatus>.Failure(
                pages.ErrorCode ?? AppErrorCodes.NotFound,
                pages.ErrorMessage ?? "The document pages could not be read.");
        }

        Result<IReadOnlyList<TranslationPageTarget>> targets =
            ResolveTargets(request.DocumentInstanceId, pages.Value, request.PageIndexes);
        if (targets.IsFailure)
        {
            return Result<TranslationRunStatus>.Failure(targets.ErrorCode!, targets.ErrorMessage!);
        }

        TranslationSettings settings = ResolveSettings(request.TargetLanguage);
        TranslationRunRequest runRequest = new(
            request.DocumentInstanceId,
            targets.Value,
            settings,
            ForceRetranslate: request.ForceRetranslate);

        Guid runId = Guid.NewGuid();
        CancellationTokenSource cancellation = _hostLifetime.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_hostLifetime)
            : new CancellationTokenSource();
        RunState state = new(runId, request.DocumentInstanceId, Interlocked.Increment(ref _runSequence), targets.Value,
            cancellation, request with { TargetLanguage = settings.TargetLanguage });
        lock (_startGate)
        {
            if (_disposed)
            {
                cancellation.Dispose();
                return Result<TranslationRunStatus>.Failure(
                    AppErrorCodes.InvalidState,
                    "The translation run service has been disposed.");
            }

            // Start the background execution before publishing the run so a concurrent dispose always
            // observes a task to await instead of a placeholder.
            state.ExecutionGate.SetPaused(!_queueRunning || _globallyPaused);
            state.RunTask = Task.Run(() => ExecuteAsync(state, runRequest), CancellationToken.None);
            _runs[runId] = state;
        }

        return Result<TranslationRunStatus>.Success(state.Snapshot());
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<TranslationRunStatus>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TranslationRunStatus> runs = _runs.Values
            .OrderByDescending(static run => run.Sequence)
            .Select(static run => run.Snapshot())
            .ToArray();
        return Task.FromResult(Result<IReadOnlyList<TranslationRunStatus>>.Success(runs));
    }

    /// <inheritdoc />
    public Task<Result<TranslationRunStatus>> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_runs.TryGetValue(runId, out RunState? run)
            ? Result<TranslationRunStatus>.Success(run.Snapshot())
            : Result<TranslationRunStatus>.Failure(
                AppErrorCodes.NotFound,
                $"The translation run '{runId}' does not exist."));
    }

    /// <inheritdoc />
    public Task<Result<TranslationRunStatus>> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(runId, out RunState? run))
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure(
                AppErrorCodes.NotFound,
                $"The translation run '{runId}' does not exist."));
        }

        if (run.TryMarkCancelled())
        {
            run.Cancellation.Cancel();
        }

        return Task.FromResult(Result<TranslationRunStatus>.Success(run.Snapshot()));
    }

    public Task<Result> SetQueueRunningAsync(bool running, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_startGate)
        {
            if (_disposed)
            {
                return Task.FromResult(Result.Failure(AppErrorCodes.InvalidState, "Service disposed."));
            }

            _queueRunning = running;
            UpdateExecutionGates();
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> SetPausedAsync(bool paused, Guid? runId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_startGate)
        {
            if (_disposed)
            {
                return Task.FromResult(Result.Failure(AppErrorCodes.InvalidState, "Service disposed."));
            }

            if (runId is { } id)
            {
                if (!_runs.TryGetValue(id, out RunState? run))
                {
                    return Task.FromResult(Result.Failure(AppErrorCodes.NotFound, "Translation run not found."));
                }

                if (run.Snapshot().State != TranslationRunLifecycle.Running)
                {
                    return Task.FromResult(Result.Failure(AppErrorCodes.InvalidState, "Translation run has finished."));
                }

                run.LocallyPaused = paused;
            }
            else
            {
                _globallyPaused = paused;
            }

            UpdateExecutionGates();
        }

        return Task.FromResult(Result.Success());
    }

    private void UpdateExecutionGates()
    {
        foreach (RunState run in _runs.Values)
        {
            run.ExecutionGate.SetPaused(!_queueRunning || _globallyPaused || run.LocallyPaused);
        }
    }

    public Task<Result<TranslationRunStatus>> RetryAsync(Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out RunState? run))
        {
            return Task.FromResult(
                Result<TranslationRunStatus>.Failure(AppErrorCodes.NotFound, "Translation run not found."));
        }

        TranslationRunStatus status = run.Snapshot();
        if (status.State == TranslationRunLifecycle.Running ||
            (status.State != TranslationRunLifecycle.Failed && status.Failed == 0 && status.Partial == 0))
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure(AppErrorCodes.InvalidState,
                "No failed translation pages to retry."));
        }

        int[] pages = status.Pages
            .Where(page => page.State is TranslationPageRunState.Failed or TranslationPageRunState.Partial)
            .Select(page => page.PageIndex).ToArray();
        return StartAsync(run.Request with
        {
            PageIndexes = pages.Length > 0 ? pages : run.Request.PageIndexes,
            ForceRetranslate = false,
            RequestId = $"queue-retry:{runId}"
        }, cancellationToken);
    }

    /// <summary>
    /// Cancels every in-flight run, waits for the background executions to settle, then releases their
    /// cancellation sources. Runs already finished keep their final snapshot.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        RunState[] runs;
        lock (_startGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runs = _runs.Values.ToArray();
        }

        foreach (RunState run in runs)
        {
            if (run.TryMarkCancelled())
            {
                run.Cancellation.Cancel();
            }
        }

        foreach (RunState run in runs)
        {
            try
            {
                await run.RunTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                                  exception, "host.translation-run"))
            {
            }
        }

        foreach (RunState run in runs)
        {
            run.Cancellation.Dispose();
        }
    }

    private async Task ExecuteAsync(RunState state, TranslationRunRequest request)
    {
        try
        {
            await state.ExecutionGate.WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
            TranslationRunResult result = await _engine.RunAsync(
                request,
                new RunProgress(state),
                state.Cancellation.Token, state.ExecutionGate).ConfigureAwait(false);
            state.Complete(result);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            state.CompleteCancelled();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "host.translation-run"))
        {
            state.Fail(AppErrorCodes.InvalidState, $"The translation run failed: {exception.Message}");
        }
    }

    private TranslationSettings ResolveSettings(string? targetLanguage)
    {
        LlmAppSettings settings = _settings();
        TranslationSettings runtime = (settings.Translation ?? new TranslationAppSettings())
            .Normalize()
            .ToRuntimeSettings();
        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            runtime = runtime with { TargetLanguage = targetLanguage.Trim() };
        }

        return runtime.Normalize();
    }

    /// <summary>
    /// Resolves requested one-based page indexes against the document's page list. An empty request
    /// selects every page; explicit indexes are deduplicated and returned in document order because the
    /// engine uses array adjacency for neighbouring-page context.
    /// </summary>
    private static Result<IReadOnlyList<TranslationPageTarget>> ResolveTargets(
        DocumentInstanceId documentInstanceId,
        IReadOnlyList<Page> pages,
        IReadOnlyList<int>? requestedPageIndexes)
    {
        List<Page> ordered = [.. pages.OrderBy(static page => page.PageIndex)];
        if (requestedPageIndexes is not { Count: > 0 })
        {
            if (ordered.Count == 0)
            {
                return Result<IReadOnlyList<TranslationPageTarget>>.Failure(
                    AppErrorCodes.InvalidArgument,
                    $"Document '{documentInstanceId}' has no pages to translate.");
            }

            return Result<IReadOnlyList<TranslationPageTarget>>.Success(
                ordered.Select(static page => new TranslationPageTarget(page.PageId, page.PageIndex + 1))
                    .ToArray());
        }

        Dictionary<int, Page> pageByIndex = new(ordered.Count);
        foreach (Page page in ordered)
        {
            pageByIndex[page.PageIndex + 1] = page;
        }

        SortedSet<int> wanted = [];
        foreach (int pageIndex in requestedPageIndexes)
        {
            if (pageIndex <= 0)
            {
                return Result<IReadOnlyList<TranslationPageTarget>>.Failure(
                    AppErrorCodes.InvalidArgument,
                    "Page indexes are one-based and must be positive.");
            }

            if (!pageByIndex.ContainsKey(pageIndex))
            {
                return Result<IReadOnlyList<TranslationPageTarget>>.Failure(
                    AppErrorCodes.InvalidArgument,
                    $"Page {pageIndex} does not belong to document '{documentInstanceId}'.");
            }

            wanted.Add(pageIndex);
        }

        return Result<IReadOnlyList<TranslationPageTarget>>.Success(
            wanted.Select(pageIndex => new TranslationPageTarget(pageByIndex[pageIndex].PageId, pageIndex)).ToArray());
    }

    private sealed class RunProgress(RunState state) : IProgress<TranslationRunProgress>
    {
        public void Report(TranslationRunProgress value)
        {
            state.RecordProgress(value);
        }
    }

    /// <summary>One volatile run: immutable identity plus a lock-guarded progress and lifecycle buffer.</summary>
    private sealed class RunState
    {
        private readonly object _gate = new();
        private readonly TranslationPageTarget[] _targets;
        private readonly TranslationPageOutcome?[] _outcomes;
        private readonly Dictionary<int, int> _slotByPageIndex;
        private TranslationRunLifecycle _state = TranslationRunLifecycle.Running;
        private string? _errorCode;
        private string? _errorMessage;

        public RunState(
            Guid runId,
            DocumentInstanceId documentInstanceId,
            long sequence,
            IReadOnlyList<TranslationPageTarget> targets,
            CancellationTokenSource cancellation, TranslationRunStartRequest request)
        {
            Request = request;
            RunId = runId;
            DocumentInstanceId = documentInstanceId;
            Sequence = sequence;
            _targets = [.. targets];
            _outcomes = new TranslationPageOutcome?[_targets.Length];
            _slotByPageIndex = new Dictionary<int, int>(_targets.Length);
            for (int index = 0; index < _targets.Length; index++)
            {
                _slotByPageIndex[_targets[index].PageIndex] = index;
            }

            Cancellation = cancellation;
        }

        public TranslationRunStartRequest Request { get; }
        public TranslationExecutionGate ExecutionGate { get; } = new();
        public bool LocallyPaused { get; set; }
        public Guid RunId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public long Sequence { get; }
        public CancellationTokenSource Cancellation { get; }
        public Task RunTask { get; set; } = Task.CompletedTask;

        public void RecordProgress(TranslationRunProgress progress)
        {
            if (progress.Outcome is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_slotByPageIndex.TryGetValue(progress.Outcome.PageIndex, out int slot))
                {
                    _outcomes[slot] = progress.Outcome;
                }
            }
        }

        public void Complete(TranslationRunResult result)
        {
            lock (_gate)
            {
                foreach (TranslationPageOutcome outcome in result.Pages)
                {
                    if (_slotByPageIndex.TryGetValue(outcome.PageIndex, out int slot))
                    {
                        _outcomes[slot] = outcome;
                    }
                }

                _state = result.Cancelled ? TranslationRunLifecycle.Cancelled : TranslationRunLifecycle.Completed;
                if (result.Cancelled)
                {
                    _errorCode = "cancelled";
                    _errorMessage = "The translation run was cancelled.";
                }
                else
                {
                    _errorCode = null;
                    _errorMessage = null;
                }
            }
        }

        public void CompleteCancelled()
        {
            lock (_gate)
            {
                _state = TranslationRunLifecycle.Cancelled;
                _errorCode = "cancelled";
                _errorMessage = "The translation run was cancelled.";
            }
        }

        public void Fail(string errorCode, string errorMessage)
        {
            lock (_gate)
            {
                _state = TranslationRunLifecycle.Failed;
                _errorCode = errorCode;
                _errorMessage = errorMessage;
            }
        }

        /// <summary>Marks a running run cancelled and reports whether the caller should signal the token.</summary>
        public bool TryMarkCancelled()
        {
            lock (_gate)
            {
                if (_state != TranslationRunLifecycle.Running)
                {
                    return false;
                }

                _state = TranslationRunLifecycle.Cancelled;
                _errorCode = "cancelled";
                _errorMessage = "The translation run was cancelled.";
                return true;
            }
        }

        public TranslationRunStatus Snapshot()
        {
            lock (_gate)
            {
                TranslationPageOutcome[] settled = _outcomes
                    .Where(static outcome => outcome is not null)
                    .Cast<TranslationPageOutcome>()
                    .ToArray();
                return new TranslationRunStatus(
                    RunId,
                    DocumentInstanceId,
                    _state,
                    _targets.Length,
                    settled.Length,
                    settled.Count(static outcome => outcome.State == TranslationPageRunState.Committed),
                    settled.Count(static outcome => outcome.State == TranslationPageRunState.Partial),
                    settled.Count(static outcome => outcome.State == TranslationPageRunState.Failed),
                    settled.Count(static outcome => outcome.State == TranslationPageRunState.Skipped),
                    settled.Count(static outcome => outcome.State == TranslationPageRunState.Cancelled),
                    settled,
                    _errorCode,
                    _errorMessage, IsPaused: _state == TranslationRunLifecycle.Running && ExecutionGate.IsPaused);
            }
        }
    }
}
