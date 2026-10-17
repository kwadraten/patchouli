using Microsoft.FSharp.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.FSharp.Core;
using Patchouli.Agent;
using Patchouli.Core.Results;
using Patchouli.Llm;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Host.Agent;

/// <summary>
///     Everything the interpreter needs to execute one effect for a session: the conversation key
///     (the session id), the immutable instruction prefix, the stable tool definitions and the
///     recorded append-only history.
/// </summary>
/// <param name="SessionId">Conversation key; also the session whose history is being sent.</param>
/// <param name="Instructions">The immutable instruction head of the deterministic prefix.</param>
/// <param name="ToolDefinitions">Stable tool definitions in their recorded order.</param>
/// <param name="History">The recorded append-only history, in recorded order.</param>
public sealed record AgentEffectContext(
    string SessionId,
    string Instructions,
    IReadOnlyList<string> ToolDefinitions,
    IReadOnlyList<HistoryEntry> History)
{
    /// <summary>Stable history and FSI workspace partition within the owning audit session.</summary>
    public string ContextId { get; init; } = SessionId;

    /// <summary>Stable agent invocation identity, frozen across request retries and recovery.</summary>
    public string ActivationId { get; init; } = "main";

    /// <summary>Provider conversation identity for prefix caching, independent of the audit session id.</summary>
    public string ConversationKey => SessionId + "/context/" + ContextId + "/activation/" + ActivationId;

    /// <summary>Explicit model frozen into this workflow session; null selects the current chat model.</summary>
    public ModelSelection? ModelSelection { get; init; }

    /// <summary>Launch-parameter key used to persist the workflow model selection.</summary>
    public const string ModelSelectionParameter = "__workflow.modelSelection";

    public bool UsesTextToolProtocol { get; init; }
    public string? SessionDirectory { get; init; }

    /// <summary>
    /// Issue #11: resolves a session media id to its image bytes for model messages.
    /// Set when the session has a media directory; null keeps history text-only. The
    /// model adapter converts resolved images to <c>LlmImagePart</c> data-URIs — real
    /// vision input, never a path or base64 text.
    /// </summary>
    public Func<string, AgentSessionImageData?>? ResolveSessionImage { get; init; }

    public AgentSdkPolicy? SdkPolicy { get; init; }
    public Func<AgentSdkReceipt, Task>? RecordSdk { get; init; }
    public int ToolResultMaxCharacters { get; init; } = LlmAppSettings.DefaultToolResultMaxCharacters;
    public IReadOnlyList<HistoryEntry>? ModelHistory { get; init; }
    public int RequestRetryAttempt { get; init; }

    public int HistoryPageCharacters { get; init; } = 16000;
}

/// <summary>How one executed effect concluded.</summary>
public enum AgentEffectDisposition
{
    /// <summary>The effect produced a result <c>Event</c> to feed back at the next boundary.</summary>
    Completed,

    /// <summary>The effect produced no event yet; the session waits for a host control event.</summary>
    Deferred,

    /// <summary>The effect is terminal (<c>Finish</c> / <c>Stop</c>) and changes the run status.</summary>
    Terminal
}

/// <summary>
///     The outcome of one effect: the <c>Event</c> to feed back, whether the effect entered an
///     atomic commit point (so cancellation must not revoke it), and a short summary for the log.
/// </summary>
/// <param name="Disposition">Whether a result event exists, the effect is waiting, or it is terminal.</param>
/// <param name="ResultEvent">The event to feed back at the next boundary (null when not applicable).</param>
/// <param name="CommitPointEntered">True once an atomic <c>put</c> has started.</param>
/// <param name="TerminalStatus">Terminal run status for <see cref="AgentEffectDisposition.Terminal" />.</param>
/// <param name="Summary">Terminal-style summary recorded in the session event log.</param>
public sealed record AgentEffectOutcome(
    AgentEffectDisposition Disposition,
    Event? ResultEvent,
    bool CommitPointEntered,
    AgentSessionStatus? TerminalStatus,
    string Summary)
{
    /// <summary>Structured host failure; tool failures still return to the agent for recovery.</summary>
    public string? FailureCode { get; init; }

    public IReadOnlyList<AgentSdkReceipt> Receipts { get; init; } = [];

    /// <summary>A completed effect carrying the event to feed back.</summary>
    public static AgentEffectOutcome Completed(Event resultEvent, string summary, bool commitPointEntered = false)
    {
        return new AgentEffectOutcome(AgentEffectDisposition.Completed, resultEvent, commitPointEntered, null,
            summary);
    }

    /// <summary>A terminal effect (Finish or Stop).</summary>
    public static AgentEffectOutcome Terminal(AgentSessionStatus status, string summary)
    {
        return new AgentEffectOutcome(AgentEffectDisposition.Terminal, null, false, status, summary);
    }
}

