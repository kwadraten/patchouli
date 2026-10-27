using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr;

/// <summary>
/// Deep <see cref="IOcrRunCoordinator"/> façade: every awaitable run is enqueued on the shared
/// <see cref="IOcrQueueScheduler"/> and awaited until the queue task reaches a terminal state.
/// </summary>
public sealed class QueuedOcrRunCoordinator : IOcrRunCoordinator
{
    private readonly IOcrRunEngine _engine;
    private readonly Func<string>? _multimodalProviderId;
    private readonly IOcrEngineCatalog? _engineCatalog;

    public QueuedOcrRunCoordinator(IOcrQueueScheduler queue, IOcrRunEngine engine,
        Func<string>? multimodalProviderId = null, IOcrEngineCatalog? engineCatalog = null)
    {
        Queue = queue;
        _engine = engine;
        _multimodalProviderId = multimodalProviderId;
        _engineCatalog = engineCatalog;
        _engine.CommitCompleted += (_, args) => CommitCompleted?.Invoke(this, args);
    }

    public IOcrQueueScheduler Queue { get; }

    /// <inheritdoc />
    public event EventHandler<OcrCommitCompletedEventArgs>? CommitCompleted;

    public async Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        string engineId,
        string adapterKind,
        string? providerId,
        string priority,
        CancellationToken cancellationToken = default)
    {
        // Freeze the preset version at accept time: the task must execute the version that was
        // current when it was accepted even if the preset changes while it waits.
        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrQueueTask>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        // The caller's routing identity must agree with the frozen version; scheduling under one
        // engine's resource lane while executing another would corrupt queue classification.
        // Rather than silently switching the caller's choices, disagreements fail closed.
        (string expectedAdapter, string? expectedProvider) =
            await RoutingForAsync(version.Value, cancellationToken);
        if (engineId != version.Value.EngineId || adapterKind != expectedAdapter ||
            (providerId is not null && providerId != expectedProvider))
        {
            return Result<OcrQueueTask>.Failure(AppErrorCodes.InvalidArgument,
                $"OCR routing '{engineId}/{adapterKind}/{providerId ?? "-"}' does not match the " +
                $"frozen preset version '{version.Value.EngineId}/{expectedAdapter}/{expectedProvider ?? "-"}'.");
        }

        Result<OcrQueueTask> queued = await Queue.EnqueueAsync(
            new OcrQueueTaskRequest(documentInstanceId, presetId, pageIds, OcrQueueTaskKind.Document,
                version.Value.EngineId, expectedAdapter, providerId ?? expectedProvider, priority,
                PresetVersionId: version.Value.PresetVersionId),
            cancellationToken);
        if (queued.IsSuccess)
        {
            await Queue.StartAsync(cancellationToken);
        }

        return queued;
    }

    public async Task<Result<OcrRun>> RunPresetOnDocumentAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        Result<IReadOnlyList<PageId>> pages = await _engine.ListPageIdsAsync(documentInstanceId, cancellationToken);
        if (pages.IsFailure)
        {
            return Result<OcrRun>.Failure(pages.ErrorCode!, pages.ErrorMessage!);
        }

        if (pages.Value.Count == 0)
        {
            return Result<OcrRun>.Failure(AppErrorCodes.ValidationFailed,
                "Document instance has no pages to OCR.");
        }

        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        (string adapter, string? provider) = await RoutingForAsync(version.Value, cancellationToken);
        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueAsync(
                new OcrQueueTaskRequest(documentInstanceId, presetId, pages.Value, OcrQueueTaskKind.Document,
                    version.Value.EngineId, adapter, provider,
                    OcrQueuePriority.UserStartedDocument, PresetVersionId: version.Value.PresetVersionId,
                    Source: OcrQueueSource.UiDocument),
                cancellationToken),
            cancellationToken);
    }

    public async Task<Result<OcrRun>> RunPresetOnPagesAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        IReadOnlyList<PageId> pageIds,
        CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        (string adapter, string? provider) = await RoutingForAsync(version.Value, cancellationToken);
        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueAsync(
                new OcrQueueTaskRequest(documentInstanceId, presetId, pageIds, OcrQueueTaskKind.Pages,
                    version.Value.EngineId, adapter, provider,
                    PriorityFor(pageIds), PresetVersionId: version.Value.PresetVersionId,
                    Source: OcrQueueSource.UiPages),
                cancellationToken),
            cancellationToken);
    }

    public async Task<Result<OcrRun>> RunPresetOnRegionAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default)
    {
        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        (string adapter, string? provider) = await RoutingForAsync(version.Value, cancellationToken);
        return await EnqueueAndAwaitAsync(
            queue => queue.EnqueueAsync(
                new OcrQueueTaskRequest(documentInstanceId, presetId, [pageId], OcrQueueTaskKind.Region,
                    version.Value.EngineId, adapter, provider,
                    OcrQueuePriority.InteractiveCurrentPage, RegionBBox: regionBBox,
                    CommitOnCompletion: false, PresetVersionId: version.Value.PresetVersionId,
                    Source: OcrQueueSource.UiRegion),
                cancellationToken),
            cancellationToken);
    }

    public async Task<Result<OcrRun>> RunWorkingOnPageAsync(
        DocumentInstanceId documentInstanceId, OcrPresetId presetId, PageId pageId,
        CancellationToken cancellationToken = default)
    {
        Result<OcrPresetVersion> version = await _engine.ResolvePresetVersionAsync(presetId, cancellationToken);
        if (version.IsFailure)
        {
            return Result<OcrRun>.Failure(version.ErrorCode!, version.ErrorMessage!);
        }

        (string adapter, string? provider) = await RoutingForAsync(version.Value, cancellationToken);
        return await EnqueueAndAwaitAsync(queue => queue.EnqueueAsync(
                new OcrQueueTaskRequest(documentInstanceId, presetId, [pageId], OcrQueueTaskKind.Pages,
                    version.Value.EngineId, adapter, provider,
                    OcrQueuePriority.InteractiveCurrentPage, CommitOnCompletion: false,
                    PresetVersionId: version.Value.PresetVersionId, Source: OcrQueueSource.UiWorking),
                cancellationToken),
            cancellationToken);
    }

    public Task<Result> DiscardWorkingRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return _engine.DiscardWorkingRunAsync(runId, cancellationToken);
    }

    public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(
        DocumentInstanceId documentInstanceId,
        OcrPresetId presetId,
        PageId pageId,
        NormalizedBBox regionBBox,
        CancellationToken cancellationToken = default)
    {
        // Region candidates are ephemeral: the engine persists no run for them, so there is no
        // status to track and queueing is not required for status correctness. The facade still
        // owns a real background boundary for synchronous adapters and post-HTTP parsing.
        return Task.Run(() => _engine.RecognizeRegionCandidateAsync(documentInstanceId, presetId, pageId,
            regionBBox, cancellationToken), cancellationToken);
    }

    public Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return _engine.CancelRunAsync(runId, cancellationToken);
    }

    public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        return _engine.UnsetCurrentOcrAsync(documentInstanceId, cancellationToken);
    }

    public Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return _engine.HideOcrRunAsync(runId, cancellationToken);
    }

    public Task<Result<OcrCandidateCommit>> CommitCandidateRunAsync(OcrRunId runId,
        IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default,
        IProgress<OcrTaskStageProgress>? progress = null)
    {
        return _engine.CommitCandidateRunAsync(runId, selectedPages, cancellationToken, progress);
    }

    public Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
    {
        return _engine.GetRunAsync(runId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId,
        CancellationToken cancellationToken = default)
    {
        return _engine.ListPageResultsAsync(runId, cancellationToken);
    }

    private async Task<Result<OcrRun>> EnqueueAndAwaitAsync(
        Func<IOcrQueueScheduler, Task<Result<OcrQueueTask>>> enqueue,
        CancellationToken cancellationToken)
    {
        Result<OcrQueueTask> queued = await enqueue(Queue);
        if (queued.IsFailure)
        {
            return Result<OcrRun>.Failure(queued.ErrorCode!, queued.ErrorMessage!);
        }

        OcrQueueTaskId taskId = queued.Value.TaskId;
        TaskCompletionSource<OcrQueueTask> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<OcrQueueChangedEventArgs> handler = (_, args) =>
        {
            if (args.Task is { } task && task.TaskId == taskId && IsTerminal(task.State))
            {
                completion.TrySetResult(task);
            }
        };

        Queue.Changed += handler;
        try
        {
            await Queue.StartAsync(cancellationToken);
            Result<OcrQueueTask> current = await Queue.GetTaskAsync(taskId, cancellationToken);
            if (current.IsSuccess && IsTerminal(current.Value.State))
            {
                completion.TrySetResult(current.Value);
            }

            OcrQueueTask terminal;
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                try
                {
                    terminal = await completion.Task;
                }
                catch (OperationCanceledException)
                {
                    await Queue.CancelTaskAsync(taskId, CancellationToken.None);
                    return Result<OcrRun>.Failure(OcrFailureCode.Cancelled, "OCR run was cancelled.");
                }
            }

            return terminal.State switch
            {
                OcrQueueTaskState.Succeeded => terminal.RunId is null
                    ? Result<OcrRun>.Failure(AppErrorCodes.InvalidState,
                        "OCR queue task succeeded without a run id.")
                    : await _engine.GetRunAsync(terminal.RunId.Value, cancellationToken),
                OcrQueueTaskState.Cancelled => Result<OcrRun>.Failure(OcrFailureCode.Cancelled,
                    "OCR run was cancelled."),
                _ => Result<OcrRun>.Failure(terminal.LastErrorCode ?? "ocr_failed",
                    terminal.LastErrorMessage ?? "OCR task failed.")
            };
        }
        finally
        {
            Queue.Changed -= handler;
        }
    }

    private static bool IsTerminal(string state)
    {
        return state is OcrQueueTaskState.Succeeded or OcrQueueTaskState.Failed or OcrQueueTaskState.Cancelled
            or OcrQueueTaskState.Blocked;
    }

    private static string PriorityFor(IReadOnlyList<PageId> pageIds)
    {
        return pageIds.Count <= 1
            ? OcrQueuePriority.InteractiveCurrentPage
            : OcrQueuePriority.InteractiveSelectedPages;
    }

    /// <summary>
    /// Queue lane resolution from frozen inputs only: the registered engine definition supplies
    /// the adapter kind, and the provider identity comes from the frozen configuration or the
    /// definition's declared default — there is no per-engine routing switch. MinerU's intrinsic
    /// cloud provider remains the one built-in exception inside <see cref="OcrQueueRouting"/>.
    /// </summary>
    private async Task<(string AdapterKind, string? ProviderId)> RoutingForAsync(
        OcrPresetVersion version, CancellationToken cancellationToken)
    {
        OcrEngineDefinition? definition = null;
        if (_engineCatalog is not null)
        {
            Result<OcrEngineDefinition> resolved =
                await _engineCatalog.GetDefinitionAsync(version.EngineId, cancellationToken);
            if (resolved.IsSuccess)
            {
                definition = resolved.Value;
            }
        }

        return OcrQueueRouting.ForEngine(version.EngineId, definition, ResolveProvider(version, definition));
    }

    private string? ResolveProvider(OcrPresetVersion version, OcrEngineDefinition? definition)
    {
        string? provider = ReadProviderKey(version.ParametersJson);
        if (provider is null && definition?.DefaultConfig is { } defaults)
        {
            if (!defaults.TryGetValue("provider", out provider))
            {
                defaults.TryGetValue("llm-provider", out provider);
            }
        }

        // A cloud engine that needs credentials but names no provider falls back to the
        // configured LLM provider identity — still configuration-derived, not engine-id-driven.
        if (provider is null && definition is { RequiresCredential: true, AdapterKind: "cloud_api" })
        {
            provider = _multimodalProviderId?.Invoke();
        }

        return provider;
    }

    /// <summary>Reads a pinned provider id out of the frozen preset parameters envelope.</summary>
    private static string? ReadProviderKey(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return null;
        }

        try
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(parametersJson);
            System.Text.Json.JsonElement root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            foreach (string key in new[] { "provider", "llm-provider", "llmProvider" })
            {
                if (root.TryGetProperty(key, out System.Text.Json.JsonElement value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }

            foreach (string envelope in new[] { "_extension", "extension", "_llm", "llm" })
            {
                if (!root.TryGetProperty(envelope, out System.Text.Json.JsonElement section) ||
                    section.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    continue;
                }

                System.Text.Json.JsonElement values =
                    section.TryGetProperty("values", out System.Text.Json.JsonElement nested) &&
                    nested.ValueKind == System.Text.Json.JsonValueKind.Object
                        ? nested
                        : section;
                foreach (string key in new[] { "provider", "llm-provider", "llmProvider" })
                {
                    if (values.TryGetProperty(key, out System.Text.Json.JsonElement value) &&
                        value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString();
                    }
                }
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
