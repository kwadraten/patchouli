using Patchouli.Agent;
using Patchouli.Workflows;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchouli.Host.Agent;

public sealed partial class AgentSessionService
{
    private readonly HashSet<Task> _harnessDrivers = [];

    /// <summary>Publishes a harness boundary to the same journal and observable session as chat.</summary>
    public async Task CheckpointHarnessAsync(string sessionId, WorkflowEffectScope scope, Effect effect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Context context = scope.Context;
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int historyCursor = scope.IsMain
                ? Math.Min(session.Context.History.Length, context.History.Length)
                : await HarnessHistoryCursorAsync(sessionId, scope.ContextId, cancellationToken).ConfigureAwait(false);
            int historyIndex = 0;
            foreach (HistoryEntry entry in context.History)
            {
                if (historyIndex >= historyCursor && entry is HistoryEntry.UserMessage message)
                {
                    Event goal = Event.NewUserMessage(
                        "harness:" + scope.ActivationId + ":" + historyIndex, message.text);
                    await AppendLogAsync(session, AgentLogKinds.Event,
                            AddScopeMetadata(AgentSessionCodec.ToJson(goal), scope, historyIndex), cancellationToken)
                        .ConfigureAwait(false);
                }

                historyIndex++;
            }

            if (scope.IsMain)
            {
                await AppendRetryFeedbackAsync(session, session.Context, context, cancellationToken)
                    .ConfigureAwait(false);
                session.Context = context;
            }

            if (effect is Effect.LlmChat && context.Retry.Attempts > 0)
            {
                session.Detail = $"模型请求重试 {context.Retry.Attempts}/{context.Retry.Limit}，等待后重试";
                await AppendLogAsync(session, AgentLogKinds.Event,
                        AddScopeMetadata(AgentSessionCodec.ToJson(Event.NewScriptProgress(session.Detail)), scope),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string issued = AddScopeMetadata(EffectPayload(effect, context), scope);
            JsonObject issuedPayload = JsonNode.Parse(issued)!.AsObject();
            issuedPayload["historyLength"] = context.History.Length;
            await AppendLogAsync(session, AgentLogKinds.EffectIssued, issuedPayload.ToJsonString(), cancellationToken)
                .ConfigureAwait(false);
            if (scope.IsMain)
            {
                session.RefreshStatusFromCore();
            }

            if (effect is not Effect.LlmChat || context.Retry.Attempts == 0)
            {
                session.Detail = "agent harness: " + effect.GetType().Name;
            }

            if (scope.IsMain)
            {
                await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The workflow executor's checkpoints own this worker context. Persist only the
                // observable session state so a worker can never replace the platform transcript.
                await PersistStateAsync(session, cancellationToken).ConfigureAwait(false);
            }

            Report(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Records a completed result without scheduling a second independent agent loop.</summary>
    public async Task RecordHarnessResultAsync(string sessionId, WorkflowEffectScope scope, Effect effect, Event result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendLogAsync(session, AgentLogKinds.Event,
                    AddScopeMetadata(AgentSessionCodec.ToJson(result), scope),
                    cancellationToken)
                .ConfigureAwait(false);
            if (scope.IsMain)
            {
                session.Context = AgentCoreModule.observeEffectResult(session.Context, effect, result);
                await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PersistStateAsync(session, cancellationToken).ConfigureAwait(false);
            }

            Report(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task RecordHarnessOutcomeAsync(string sessionId, WorkflowEffectScope scope, Effect effect,
        AgentEffectOutcome outcome, double elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(scope);
        AgentSession session = await RequireAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        await AppendEffectResultAsync(session, effect, outcome, CancellationToken.None, elapsedMs, scope)
            .ConfigureAwait(false);
    }

    private async Task<int> HarnessHistoryCursorAsync(string sessionId, string contextId,
        CancellationToken cancellationToken)
    {
        int cursor = 0;
        foreach (AgentSessionLogEntry entry in await _store.ReadLogAsync(sessionId, cancellationToken)
                     .ConfigureAwait(false))
        {
            if (entry.Kind != AgentLogKinds.EffectIssued)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(entry.Payload);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("contextId", out JsonElement context) &&
                context.GetString() == contextId && root.TryGetProperty("historyLength", out JsonElement length) &&
                length.TryGetInt32(out int recordedLength))
            {
                cursor = Math.Max(cursor, recordedLength);
            }
        }

        return cursor;
    }

    private static string AddScopeMetadata(string payload, WorkflowEffectScope scope, int? historyIndex = null)
    {
        JsonObject json = JsonNode.Parse(payload)?.AsObject() ?? new JsonObject();
        json["contextId"] = scope.ContextId;
        json["activationId"] = scope.ActivationId;
        if (historyIndex is { } index)
        {
            json["historyIndex"] = index;
        }

        return json.ToJsonString();
    }

    private async Task AppendRetryFeedbackAsync(AgentSession session, Context previous, Context next,
        CancellationToken cancellationToken)
    {
        foreach (HistoryEntry.NativeToolResult denied in next.History.Skip(previous.History.Length)
                     .OfType<HistoryEntry.NativeToolResult>().Where(entry =>
                         entry.payload.StartsWith("AGENT_TOOL_DENIED", StringComparison.Ordinal)))
        {
            await AppendLogAsync(session, AgentLogKinds.Event,
                    AgentSessionCodec.ToJson(Event.NewScriptProgress(denied.payload)), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (HistoryEntry.ToolResult feedback in next.History.Skip(previous.History.Length)
                     .OfType<HistoryEntry.ToolResult>().Where(entry => entry.name == "agent-retry" ||
                                                                       entry.payload.StartsWith("AGENT_TOOL_DENIED",
                                                                           StringComparison.Ordinal)))
        {
            await AppendLogAsync(session, AgentLogKinds.Event,
                    AgentSessionCodec.ToJson(Event.NewScriptProgress(feedback.payload)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a typed harness under the same driver lock, lifetime and immediate stop/cancel token as chat.
    /// The state gate stays available while the model or a tool is running.
    /// </summary>
    public async Task<T> RunHarnessAsync<T>(string sessionId, Func<CancellationToken, Task<T>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _harnessDrivers.Add(finished.Task);
        }

        try
        {
            return await RunHarnessCoreAsync(sessionId, run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _harnessDrivers.Remove(finished.Task);
            }

            finished.TrySetResult();
        }
    }

    private async Task<T> RunHarnessCoreAsync<T>(string sessionId, Func<CancellationToken, Task<T>> run,
        CancellationToken cancellationToken)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Driver.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationToken runToken = default;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (session.IsPurging)
                {
                    throw new OperationCanceledException("The session is being purged.");
                }

                await ApplyStatusCoreAsync(session, AgentSessionStatus.Running, "agent harness running",
                    cancellationToken).ConfigureAwait(false);
                session.Started = true;
                runToken = session.BeginEffect(cancellationToken);
            }
            finally
            {
                session.Gate.Release();
            }

            return await run(runToken).ConfigureAwait(false);
        }
        finally
        {
            session.EndEffect(runToken);
            session.Driver.Release();
        }
    }
}
