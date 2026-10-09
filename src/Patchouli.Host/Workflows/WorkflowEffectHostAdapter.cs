using Patchouli.Agent;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Workflows;

namespace Patchouli.Host.Workflows;

/// <summary>Executes harness effects through the same agent interpreter and tool protocol as chat.</summary>
public sealed class WorkflowEffectHostAdapter : IWorkflowEffectHost, IWorkflowSdkHost
{
    /// <summary>Stable prefix of the failure raised for an effect that produced no result event.</summary>
    public const string MissingResultEventCode = "WORKFLOW_EFFECT_DEFERRED";

    private readonly IAgentEffectInterpreter _interpreter;
    private readonly Func<string, string> _instructionsForSession;
    private readonly IAgentHostPrimitives? _hostPrimitives;
    private readonly Func<string, Effect, AgentEffectOutcome, double, Task>? _recordOutcome;
    private readonly Func<string, string>? _sessionDirectory;
    private readonly Func<string, AgentSdkReceipt, Task>? _recordSdk;

    /// <summary>Creates the adapter over an interpreter and the per-session instruction accessor.</summary>
    /// <param name="interpreter">The interpreter every effect is executed through.</param>
    /// <param name="instructionsForSession">
    ///     The fixed instruction head of one session. Stage instructions travel in append-only history.
    /// </param>
    /// <param name="hostPrimitives">
    ///     The host primitives that satisfy a deferred <c>WaitRunEvent</c>; null refuses the deferral
    ///     explicitly (the default the tests and offline hosts use).
    /// </param>
    public WorkflowEffectHostAdapter(IAgentEffectInterpreter interpreter,
        Func<string, string> instructionsForSession,
        IAgentHostPrimitives? hostPrimitives = null,
        Func<string, Effect, AgentEffectOutcome, double, Task>? recordOutcome = null,
        Func<string, string>? sessionDirectory = null, Func<string, AgentSdkReceipt, Task>? recordSdk = null)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(instructionsForSession);
        _interpreter = interpreter;
        _instructionsForSession = instructionsForSession;
        _hostPrimitives = hostPrimitives;
        _recordOutcome = recordOutcome;
        _sessionDirectory = sessionDirectory;
        _recordSdk = recordSdk;
    }

    /// <inheritdoc />
    public async Task<WorkflowEffectOutcome> ExecuteEffectAsync(string sessionId, WorkflowEffectScope scope,
        Effect effect, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(effect);
        IReadOnlySet<string>? allowed =
            scope.AllowedTools is { } names ? names.Value.ToHashSet(StringComparer.Ordinal) : null;
        string[] definitions = AgentNativeTools.Definitions.Where(definition =>
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(definition);
            return allowed is null ||
                   allowed.Contains(document.RootElement.GetProperty("function").GetProperty("name").GetString()!);
        }).Concat(scope.Exports.Select(tool => tool.NativeDefinition)).ToArray();
        AgentEffectContext context = new(sessionId,
            _instructionsForSession(sessionId) + "\n" + AgentNativeTools.Instructions,
            definitions,
            AgentChatHistoryBuilder.ToHistoryList(scope.Context.History))
        {
            SessionDirectory = _sessionDirectory?.Invoke(sessionId),
            RecordSdk = _recordSdk is { } recordSdk ? receipt => recordSdk(sessionId, receipt) : null,
            SdkPolicy = new AgentSdkPolicy(allowed,
                scope.PrimitiveLimit is { } limit ? limit.Value : null, scope.Exports, scope.Activation)
        };
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        AgentEffectOutcome outcome;
        try
        {
            outcome = await _interpreter.ExecuteAsync(context, effect, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (_recordOutcome is { } record)
            {
                AgentSessionStatus status = error is OperationCanceledException
                    ? AgentSessionStatus.Cancelled
                    : AgentSessionStatus.Failed;
                await record(sessionId, effect, AgentEffectOutcome.Terminal(status, error.ToString()) with
                    {
                        FailureCode = error is OperationCanceledException ? "CANCELLED" : error.GetType().Name
                    },
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds).ConfigureAwait(false);
            }

            throw;
        }

        if (_recordOutcome is { } recorder)
        {
            await recorder(sessionId, effect, outcome,
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds)
                .ConfigureAwait(false);
        }

        if (effect is Effect.LlmChat && outcome.FailureCode is not null &&
            outcome.ResultEvent is not Event.ModelFailure { retryable: true })
        {
            return outcome.ResultEvent is { } failureEvent
                ? WorkflowEffectOutcome.NewFailedEvent(failureEvent, outcome.Summary)
                : throw new InvalidOperationException(outcome.Summary);
        }

        return await MapAsync(sessionId, effect, outcome, cancellationToken).ConfigureAwait(false);
    }

    public string DescribeSdk(Patchouli.Agent.Sdk.ExportedTool[] exports)
    {
        return "Stage FSI functions (installed in AgentTools):\n" + AgentSdkBindings.Source(exports);
    }

    /// <summary>Maps one interpreter outcome to the workflow effect outcome the executor consumes.</summary>
    private async Task<WorkflowEffectOutcome> MapAsync(string sessionId, Effect effect,
        AgentEffectOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Disposition == AgentEffectDisposition.Completed)
        {
            return outcome.ResultEvent is { } resultEvent
                ? outcome.Receipts.Count == 0
                    ? WorkflowEffectOutcome.NewCompletedEvent(resultEvent, outcome.Summary)
                    : WorkflowEffectOutcome.NewObservedEvent(resultEvent, outcome.Summary,
                        outcome.Receipts.Select(r => new Patchouli.Agent.Sdk.SdkObservation(r.Tool, r.Arguments,
                            r.Status == "Started" ? "SDK_OPERATION_UNKNOWN: no durable outcome." : r.Payload,
                            r.Status == "Succeeded", r.OperationId,
                            r.Status == "Started" ? "SDK_OPERATION_UNKNOWN" : r.ErrorCode ?? "")).ToArray())
                : throw new NotSupportedException(
                    $"{MissingResultEventCode}: the host completed effect '{effect.Tag}' without a result event.");
        }

        if (outcome.Disposition == AgentEffectDisposition.Terminal)
        {
            WorkflowRunStatus status = outcome.TerminalStatus switch
            {
                AgentSessionStatus.Finished => WorkflowRunStatus.Finished,
                AgentSessionStatus.Stopped => WorkflowRunStatus.Stopped,
                AgentSessionStatus.Cancelled => WorkflowRunStatus.Cancelled,
                AgentSessionStatus.Failed => WorkflowRunStatus.Failed,
                _ => WorkflowRunStatus.Finished
            };
            return WorkflowEffectOutcome.NewTerminalEffect(status, outcome.Summary);
        }

        if (outcome.Disposition == AgentEffectDisposition.Deferred)
        {
            // A deferred WaitRunEvent is satisfied by the host itself: arm the real run-event wait and
            // feed the outcome back as the RunEvent the executor records. The wait is event-driven, so
            // the run stays parked until the host satisfies or cancels it (ADR 0036, no polling).
            if (_hostPrimitives is { } host && effect is Effect.WaitRunEvent wait)
            {
                Result<string> waited = await host
                    .WaitRunEventAsync(wait.runUri, wait.waitId.Item, cancellationToken).ConfigureAwait(false);
                string payload = waited.IsSuccess
                    ? waited.Value
                    : $"{waited.ErrorCode}: {waited.ErrorMessage}";
                return WorkflowEffectOutcome.NewCompletedEvent(
                    Event.NewRunEvent(wait.waitId, payload),
                    waited.IsSuccess
                        ? $"host run event for {wait.runUri}"
                        : $"host wait for {wait.runUri} was not armed");
            }

            // Without host primitives — or for an effect that carries no run URI to arm — refusing
            // explicitly keeps the failure structured instead of reporting a wait that never happened
            // as a completed effect.
            throw new NotSupportedException(
                $"{MissingResultEventCode}: effect '{effect.Tag}' of session '{sessionId}' is waiting for a " +
                "host control event and no host run-event primitive can satisfy it.");
        }

        throw new NotSupportedException(
            $"{MissingResultEventCode}: the host reported disposition '{outcome.Disposition}' for effect " +
            $"'{effect.Tag}', which the workflow executor cannot consume.");
    }
}