/// <summary>
///     Executes the effects the pure core issues. This is the only place where an effect becomes
///     I/O, and the mapping is defined per <c>Effect</c> case (ADR 0036 revocability table) rather
///     than by ad-hoc special-casing.
/// </summary>
public interface IAgentEffectInterpreter
{
    /// <summary>Executes one effect and reports what to feed back.</summary>
    Task<AgentEffectOutcome> ExecuteAsync(AgentEffectContext context, Effect effect,
        CancellationToken cancellationToken);

    /// <summary>Validates the selected model before a workflow session is persisted.</summary>
    Task<string?> ValidateModelSelectionAsync(ModelSelection? selection, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }
}

/// <summary>
///     The production effect interpreter:
///     <list type="table">
///         <item>
///             <term><c>LlmChat</c></term>
///             <description>
///                 <see cref="ILlmChatClient.CompleteAsync" /> over the append-only prefix; in-flight
///                 cancellation is immediate and consumed tokens are not returned.
///             </description>
///         </item>
///         <item>
///             <term><c>McpToolCall</c></term>
///             <description>
///                 <see cref="IAgentMcpGateway.CallToolAsync" />; revocable until execution starts,
///                 never revoked once started.
///             </description>
///         </item>
///         <item>
///             <term><c>Put</c></term>
///             <description>
///                 <see cref="IAgentMcpGateway.PutAsync" />; executed without a cancellation token
///                 because the atomic commit point, once entered, is never bypassed (ADR 0024).
///             </description>
///         </item>
///         <item>
///             <term><c>OcrEnqueue</c>, <c>WaitRunEvent</c>, <c>ReportProgress</c></term>
///             <description>Host primitives; the OCR wakeup is the S4 extension point.</description>
///         </item>
///         <item>
///             <term><c>Finish</c>, <c>Stop</c></term>
///             <description>Terminal: the host applies the status at the boundary.</description>
///         </item>
///     </list>
/// </summary>
public sealed class AgentEffectInterpreter : IAgentEffectInterpreter
{
    private readonly IAgentLlmClientProvider _llm;
    private readonly IAgentMcpGateway _mcp;
    private readonly IAgentHostPrimitives _host;
    private readonly AgentFsiRepl? _fsi;
    private readonly AgentContextCompactor? _compactor;
    private readonly AgentSdkRuntime _sdk;
    private readonly Func<int>? _historyPageCharacters;
    private readonly AgentSessionStore? _sessionStore;

    /// <summary>Creates the interpreter over the three host surfaces an effect can reach.</summary>
    public AgentEffectInterpreter(IAgentLlmClientProvider llm, IAgentMcpGateway mcp, IAgentHostPrimitives host,
        AgentFsiRepl? fsi = null, AgentContextCompactor? compactor = null, Func<int>? historyPageCharacters = null,
        AgentSessionStore? sessionStore = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(host);
        _llm = llm;
        _mcp = mcp;
        _sdk = new AgentSdkRuntime(mcp);
        _host = host;
        _fsi = fsi;
        _compactor = compactor;
        _historyPageCharacters = historyPageCharacters;
        _sessionStore = sessionStore;
    }

