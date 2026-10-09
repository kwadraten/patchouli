using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;

namespace Patchouli.Host.Agent;

/// <summary>
///     The real host primitives (S4): OCR jobs are enqueued through the OCR queue's own public
///     contract, and a <c>WaitRunEvent</c> arms an event-driven wait that the queue's
///     <see cref="IOcrQueueScheduler.Changed" /> notification satisfies — never a poll.
/// </summary>
/// <remarks>
///     <para>
///         <b>D7 (ADR 0036).</b> This class only enqueues through
///         <see cref="IOcrQueueScheduler.EnqueueDocumentAsync" /> and only subscribes to
///         <see cref="IOcrQueueScheduler.Changed" />. It never shares, renames or embeds the queue's
///         scheduler, execution state machine or activity tracking: agent sessions and OCR runs stay
///         observed side by side in <c>runs/</c>.
///     </para>
///     <para>
///         <b>Wakeup and cancellation.</b> A wait is satisfied by the first terminal OCR queue task
///         for the same document instance and returns a structured payload. A wait is cancelled
///         explicitly — never silently dropped — with a <see cref="WaitCancelledCode" /> result when
///         the calling session's control event cancels it, and with a
///         <see cref="WaitHostShutdownCode" /> result when the host lifetime ends or the instance is
///         disposed; both complete the returned task with a failure <see cref="Result{T}" /> that the
///         effect interpreter feeds back as the <c>RunEvent</c> payload.
///     </para>
/// </remarks>
public sealed class OcrQueueHostPrimitives : IAgentHostPrimitives, IDisposable
{
    /// <summary>The run-URI prefix one OCR document run satisfies (<c>patchouli://runs/ocr/&lt;documentId&gt;</c>).</summary>
    public const string OcrRunUriPrefix = "patchouli://runs/ocr/";

    /// <summary>Stable code of the cancellation result a session control event gives an armed wait.</summary>
    public const string WaitCancelledCode = "HOST_WAIT_CANCELLED";

    /// <summary>Stable code of the cancellation result a host shutdown gives an armed wait.</summary>
    public const string WaitHostShutdownCode = "HOST_WAIT_HOST_SHUTDOWN";

    /// <summary>Stable code reported when a run URI can never be satisfied because it names no OCR run.</summary>
    public const string UnknownRunUriCode = "HOST_WAIT_UNKNOWN_RUN_URI";

    private readonly IOcrQueueScheduler _queue;
    private readonly IPageService _pages;
    private readonly IOcrPresetService _presets;
    private readonly Func<string> _documentOcrEngineId;
    private readonly CancellationToken _hostLifetime;
    private readonly ConcurrentDictionary<long, ArmedWait> _armed = new();
    private long _nextWaitKey;

    /// <summary>Creates the primitives over the OCR queue and the document/preset resolution services.</summary>
    /// <param name="queue">The OCR queue everything is enqueued through and observed from.</param>
    /// <param name="pages">The page listing used to resolve a document's page range.</param>
    /// <param name="presets">The preset service used to resolve the active preset of the document engine.</param>
    /// <param name="documentOcrEngineId">The configured document OCR engine id, read live so settings changes apply.</param>
    /// <param name="hostLifetime">Cancels every armed wait when the host shuts down.</param>
    public OcrQueueHostPrimitives(IOcrQueueScheduler queue, IPageService pages, IOcrPresetService presets,
        Func<string> documentOcrEngineId, CancellationToken hostLifetime = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(documentOcrEngineId);
        _queue = queue;
        _pages = pages;
        _presets = presets;
        _documentOcrEngineId = documentOcrEngineId;
        _hostLifetime = hostLifetime;
        _queue.Changed += OnQueueChanged;
    }

    /// <inheritdoc />
    public async Task<AgentOcrEnqueueOutcome> EnqueueOcrAsync(string documentId, string pageRange,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return Rejected("the document id is empty.");
        }

        DocumentInstanceId document;
        try
        {
            document = DocumentInstanceId.Parse(documentId.Trim());
        }
        catch (FormatException)
        {
            return Rejected($"'{documentId}' is not a document instance id.");
        }

        Result<IReadOnlyList<Page>> pages = await _pages.ListPagesAsync(document, cancellationToken)
            .ConfigureAwait(false);
        if (pages.IsFailure)
        {
            return Rejected($"the pages of document '{documentId}' are unavailable " +
                            $"({pages.ErrorCode}: {pages.ErrorMessage}).");
        }

