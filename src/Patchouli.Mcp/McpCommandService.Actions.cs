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
            registry.Register(descriptor, (call, ct) => descriptor.Endpoint == RunEndpointCatalog.Uri("ocr")
                ? ExecuteOcrActionAsync(descriptor.Id, call, ct)
                : ExecuteTranslationActionAsync(descriptor.Id, call, ct));
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
                "send requires endpoint/action/target/args; use runs/ocr/control start texts/<document-id>/.");
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
        return endpoint == RunEndpointCatalog.Uri("ocr")
            ? _ocrCommands is not null
            : endpoint == RunEndpointCatalog.Uri("translation") && _translationRuns is not null;
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

        string content = JsonSerializer.Serialize(new
        {
            endpoint = target.CanonicalUri, domain = target.EndpointDomain,
            messages = _actions.Describe().Where(value => value.Endpoint == target.CanonicalUri)
                .OrderBy(value => value.Id)
                .Select(value => new
                {
                    action = value.Id, target_types = value.TargetTypes, input_schema = value.InputSchema,
                    result_schema = value.ResultSchema, execution_mode = value.Mode, side_effects = value.SideEffects,
                    required_capabilities = value.RequiredCapabilities
                })
        });
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

    private static string? StringArg(JsonElement args, string key)
    {
        return args.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;
    }
}