    /// <inheritdoc />
    public async Task<AgentEffectOutcome> ExecuteAsync(AgentEffectContext context, Effect effect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(effect);
        return effect switch
        {
            Effect.LlmChat chat => await ChatAsync(context, chat, cancellationToken).ConfigureAwait(false),
            Effect.McpToolCall tool => await ToolAsync(context, tool, cancellationToken).ConfigureAwait(false),
            Effect.Put put => await PutAsync(put).ConfigureAwait(false),
            Effect.OcrEnqueue ocr => await OcrAsync(ocr, cancellationToken).ConfigureAwait(false),
            Effect.WaitRunEvent wait => await WaitAsync(wait, cancellationToken).ConfigureAwait(false),
            Effect.ReportProgress progress => await ProgressAsync(progress, cancellationToken).ConfigureAwait(false),
            _ => Terminal(effect)
        };
    }

    /// <summary>Resolves the launch model before a session is created, without making a model request.</summary>
    public async Task<string?> ValidateModelSelectionAsync(ModelSelection? selection,
        CancellationToken cancellationToken = default)
    {
        Result<ILlmChatClient> result = await _llm.TryGetAsync(selection, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? null : result.ErrorMessage ?? "The selected model could not be resolved.";
    }

    private async Task<AgentEffectOutcome> ChatAsync(AgentEffectContext context, Effect.LlmChat chat,
        CancellationToken cancellationToken)
    {
        if (context.RequestRetryAttempt > 0)
        {
            double seconds = Math.Min(60, 2 * Math.Pow(2, Math.Min(context.RequestRetryAttempt - 1, 5)));
            seconds = Math.Min(60, seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }

        Result<ILlmChatClient> client = await _llm.TryGetAsync(context.ModelSelection, cancellationToken)
            .ConfigureAwait(false);
        if (client.IsFailure)
        {
            // The loop stays total: an unresolvable provider is surfaced as a model result so the
            // run can report it through history instead of throwing into the host.
            string unavailable = $"LLM_UNAVAILABLE {client.ErrorCode}: {client.ErrorMessage}";
            return AgentEffectOutcome.Completed(Event.NewModelFailure(chat.effectId, client.ErrorCode!, unavailable,
                    AgentRequestRetry.IsRetryable(client.ErrorCode)), unavailable) with
                {
                    FailureCode = client.ErrorCode
                };
        }

        int? maxTokens = client.Value.SupportsMaxTokens ? AgentContextBudget.OutputTokens(client.Value) : null;
        if (_sessionStore is not null)
        {
            AgentActivationEnvelope envelope = AgentActivationEnvelope.Create(context,
                client.Value.ConfigurationKey, maxTokens);
            string? divergence = await _sessionStore.EnsureActivationEnvelopeAsync(context.SessionId, envelope,
                cancellationToken).ConfigureAwait(false);
            if (divergence is not null)
            {
                return AgentEffectOutcome.Completed(Event.NewModelFailure(chat.effectId,
                        "AGENT_ACTIVATION_DIVERGED", divergence, false), divergence) with
                    {
                        FailureCode = "AGENT_ACTIVATION_DIVERGED"
                    };
            }
        }

        // An in-flight chat is cancelled promptly; consumed tokens are not returned (ADR 0036).
        Result<LlmChatCompletion> completion = _compactor is null
            ? await client.Value.CompleteAsync(context.ConversationKey + "/prefix/" +
                                               AgentContextBudget.EnvelopeKey(context, client.Value),
                    new LlmChatRequest(AgentChatHistoryBuilder.Build(context), context.ToolDefinitions),
                    cancellationToken)
                .ConfigureAwait(false)
            : await _compactor.CompleteAsync(context, client.Value, cancellationToken).ConfigureAwait(false);
        if (completion.IsFailure)
        {
            string failure = $"LLM_ERROR {completion.ErrorCode}: {completion.ErrorMessage}";
            return AgentEffectOutcome.Completed(Event.NewModelFailure(chat.effectId, completion.ErrorCode!, failure,
                    AgentRequestRetry.IsRetryable(completion.ErrorCode)), failure) with
                {
                    FailureCode = completion.ErrorCode
                };
        }

        LlmChatCompletion value = completion.Value;
        string finish = (value.FinishReason ?? "stop").Replace("_", "").Replace("-", "").ToLowerInvariant();
        AssistantFinish ending = finish is "length" or "maxtokens" ? AssistantFinish.Truncated
            : value.ToolCalls.Count > 0 ? AssistantFinish.Tools : AssistantFinish.Complete;
        AssistantReply reply = new(value.Text, value.Model, ending,
            ListModule.OfSeq(value.ToolCalls.Select(call =>
                new NativeToolCall(call.Id, call.Name, call.Arguments, call.Metadata))), value.ProviderMetadata);
        return AgentEffectOutcome.Completed(
            Event.NewAssistantReply(chat.effectId, reply),
            $"model turn ({completion.Value.Usage.TotalTokens} tokens, model {completion.Value.Model})");
    }

    private async Task<AgentEffectOutcome> ToolAsync(AgentEffectContext context, Effect.McpToolCall tool,
        CancellationToken cancellationToken)
    {
        context = context with
        {
            HistoryPageCharacters =
            Math.Clamp(_historyPageCharacters?.Invoke() ?? context.HistoryPageCharacters, 128, 16000)
        };
        if (_compactor is not null)
        {
            context = context with
            {
                HistoryPageCharacters = await _compactor.HistoryPageBudgetAsync(context, context.HistoryPageCharacters,
                    cancellationToken).ConfigureAwait(false)
            };
        }

        AgentToolOutcome outcome;
        using AgentSdkRuntime.Scope scope =
            _sdk.Activate(context, EffectParent(context, tool.effectId.Item), cancellationToken);
        try
        {
            string? decodeError = null;
            if (tool.name == "fsi")
            {
                FSharpResult<Unit, ToolProtocolError> decoded =
                    ToolProtocolModule.validateArguments(ToolProtocolModule.fsiSchema, tool.arguments);
                if (decoded.IsError)
                {
                    decodeError = ToolProtocolModule.describe(decoded.ErrorValue);
                }
            }

            outcome = decodeError is not null
                ? new AgentToolOutcome(false, "INVALID_ARGUMENT: " + decodeError, "INVALID_ARGUMENT")
                : tool.name == "fsi"
                    ? _fsi is null
                        ? new AgentToolOutcome(false, "FSI_UNAVAILABLE: FSI REPL is unavailable.",
                            "FSI_UNAVAILABLE")
                        : await ExecuteFsiAsync(context, tool, scope, cancellationToken).ConfigureAwait(false)
                    : FromSdk(await scope.InvokeAsync(tool.name, tool.arguments).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            outcome = new AgentToolOutcome(false, error.ToString(), error.GetType().Name);
        }
        finally
        {
            // Settle admitted SDK operations even when the wrapper is cancelled. A commit is not
            // abandoned merely because a registered function returned or threw before awaiting it.
            await scope.DrainAsync().ConfigureAwait(false);
        }

        outcome = outcome with { Receipts = scope.Receipts };
        if (tool.name == "fsi")
        {
            JsonObject evaluation;
            try
            {
                evaluation = JsonNode.Parse(outcome.Payload) as JsonObject ??
                             new JsonObject { ["detail"] = outcome.Payload };
            }
            catch (JsonException)
            {
                evaluation = new JsonObject { ["detail"] = outcome.Payload };
            }

            evaluation["sdkOperations"] = JsonSerializer.SerializeToNode(outcome.Receipts);
            outcome = outcome with { Payload = evaluation.ToJsonString() };
        }

        string returnedPayload = outcome.Payload;
        if (!outcome.Succeeded)
        {
            JsonNode? result;
            try
            {
                result = JsonNode.Parse(outcome.Payload);
            }
            catch (JsonException)
            {
                result = JsonValue.Create(outcome.Payload);
            }

            returnedPayload = new JsonObject
            {
                ["succeeded"] = false,
                ["errorCode"] = outcome.ErrorCode ?? "TOOL_FAILED",
                ["result"] = result,
                ["sdkOperations"] = JsonSerializer.SerializeToNode(outcome.Receipts)
            }.ToJsonString();
        }

        // Issue #11: a successful tool result that carries images (the view tool) stores
        // them in the session-managed persistent media area and wraps the payload with
        // media references. The model adapter later resolves those references to real
        // vision input; the recorded event never carries base64. Failures never wrap.
        string toolPayload = outcome.Payload;
        if (outcome.Succeeded && outcome.Images.Count > 0 && context.SessionDirectory is { } sessionDirectory)
        {
            IReadOnlyList<AgentSessionImageRef> stored =
                await AgentSessionMediaStore.StoreToolImagesAsync(
                    sessionDirectory,
                    tool.effectId.Item.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    outcome.Images, cancellationToken)
                .ConfigureAwait(false);
            if (stored.Count > 0)
            {
                toolPayload = AgentToolImagePayload.Wrap(outcome.Payload, stored);
            }
        }

        return AgentEffectOutcome.Completed(
                outcome.Succeeded
                    ? Event.NewToolResult(tool.effectId, tool.name, toolPayload)
                    : Event.NewToolFailure(tool.effectId, tool.name, returnedPayload),
                outcome.Succeeded
                    ? $"tool {tool.name} completed"
                    : $"tool {tool.name} failed: {outcome.ErrorCode}") with
            {
                FailureCode = outcome.Succeeded ? null : outcome.ErrorCode ?? "TOOL_FAILED",
                Receipts = outcome.Receipts
            };
    }

    private sealed record FsiExecution(string Identity, string Status, AgentToolOutcome? Outcome);

    private async Task<AgentToolOutcome> ExecuteFsiAsync(AgentEffectContext context, Effect.McpToolCall tool,
        AgentSdkRuntime.Scope scope, CancellationToken cancellationToken)
    {
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tool.arguments + "/" + (context.SdkPolicy?.Activation ?? "chat") +
                                               "/" + context.ContextId + "/" + context.ActivationId)));
        string? path = context.SessionDirectory is { } directory
            ? FsiExecutionPath(directory, context, tool.effectId.Item)
            : null;
        if (path is not null && File.Exists(path))
        {
            FsiExecution previous =
                JsonSerializer.Deserialize<FsiExecution>(await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false))
                ?? throw new InvalidDataException("FSI_EXECUTION_INVALID");
            if (previous.Identity != identity)
            {
                return new AgentToolOutcome(false, "{\"detail\":\"FSI_REPLAY_DIVERGED\"}", "FSI_REPLAY_DIVERGED");
            }

            if (previous.Outcome is { } settled)
            {
                scope.Restore(settled.Receipts);
                return settled;
            }

            IReadOnlyList<AgentSdkReceipt> receipts = await AgentSdkRuntime.ReadReceiptsAsync(context.SessionDirectory!,
                EffectParent(context, tool.effectId.Item), cancellationToken).ConfigureAwait(false);
            scope.Restore(receipts);
            if (context.RecordSdk is { } recordSdk)
            {
                foreach (AgentSdkReceipt receipt in receipts.Where(r => r.Status == "Started"))
                {
                    await recordSdk(receipt with { Status = "Unknown", ErrorCode = "SDK_OPERATION_UNKNOWN" })
                        .ConfigureAwait(false);
                }
            }

            return new AgentToolOutcome(false,
                JsonSerializer.Serialize(new
                {
                    detail =
                        "FSI_EXECUTION_INTERRUPTED: the whole code block was not replayed. Recorded SDK operations are evidence; temporary FSI bindings were lost. Verify unknown operations before continuing."
                }),
                "FSI_EXECUTION_INTERRUPTED");
        }