        if (!TrySelectPages(pages.Value, pageRange, out IReadOnlyList<Page> selected))
        {
            return Rejected($"'{pageRange}' selects no page of document '{documentId}'.");
        }

        string engineId = _documentOcrEngineId();
        if (string.IsNullOrWhiteSpace(engineId))
        {
            return Rejected("no document OCR engine is configured.");
        }

        Result<OcrPreset?> preset = await _presets.FindActivePresetByEngineIdAsync(engineId, cancellationToken)
            .ConfigureAwait(false);
        if (preset.IsFailure)
        {
            return Rejected($"the active OCR preset of engine '{engineId}' could not be resolved " +
                            $"({preset.ErrorCode}: {preset.ErrorMessage}).");
        }

        if (preset.Value is null)
        {
            return Rejected($"engine '{engineId}' has no active OCR preset.");
        }

        Result<OcrQueueTask> queued = await _queue.EnqueueDocumentAsync(
            document,
            preset.Value.PresetId,
            selected.Select(static page => page.PageId).ToArray(),
            engineId,
            AdapterKindFor(engineId),
            ProviderIdFor(engineId),
            OcrQueuePriority.UserStartedDocument,
            cancellationToken).ConfigureAwait(false);
        if (queued.IsFailure)
        {
            return Rejected($"the OCR queue refused the job ({queued.ErrorCode}: {queued.ErrorMessage}).");
        }

        // Starting is idempotent; the queue loop must be running for the enqueued job to progress
        // (the same call the run coordinator makes after its own enqueue).
        await _queue.StartAsync(cancellationToken).ConfigureAwait(false);

