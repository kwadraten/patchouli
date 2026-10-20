using System.Text.RegularExpressions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.Ocr;

namespace Patchouli.Host;

/// <summary>
///     Shared host orchestration behind <see cref="IMcpOcrCommandApi" /> (issue #16): page-range
///     parsing, engine/preset resolution, readiness preflight, process-local atomic request
///     deduplication, and enqueue on the existing OCR queue. Protocol validation and authorization
///     live in the MCP command service; this class only speaks typed domain ids and sanitized
///     <see cref="Result" /> failures. Accepted tasks are owned by the host's queue lifetime — an
///     HTTP disconnect never cancels accepted work, and cancel/retry are explicit operations.
/// </summary>
/// <remarks>
///     Replay rule: a seen <c>request_id</c> is answered from the dedup map BEFORE any mutable
///     resolution (source readiness, preset versions, default-engine config), and the stored
///     receipt survives queue cleanup; only a changed request under the same id conflicts.
/// </remarks>
public sealed partial class OcrCommandService : IMcpOcrCommandApi
{
    private const int MaxRequestIdLength = 256;
    private const int MaxPagesLength = 4000;

    private readonly IOcrQueueScheduler _queue;
    private readonly IOcrRunEngine _engine;
    private readonly IOcrPresetService _presets;
    private readonly OcrPresetDefaults _defaults;
    private readonly IOcrAdapterRegistry _adapters;
    private readonly Func<string> _defaultDocumentEngineId;

    // Atomic check-and-enqueue boundary for request_id idempotency: fingerprint comparison and
    // enqueue must share one synchronization region, so concurrent identical submissions mint a
    // single task and a reused id with a changed request fails as a conflict.
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, TrackedRequest> _requests = new(StringComparer.Ordinal);

    public OcrCommandService(
        IOcrQueueScheduler queue,
        IOcrRunEngine engine,
        IOcrPresetService presets,
        OcrPresetDefaults defaults,
        IOcrAdapterRegistry adapters,
        Func<string> defaultDocumentEngineId)
    {
        _queue = queue;
        _engine = engine;
        _presets = presets;
        _defaults = defaults;
        _adapters = adapters;
        _defaultDocumentEngineId = defaultDocumentEngineId;
    }

