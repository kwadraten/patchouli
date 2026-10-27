using System.Collections.Concurrent;
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
    private readonly TranslationExtensionRunner _extensionRunner;
    private readonly Func<string, CancellationToken, Task>? _initializeEngine;
    private readonly ConcurrentDictionary<Guid, TranslationRunTracker> _runs = new();
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
    /// <param name="extensionRunner">Acquires the selected pinned translation extension.</param>
    /// <param name="initializeEngine">Initializes the selected extension package before a run starts.</param>
    /// <param name="hostLifetime">Cancels every in-flight run when the host shuts down.</param>
    public TranslationRunService(
        IPageService pages,
        IDocumentTreeService documents,
        IPageTranslationService pageTranslations,
        Func<LlmAppSettings> settings,
        TranslationExtensionRunner extensionRunner,
        Func<string, CancellationToken, Task>? initializeEngine = null,
        CancellationToken hostLifetime = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(pageTranslations);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(extensionRunner);
        _pages = pages;
        _settings = settings;
        _hostLifetime = hostLifetime;
        _extensionRunner = extensionRunner;
        _initializeEngine = initializeEngine;
        _engine = new TranslationRunEngine(documents, pageTranslations, extensionRunner);
    }

    /// <summary>Preserves backwards compatibility for callers passing a parameterless extension initializer.</summary>
    public TranslationRunService(
        IPageService pages,
        IDocumentTreeService documents,
        IPageTranslationService pageTranslations,
        Func<LlmAppSettings> settings,
        TranslationExtensionRunner extensionRunner,
        Func<CancellationToken, Task> initializeExtensions,
        CancellationToken hostLifetime = default)
        : this(
            pages,
            documents,
            pageTranslations,
            settings,
            extensionRunner,
            initializeExtensions is null ? null : (_, ct) => initializeExtensions(ct),
            hostLifetime)
    {
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

        bool hasExplicitTargetLanguage = !string.IsNullOrWhiteSpace(request.TargetLanguage);
        TranslationSettings settings = ResolveSettings(request.TargetLanguage);
        TranslationRunRequest runRequest = new(
            request.DocumentInstanceId,
            targets.Value,
            settings,
            ForceRetranslate: request.ForceRetranslate,
            DocumentPages: pages.Value.OrderBy(static page => page.PageIndex)
                .Select(static page => new TranslationPageTarget(page.PageId, page.PageIndex + 1)).ToArray());

        Guid runId = Guid.NewGuid();
        CancellationTokenSource cancellation = _hostLifetime.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_hostLifetime)
            : new CancellationTokenSource();
        TranslationRunTracker state = new(runId, request.DocumentInstanceId,
            Interlocked.Increment(ref _runSequence), targets.Value,
            cancellation, request with { TargetLanguage = settings.TargetLanguage });

        bool rejected = false;
        lock (_startGate)
        {
            if (_disposed)
            {
                cancellation.Dispose();
                rejected = true;
            }
            else
            {
                state.ExecutionGate.SetPaused(!_queueRunning || _globallyPaused);
                state.RunTask = Task.Run(() => ExecuteAsync(state, runRequest, settings,
                    hasExplicitTargetLanguage), CancellationToken.None);
                _runs[runId] = state;
            }
        }

        if (rejected)
        {
            return Result<TranslationRunStatus>.Failure(AppErrorCodes.InvalidState,
                "The translation run service has been disposed.");
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
        return Task.FromResult(_runs.TryGetValue(runId, out TranslationRunTracker? run)
            ? Result<TranslationRunStatus>.Success(run.Snapshot())
            : Result<TranslationRunStatus>.Failure(
                AppErrorCodes.NotFound,
                $"The translation run '{runId}' does not exist."));
    }

    /// <inheritdoc />
    public Task<Result<TranslationRunStatus>> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(runId, out TranslationRunTracker? run))
        {
            return Task.FromResult(Result<TranslationRunStatus>.Failure(
                AppErrorCodes.NotFound,
                $"The translation run '{runId}' does not exist."));
        }

        run.TryCancel();
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
                if (!_runs.TryGetValue(id, out TranslationRunTracker? run))
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
        foreach (TranslationRunTracker run in _runs.Values)
        {
            run.ExecutionGate.SetPaused(!_queueRunning || _globallyPaused || run.LocallyPaused);
        }
    }

    public Task<Result<TranslationRunStatus>> RetryAsync(Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out TranslationRunTracker? run))
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
        TranslationRunTracker[] runs;
        lock (_startGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runs = _runs.Values.ToArray();
        }

        foreach (TranslationRunTracker run in runs)
        {
            run.TryCancel();
        }

        foreach (TranslationRunTracker run in runs)
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

        foreach (TranslationRunTracker run in runs)
        {
            run.DisposeCancellation();
        }
    }

    private async Task ExecuteAsync(TranslationRunTracker state, TranslationRunRequest request,
        TranslationSettings initialSettings,
        bool hasExplicitTargetLanguage)
    {
        CancellationToken cancellationToken = state.Token;
        try
        {
            if (cancellationToken.IsCancellationRequested || state.IsCancellationRequested)
            {
                state.CompleteCancelled();
                return;
            }

            state.RecordStep(new TranslationStepProgress(
                "initializing",
                0,
                state.TotalPages,
                "starting",
                "Initializing translation engine..."));

            if (_initializeEngine is not null)
            {
                await _initializeEngine(initialSettings.EngineId, cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested || state.IsCancellationRequested)
            {
                state.CompleteCancelled();
                return;
            }

            state.ExtensionRun = await _extensionRunner.AcquireAsync(
                initialSettings.EngineId,
                state.ExecutionGate,
                cancellationToken).ConfigureAwait(false);

            if (state.ExtensionRun is null)
            {
                state.Fail(AppErrorCodes.InvalidState,
                    $"Translation engine '{initialSettings.EngineId}' is not active.");
                return;
            }

            if (cancellationToken.IsCancellationRequested || state.IsCancellationRequested)
            {
                state.CompleteCancelled();
                return;
            }

            TranslationSettings settings = initialSettings;
            if (!hasExplicitTargetLanguage &&
                state.ExtensionRun.Config.Values.TryGetValue("target-language", out string? language) &&
                !string.IsNullOrWhiteSpace(language))
            {
                settings = settings with { TargetLanguage = language.Trim() };
                request = request with { Settings = settings };
                state.Request = state.Request with { TargetLanguage = settings.TargetLanguage };
            }

            await state.ExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            TranslationRunResult result = await _engine.RunAsync(
                    request,
                    new RunProgress(state),
                    cancellationToken,
                    state.ExecutionGate,
                    new StepProgress(state),
                    state.ExtensionRun)
                .ConfigureAwait(false);

            state.Complete(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested ||
                                                 state.IsCancellationRequested)
        {
            state.CompleteCancelled();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or System.Text.Json.JsonException)
        {
            state.Fail("extension_binding_failed", exception.Message);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception, "host.translation-run"))
        {
            state.Fail(AppErrorCodes.InvalidState, $"The translation run failed: {exception.Message}");
        }
        finally
        {
            if (state.ExtensionRun is { } lease)
            {
                state.ExtensionRun = null;
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            state.DisposeCancellation();
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

    private sealed class RunProgress(TranslationRunTracker state) : IProgress<TranslationRunProgress>
    {
        public void Report(TranslationRunProgress value)
        {
            state.RecordProgress(value);
        }
    }

    private sealed class StepProgress(TranslationRunTracker state) : IProgress<TranslationStepProgress>
    {
        public void Report(TranslationStepProgress value)
        {
            state.RecordStep(value);
        }
    }
}