        if (path is not null)
        {
            await SaveFsiAsync(path, new FsiExecution(identity, "Started", null)).ConfigureAwait(false);
        }

        AgentToolOutcome outcome = await _fsi!.ExecuteAsync(context.SessionId, context.ContextId, tool.arguments,
                scope.InvokeAsync,
                cancellationToken, AgentSdkBindings.Source(context.SdkPolicy?.Exports ?? []))
            .ConfigureAwait(false);
        outcome = outcome with { Receipts = scope.Receipts };
        if (path is not null)
        {
            await SaveFsiAsync(path, new FsiExecution(identity, "Completed", outcome)).ConfigureAwait(false);
        }

        return outcome;
    }

    private static async Task SaveFsiAsync(string path, FsiExecution execution)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(execution), CancellationToken.None)
            .ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    private static string EffectParent(AgentEffectContext context, long effectId)
    {
        return context.ActivationId == "main" ? "effect/" + effectId : context.ActivationId + "/effect/" + effectId;
    }

    private static string FsiExecutionPath(string directory, AgentEffectContext context, long effectId)
    {
        string fileName = effectId + ".json";
        if ((context.ActivationId == "main" && context.ContextId is "main") ||
            (context.ActivationId == "main" && context.ContextId == context.SessionId))
        {
            return Path.Combine(directory, "fsi-executions", fileName);
        }

        return Path.Combine(directory, "fsi-executions", ContextKey(context), fileName);
    }

    private static string ContextKey(AgentEffectContext context)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(context.ContextId + "\n" + context.ActivationId))).ToLowerInvariant();
    }

    private static AgentToolOutcome FromSdk(Patchouli.Agent.Sdk.SdkResult result)
    {
        return new AgentToolOutcome(result.Succeeded, result.Payload, result.Succeeded ? null : result.ErrorCode);
    }

    private async Task<AgentEffectOutcome> PutAsync(Effect.Put put)
    {
        // Deliberately not cancellable: the put has entered its atomic commit point, so cancellation
        // must let the commit or rollback conclude rather than abandon it (ADR 0024/0036).
        AgentPutOutcome outcome = await _mcp.PutAsync(put.uri, put.content, CancellationToken.None)
            .ConfigureAwait(false);
        return AgentEffectOutcome.Completed(
            Event.NewPutResult(put.effectId, put.uri, outcome.Committed),
            $"put {outcome.Detail}",
            true);
    }

    private async Task<AgentEffectOutcome> OcrAsync(Effect.OcrEnqueue ocr, CancellationToken cancellationToken)
    {
        AgentOcrEnqueueOutcome outcome = await _host
            .EnqueueOcrAsync(ocr.documentId, ocr.pageRange, cancellationToken).ConfigureAwait(false);
        return AgentEffectOutcome.Completed(
            Event.NewToolResult(ocr.effectId, "ocr-enqueue", outcome.Payload),
            outcome.Accepted
                ? $"ocr job enqueued for {ocr.documentId} [{ocr.pageRange}]"
                : $"ocr job not enqueued for {ocr.documentId} [{ocr.pageRange}]");
    }

    private async Task<AgentEffectOutcome> WaitAsync(Effect.WaitRunEvent wait, CancellationToken cancellationToken)
    {
        Result<string> waited = await _host
            .WaitRunEventAsync(wait.runUri, wait.waitId.Item, cancellationToken).ConfigureAwait(false);
        // A wait is never silently dropped: an unarmed wait is reported back as the run event so the
        // loop can decide. S4 replaces the placeholder with a real OCR-completion wakeup.
        string payload = waited.IsSuccess
            ? waited.Value
            : $"{waited.ErrorCode}: {waited.ErrorMessage}";
        return AgentEffectOutcome.Completed(
            Event.NewRunEvent(wait.waitId, payload),
            waited.IsSuccess ? $"host run event for {wait.runUri}" : $"host wait for {wait.runUri} was not armed");
    }

    private async Task<AgentEffectOutcome> ProgressAsync(Effect.ReportProgress progress,
        CancellationToken cancellationToken)
    {
        await _host.ReportProgressAsync(progress.message, cancellationToken).ConfigureAwait(false);
        return AgentEffectOutcome.Completed(
            Event.NewScriptProgress(progress.message),
            $"progress: {progress.message}");
    }

    private static AgentEffectOutcome Terminal(Effect effect)
    {
        if (effect.IsFinish)
        {
            return AgentEffectOutcome.Terminal(AgentSessionStatus.Finished, "run finished");
        }

        if (effect.IsStop)
        {
            return AgentEffectOutcome.Terminal(AgentSessionStatus.Stopped, "run stopped");
        }

        throw new NotSupportedException($"Unsupported agent effect '{effect.Tag}'.");
    }
}

