namespace Patchouli.Workflows

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Patchouli.Agent
open Patchouli.Workflows.Scripting
open Patchouli.Agent.Sdk

/// <summary>Log kinds the workflow executor records in a session's append-only log.</summary>
[<RequireQualifiedAccess>]
module WorkflowLogKinds =
    /// <summary>The script snapshot the session started with (written once per session).</summary>
    [<Literal>]
    let ScriptSnapshot = "workflow-script"

    /// <summary>One recorded run step, serialized with <c>WorkflowCodec.stepToJson</c>.</summary>
    [<Literal>]
    let Step = "workflow-step"

/// <summary>Host view of a workflow run's lifecycle.</summary>
type WorkflowRunStatus =
    | Running = 0

    /// <summary>The script requested <c>finish</c>, or returned without another request.</summary>
    | Finished = 1

    /// <summary>The script requested <c>stop</c>; the run stays resumable from its recorded steps.</summary>
    | Stopped = 2

    /// <summary>The script failed to compile, threw, or the host reported a failure.</summary>
    | Failed = 3

    /// <summary>The caller cancelled the run; recorded steps stay reusable.</summary>
    | Cancelled = 4

/// <summary>One effect a run issued, for the run log and for tests.</summary>
/// <param name="Step">The step that issued the effect.</param>
/// <param name="EffectId">The effect id minted by the agent core (0 for a terminal effect).</param>
/// <param name="Kind">The core effect kind, for example <c>LlmChat</c> or <c>McpToolCall</c>.</param>
type WorkflowIssuedEffect =
    { Step: int
      EffectId: int64
      Kind: string }

/// <summary>The outcome of executing one effect for a workflow run.</summary>
type WorkflowEffectOutcome =
    /// <summary>The effect produced the event to feed back into the core.</summary>
    | CompletedEvent of resultEvent: Event * summary: string
    | ObservedEvent of resultEvent: Event * summary: string * operations: SdkObservation[]

    /// A host failure is still conversation evidence and must remain visible and replayable.
    | FailedEvent of resultEvent: Event * summary: string

    /// <summary>A terminal effect (<c>Finish</c> / <c>Stop</c>) changed the run status.</summary>
    | TerminalEffect of status: WorkflowRunStatus * summary: string

/// <summary>The immutable agent context after every preceding effect of this run.</summary>
/// <param name="Context">The core context after every effect dispatched so far in this run.</param>
[<Sealed>]
type WorkflowEffectScope(context: Context, allowedTools: string[] option, primitiveLimit: int option,
                         exports: ExportedTool[], activation: string) =
    new(context: Context) = WorkflowEffectScope(context, None, None, [||], "chat")
    member _.Context = context
    member _.AllowedTools = allowedTools
    member _.PrimitiveLimit = primitiveLimit
    member _.Exports = exports
    member _.Activation = activation

/// <summary>
///     The host surface one workflow run needs: execute the effects the script API issues and return
///     the event to feed back (ADR 0036). This is the integration point with
///     <c>Patchouli.Host</c>: the production implementation adapts the existing
///     <c>AgentEffectInterpreter</c> / <c>AgentSessionService</c> shape, so the executor never
///     depends on the session service itself and stays unit-testable with a stub.
/// </summary>
type IWorkflowEffectHost =
    /// <summary>
    ///     Executes one effect of one session under the run state the executor has reached by then
    ///     (<see cref="WorkflowEffectScope" />) and reports the event to feed back. A terminal effect
    ///     reports <see cref="WorkflowEffectOutcome.TerminalEffect" />. A started <c>put</c> is never
    ///     revoked: it completes its commit or rollback and that outcome is recorded.
    /// </summary>
    abstract ExecuteEffectAsync:
        sessionId: string * scope: WorkflowEffectScope * effect: Effect * cancellationToken: CancellationToken ->
            Task<WorkflowEffectOutcome>

/// The host supplies the exact generated SDK declarations; the driver journals them in history.
type IWorkflowSdkHost =
    abstract DescribeSdk: exports: ExportedTool[] -> string

