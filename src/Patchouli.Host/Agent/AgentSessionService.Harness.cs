using Patchouli.Agent;

namespace Patchouli.Host.Agent;

public sealed partial class AgentSessionService
{
    private readonly HashSet<Task> _harnessDrivers = [];

    /// <summary>Publishes a harness boundary to the same journal and observable session as chat.</summary>
    public async Task CheckpointHarnessAsync(string sessionId, Context context, Effect effect,
        CancellationToken cancellationToken)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (HistoryEntry.UserMessage message in context.History.Skip(session.Context.History.Length)
                         .OfType<HistoryEntry.UserMessage>())
            {
                Event goal = Event.NewUserMessage("harness-stage-" + context.EffectSeq, message.text);
                await AppendLogAsync(session, AgentLogKinds.Event, AgentSessionCodec.ToJson(goal), cancellationToken)
                    .ConfigureAwait(false);
            }

            await AppendRetryFeedbackAsync(session, session.Context, context, cancellationToken).ConfigureAwait(false);
            session.Context = context;
            if (effect is Effect.LlmChat && context.Retry.Attempts > 0)
            {
                session.Detail = $"模型请求重试 {context.Retry.Attempts}/{context.Retry.Limit}，等待后重试";
                await AppendLogAsync(session, AgentLogKinds.Event,
                        AgentSessionCodec.ToJson(Event.NewScriptProgress(session.Detail)), cancellationToken)
                    .ConfigureAwait(false);
            }

            await AppendLogAsync(session, AgentLogKinds.EffectIssued, EffectPayload(effect, context), cancellationToken)
                .ConfigureAwait(false);
            session.RefreshStatusFromCore();
            if (effect is not Effect.LlmChat || context.Retry.Attempts == 0)
            {
                session.Detail = "agent harness: " + effect.GetType().Name;
            }

            await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            Report(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Records a completed result without scheduling a second independent agent loop.</summary>
    public async Task RecordHarnessResultAsync(string sessionId, Effect effect, Event result,
        CancellationToken cancellationToken)
    {
        AgentSession session = await RequireAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Context next = AgentCoreModule.observeEffectResult(session.Context, effect, result);
            await AppendLogAsync(session, AgentLogKinds.Event, AgentSessionCodec.ToJson(result), cancellationToken)
                .ConfigureAwait(false);
            session.Context = next;
            await PersistAsync(session, cancellationToken).ConfigureAwait(false);
            Report(session);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task RecordHarnessOutcomeAsync(string sessionId, Effect effect, AgentEffectOutcome outcome,
        double elapsedMs)
    {
        AgentSession session = await RequireAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        await AppendEffectResultAsync(session, effect, outcome, CancellationToken.None, elapsedMs)
            .ConfigureAwait(false);
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