/// <summary>
///     Rebuilds the provider-visible append-only chat history from the recorded core history. The
///     mapping is deterministic and only ever appends, which is what keeps the provider prefix
///     cache stable across a resume (ADR 0036).
/// </summary>
public static class AgentChatHistoryBuilder
{
    /// <summary>Placeholder text for a recorded entry that carries no content.</summary>
    public const string EmptyContent = "(empty)";

    /// <summary>Builds the chat history: immutable instructions, then the recorded entries in order.</summary>
    public static LlmChatHistory Build(AgentEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string instructions = context.Instructions;
        LlmChatHistory history = LlmChatHistory.Create(instructions, context.ToolDefinitions);
        List<LlmChatMessage> messages = [];
        foreach (HistoryEntry original in context.ModelHistory ?? context.History)
        {
            if (original is HistoryEntry.ToolResult { name: "model-error" or "agent-retry" })
            {
                continue;
            }

            HistoryEntry entry = context.ModelHistory is null
                ? AgentToolPayloadFolder.Project(original, context.ToolResultMaxCharacters)
                : original;
            // Text tool requests have no provider-native tool_call_id. Their results are
            // ordinary user messages so OpenAI-compatible providers accept the next turn.
            // Tool results that carry session media images (the view tool) additionally
            // attach one LlmImagePart per image: real vision input, never a path or
            // base64 text.
            messages.Add(entry is HistoryEntry.ToolResult tool
                ? ToToolResultMessage(context, tool)
                : ToModelMessage(entry, context));
        }

        return messages.Count == 0 ? history : history.Append(messages);
    }