    public async Task<Result<McpOcrCommandResult>> StartAsync(McpOcrStartRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Engine))
        {
            return Result<McpOcrCommandResult>.Failure(AppErrorCodes.InvalidArgument,
                "The engine selector must not be blank; use 'default' for the configured engine.");
        }

        Result<string?> requestId = NormalizeRequestId(request.RequestId);
        if (requestId.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(requestId.ErrorCode!, requestId.ErrorMessage!);
        }

        // Input-only canonical fingerprint, computed before any mutable resolution (source
        // readiness, preset versions, default-engine configuration): an accepted id replay must
        // return the original receipt even when the world changed after acceptance.
        Result<string> canonicalPages = CanonicalizePages(request.Pages);
        if (canonicalPages.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(canonicalPages.ErrorCode!, canonicalPages.ErrorMessage!);
        }

        string engineSelector = string.IsNullOrWhiteSpace(request.Engine)
            ? "default"
            : request.Engine.Trim().ToLowerInvariant();
        string fingerprint = string.Join('|',
            request.DocumentInstanceId.ToString(), engineSelector, canonicalPages.Value);

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (requestId.Value is not null &&
                _requests.TryGetValue(requestId.Value, out TrackedRequest? tracked))
            {
                if (!string.Equals(tracked.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Result<McpOcrCommandResult>.Failure(AppErrorCodes.Conflict,
                        "The request id was already used with a different OCR request.");
                }

                // Receipt replay: the stored acceptance is returned as-is (the receipt survives
                // finished-task cleanup; a host restart rebuilds an empty map, which is the
                // declared volatile lifetime boundary for task ids).
                return Result<McpOcrCommandResult>.Success(tracked.Receipt with { Reused = true });
            }

            Result<McpOcrCommandResult> accepted =
                await StartFreshAsync(request, cancellationToken);
            if (accepted.IsSuccess && requestId.Value is not null)
            {
                _requests[requestId.Value] = new TrackedRequest(fingerprint, accepted.Value);
            }

            if (accepted.IsSuccess)
            {
                await _queue.StartAsync(CancellationToken.None);
            }

            return accepted;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<Result<McpOcrCommandResult>> StartFreshAsync(McpOcrStartRequest request,
        CancellationToken cancellationToken)
    {
        Result<string> engineSelection = ResolveEngineId(request.Engine);
        if (engineSelection.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(engineSelection.ErrorCode!, engineSelection.ErrorMessage!);
        }

        string engineId = engineSelection.Value;
        if (_adapters.GetAdapter(engineId) is null)
        {
            return Result<McpOcrCommandResult>.Failure(AppErrorCodes.NotFound,
                $"OCR engine '{engineId}' is not available on this host.");
        }

        Result<OcrPresetId> presetId = await ResolvePresetAsync(engineId, cancellationToken);
        if (presetId.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(presetId.ErrorCode!, presetId.ErrorMessage!);
        }

        Result<OcrPresetVersion> version =
            await _engine.ResolvePresetVersionAsync(presetId.Value, cancellationToken);
        if (version.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        Result<IReadOnlyList<PageId>> allPages =
            await _engine.ListPageIdsAsync(request.DocumentInstanceId, cancellationToken);
        if (allPages.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(allPages.ErrorCode!, allPages.ErrorMessage!);
        }

        if (allPages.Value.Count == 0)
        {
            return Result<McpOcrCommandResult>.Failure(AppErrorCodes.NotFound,
                "The document has no physical pages to OCR.");
        }

        // Selected-page tasks always take the MockPages kind, which executes
        // RunPresetOnPagesAsync — never the Document branch that ignores the page list.
        Result<IReadOnlyList<PageId>> selected = SelectPages(request.Pages, allPages.Value);
        if (selected.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(selected.ErrorCode!, selected.ErrorMessage!);
        }

        Result ready = await _engine.CheckRunReadinessAsync(
            request.DocumentInstanceId, version.Value, cancellationToken);
        if (ready.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(ready.ErrorCode!, ready.ErrorMessage!);
        }

        (string AdapterKind, string? ProviderId) routing = _defaults.QueueRoutingFor(engineId);
        OcrQueueTaskRequest taskRequest = new(
            request.DocumentInstanceId,
            presetId.Value,
            selected.Value,
            OcrQueueTaskKind.MockPages,
            engineId,
            routing.AdapterKind,
            routing.ProviderId,
            OcrQueuePriority.UserStartedDocument,
            PresetVersionId: version.Value.PresetVersionId);
        Result<OcrQueueTask> queued = await _queue.EnqueueAsync(taskRequest, cancellationToken);
        if (queued.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(queued.ErrorCode!, queued.ErrorMessage!);
        }

        // Return the receipt without another await after enqueue. The caller records acceptance
        // under the request gate before starting the host-owned scheduler.
        McpOcrCommandResult result = new(
            queued.Value.TaskId.ToString(),
            McpResourceUris.OcrRunStatusUri(queued.Value.TaskId.ToString()),
            queued.Value.State,
            queued.Value.EngineId,
            queued.Value.PresetId.ToString(),
            version.Value.PresetVersionId.ToString());
        return Result<McpOcrCommandResult>.Success(result);
    }

    public async Task<Result<McpOcrCommandResult>> CancelAsync(OcrQueueTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<OcrQueueTask> task = await _queue.GetTaskAsync(taskId, cancellationToken);
        if (task.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(AppErrorCodes.NotFound, "The OCR task was not found.");
        }

        McpOcrCommandResult echo = await BuildResultAsync(task.Value, false, cancellationToken);
        Result cancelled = await _queue.CancelTaskAsync(taskId, cancellationToken);
        if (cancelled.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(cancelled.ErrorCode!, cancelled.ErrorMessage!);
        }

        // A running task stays "running" until the worker observes the cancellation token; the
        // accepted cancel is confirmed and its terminal state is observed via the status URI.
        Result<OcrQueueTask> after = await _queue.GetTaskAsync(taskId, CancellationToken.None);
        return Result<McpOcrCommandResult>.Success(after.IsSuccess
            ? await BuildResultAsync(after.Value, false, CancellationToken.None)
            : echo);
    }

    public async Task<Result<McpOcrCommandResult>> RetryAsync(OcrQueueTaskId taskId, string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Result<string?> normalizedRequestId = NormalizeRequestId(requestId);
        if (normalizedRequestId.IsFailure)
        {
            return Result<McpOcrCommandResult>.Failure(
                normalizedRequestId.ErrorCode!, normalizedRequestId.ErrorMessage!);
        }

        string fingerprint = string.Concat("retry|", taskId.ToString());
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (normalizedRequestId.Value is not null &&
                _requests.TryGetValue(normalizedRequestId.Value, out TrackedRequest? tracked))
            {
                if (!string.Equals(tracked.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Result<McpOcrCommandResult>.Failure(AppErrorCodes.Conflict,
                        "The request id was already used with a different OCR request.");
                }

                return Result<McpOcrCommandResult>.Success(tracked.Receipt with { Reused = true });
            }

            Result<OcrQueueTask> task = await _queue.GetTaskAsync(taskId, cancellationToken);
            if (task.IsFailure)
            {
                return Result<McpOcrCommandResult>.Failure(AppErrorCodes.NotFound, "The OCR task was not found.");
            }

            if (!task.Value.CommitOnCompletion || task.Value.TaskKind is not
                    (OcrQueueTaskKind.Document or OcrQueueTaskKind.MockPages))
            {
                return Result<McpOcrCommandResult>.Failure(AppErrorCodes.UnsupportedOperation,
                    "Public OCR retry supports document and selected-page tasks that commit on completion.");
            }

            // Retry revalidates readiness against the task's immutable version: a
            // retry must not silently re-enqueue work whose engine, model, credential, or source
            // is no longer available.
            Result<OcrPresetVersion> version = await ResolveTaskVersionAsync(task.Value, cancellationToken);
            if (version.IsFailure)
            {
                return Result<McpOcrCommandResult>.Failure(version.ErrorCode!, version.ErrorMessage!);
            }

            if (version.Value.PresetId != task.Value.PresetId ||
                !string.Equals(version.Value.EngineId, task.Value.EngineId, StringComparison.Ordinal))
            {
                return Result<McpOcrCommandResult>.Failure(AppErrorCodes.Conflict,
                    "The task's OCR preset or engine no longer matches its execution version.");
            }

            Result ready = await _engine.CheckRunReadinessAsync(
                task.Value.DocumentInstanceId, version.Value, cancellationToken);
            if (ready.IsFailure)
            {
                return Result<McpOcrCommandResult>.Failure(ready.ErrorCode!, ready.ErrorMessage!);
            }

            Result<OcrQueueTask> retried = await _queue.RetryTaskWithVersionAsync(taskId,
                version.Value.PresetVersionId, cancellationToken);
            if (retried.IsFailure)
            {
                return Result<McpOcrCommandResult>.Failure(retried.ErrorCode!, retried.ErrorMessage!);
            }

            McpOcrCommandResult result = new(
                retried.Value.TaskId.ToString(),
                McpResourceUris.OcrRunStatusUri(retried.Value.TaskId.ToString()),
                retried.Value.State,
                retried.Value.EngineId,
                retried.Value.PresetId.ToString(),
                version.Value.PresetVersionId.ToString());
            if (normalizedRequestId.Value is not null)
            {
                _requests[normalizedRequestId.Value] = new TrackedRequest(fingerprint, result);
            }

            await _queue.StartAsync(CancellationToken.None);

            return Result<McpOcrCommandResult>.Success(result);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<Result<OcrPresetVersion>> ResolveTaskVersionAsync(OcrQueueTask task,
        CancellationToken cancellationToken)
    {
        if (task.RunId is { } runId)
        {
            Result<OcrRun> run = await _engine.GetRunAsync(runId, cancellationToken);
            if (run.IsFailure)
            {
                return Result<OcrPresetVersion>.Failure(run.ErrorCode!, run.ErrorMessage!);
            }

            // An already recognized candidate has a durable version identity. It must not
            // be relabeled as a newer current version when retry only resumes adoption/indexing.
            return await _engine.GetPresetVersionAsync(run.Value.PresetVersionId, cancellationToken);
        }

        return task.PresetVersionId is { } pinned
            ? await _engine.GetPresetVersionAsync(pinned, cancellationToken)
            : await _engine.ResolvePresetVersionAsync(task.PresetId, cancellationToken);
    }

    private async Task<McpOcrCommandResult> BuildResultAsync(OcrQueueTask task, bool reused,
        CancellationToken cancellationToken)
    {
        // Tasks accepted through this service always carry the pinned version; the lookup only
        // covers legacy queue tasks that predate version pinning.
        string presetVersionId = task.PresetVersionId?.ToString() ?? string.Empty;
        if (presetVersionId.Length == 0 || task.RunId is not null)
        {
            Result<OcrPresetVersion> current = await ResolveTaskVersionAsync(task, cancellationToken);
            if (current.IsSuccess)
            {
                presetVersionId = current.Value.PresetVersionId.ToString();
            }
        }

        return new McpOcrCommandResult(
            task.TaskId.ToString(),
            McpResourceUris.OcrRunStatusUri(task.TaskId.ToString()),
            task.State,
            task.EngineId,
            task.PresetId.ToString(),
            presetVersionId,
            reused);
    }

    private async Task<Result<OcrPresetId>> ResolvePresetAsync(string engineId,
        CancellationToken cancellationToken)
    {
        // The user's existing active preset for the engine wins; only create the managed
        // default preset when the engine has none yet.
        Result<OcrPreset?> active = await _presets.FindActivePresetByEngineIdAsync(engineId, cancellationToken);
        if (active.IsFailure)
        {
            return Result<OcrPresetId>.Failure(active.ErrorCode!, active.ErrorMessage!);
        }

        return active.Value is not null
            ? Result<OcrPresetId>.Success(active.Value.PresetId)
            : await _defaults.EnsurePresetAsync(engineId, cancellationToken);
    }

    private Result<string> ResolveEngineId(string? engine)
    {
        string selector = string.IsNullOrWhiteSpace(engine) ? "default" : engine.Trim().ToLowerInvariant();
        if (selector.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            string configured = _defaultDocumentEngineId();
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Result<string>.Failure(AppErrorCodes.UnsupportedOperation,
                    "No default OCR engine is configured on this host.");
            }

            selector = configured.Trim().ToLowerInvariant();
        }

        return EngineIdPattern().IsMatch(selector)
            ? Result<string>.Success(selector)
            : Result<string>.Failure(AppErrorCodes.InvalidArgument, $"Unknown OCR engine '{selector}'.");
    }

    /// <summary>
    ///     Canonicalizes the pages selector for fingerprinting: syntax only, no document access.
    ///     <c>null</c> means the whole document; an explicit blank selector is an error.
    /// </summary>
    private static Result<string> CanonicalizePages(string? pages)
    {
        if (pages is null)
        {
            return Result<string>.Success(string.Empty);
        }

        Result<IReadOnlyList<(int Start, int End)>> parsed = ParsePageSelection(pages);
        return parsed.IsFailure
            ? Result<string>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!)
            : Result<string>.Success(string.Join(',', parsed.Value.Select(range =>
                range.Start == range.End ? range.Start.ToString() : $"{range.Start}-{range.End}")));
    }

    private static Result<IReadOnlyList<PageId>> SelectPages(string? pages, IReadOnlyList<PageId> allPages)
    {
        if (pages is null)
        {
            return Result<IReadOnlyList<PageId>>.Success(allPages);
        }

        Result<IReadOnlyList<(int Start, int End)>> parsed = ParsePageSelection(pages);
        if (parsed.IsFailure)
        {
            return Result<IReadOnlyList<PageId>>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        if (parsed.Value.Any(range => range.End > allPages.Count))
        {
            return Result<IReadOnlyList<PageId>>.Failure(AppErrorCodes.InvalidArgument,
                $"Page range '{pages.Trim()}' exceeds the document's {allPages.Count} physical page(s).");
        }

        List<PageId> selected = new();
        foreach ((int start, int end) in parsed.Value)
        {
            // Bounds are checked before expansion; zero-based indexing also avoids overflow
            // when the last valid one-based page is Int32.MaxValue.
            for (int index = start - 1; index < end; index++)
            {
                selected.Add(allPages[index]);
            }
        }

        return Result<IReadOnlyList<PageId>>.Success(selected);
    }

    private static Result<IReadOnlyList<(int Start, int End)>> ParsePageSelection(string pages)
    {
        if (pages.Length > MaxPagesLength)
        {
            return Result<IReadOnlyList<(int Start, int End)>>.Failure(AppErrorCodes.InvalidArgument,
                "The pages selector is too long.");
        }

        if (pages.Trim().Length == 0)
        {
            return Result<IReadOnlyList<(int Start, int End)>>.Failure(AppErrorCodes.InvalidArgument,
                "The pages selector must not be blank; omit it to OCR the whole document.");
        }

        List<(int Start, int End)> ranges = new();
        foreach (string rawToken in pages.Split(','))
        {
            string token = rawToken.Trim();
            if (token.Length == 0 || token.Any(character => !char.IsAsciiDigit(character) && character != '-'))
            {
                return Result<IReadOnlyList<(int Start, int End)>>.Failure(AppErrorCodes.InvalidArgument,
                    $"Invalid page range '{token}'. Use one-based physical pages like '1,3-5'.");
            }

            string[] bounds = token.Split('-');
            if (bounds.Length > 2 || !TryParsePage(bounds[0], out int start))
            {
                return Result<IReadOnlyList<(int Start, int End)>>.Failure(AppErrorCodes.InvalidArgument,
                    $"Invalid page range '{token}'. Use one-based physical pages like '1,3-5'.");
            }

            int end = start;
            if (bounds.Length == 2 && (!TryParsePage(bounds[1], out end) || end < start))
            {
                return Result<IReadOnlyList<(int Start, int End)>>.Failure(AppErrorCodes.InvalidArgument,
                    $"Invalid page range '{token}'. Use one-based physical pages like '1,3-5'.");
            }

            ranges.Add((start, end));
        }

        List<(int Start, int End)> merged = new();
        foreach ((int start, int end) in ranges.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (merged.Count > 0 && (long)start <= (long)merged[^1].End + 1)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return Result<IReadOnlyList<(int Start, int End)>>.Success(merged);
    }

    private static bool TryParsePage(string value, out int page)
    {
        return int.TryParse(value, out page) && page >= 1;
    }

    private static Result<string?> NormalizeRequestId(string? requestId)
    {
        string? normalized = requestId?.Trim();
        if (normalized is null || normalized.Length == 0)
        {
            return Result<string?>.Success(null);
        }

        return normalized.Length > MaxRequestIdLength
            ? Result<string?>.Failure(AppErrorCodes.InvalidArgument, "The request id is too long.")
            : Result<string?>.Success(normalized);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex EngineIdPattern();

    private sealed record TrackedRequest(string Fingerprint, McpOcrCommandResult Receipt);
}