/// <summary>
///     The recording surface of a workflow run: the session's script snapshot and its append-only
///     step log. The host persists the core <c>Context</c> itself (the executor returns it), so this
///     sink never writes a second context document.
/// </summary>
type IWorkflowRunSink =
    /// <summary>
    ///     Stores the script snapshot the session started with. The implementation must be
    ///     idempotent and must keep the original snapshot of an existing session: editing a workflow
    ///     definition never changes a running session.
    /// </summary>
    abstract RecordScriptSnapshotAsync:
        sessionId: string * snapshot: WorkflowScriptSnapshot * cancellationToken: CancellationToken -> Task

    /// <summary>Appends one performed step to the session's append-only step log.</summary>
    abstract RecordStepAsync:
        sessionId: string * step: WorkflowStepRecord * cancellationToken: CancellationToken -> Task

/// <summary>The run sinks Patchouli ships.</summary>
[<RequireQualifiedAccess>]
module WorkflowRunSinks =
    /// <summary>A sink that records nothing (a run without a session directory, for example a test).</summary>
    let noop: IWorkflowRunSink =
        { new IWorkflowRunSink with
            member _.RecordScriptSnapshotAsync(_sessionId, _snapshot, _cancellationToken) = Task.CompletedTask
            member _.RecordStepAsync(_sessionId, _step, _cancellationToken) = Task.CompletedTask }

