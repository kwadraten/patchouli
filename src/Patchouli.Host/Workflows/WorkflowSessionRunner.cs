using System.Globalization;
using System.Text.Json;
using Microsoft.FSharp.Core;
using Patchouli.Agent;
using Patchouli.Host.Agent;
using Patchouli.Llm;
using Patchouli.Workflows;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Host.Workflows;

/// <summary>
///     One run request of a host workflow session: the workflow to launch, the session it runs as,
///     the selection it was launched against and the parameters the launch supplied.
/// </summary>
/// <param name="WorkflowId">Id of the workflow definition to load and snapshot.</param>
/// <param name="Parameters">Launch parameters as the launch form supplied them (they win over settings).</param>
/// <param name="Selection">The Library selection the run was launched against.</param>
/// <param name="SessionId">Session id to use; null mints a fresh one.</param>
public sealed record WorkflowSessionRequest(
    string WorkflowId,
    IReadOnlyDictionary<string, string> Parameters,
    WorkflowSelection Selection,
    string? SessionId = null);

/// <summary>
///     The result of one host workflow session run: the durable session state plus the run outcome
///     (status, context, steps, diagnostics).
/// </summary>
/// <param name="Session">The observable session state after the run.</param>
/// <param name="Outcome">The workflow run outcome the executor reported.</param>
public sealed record WorkflowSessionResult(AgentSessionSnapshot Session, WorkflowRunOutcome Outcome);

/// <summary>
///     Starts, resumes and persists one <c>.fsx</c> workflow session: it composes the workflow store,
///     the host session service and <see cref="WorkflowExecutor" /> so the script snapshot, the core
///     context and the append-only step log survive a restart (ADR 0036, plan §3.4).
/// </summary>
/// <remarks>
///     <para>
///         <b>Launch parameters.</b> <see cref="MergeParameters" /> is the single place the launch form
///         and the LLM settings meet: the launch wins, and a translation workflow that omitted a key
///         falls back to <see cref="LlmAppSettings.EffectiveTargetLanguage" />,
///         <see cref="LlmAppSettings.EffectiveTranslationWindowRadius" /> or
///         <see cref="LlmAppSettings.BackfillPreviousWindowTranslation" /> (D5/D6). A resume reuses the
///         recorded launch parameters unchanged, so a run never re-reads settings it already applied.
///     </para>
///     <para>
///         <b>Persistence.</b> The session directory owns <c>launch.json</c>, <c>snapshot.json</c> and
///         <c>context.json</c> (session service) plus <c>workflow/snapshot.json</c>,
///         <c>workflow/script.fsx</c> and <c>workflow/steps.jsonl</c> (<see cref="FileWorkflowRunSink" />).
///         The instruction prefix is stored with the session, so the prefix-cache head is identical
///         after a restart.
///     </para>
/// </remarks>
public sealed class WorkflowSessionRunner
{
    /// <summary>Launch-parameter key of the selected document.</summary>
    public const string DocumentIdParameter = "documentId";

    /// <summary>Launch-parameter key of the selected page range.</summary>
    public const string PageRangeParameter = "pageRange";

    /// <summary>Launch-parameter key of the target language (D6).</summary>
    public const string TargetLanguageParameter = "targetLanguage";

    /// <summary>Launch-parameter key of the translation sliding-window radius (D5).</summary>
    public const string WindowRadiusParameter = "windowRadius";

    /// <summary>Launch-parameter key that enables previous-window translation backfill.</summary>
    public const string BackfillPreviousWindowParameter = "backfillPreviousWindowTranslation";

    private const string SelectionParameter = "__workflow.selection";

    private readonly WorkflowStore _store;
    private readonly AgentSessionService _sessions;
    private readonly AgentSessionStore _sessionStore;
    private readonly IAgentEffectInterpreter _interpreter;
    private readonly Func<LlmAppSettings> _settings;
    private readonly IAgentHostPrimitives? _hostPrimitives;