        string runUri = OcrRunUriPrefix + documentId.Trim();
        return new AgentOcrEnqueueOutcome(true,
            $"ocr job enqueued for '{runUri}' [{pageRange}]: task {queued.Value.TaskId}");
    }

    /// <inheritdoc />
    public async Task<Result<string>> WaitRunEventAsync(string runUri, long waitId,
        CancellationToken cancellationToken)
    {
        if (!TryParseOcrRunUri(runUri, out DocumentInstanceId document))
        {
            // A wait that can never be satisfied is reported, not armed: the caller feeds this failure
            // back as the RunEvent payload (ADR 0036 — never a silently dropped wait).
            return Result<string>.Failure(UnknownRunUriCode,
                $"The run URI '{runUri}' names no OCR document run; the wait was not armed.");
        }

        TaskCompletionSource<Result<string>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        long key = Interlocked.Increment(ref _nextWaitKey);
        ArmedWait wait = new(document, completion);
        _armed[key] = wait;

        // A session control event cancels the wait explicitly; the host lifetime does the same for
        // every armed wait on shutdown. Both complete the task with a structured cancellation result.
        using CancellationTokenRegistration sessionRegistration = cancellationToken.Register(() =>
            Cancel(key, WaitCancelledCode,
                $"The wait for '{runUri}' (wait {waitId}) was cancelled by a session control event."));
        using CancellationTokenRegistration hostRegistration = _hostLifetime.Register(() =>
            Cancel(key, WaitHostShutdownCode,
                $"The wait for '{runUri}' (wait {waitId}) was cancelled because the host is shutting down."));

        // The task may already be terminal before the subscription's first notification; consult the
        // queue once so the wait is satisfied even when the completion event fired before arming.
        await SatisfyFromCurrentStateAsync(wait).ConfigureAwait(false);

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _armed.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    public Task ReportProgressAsync(string message, CancellationToken cancellationToken)
    {
        // Progress is recorded in the session event sequence (and reported through
        // IHostActivityTracker by the session service); there is no extra host surface to feed.
        return Task.CompletedTask;
    }

    /// <summary>Detaches the queue subscription and cancels every still-armed wait with the shutdown code.</summary>
    public void Dispose()
    {
        _queue.Changed -= OnQueueChanged;
        foreach (ArmedWait wait in _armed.Values)
        {
            wait.TryComplete(Result<string>.Failure(WaitHostShutdownCode,
                "The wait was cancelled because the host primitives were disposed."));
        }

        _armed.Clear();
    }

    private static AgentOcrEnqueueOutcome Rejected(string reason)
    {
        return new AgentOcrEnqueueOutcome(false, $"ocr job not enqueued: {reason}");
    }

    private static bool TryParseOcrRunUri(string runUri, out DocumentInstanceId document)
    {
        document = default;
        if (string.IsNullOrEmpty(runUri) || !runUri.StartsWith(OcrRunUriPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            document = DocumentInstanceId.Parse(runUri[OcrRunUriPrefix.Length..]);
            return true;
        }
        catch (FormatException)
        {
            document = default;
            return false;
        }
    }

    private static bool IsTerminal(string state)
    {
        return state is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed
            or OcrQueueTaskState.Cancelled or OcrQueueTaskState.Blocked;
    }

    private static string AdapterKindFor(string engineId)
    {
        return engineId == OcrEngineIds.MinerU ? OcrAdapterKind.CloudApi : OcrAdapterKind.LocalLibrary;
    }

    private static string? ProviderIdFor(string engineId)
    {
        return engineId == OcrEngineIds.MinerU ? ProviderIds.MinerU : null;
    }

    private void OnQueueChanged(object? sender, OcrQueueChangedEventArgs args)
    {
        if (args.Task is not { } task || !IsTerminal(task.State))
        {
            return;
        }

        foreach (ArmedWait wait in _armed.Values)
        {
            if (wait.Document == task.DocumentInstanceId)
            {
                wait.TryComplete(Result<string>.Success(TerminalPayload(task)));
            }
        }
    }

    private void Cancel(long key, string code, string message)
    {
        if (_armed.TryGetValue(key, out ArmedWait? wait))
        {
            wait.TryComplete(Result<string>.Failure(code, message));
        }
    }

    private async Task SatisfyFromCurrentStateAsync(ArmedWait wait)
    {
        Result<IReadOnlyList<OcrQueueTask>> tasks = await _queue
            .ListTasksAsync(new OcrQueueTaskFilter(), CancellationToken.None).ConfigureAwait(false);
        if (tasks.IsFailure)
        {
            // The subscription still stands: a later completion event satisfies the wait, and shutdown
            // cancels it explicitly, so a failed listing never strands an armed wait.
            return;
        }

        OcrQueueTask? terminal = tasks.Value
            .Where(task => task.DocumentInstanceId == wait.Document && IsTerminal(task.State))
            .MaxBy(static task => task.UpdatedAt);
        if (terminal is not null)
        {
            wait.TryComplete(Result<string>.Success(TerminalPayload(terminal)));
        }
    }

    private static string TerminalPayload(OcrQueueTask task)
    {
        return JsonSerializer.Serialize(new
        {
            state = task.State,
            taskId = task.TaskId.Value,
            documentId = task.DocumentInstanceId.Value,
            runId = task.RunId?.Value,
            completedPages = task.CompletedPageCount,
            failedPages = task.FailedPageCount,
            errorCode = task.LastErrorCode,
            errorMessage = task.LastErrorMessage
        });
    }

    /// <summary>
    ///     Selects the pages of one document instance a page-range text names: empty text selects every
    ///     page; otherwise the text is a 1-based list of pages and <c>first-last</c> ranges separated
    ///     by commas, semicolons or spaces (the same syntax the built-in scripts parse).
    /// </summary>
    private static bool TrySelectPages(IReadOnlyList<Page> pages, string pageRange, out IReadOnlyList<Page> selected)
    {
        if (string.IsNullOrWhiteSpace(pageRange))
        {
            selected = pages.OrderBy(static page => page.PageIndex).ToArray();
            return selected.Count > 0;
        }

        SortedSet<int> wanted = new();
        string[] tokens = pageRange.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            string[] parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            switch (parts.Length)
            {
                case 1 when TryParsePageNumber(parts[0], out int single) && single > 0:
                    wanted.Add(single);
                    break;
                case 2 when TryParsePageNumber(parts[0], out int first) &&
                            TryParsePageNumber(parts[1], out int second) && first > 0 && second > 0:
                    for (int page = Math.Min(first, second); page <= Math.Max(first, second); page++)
                    {
                        wanted.Add(page);
                    }

                    break;
                default:
                    selected = [];
                    return false;
            }
        }

        selected = pages.Where(page => wanted.Contains(page.PageIndex + 1))
            .OrderBy(static page => page.PageIndex).ToArray();
        return selected.Count > 0;
    }

    private static bool TryParsePageNumber(string part, out int page)
    {
        return int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out page) && page > 0;
    }

    private sealed class ArmedWait(DocumentInstanceId document, TaskCompletionSource<Result<string>> completion)
    {
        public DocumentInstanceId Document { get; } = document;

        public void TryComplete(Result<string> result)
        {
            completion.TrySetResult(result);
        }
    }
}