    /// <summary>
    /// Issue #11: maps a tool result to its chat message, attaching session media images
    /// as <c>LlmImagePart</c> data-URI parts. The image bytes come from the session-managed
    /// persistent media area (reloaded by media reference, so retry/resume/context rebuild
    /// keep working); a missing image is reported as explicit text, never faked.
    /// </summary>
    private static LlmChatMessage ToToolResultMessage(AgentEffectContext context, HistoryEntry.ToolResult tool)
    {
        if (context.ResolveSessionImage is null ||
            !AgentToolImagePayload.TryUnwrap(tool.payload, out string resultText,
                out IReadOnlyList<AgentSessionImageRef> images))
        {
            return LlmChatMessage.User("Tool result " + Content(tool.name + ": " + tool.payload));
        }

        List<LlmMessagePart> parts =
        [
            new LlmMessagePart.LlmTextPart("Tool result " + Content(tool.name + ": " + resultText))
        ];
        foreach (AgentSessionImageRef image in images)
        {
            AgentSessionImageData? data = context.ResolveSessionImage(image.MediaId);
            if (data is null)
            {
                parts.Add(new LlmMessagePart.LlmTextPart(
                    $"[image {image.MediaId} ({image.ResourceUri}, revision {image.Revision}) is unavailable: session media missing]"));
                continue;
            }

            parts.Add(new LlmMessagePart.LlmImagePart(
                "data:" + data.Ref.MimeType + ";base64," + Convert.ToBase64String(data.PngBytes),
                data.Ref.MimeType,
                "auto"));
        }

        return new LlmChatMessage(LlmChatRole.User, parts);
    }

