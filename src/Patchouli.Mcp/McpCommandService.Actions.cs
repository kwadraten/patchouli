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
        JsonElement receipt = Schema("""
                                     {"type":"object","required":["task_id","status_uri","state","engine_id","preset_id","preset_version_id","reused"],
                                      "properties":{"task_id":{"type":"string"},"status_uri":{"type":"string"},"state":{"type":"string"},
                                      "engine_id":{"type":"string"},"preset_id":{"type":"string"},"preset_version_id":{"type":"string"},"reused":{"type":"boolean"}}}
                                     """);
        registry.Register(new ActionDescriptor("ocr.start", [nameof(McpUriKind.Document)], Schema("""
                    {"type":"object","additionalProperties":false,"properties":{"pages":{"type":"string","minLength":1},
                     "engine":{"type":"string","minLength":1},"request_id":{"type":"string","minLength":1}}}
                    """), receipt, "started", ["ocr", "write_text", "rebuild_index", "engine_network"],
                ["texts:ocr.start"]),
            (invocation, ct) => ExecuteOcrActionAsync("ocr.start", invocation, ct));
        registry.Register(new ActionDescriptor("ocr.cancel", [nameof(McpUriKind.RunOcrStatus)],
                Schema("""{"type":"object","additionalProperties":false,"properties":{}}"""), receipt,
                "completed", ["cancel_ocr"], ["runs:ocr.cancel"]),
            (invocation, ct) => ExecuteOcrActionAsync("ocr.cancel", invocation, ct));
        registry.Register(new ActionDescriptor("ocr.retry", [nameof(McpUriKind.RunOcrStatus)], Schema("""
                {"type":"object","additionalProperties":false,"properties":{"request_id":{"type":"string","minLength":1}}}
                """), receipt, "started", ["ocr", "write_text", "rebuild_index", "engine_network"], ["runs:ocr.retry"]),
            (invocation, ct) => ExecuteOcrActionAsync("ocr.retry", invocation, ct));
        JsonElement translationReceipt = Schema("""
                                                {"type":"object","required":["task_id","status_uri","state","total_pages"],
                                                 "properties":{"task_id":{"type":"string"},"status_uri":{"type":"string"},
                                                 "state":{"type":"string"},"total_pages":{"type":"integer"}}}
                                                """);
        registry.Register(new ActionDescriptor("translation.start", [nameof(McpUriKind.Document)], Schema("""
                    {"type":"object","additionalProperties":false,"properties":{"pages":{"type":"string"},
                     "target_language":{"type":"string"},"force":{"type":"boolean"},"request_id":{"type":"string"}}}
                    """), translationReceipt, "started", ["translation", "write_text", "engine_network"],
                ["texts:translation.start"]),
            (invocation, ct) => ExecuteTranslationActionAsync("translation.start", invocation, ct));
        registry.Register(new ActionDescriptor("translation.cancel", [nameof(McpUriKind.RunTranslationStatus)],
                Schema("""{"type":"object","additionalProperties":false,"properties":{}}"""), translationReceipt,
                "completed", ["cancel_translation"], ["runs:translation.cancel"]),
            (invocation, ct) => ExecuteTranslationActionAsync("translation.cancel", invocation, ct));
        registry.Register(new ActionDescriptor("cite.render",
                [
                    nameof(McpUriKind.Item), nameof(McpUriKind.Document), nameof(McpUriKind.Page),
                    nameof(McpUriKind.Evidence)
                ],
                Schema("""
                       {"type":"object","additionalProperties":false,"properties":{
                       "refs":{"type":"array","minItems":1,"items":{"type":"string"}},
                        "style":{"type":"string","minLength":1},"locale":{"type":"string","minLength":1},
                        "bibliography":{"type":"boolean"},"html":{"type":"boolean"}}}
                       """), Schema("""
                                    {"type":"object","required":["meta","entries","continuation"],"properties":{
                                    "meta":{"type":"object","required":["library_revision","effective_style_uri","effective_locale","render_format","bibliography"],
                                    "properties":{"library_revision":{"type":"string"},"effective_style_uri":{"type":["string","null"]},
                                    "effective_locale":{"type":"string"},"render_format":{"type":"string","enum":["text","html"]},"bibliography":{"type":["string","null"]}}},
                                    "entries":{"type":"array","items":{"type":"object","required":["ref","item_uri","citation","error"],
                                    "properties":{"ref":{"type":"string"},"item_uri":{"type":["string","null"]},"citation":{"type":["string","null"]},"error":{"type":["string","null"]}}}},
                                    "continuation":{"type":"null"},"message":{"type":"object","required":["error","warnings"],
                                    "properties":{"error":{"type":["string","null"]},"warnings":{"type":"array","items":{"type":"string"}}}}}}
                                    """), "completed", ["read_library"],
                ["refs:cite", "items:cite", "csl-styles:cite"]),
            RenderCitationActionAsync);
        return registry;
    }

    private static JsonElement Schema(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    public async Task<McpCommandResult<McpActionSendMeta, McpActionSendResult>> SendAsync(
        McpActionSendRequest request, CancellationToken cancellationToken = default)
    {
        string revision = await CurrentLibraryRevisionAsync(cancellationToken);
        string action = request.Action ?? string.Empty;
        if (!_sendEnabled)
        {
            return ActionFailure(revision, action, McpErrorCode.PermissionDenied, "send is disabled by the host.");
        }

        if (string.IsNullOrWhiteSpace(request.Target) || string.IsNullOrWhiteSpace(action))
        {
            return ActionFailure(revision, action, McpErrorCode.InvalidArgument, "send requires target/action/args.");
        }

        Result<McpUriParseResult> target = McpResourceUris.Parse(request.Target);
        if (target.IsFailure)
        {
            return ActionFailure(revision, action, McpErrorCode.InvalidArgument,
                target.ErrorMessage ?? "Invalid target.");
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
        ActionInvocation invocation = new(target.Value.CanonicalUri, target.Value.Kind.ToString(), request.Args);
        Result<ActionOutcome> outcome = await _actions.InvokeAsync(action, invocation,
            (descriptor, call) => SendToolAllowed(permissions) && ActionAllowed(descriptor, permissions, call.Target),
            cancellationToken);
        if (outcome.IsFailure)
        {
            return ActionFailure(revision, action, McpErrorMappings.ToOcrError(outcome.ErrorCode),
                outcome.ErrorMessage ?? "Action failed.");
        }

        object? payload = outcome.Value.Result;
        McpToolError? error = null;
        if (payload is McpCommandResult<McpCiteMeta, McpCitationResult> citation)
        {
            citation = WithLegacyWarnings(citation, [request.Target],
                new McpCiteMeta(revision, null, null, "text", null));
            if (citation.Envelope is null)
            {
                return ActionFailure(revision, action, (McpErrorCode)(citation.Error?.Code ?? 1),
                    citation.Error?.Detail ?? "Citation rendering failed.");
            }

            payload = citation.Envelope;
            error = citation.Error;
        }

        McpActionSendResult entry = new(outcome.Value.State, payload, outcome.Value.RunUri);
        McpEnvelope<McpActionSendMeta, McpActionSendResult> envelope =
            McpEnvelope<McpActionSendMeta, McpActionSendResult>.Create(
                new McpActionSendMeta(revision, action, outcome.Value.State), [entry],
                message: error is null ? null : new McpMessage(error.ToTerminalLine(), []));
        return error is null
            ? McpCommandResult<McpActionSendMeta, McpActionSendResult>.Ok(envelope)
            : McpCommandResult<McpActionSendMeta, McpActionSendResult>.Partial(envelope,
                (McpErrorCode)error.Code, error.Detail ?? error.Name);
    }

    private static McpCommandResult<McpActionSendMeta, McpActionSendResult> ActionFailure(
        string revision, string action, McpErrorCode code, string detail)
    {
        McpEnvelope<McpActionSendMeta, McpActionSendResult> envelope =
            McpEnvelope<McpActionSendMeta, McpActionSendResult>.Create(
                new McpActionSendMeta(revision, action, "rejected"), [],
                message: new McpMessage(McpToolError.From(code, detail).ToTerminalLine(), []));
        return McpCommandResult<McpActionSendMeta, McpActionSendResult>.Partial(envelope, code, detail);
    }

    private bool ActionAllowed(ActionDescriptor descriptor, McpServerSettings? permissions, string target)
    {
        if (descriptor.Id == "cite.render")
        {
            return Allowed(permissions, "cite", target) && Allowed(permissions, "cite", "patchouli://items/") &&
                   Allowed(permissions, "cite", "patchouli://csl-styles/");
        }

        return _sendEnabled && permissions is not null &&
               McpPermissionPolicy.Allows(permissions, descriptor.Id, target);
    }

    private bool SendToolAllowed(McpServerSettings? permissions)
    {
        return _sendEnabled &&
               (permissions is null ||
                !permissions.ToolOverrides.Any(value => value.ToolName == "patchouli.send" && !value.Enabled));
    }

    private IReadOnlyList<ActionDescriptor> VisibleActions(McpServerSettings? permissions)
    {
        return _actions.Describe().Where(descriptor => descriptor.Id == "cite.render"
            ? new[] { "items", "texts" }.Any(domain =>
                ActionAllowed(descriptor, permissions, "patchouli://" + domain + "/"))
            : ActionServiceAvailable(descriptor) && _sendEnabled &&
              descriptor.RequiredCapabilities.Any(capability =>
              {
                  string domain = capability.Split(':')[0];
                  return SendToolAllowed(permissions) &&
                         ActionAllowed(descriptor, permissions, "patchouli://" + domain + "/");
              })).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    ///     Whether the command service an Action needs is available on this host. OCR verbs need
    ///     <see cref="_ocrCommands" />, translation verbs need <see cref="_translationRuns" />; other
    ///     verbs have no optional command-service dependency.
    /// </summary>
    private bool ActionServiceAvailable(ActionDescriptor descriptor)
    {
        foreach (string capability in descriptor.RequiredCapabilities)
        {
            string verb = capability.Split(':').Last();
            if (verb.StartsWith("ocr.", StringComparison.Ordinal) && _ocrCommands is null)
            {
                return false;
            }

            if (verb.StartsWith("translation.", StringComparison.Ordinal) && _translationRuns is null)
            {
                return false;
            }
        }

        return true;
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
        if (action == "ocr.start")
        {
            string? engine = StringArg(args, "engine");
            if (engine is not null && string.IsNullOrWhiteSpace(engine))
            {
                return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument, "engine must not be blank.");
            }

            result = await _ocrCommands.StartAsync(new McpOcrStartRequest(parsed.Value.DocumentId!.Value,
                StringArg(args, "pages"), engine ?? "default", StringArg(args, "request_id")), ct);
        }
        else
        {
            if (!Guid.TryParse(parsed.Value.RunTaskId, out _))
            {
                return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                    "OCR task identity must be a UUID.");
            }

            OcrQueueTaskId taskId = OcrQueueTaskId.Parse(parsed.Value.RunTaskId!);
            result = action == "ocr.cancel"
                ? await _ocrCommands.CancelAsync(taskId, ct)
                : await _ocrCommands.RetryAsync(taskId, StringArg(args, "request_id"), ct);
        }

        if (result.IsFailure)
        {
            return Result<ActionOutcome>.Failure(result.ErrorCode!,
                McpOutputSanitizer.Sanitize(result.ErrorMessage ?? "OCR failed."));
        }

        McpOcrCommandResult receipt = result.Value;
        return Result<ActionOutcome>.Success(new ActionOutcome(action == "ocr.cancel" ? "completed" : "started",
            new McpOcrResult(action, receipt.TaskId, receipt.StatusUri, receipt.State, receipt.EngineId,
                receipt.PresetId, receipt.PresetVersionId, receipt.Reused),
            action == "ocr.cancel" ? null : receipt.StatusUri));
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

        if (action == "translation.start")
        {
            JsonElement args = invocation.Args;
            Result<McpTranslationRunStatusProjection> started = await _translationRuns.StartAsync(
                new McpTranslationRunStartRequest(
                    parsed.Value.DocumentId!.Value,
                    StringArg(args, "pages"),
                    StringArg(args, "target_language"),
                    args.TryGetProperty("force", out JsonElement force) && force.ValueKind == JsonValueKind.True,
                    StringArg(args, "request_id")), ct);
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
            new McpTranslationActionResult(run.TaskId, statusUri, run.State, run.Total),
            start ? statusUri : null));
    }

    private static string? StringArg(JsonElement args, string key)
    {
        return args.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;
    }

    private async Task<Result<ActionOutcome>> RenderCitationActionAsync(ActionInvocation invocation,
        CancellationToken ct)
    {
        JsonElement args = invocation.Args;
        string[] refs = args.TryGetProperty("refs", out JsonElement values)
            ? values.EnumerateArray().Select(value => value.GetString()!).ToArray()
            : [invocation.Target];
        if (!refs.Any(value => McpResourceUris.Normalize(value) == invocation.Target))
        {
            return Result<ActionOutcome>.Failure(AppErrorCodes.InvalidArgument,
                "cite.render refs must contain target.");
        }

        McpCommandResult<McpCiteMeta, McpCitationResult> result = await CiteCoreAsync(new McpCiteRequest(refs,
            StringArg(args, "style"), StringArg(args, "locale"),
            args.TryGetProperty("bibliography", out JsonElement bibliography) && bibliography.GetBoolean(),
            args.TryGetProperty("html", out JsonElement html) && html.GetBoolean()), ct);
        result = WithLegacyWarnings(result, refs, new McpCiteMeta("lib:0", null, null, "text", null));
        return Result<ActionOutcome>.Success(new ActionOutcome("completed", result));
    }

    private async Task<McpCommandResult<McpCiteMeta, McpCitationResult>> CiteThroughRegistryAsync(
        McpCiteRequest request, CancellationToken ct)
    {
        // The dedicated read entry shares the registered handler, without requiring send.
        Dictionary<string, object> args = new()
            { ["refs"] = request.Refs ?? [], ["bibliography"] = request.Bibliography, ["html"] = request.Html };
        if (request.Style is not null)
        {
            args["style"] = request.Style;
        }

        if (request.Locale is not null)
        {
            args["locale"] = request.Locale;
        }

        string target = request.Refs?.FirstOrDefault() ?? "patchouli://items/";
        Result<ActionOutcome> result = await _actions.InvokeAsync("cite.render",
            new ActionInvocation(McpResourceUris.Normalize(target), nameof(McpUriKind.Item),
                JsonSerializer.SerializeToElement(args)),
            (_, _) => true, ct);
        if (result.IsFailure)
        {
            return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(McpErrorMappings.ToReadError(result.ErrorCode),
                result.ErrorMessage!);
        }

        return (McpCommandResult<McpCiteMeta, McpCitationResult>)result.Value.Result!;
    }

    private async Task<McpFetchResult> FetchActionDescriptionAsync(McpUriParseResult target, string? range,
        int limitBytes, McpLibraryStateResponse state, CancellationToken ct)
    {
        if (range is not null)
        {
            return FailedFetch(target.CanonicalUri,
                McpToolError.From(McpErrorCode.InvalidArgument, "Action schemas do not accept ranges."), limitBytes);
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(ct);
        IReadOnlyList<ActionDescriptor> visible = VisibleActions(permissions);
        ActionDescriptor? descriptor = target.Kind == McpUriKind.Action ? _actions.Find(target.ActionId!) : null;
        if (target.Kind == McpUriKind.Action && descriptor is null)
        {
            return FailedFetch(target.CanonicalUri, McpToolError.From(McpErrorCode.NotFound, "Action was not found."),
                limitBytes);
        }

        if (descriptor is not null && !visible.Any(value => value.Id == descriptor.Id))
        {
            return FailedFetch(target.CanonicalUri,
                McpToolError.From(McpErrorCode.PermissionDenied, "Action is not authorized."), limitBytes);
        }

        object Projection(ActionDescriptor value)
        {
            return new
            {
                action_id = value.Id, target_types = value.TargetTypes,
                input_schema = value.InputSchema, result_schema = value.ResultSchema, execution_mode = value.Mode,
                side_effects = value.SideEffects, required_capabilities = value.RequiredCapabilities
            };
        }

        string content =
            JsonSerializer.Serialize(descriptor is null
                ? visible.Select(Projection).ToArray()
                : Projection(descriptor));
        if (System.Text.Encoding.UTF8.GetByteCount(content) > limitBytes)
        {
            return FailedFetch(target.CanonicalUri,
                McpToolError.From(McpErrorCode.ResponseTruncated,
                    "Action schema exceeds limit_bytes; request a larger limit."), limitBytes);
        }

        return FitTextEntry(target.CanonicalUri, "action", null, content, limitBytes, state.LibraryRevision);
    }
}
