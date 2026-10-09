using Microsoft.FSharp.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.FSharp.Core;
using Patchouli.Agent;
using Patchouli.Core.Results;
using Patchouli.Llm;

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
    public bool UsesTextToolProtocol { get; init; }
    public string? SessionDirectory { get; init; }
    public AgentSdkPolicy? SdkPolicy { get; init; }
    public Func<AgentSdkReceipt, Task>? RecordSdk { get; init; }
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

    /// <summary>Creates the interpreter over the three host surfaces an effect can reach.</summary>
    public AgentEffectInterpreter(IAgentLlmClientProvider llm, IAgentMcpGateway mcp, IAgentHostPrimitives host,
        AgentFsiRepl? fsi = null, AgentContextCompactor? compactor = null)
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

    private async Task<AgentEffectOutcome> ChatAsync(AgentEffectContext context, Effect.LlmChat chat,
        CancellationToken cancellationToken)
    {
        Result<ILlmChatClient> client = await _llm.TryGetAsync(cancellationToken).ConfigureAwait(false);
        if (client.IsFailure)
        {
            // The loop stays total: an unresolvable provider is surfaced as a model result so the
            // run can report it through history instead of throwing into the host.
            string unavailable = $"LLM_UNAVAILABLE {client.ErrorCode}: {client.ErrorMessage}";
            return AgentEffectOutcome.Completed(Event.NewModelFailure(chat.effectId, client.ErrorCode!, unavailable,
                    LlmFailureClassifier.IsRetryable(client.ErrorCode)), unavailable) with
                {
                    FailureCode = client.ErrorCode
                };
        }

        // An in-flight chat is cancelled promptly; consumed tokens are not returned (ADR 0036).
        Result<LlmChatCompletion> completion = _compactor is null
            ? await client.Value.CompleteAsync(context.SessionId,
                    new LlmChatRequest(AgentChatHistoryBuilder.Build(context), context.ToolDefinitions),
                    cancellationToken)
                .ConfigureAwait(false)
            : await _compactor.CompleteAsync(context, client.Value, cancellationToken).ConfigureAwait(false);
        if (completion.IsFailure)
        {
            string failure = $"LLM_ERROR {completion.ErrorCode}: {completion.ErrorMessage}";
            return AgentEffectOutcome.Completed(Event.NewModelFailure(chat.effectId, completion.ErrorCode!, failure,
                    completion.ErrorCode != LlmFailureCodes.Cancelled &&
                    LlmFailureClassifier.IsRetryable(completion.ErrorCode)), failure) with
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
        AgentToolOutcome outcome;
        using AgentSdkRuntime.Scope scope = _sdk.Activate(context, "effect/" + tool.effectId.Item, cancellationToken);
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

        return AgentEffectOutcome.Completed(
                outcome.Succeeded
                    ? Event.NewToolResult(tool.effectId, tool.name, outcome.Payload)
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
            System.Text.Encoding.UTF8.GetBytes(tool.arguments + "/" + (context.SdkPolicy?.Activation ?? "chat"))));
        string? path = context.SessionDirectory is { } directory
            ? Path.Combine(directory, "fsi-executions", tool.effectId.Item + ".json")
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
                "effect/" + tool.effectId.Item, cancellationToken).ConfigureAwait(false);
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

        AgentToolOutcome outcome = await _fsi!.ExecuteAsync(context.SessionId, tool.arguments, scope.InvokeAsync,
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
        foreach (HistoryEntry entry in context.History)
        {
            // Text tool requests have no provider-native tool_call_id. Their results are
            // ordinary user messages so OpenAI-compatible providers accept the next turn.
            messages.Add(entry is HistoryEntry.ToolResult tool
                ? LlmChatMessage.User("Tool result " + Content(tool.name + ": " + tool.payload))
                : ToMessage(entry));
        }

        return messages.Count == 0 ? history : history.Append(messages);
    }

    /// <summary>Maps one core history entry to its chat message.</summary>
    public static LlmChatMessage ToMessage(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
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
            HistoryEntry.NativeToolResult result => new LlmChatMessage(LlmChatRole.Tool,
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