    /// <summary>
    /// P1-3: maps a native tool result to its chat message. The LlmToolResultPart is
    /// always present to maintain provider-required tool-call/result ordering. If the
    /// payload carries session media image references (the view tool), LlmImageParts
    /// are appended so the images become real vision input — not just text.
    /// </summary>
    private static LlmChatMessage ToNativeToolResultMessage(
        AgentEffectContext context,
        HistoryEntry.NativeToolResult result)
    {
        List<LlmMessagePart> parts =
        [
            new LlmMessagePart.LlmToolResultPart(result.callId, result.name, result.payload, result.isError)
        ];

        if (context.ResolveSessionImage is not null &&
            AgentToolImagePayload.TryUnwrap(result.payload, out string _,
                out IReadOnlyList<AgentSessionImageRef> images))
        {
            foreach (AgentSessionImageRef image in images)
            {
                AgentSessionImageData? data = context.ResolveSessionImage(image.MediaId);
                if (data is null)
                {
                    parts.Add(new LlmMessagePart.LlmTextPart(
                        $"[image {image.MediaId} ({image.ResourceUri}, revision {image.Revision}) is unavailable: session media missing]"));
                    continue;
                }

                parts.Add(new LlmMessagePart.LlmImagePart(
                    "data:" + data.Ref.MimeType + ";base64," + Convert.ToBase64String(data.PngBytes),
                    data.Ref.MimeType,
                    "auto"));
            }
        }

        return new LlmChatMessage(LlmChatRole.Tool, parts);
    }