/// <summary>
///     File-backed run sink: one <c>workflow/</c> subdirectory inside a session directory that holds
///     the script snapshot, the exact snapshot script text and the append-only step log.
/// </summary>
/// <remarks>
///     The layout is deliberately a subdirectory of the host session directory (the
///     <c>agent-sessions/&lt;sessionId&gt;/</c> directory owned by <c>AgentSessionStore</c>), so it
///     never collides with the session's own <c>launch.json</c>, <c>snapshot.json</c>,
///     <c>context.json</c> and <c>events.jsonl</c>. A host that prefers to keep everything in one
///     log can append <see cref="WorkflowLogKinds.Step" /> entries to its own event log with
///     <c>WorkflowCodec.stepToJson</c> instead of using this sink.
/// </remarks>
[<Sealed>]
type FileWorkflowRunSink(sessionsRoot: string) =
    static let sessionIdPattern = Regex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)

    do
        if String.IsNullOrWhiteSpace sessionsRoot then
            nullArg "sessionsRoot"

    let resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath sessionsRoot)

    let workflowDirectory (sessionId: string) =
        if String.IsNullOrWhiteSpace sessionId || not (sessionIdPattern.IsMatch sessionId) then
            invalidArg "sessionId" $"Invalid agent session id '{sessionId}'."

        Path.Combine(resolvedRoot, sessionId, "workflow")

    let recordScriptSnapshotAsync (sessionId: string) (snapshot: WorkflowScriptSnapshot)
                                  (cancellationToken: CancellationToken) : Task =
        task {
            let directory = workflowDirectory sessionId
            Directory.CreateDirectory directory |> ignore
            let snapshotPath = Path.Combine(directory, "snapshot.json")
            if not (File.Exists snapshotPath) then
                do!
                    FileWorkflowRunSink.WriteAtomicAsync(snapshotPath,
                        WorkflowCodec.snapshotToJson snapshot, cancellationToken)

                do!
                    FileWorkflowRunSink.WriteAtomicAsync(Path.Combine(directory, "script.fsx"),
                        snapshot.ScriptText, cancellationToken)
        }

    let recordStepAsync (sessionId: string) (step: WorkflowStepRecord) (cancellationToken: CancellationToken)
        : Task =
        task {
            let directory = workflowDirectory sessionId
            Directory.CreateDirectory directory |> ignore
            let path = Path.Combine(directory, "steps.jsonl")
            do! File.AppendAllTextAsync(path, WorkflowCodec.stepToJson step + "\n", Encoding.UTF8, cancellationToken)
        }

    /// <summary>The sessions root this sink writes into.</summary>
    member _.SessionsRoot = resolvedRoot

    /// <summary>The workflow directory of one session (it may not exist yet).</summary>
    member _.ResolveWorkflowDirectory(sessionId: string) : string = workflowDirectory sessionId

    /// <summary>
    ///     Stores the session's script snapshot once. If a snapshot already exists it is kept
    ///     unchanged, so a running session always executes the script it started with.
    /// </summary>
    member _.RecordScriptSnapshotAsync(sessionId: string, snapshot: WorkflowScriptSnapshot,
                                       cancellationToken: CancellationToken) : Task =
        if Object.ReferenceEquals(snapshot, null) then
            nullArg "snapshot"

        recordScriptSnapshotAsync sessionId snapshot cancellationToken

    /// <summary>Appends one performed step to the session's step log.</summary>
    member _.RecordStepAsync(sessionId: string, step: WorkflowStepRecord, cancellationToken: CancellationToken)
        : Task =
        if Object.ReferenceEquals(step, null) then
            nullArg "step"

        recordStepAsync sessionId step cancellationToken

    /// <summary>Reads the stored snapshot of one session, or <c>None</c> when it has none yet.</summary>
    member _.ReadSnapshotAsync(sessionId: string, cancellationToken: CancellationToken)
        : Task<WorkflowScriptSnapshot option> =
        task {
            let path = Path.Combine(workflowDirectory sessionId, "snapshot.json")
            if not (File.Exists path) then
                return None
            else
                let! json = File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                return Some(WorkflowCodec.snapshotFromJson json)
        }

    /// <summary>Reads the recorded steps of one session in recorded order.</summary>
    member _.ReadStepsAsync(sessionId: string, cancellationToken: CancellationToken)
        : Task<WorkflowStepRecord[]> =
        task {
            let path = Path.Combine(workflowDirectory sessionId, "steps.jsonl")
            if not (File.Exists path) then
                return [||]
            else
                let! lines = File.ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken)
                let steps = ResizeArray<WorkflowStepRecord>()
                for line in lines do
                    if not (String.IsNullOrWhiteSpace line) then
                        steps.Add(WorkflowCodec.stepFromJson line)
                return steps.ToArray()
        }

    interface IWorkflowCheckpointSink with
        member _.ReadCheckpointsAsync(sessionId, token) = task {
            let path = Path.Combine(workflowDirectory sessionId, "checkpoints.jsonl")
            if not (File.Exists path) then return [||]
            else
                let! lines = File.ReadAllLinesAsync(path, Encoding.UTF8, token)
                // Corrupt completed records are errors, not permission to execute their effects again.
                return lines |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.map WorkflowCheckpoints.codec.Decode }
        member _.RecordCheckpointAsync(sessionId, checkpoint, token) = task {
            let directory = workflowDirectory sessionId
            Directory.CreateDirectory directory |> ignore
            do! File.AppendAllTextAsync(Path.Combine(directory, "checkpoints.jsonl"),
                WorkflowCheckpoints.codec.Encode checkpoint + "\n", Encoding.UTF8, token) }

    interface IWorkflowRunSink with
        member _.RecordScriptSnapshotAsync(sessionId, snapshot, cancellationToken) =
            recordScriptSnapshotAsync sessionId snapshot cancellationToken

        member _.RecordStepAsync(sessionId, step, cancellationToken) =
            recordStepAsync sessionId step cancellationToken

    static member private WriteAtomicAsync(path: string, content: string, cancellationToken: CancellationToken)
        : Task =
        task {
            let directory = WorkflowPaths.parentDirectory path
            Directory.CreateDirectory directory |> ignore

            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
            try
                do! File.WriteAllTextAsync(temporary, WorkflowText.orEmpty content, UTF8Encoding(false), cancellationToken)
                File.Move(temporary, path, true)
            finally
                if File.Exists temporary then
                    File.Delete temporary
        }

