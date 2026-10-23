using System.Text.Json;
using Patchouli.Core.Actions;
using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public sealed partial class McpCommandService
{
    private readonly ActionRegistry _actions;

    private ActionRegistry CreateActionRegistry()
    {
        ActionRegistry registry = new();
        foreach (ActionDescriptor descriptor in RunEndpointCatalog.Messages)
        {
            registry.Register(descriptor, (call, ct) => RunEndpointCatalog.Uri("ocr") == descriptor.Endpoint
                ? ExecuteOcrActionAsync(descriptor.Id, call, ct)
                : RunEndpointCatalog.Uri("translation") == descriptor.Endpoint
                    ? ExecuteTranslationActionAsync(descriptor.Id, call, ct)
                    : ExecuteMetadataActionAsync(descriptor.Id, call, ct));
        }

        return registry;
    }

    public async Task<McpCommandResult<McpActionSendMeta, McpActionSendResult>> SendAsync(
        McpActionSendRequest request, CancellationToken cancellationToken = default)
    {
        string revision = await CurrentLibraryRevisionAsync(cancellationToken);
        string action = request.Action ?? string.Empty;
        string endpoint = McpResourceUris.Normalize(request.Endpoint);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> Fail(McpErrorCode code, string detail)
        {
            return ActionFailure(revision, endpoint, action, code, detail);
        }

        if (!_sendEnabled)
        {
            return Fail(McpErrorCode.PermissionDenied, "send is disabled by the host.");
        }

        Result<McpUriParseResult> address = McpResourceUris.Parse(request.Endpoint);
        if (address.IsFailure || address.Value.Kind != McpUriKind.RunEndpoint || string.IsNullOrWhiteSpace(action))
        {
            return Fail(McpErrorCode.InvalidArgument,
                "send requires endpoint/action/target/args; use runs/ocr/control start texts/<document-id>/, " +
                "runs/translation/control start texts/<document-id>/, or runs/metadata/control lookup items/<item-id>.yml.");
        }

        endpoint = address.Value.CanonicalUri;
        if (request.RequestId is not null &&
            (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Trim().Length > 256))
        {
            return Fail(McpErrorCode.InvalidArgument, "request_id must contain 1 through 256 non-blank characters.");
        }

        Result<McpUriParseResult> target = McpResourceUris.Parse(request.Target);
        if (target.IsFailure)
        {
            return Fail(McpErrorCode.InvalidArgument, target.ErrorMessage ?? "Invalid target.");
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
        if (!EndpointAllowed(permissions, endpoint))
        {
            return Fail(McpErrorCode.PermissionDenied, "Endpoint send permission is required.");
        }

        if (!EndpointAvailable(endpoint))
        {
            return Fail(McpErrorCode.Unavailable, "The endpoint service is unavailable on this host.");
        }

        ActionInvocation invocation = new(target.Value.CanonicalUri, target.Value.Kind.ToString(), request.Args,
            request.RequestId?.Trim());
        Result<ActionOutcome> outcome = await _actions.InvokeAsync(endpoint, action, invocation,
            (_, call) => EndpointAllowed(permissions, endpoint) && Allowed(permissions, "fetch", call.Target),
            cancellationToken);
        if (outcome.IsFailure)
        {
            return Fail(McpErrorMappings.ToOcrError(outcome.ErrorCode), outcome.ErrorMessage ?? "Message rejected.");
        }

        McpEnvelope<McpActionSendMeta, McpActionSendResult> envelope =
            McpEnvelope<McpActionSendMeta, McpActionSendResult>.Create(
                new McpActionSendMeta(revision, action, outcome.Value.State, endpoint),
                [new McpActionSendResult(outcome.Value.State, outcome.Value.Result, outcome.Value.RunUri)]);
        return McpCommandResult<McpActionSendMeta, McpActionSendResult>.Ok(envelope);
    }

    private static McpCommandResult<McpActionSendMeta, McpActionSendResult> ActionFailure(
        string revision, string endpoint, string action, McpErrorCode code, string detail)
    {
        return McpCommandResult<McpActionSendMeta, McpActionSendResult>.Partial(
            McpEnvelope<McpActionSendMeta, McpActionSendResult>.Create(
                new McpActionSendMeta(revision, action, "rejected", endpoint), [],
                message: new McpMessage(McpToolError.From(code, detail).ToTerminalLine(), [])), code, detail);
    }

    private bool EndpointAvailable(string endpoint)
    {
        if (endpoint == RunEndpointCatalog.Uri("ocr"))
        {
            return _ocrCommands is not null;
        }

        if (endpoint == RunEndpointCatalog.Uri("translation"))
        {
            return _translationRuns is not null;
        }

        return endpoint == RunEndpointCatalog.Uri("metadata") && _metadataRuns is not null;
    }

    private bool EndpointAllowed(McpServerSettings? permissions, string endpoint)
    {
        return _sendEnabled && permissions is not null && McpPermissionPolicy.Allows(permissions, "send", endpoint);
    }

    private bool EndpointVisible(McpServerSettings? permissions, string endpoint)
    {
        return EndpointAvailable(endpoint) && EndpointAllowed(permissions, endpoint);
    }

    private async Task<McpFetchResult> FetchEndpointDescriptionAsync(McpUriParseResult target, string? range,
        int limitBytes, McpLibraryStateResponse state, CancellationToken ct)
    {
        if (range is not null)
        {
            return FailedFetch(target.CanonicalUri,
                McpToolError.From(McpErrorCode.InvalidArgument, "Endpoint schemas do not accept ranges."), limitBytes);
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(ct);
        if (!EndpointAllowed(permissions, target.CanonicalUri))
        {
            return FailedFetch(target.CanonicalUri,
                McpToolError.From(McpErrorCode.PermissionDenied, "Endpoint send permission is required."), limitBytes);
        }

        if (!EndpointAvailable(target.CanonicalUri))
        {
            return FailedFetch(target.CanonicalUri,
                McpToolError.From(McpErrorCode.Unavailable, "Endpoint is unavailable on this host."), limitBytes);
        }

        var messages = _actions.Describe().Where(value => value.Endpoint == target.CanonicalUri)
            .OrderBy(value => value.Id)
            .Select(value => new
            {
                action = value.Id, target_types = value.TargetTypes, input_schema = value.InputSchema,
                result_schema = value.ResultSchema, execution_mode = value.Mode, side_effects = value.SideEffects,
                required_capabilities = value.RequiredCapabilities
            })
            .ToArray();
        string content;
        if (_implementationCatalog is not null && target.EndpointDomain is not null)
        {
            // Issue #28: the description also enumerates the domain's internal
            // implementations (OCR engines, translation extensions, metadata sources)
            // from the host's live registries, so an agent learns which ids it may
            // pass to this endpoint instead of guessing. A catalog failure fails the
            // fetch honestly instead of silently recreating the missing-list problem.
            Result<IReadOnlyList<McpImplementationEntry>> implementations =
                await _implementationCatalog.ListAsync(target.EndpointDomain, ct);
            if (implementations.IsFailure)
            {
                return FailedFetch(target.CanonicalUri,
                    McpToolError.From(McpErrorCode.Unavailable,
                        implementations.ErrorMessage ?? "The implementation catalog is unavailable."), limitBytes);
            }

            content = JsonSerializer.Serialize(new
            {
                endpoint = target.CanonicalUri, domain = target.EndpointDomain, messages,
                implementations = implementations.Value
            });
        }
        else
        {
            content = JsonSerializer.Serialize(new
            {
                endpoint = target.CanonicalUri, domain = target.EndpointDomain, messages
            });
        }

        McpFetchResult description = CompleteFetch(target.CanonicalUri, "endpoint", null, content, limitBytes) with
        {
            ContentType = "application/json"
        };
        // Include JSON escaping/envelope overhead: a truncated schema must never be a success.
        if (SerializedSize(description, state.LibraryRevision) > limitBytes)
        {
            return FailedFetch(target.CanonicalUri,
                McpToolError.From(McpErrorCode.ResponseTruncated,
                    "Endpoint schema exceeds limit_bytes; request a larger limit."), limitBytes);
        }

        return description;
    }

    private async Task<Result<ActionOutcome>> ExecuteOcrActionAsync(string action, ActionInvocation invocation,
        CancellationToken ct)
    {
        if (_ocrCommands is null)
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.UnsupportedOperation,
                "OCR commands are unavailable on this host.");
        }

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(invocation.Target);
        JsonElement args = invocation.Args;
        Result<McpOcrCommandResult> result;
        if (action == "start")
        {
            string? engine = StringArg(args, "engine");
            if (engine is not null && string.IsNullOrWhiteSpace(engine))
            {
                return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument, "engine must not be blank.");
            }

            result = await _ocrCommands.StartAsync(new McpOcrStartRequest(parsed.Value.DocumentId!.Value,
                StringArg(args, "pages"), engine ?? "default", invocation.RequestId), ct);
        }
        else
        {
            if (!Guid.TryParse(parsed.Value.RunTaskId, out _))
            {
                return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                    "OCR task identity must be a UUID.");
            }

            OcrQueueTaskId taskId = OcrQueueTaskId.Parse(parsed.Value.RunTaskId!);
            result = action == "cancel"
                ? await _ocrCommands.CancelAsync(taskId, ct)
                : await _ocrCommands.RetryAsync(taskId, invocation.RequestId, ct);
        }

        if (result.IsFailure)
        {
            return Result<ActionOutcome>.Failure(result.ErrorCode!,
                McpOutputSanitizer.Sanitize(result.ErrorMessage ?? "OCR failed."));
        }

        McpOcrCommandResult receipt = result.Value;
        return Result<ActionOutcome>.Success(new ActionOutcome(action == "cancel" ? "completed" : "started",
            new McpOcrResult(action, receipt.TaskId, receipt.StatusUri, receipt.State, receipt.EngineId,
                receipt.PresetId, receipt.PresetVersionId, receipt.Reused),
            action == "cancel" ? null : receipt.StatusUri));
    }

    private async Task<Result<ActionOutcome>> ExecuteTranslationActionAsync(string action, ActionInvocation invocation,
        CancellationToken ct)
    {
        if (_translationRuns is null)
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.UnsupportedOperation,
                "Translation runs are unavailable on this host.");
        }

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(invocation.Target);
        if (parsed.IsFailure)
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid target.");
        }

        if (action == "start")
        {
            JsonElement args = invocation.Args;
            Result<McpTranslationRunStatusProjection> started = await _translationRuns.StartAsync(
                new McpTranslationRunStartRequest(
                    parsed.Value.DocumentId!.Value,
                    StringArg(args, "pages"),
                    StringArg(args, "target_language"),
                    args.TryGetProperty("force", out JsonElement force) && force.ValueKind == JsonValueKind.True,
                    invocation.RequestId), ct);
            return ToTranslationOutcome(started, true);
        }

        if (!Guid.TryParse(parsed.Value.RunTaskId, out _))
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                "The translation run identity must be a UUID.");
        }

        Result<McpTranslationRunStatusProjection> cancelled =
            await _translationRuns.CancelAsync(parsed.Value.RunTaskId!, ct);
        return ToTranslationOutcome(cancelled, false);
    }

    private static Result<ActionOutcome> ToTranslationOutcome(
        Result<McpTranslationRunStatusProjection> result,
        bool start)
    {
        if (result.IsFailure)
        {
            return Result<ActionOutcome>.Failure(result.ErrorCode!,
                McpOutputSanitizer.Sanitize(result.ErrorMessage ?? "The translation run failed."));
        }

        McpTranslationRunStatusProjection run = result.Value;
        string statusUri = McpResourceUris.TranslationRunStatusUri(run.TaskId);
        return Result<ActionOutcome>.Success(new ActionOutcome(
            start ? "started" : "completed",
            new McpTranslationActionResult(run.TaskId, statusUri, run.State, run.Total, run.Reused),
            start ? statusUri : null));
    }

    private async Task<Result<ActionOutcome>> ExecuteMetadataActionAsync(string action, ActionInvocation invocation,
        CancellationToken ct)
    {
        if (_metadataRuns is null)
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.UnsupportedOperation,
                "Metadata runs are unavailable on this host.");
        }

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(invocation.Target);
        if (parsed.IsFailure)
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid target.");
        }

        JsonElement args = invocation.Args;
        switch (action)
        {
            case "lookup":
            {
                Result<McpMetadataProposalProjection> proposal = await _metadataRuns.LookupAsync(
                    parsed.Value.ItemId!.Value.ToString(), StringArg(args, "scheme") ?? string.Empty,
                    StringArg(args, "value") ?? string.Empty, ct);
                if (proposal.IsFailure)
                {
                    return Result<ActionOutcome>.Failure(proposal.ErrorCode!,
                        McpOutputSanitizer.Sanitize(proposal.ErrorMessage ?? "The metadata lookup failed."));
                }

                return Result<ActionOutcome>.Success(new ActionOutcome("completed", proposal.Value));
            }

            case "apply":
            {
                McpMetadataCandidateDto? candidate = DeserializeArg<McpMetadataCandidateDto>(args, "candidate");
                IReadOnlyList<McpMetadataAttemptDto>? attempts = DeserializeArg<McpMetadataAttemptDto[]>(args,
                    "attempts");
                if (candidate is null || attempts is null)
                {
                    return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                        "apply requires candidate and attempts arguments.");
                }

                Result<McpMetadataApplyProjection> applied = await _metadataRuns.ApplyAsync(
                    parsed.Value.ItemId!.Value.ToString(), StringArg(args, "expected_updated_at") ?? string.Empty,
                    candidate, attempts, ct);
                if (applied.IsFailure)
                {
                    return Result<ActionOutcome>.Failure(applied.ErrorCode!,
                        McpOutputSanitizer.Sanitize(applied.ErrorMessage ?? "The metadata apply failed."));
                }

                return Result<ActionOutcome>.Success(new ActionOutcome("completed", applied.Value));
            }

            case "enrich":
            {
                Result<McpMetadataApplyProjection> enriched = await _metadataRuns.EnrichAsync(
                    parsed.Value.ItemId!.Value.ToString(), ct);
                if (enriched.IsFailure)
                {
                    return Result<ActionOutcome>.Failure(enriched.ErrorCode!,
                        McpOutputSanitizer.Sanitize(enriched.ErrorMessage ?? "The metadata enrich failed."));
                }

                return Result<ActionOutcome>.Success(new ActionOutcome("completed", enriched.Value));
            }

            case "enrich_batch":
            {
                IReadOnlyList<string>? itemIds = DeserializeArg<string[]>(args, "item_ids");
                if (itemIds is null)
                {
                    return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                        "enrich_batch requires item_ids.");
                }

                Result<McpMetadataTaskStatusProjection> started =
                    await _metadataRuns.StartBatchAsync(itemIds, invocation.RequestId, ct);
                if (started.IsFailure)
                {
                    return Result<ActionOutcome>.Failure(started.ErrorCode!,
                        McpOutputSanitizer.Sanitize(started.ErrorMessage ?? "The metadata batch failed."));
                }

                McpMetadataTaskStatusProjection run = started.Value;
                string statusUri = McpResourceUris.MetadataRunStatusUri(run.TaskId);
                return Result<ActionOutcome>.Success(new ActionOutcome("started",
                    new McpMetadataBatchActionResult(run.TaskId, statusUri, run.State, run.Total, run.Reused),
                    statusUri));
            }

            default:
            {
                if (!Guid.TryParse(parsed.Value.RunTaskId, out _))
                {
                    return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                        "The metadata task identity must be a UUID.");
                }

                Result<McpMetadataTaskStatusProjection> task = action == "cancel"
                    ? await _metadataRuns.CancelAsync(parsed.Value.RunTaskId!, ct)
                    : await _metadataRuns.RetryAsync(parsed.Value.RunTaskId!, ct);
                if (task.IsFailure)
                {
                    return Result<ActionOutcome>.Failure(task.ErrorCode!,
                        McpOutputSanitizer.Sanitize(task.ErrorMessage ?? "The metadata task command failed."));
                }

                if (action == "cancel")
                {
                    return Result<ActionOutcome>.Success(new ActionOutcome("completed", task.Value));
                }

                string retryUri = McpResourceUris.MetadataRunStatusUri(task.Value.TaskId);
                return Result<ActionOutcome>.Success(new ActionOutcome("started",
                    new McpMetadataBatchActionResult(task.Value.TaskId, retryUri, task.Value.State, task.Value.Total,
                        task.Value.Reused), retryUri));
            }
        }
    }

    private static T? DeserializeArg<T>(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(value.GetRawText());
    }

    private static string? StringArg(JsonElement args, string key)
    {
        return args.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;
    }
}