    /// <summary>Maps one core history entry to its chat message.</summary>
    public static LlmChatMessage ToMessage(HistoryEntry entry,
        int toolResultMaxCharacters = LlmAppSettings.DefaultToolResultMaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ToModelMessage(AgentToolPayloadFolder.Project(entry, toolResultMaxCharacters));
    }

    private static LlmChatMessage ToModelMessage(HistoryEntry entry, AgentEffectContext? context = null)
    {
        return entry switch
        {
            HistoryEntry.Instruction instruction => LlmChatMessage.System(Content(instruction.text)),
            HistoryEntry.UserMessage message => LlmChatMessage.User(Content(message.text)),
            HistoryEntry.ModelResult result => LlmChatMessage.Assistant(Content(result.text)),
            HistoryEntry.AssistantReply assistant => new LlmChatMessage(LlmChatRole.Assistant,
                new LlmMessagePart[] { new LlmMessagePart.LlmTextPart(assistant.reply.Text) }
                    .Concat([new LlmMessagePart.LlmAssistantMetadataPart(assistant.reply.Metadata)])
                    .Concat(assistant.reply.ToolCalls.Select(call =>
                        new LlmMessagePart.LlmToolCallPart(call.Id, call.Name, call.Arguments, call.Metadata)))
                    .ToArray()),
            HistoryEntry.NativeToolResult result => context is not null
                ? ToNativeToolResultMessage(context, result)
                : new LlmChatMessage(LlmChatRole.Tool,
                    [new LlmMessagePart.LlmToolResultPart(result.callId, result.name, result.payload, result.isError)]),
            HistoryEntry.ToolResult result =>
                LlmChatMessage.Tool(Content(result.name + ": " + result.payload)),
            _ => throw new NotSupportedException($"Unsupported history entry '{entry.Tag}'.")
        };
    }

    /// <summary>
    ///     A recorded entry may legally be empty (for example an empty model result); the provider
    ///     history rejects empty messages, so it is replaced by a stable marker rather than dropped,
    ///     which would break the append-only prefix.
    /// </summary>
    private static string Content(string text)
    {
        return string.IsNullOrEmpty(text) ? EmptyContent : text;
    }

    /// <summary>Materializes the recorded history as a list (convenience for callers holding an F# list).</summary>
    public static IReadOnlyList<HistoryEntry> ToHistoryList(FSharpList<HistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return [.. ListModule.ToSeq(history)];
    }
}