/// <summary>
///     One run request: everything the executor needs, with nothing it must look up. The snapshot,
///     the core context and the recorded steps all come from the session's own storage, so a run
///     never re-reads the workflow definition (ADR 0036 script snapshots).
/// </summary>
/// <param name="SessionId">The session (run) id.</param>
/// <param name="Snapshot">The script snapshot the session executes.</param>
/// <param name="Context">The recovered core context (pipeline: <c>AgentCore.initial</c> on a first run).</param>
/// <param name="RecordedSteps">The recorded steps to reuse, in recorded order.</param>
/// <param name="Selection">The selection the session was launched against.</param>
/// <param name="Parameters">The launch parameters, in recorded order.</param>
type WorkflowRunRequest =
    { SessionId: string
      Snapshot: WorkflowScriptSnapshot
      Context: Context
      RecordedSteps: WorkflowStepRecord[]
      Selection: WorkflowSelection
      Parameters: IReadOnlyDictionary<string, string> }

/// <summary>Constructors for run requests.</summary>
[<RequireQualifiedAccess>]
module WorkflowRunRequests =
    let private nonNull (values: 'T) (fallback: 'T) : 'T =
        if Object.ReferenceEquals(values, null) then fallback else values

    let private emptyParameters: IReadOnlyDictionary<string, string> =
        Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

    /// <summary>
    ///     Creates the request of a brand-new session from a workflow definition and its script text.
    ///     The script text is captured into a snapshot here, so later edits to the workflow
    ///     definition cannot reach this run.
    /// </summary>
    let create (sessionId: string) (definition: WorkflowDefinition) (scriptText: string)
               (selection: WorkflowSelection) (parameters: IReadOnlyDictionary<string, string>)
               (launchedAt: DateTimeOffset) : WorkflowRunRequest =
        if Object.ReferenceEquals(definition, null) then
            nullArg "definition"

        { SessionId = sessionId
          Snapshot = WorkflowSnapshots.capture definition scriptText ScriptApiVersion.Current launchedAt
          Context = AgentCore.initial
          RecordedSteps = [||]
          Selection = selection
          Parameters = nonNull parameters emptyParameters }

    /// <summary>
    ///     Resumes one session from its stored snapshot, its recovered core context and its recorded
    ///     steps. The recorded steps are the material a replay reuses instead of re-invoking models
    ///     or tools.
    /// </summary>
    let resume (sessionId: string) (snapshot: WorkflowScriptSnapshot) (context: Context)
               (recordedSteps: WorkflowStepRecord[]) (selection: WorkflowSelection)
               (parameters: IReadOnlyDictionary<string, string>) : WorkflowRunRequest =
        { SessionId = sessionId
          Snapshot = snapshot
          Context = context
          RecordedSteps = nonNull recordedSteps [||]
          Selection = selection
          Parameters = nonNull parameters emptyParameters }

/// <summary>The result of one workflow run.</summary>
/// <param name="SessionId">The session (run) id.</param>
/// <param name="Status">How the run ended.</param>
/// <param name="Context">The core context after the run (history, call ledger, counters).</param>
/// <param name="Steps">The full step log after the run: recovered steps first, then the steps performed now.</param>
/// <param name="NewSteps">Only the steps performed by this run, in order (the part a host appends).</param>
/// <param name="IssuedEffects">Every effect this run issued, in issue order.</param>
/// <param name="Diagnostics">Compile diagnostics; empty for a script that compiled.</param>
/// <param name="Detail">Short human-readable description of the outcome.</param>
type WorkflowRunOutcome =
    { SessionId: string
      Status: WorkflowRunStatus
      Context: Context
      Steps: WorkflowStepRecord[]
      NewSteps: WorkflowStepRecord[]
      IssuedEffects: WorkflowIssuedEffect[]
      Diagnostics: ScriptDiagnostic[]
      Detail: string }

[<RequireQualifiedAccess>]
module WorkflowRunOutcomes =
    let stopped outcome =
        { outcome with Status = WorkflowRunStatus.Stopped
                       Context = { outcome.Context with Status = RunStatus.Stopped }
                       Detail = "Agent workflow stopped by the host." }