    /// <summary>Creates the runner over a workflow store, the host session service and the interpreter.</summary>
    /// <param name="hostPrimitives">
    ///     The host primitives a deferred <c>WaitRunEvent</c> is satisfied through; null refuses the
    ///     deferral explicitly (offline hosts and tests).
    /// </param>
    public WorkflowSessionRunner(WorkflowStore store, AgentSessionService sessions,
        IAgentEffectInterpreter interpreter, Func<LlmAppSettings> settings,
        IAgentHostPrimitives? hostPrimitives = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _sessions = sessions;
        _interpreter = interpreter;
        _settings = settings;
        _hostPrimitives = hostPrimitives;
        _sessionStore = new AgentSessionStore(sessions.SessionsRoot);
    }

    /// <summary>The workflow store this runner loads definitions and scripts from.</summary>
    public WorkflowStore Store => _store;

    /// <summary>The sessions root the run artifacts are written under.</summary>
    public string SessionsRoot => _sessions.SessionsRoot;

    /// <summary>
    ///     The launch parameters of one run: every launch parameter first, in recorded order, then the
    ///     settings-derived defaults the launch omitted. A key the launch supplied always wins, and a
    ///     blank supplied value counts as omitted.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MergeParameters(LlmAppSettings settings,
        IReadOnlyDictionary<string, string>? launchParameters)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Dictionary<string, string> merged = new(StringComparer.Ordinal);
        if (launchParameters is not null)
        {
            foreach (KeyValuePair<string, string> parameter in launchParameters)
            {
                if (!string.IsNullOrWhiteSpace(parameter.Value))
                {
                    merged[parameter.Key] = parameter.Value;
                }
            }
        }

        MergeDefault(merged, TargetLanguageParameter, settings.EffectiveTargetLanguage);
        MergeDefault(merged, WindowRadiusParameter,
            settings.EffectiveTranslationWindowRadius.ToString(CultureInfo.InvariantCulture));
        MergeDefault(merged, BackfillPreviousWindowParameter, Text(settings.BackfillPreviousWindowTranslation));
        return merged;
    }

    /// <summary>
    ///     Starts a workflow session: loads the definition and its script, creates the session with the
    ///     merged launch parameters and the script contract as its instruction head, records the script
    ///     snapshot, executes the script and persists the resulting context, steps and instructions.
    /// </summary>
    public async Task<WorkflowSessionResult> StartAsync(WorkflowSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkflowId);
        FSharpOption<WorkflowDefinition>? loaded =
            await _store.TryLoadAsync(request.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            throw new KeyNotFoundException(
                $"The workflow '{request.WorkflowId}' has no definition under '{_store.Root}'.");
        }

        WorkflowDefinition definition = loaded.Value;
        FSharpOption<string>? scriptText = await _store.ReadScriptAsync(request.WorkflowId, cancellationToken)
            .ConfigureAwait(false);
        string script = scriptText is null ? string.Empty : scriptText.Value;
        ArgumentNullException.ThrowIfNull(request.Selection);
        Dictionary<string, string> parameters = MergeParameters(_settings(), request.Parameters)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        // Persist the complete selection, including multiple documents and languages, for exact replay.
        parameters[SelectionParameter] = JsonSerializer.Serialize(request.Selection);
        string sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString("N")
            : request.SessionId.Trim();
        if (_sessionStore.Exists(sessionId))
        {
            // A launch never overwrites a recorded session: its snapshot, its launch parameters and its
            // instruction head are the material the resume path reuses.
            throw new InvalidOperationException(
                $"The session '{sessionId}' already exists under '{SessionsRoot}'; resume it instead of launching it.");
        }

        WorkflowRunRequest run = WorkflowRunRequests.create(sessionId, definition, script, request.Selection,
            parameters, DateTimeOffset.UtcNow);
        AgentSessionLaunchParameters launch =
            AgentSessionLaunchParameters.Create(WorkflowUri(run.Snapshot.WorkflowId), parameters, sessionId, null,
                BaseInstructions(run.Snapshot));
        await _sessions.CreateAsync(launch, cancellationToken).ConfigureAwait(false);
        return await RunAsync(run, launch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resumes a workflow session from its own storage: the stored snapshot, the recovered core
    ///     context and the recorded steps. The recorded launch parameters are reused as recorded, so a
    ///     resume reproduces the exact prompt prefix.
    /// </summary>
    public async Task<WorkflowSessionResult> ResumeAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        FileWorkflowRunSink sink = new(SessionsRoot);
        FSharpOption<WorkflowScriptSnapshot>? recordedSnapshot =
            await sink.ReadSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (recordedSnapshot is null)
        {
            throw new InvalidOperationException(
                $"The session '{sessionId}' recorded no workflow script snapshot.");
        }

        WorkflowScriptSnapshot snapshot = recordedSnapshot.Value;
        AgentSessionLaunchParameters? recordedLaunch =
            await _sessionStore.TryReadLaunchAsync(sessionId, cancellationToken).ConfigureAwait(false);
        AgentSessionLaunchParameters launch = recordedLaunch ?? AgentSessionLaunchParameters.Create(
            WorkflowUri(snapshot.WorkflowId), null, sessionId, null, BaseInstructions(snapshot));
        if (!_sessions.IsOpen(sessionId))
        {
            if (recordedLaunch is null)
            {
                await _sessions.CreateAsync(launch, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sessions.TryOpenAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
        }

        return await _sessions.RunHarnessAsync(sessionId, async token =>
        {
            // Read the recovery ledger under the driver lock: a queued resume must see the effects
            // completed by the run ahead of it, rather than repeating them from a stale request.
            Context context = await _sessionStore.TryReadContextAsync(sessionId, token).ConfigureAwait(false)
                              ?? AgentCoreModule.initial;
            WorkflowStepRecord[] steps = await sink.ReadStepsAsync(sessionId, token).ConfigureAwait(false);
            WorkflowRunRequest run = WorkflowRunRequests.resume(sessionId, snapshot, context, steps,
                SelectionOf(launch.Parameters), launch.Parameters);
            return await RunControlledAsync(run, launch, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the typed harness through the existing agent interpreter and persists its state.</summary>
    private async Task<WorkflowSessionResult> RunAsync(WorkflowRunRequest request,
        AgentSessionLaunchParameters launch, CancellationToken cancellationToken)
    {
        return await _sessions.RunHarnessAsync(request.SessionId,
            token => RunControlledAsync(request, launch, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowSessionResult> RunControlledAsync(WorkflowRunRequest request,
        AgentSessionLaunchParameters launch, CancellationToken cancellationToken)
    {
        FileWorkflowRunSink sink = new(SessionsRoot);
        WorkflowEffectHostAdapter effects = new(_interpreter, _ => launch.InstructionPrefix ?? string.Empty,
            _hostPrimitives, _sessions.RecordHarnessOutcomeAsync,
            _sessionStore.ResolveSessionDirectory, _sessions.RecordSdkAsync);
        SessionEffectHost liveHost = new(_sessions, effects);
        WorkflowRunOutcome outcome = await new WorkflowExecutor(liveHost, sink)
            .RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (outcome.Status == WorkflowRunStatus.Cancelled &&
            _sessions.TryGetSnapshot(request.SessionId)?.Status == AgentSessionStatus.Stopped)
        {
            outcome = WorkflowRunOutcomes.stopped(outcome);
        }

        // Cancellation must not discard completed tools or the durable cancelled status.
        await _sessions.PersistContextAsync(request.SessionId, outcome.Context, launch, CancellationToken.None)
            .ConfigureAwait(false);
        await _sessions.RecordRunOutcomeAsync(request.SessionId, HostStatusOf(outcome.Status), outcome.Detail,
            CancellationToken.None).ConfigureAwait(false);
        return new WorkflowSessionResult(Snapshot(request.SessionId, outcome), outcome);
    }

    private sealed class SessionEffectHost(
        AgentSessionService sessions,
        IWorkflowEffectHost effects) : IWorkflowEffectHost, IWorkflowSdkHost
    {
        public string DescribeSdk(Patchouli.Agent.Sdk.ExportedTool[] exports)
        {
            return effects is IWorkflowSdkHost sdk ? sdk.DescribeSdk(exports) : "";
        }

        public async Task<WorkflowEffectOutcome> ExecuteEffectAsync(string sessionId, WorkflowEffectScope scope,
            Effect effect, CancellationToken cancellationToken)
        {
            await sessions.CheckpointHarnessAsync(sessionId, scope.Context, effect, cancellationToken)
                .ConfigureAwait(false);
            WorkflowEffectOutcome outcome = await effects
                .ExecuteEffectAsync(sessionId, scope, effect, cancellationToken)
                .ConfigureAwait(false);
            if (outcome is WorkflowEffectOutcome.CompletedEvent completed)
            {
                await sessions
                    .RecordHarnessResultAsync(sessionId, effect, completed.resultEvent, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (outcome is WorkflowEffectOutcome.ObservedEvent observed)
            {
                await sessions.RecordHarnessResultAsync(sessionId, effect, observed.resultEvent, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (outcome is WorkflowEffectOutcome.FailedEvent failed)
            {
                await sessions.RecordHarnessResultAsync(sessionId, effect, failed.resultEvent, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return outcome;
        }
    }

    /// <summary>The durable host status of one run outcome; only an unfinished run maps to a failure.</summary>
    private static AgentSessionStatus HostStatusOf(WorkflowRunStatus status)
    {
        return status switch
        {
            WorkflowRunStatus.Finished => AgentSessionStatus.Finished,
            WorkflowRunStatus.Stopped => AgentSessionStatus.Stopped,
            WorkflowRunStatus.Cancelled => AgentSessionStatus.Cancelled,
            _ => AgentSessionStatus.Failed
        };
    }

    private AgentSessionSnapshot Snapshot(string sessionId, WorkflowRunOutcome outcome)
    {
        return _sessions.TryGetSnapshot(sessionId) ?? new AgentSessionSnapshot(sessionId, AgentSessionStatus.Running,
            outcome.Context.EventSeq, outcome.Context.History.Length, 0, 0, outcome.Detail, DateTimeOffset.UtcNow);
    }

    /// <summary>The selection a recorded launch carries (documents, page range, target language).</summary>
    private static WorkflowSelection SelectionOf(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue(SelectionParameter, out string? recordedSelection))
        {
            return JsonSerializer.Deserialize<WorkflowSelection>(recordedSelection)
                   ?? throw new InvalidDataException("The workflow launch contains no recorded selection.");
        }

        string documentId = Parameter(parameters, DocumentIdParameter);
        return new WorkflowSelection(
            string.IsNullOrWhiteSpace(documentId) ? [] : [documentId],
            Parameter(parameters, PageRangeParameter),
            Parameter(parameters, TargetLanguageParameter),
            []);
    }

    private static string Parameter(IReadOnlyDictionary<string, string> parameters, string key)
    {
        return parameters.TryGetValue(key, out string? value) ? value : string.Empty;
    }

    /// <summary>The instruction head a workflow session starts with (deterministic, prefix-cache stable).</summary>
    private static string BaseInstructions(WorkflowScriptSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return "Patchouli workflow session for '" + snapshot.WorkflowId + "' (" + snapshot.WorkflowName + ").\n" +
               "Script contract " + snapshot.ApiVersion + ", script hash " + snapshot.ScriptHash + ".\n" +
               "You are the Patchouli agent controlled by a typed workflow harness. Use tools to fulfill each stage goal.";
    }

    private static string WorkflowUri(string workflowId)
    {
        return "patchouli://workflows/" + workflowId;
    }

    private static void MergeDefault(Dictionary<string, string> merged, string key, string value)
    {
        if (!merged.ContainsKey(key))
        {
            merged[key] = value;
        }
    }

    private static string Text(bool value)
    {
        return value ? "true" : "false";
    }
}
