namespace Patchouli.Workflows

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Agent
open Patchouli.Workflows.Scripting
open Patchouli.Agent.Sdk

type internal HarnessState =
    { Context: Context
      Replay: WorkflowStepRecord list
      Completed: WorkflowStepRecord list
      Fresh: WorkflowStepRecord list
      Issued: WorkflowIssuedEffect list
      Position: int
      Checkpoints: Map<string, WorkflowCheckpoint>
      Plan: string }

type internal AgentRefusal =
    | ForbiddenTool of effectId: EffectId * name: string
    | ExhaustedBudget of reason: string

module internal HarnessRuntime =
    let kind = function
        | LlmChat _ -> "LlmChat" | McpToolCall _ -> "McpToolCall" | WaitRunEvent _ -> "WaitRunEvent"
        | other -> invalidArg "effect" $"Unexpected harness effect: {other}"

    let effectId = function
        | LlmChat(EffectId id) | McpToolCall(EffectId id, _, _) | WaitRunEvent(EffectId id, _, _) -> id
        | other -> invalidArg "effect" $"Unexpected harness effect: {other}"

    let payload effect result =
        match effect, result with
        | LlmChat id, AssistantReply(resultId, reply) when id = resultId -> AssistantReplies.toJson reply
        | LlmChat id, ModelResult(resultId, text) when id = resultId -> text
        | LlmChat id, ModelFailure(resultId, code, detail, retryable) when id = resultId ->
            JsonSerializer.Serialize {| code = code; detail = detail; retryable = retryable |}
        | McpToolCall(id, name, _), ToolResult(resultId, resultName, text)
        | McpToolCall(id, name, _), ToolFailure(resultId, resultName, text)
            when id = resultId && name = resultName -> text
        | WaitRunEvent(_, id, _), RunEvent(resultId, text) when id = resultId -> text
        | _ -> invalidOp "WORKFLOW_EFFECT_MISMATCH: host result does not match the issued effect."

    let restoredEvent effect (text: string) native modelFailed =
        match effect with
        | LlmChat id when modelFailed ->
            use document = JsonDocument.Parse text
            let root = document.RootElement
            let field (name: string) =
                match root.GetProperty(name).GetString() with
                | null -> invalidOp ("WORKFLOW_REPLAY_INVALID: missing model diagnostic " + name)
                | value -> value
            ModelFailure(id, field "code", field "detail",
                         root.GetProperty("retryable").GetBoolean())
        | LlmChat id when native -> AssistantReply(id, AssistantReplies.fromJson text)
        | LlmChat id -> ModelResult(id, text)
        | McpToolCall(id, name, _) -> ToolResult(id, name, text)
        | WaitRunEvent(_, id, _) -> RunEvent(id, text)
        | other -> invalidArg "effect" $"Unexpected harness effect: {other}"

    let dispatch (request: WorkflowRunRequest) (host: IWorkflowEffectHost) (sink: IWorkflowRunSink)
                 (token: CancellationToken) (scope: WorkflowEffectScope) key effect state =
        task {
            try
                token.ThrowIfCancellationRequested()
                let position = state.Position + 1
                let expectedKind = kind effect
                let id = effectId effect
                match state.Replay with
                | recorded :: remaining ->
                    let observed = recorded.Kind.Contains(".Observed", StringComparison.Ordinal)
                    let recordedKind = recorded.Kind.Replace(".Observed", "")
                    let failed = recordedKind = expectedKind + ".Failed" || recordedKind = expectedKind + ".ModelFailure.Failed"
                    let modelFailed = recordedKind = expectedKind + ".ModelFailure" || recordedKind = expectedKind + ".ModelFailure.Failed"
                    let native = recordedKind = expectedKind + ".Native"
                    let toolFailed = recordedKind = expectedKind + ".ToolFailure"
                    if not recorded.Completed || recorded.Step <> position || (recordedKind <> expectedKind && not failed && not toolFailed && not native && not modelFailed)
                       || recorded.Request <> key || recorded.EffectId <> id then
                        return Error(state, WorkflowRunStatus.Failed,
                                     $"WORKFLOW_REPLAY_DIVERGED: step {position} differs from its recorded control path or request.")
                    else
                        let data, operations =
                            if observed then
                                use json = JsonDocument.Parse recorded.Result
                                json.RootElement.GetProperty("event").GetString() |> Option.ofObj |> Option.defaultValue "",
                                (match JsonSerializer.Deserialize<SdkObservation[]>(json.RootElement.GetProperty("operations").GetRawText()) with
                                 | null -> raise (JsonException("Missing SDK observations."))
                                 | value -> value)
                            else recorded.Result, [||]
                        let result =
                            match effect with
                            | McpToolCall(id, name, _) when toolFailed -> ToolFailure(id, name, data)
                            | _ -> restoredEvent effect data native modelFailed
                        let replayed = { state with Replay = remaining; Position = position; Completed = recorded :: state.Completed }
                        if failed then
                            return Error({ replayed with Context = AgentCore.observeEffectResult state.Context effect result },
                                         WorkflowRunStatus.Failed, (match result with ModelFailure(_, _, detail, _) -> detail | _ -> recorded.Result))
                        else return Ok(result, replayed, operations)
                | [] ->
                    let issued = { Step = position; EffectId = id; Kind = expectedKind }
                    let! outcome = host.ExecuteEffectAsync(request.SessionId,
                        scope, effect, token)
                    match outcome with
                    | TerminalEffect(WorkflowRunStatus.Finished, _) ->
                        return Error(state, WorkflowRunStatus.Failed,
                                     "WORKFLOW_EFFECT_MISMATCH: an agent effect cannot finish the control plan.")
                    | TerminalEffect(status, detail) -> return Error(state, status, detail)
                    | CompletedEvent(result, _)
                    | ObservedEvent(result, _, _)
                    | FailedEvent(result, _) ->
                        let failed = match outcome with FailedEvent _ -> true | _ -> false
                        let operations = match outcome with ObservedEvent(_, _, operations) -> operations | _ -> [||]
                        let observed = operations.Length > 0
                        let suffix = match result with
                                     | ModelFailure _ -> if failed then ".ModelFailure.Failed" else ".ModelFailure"
                                     | _ when failed -> ".Failed"
                                     | ToolFailure _ -> ".ToolFailure" | AssistantReply _ -> ".Native" | _ -> ""
                        let record = { Step = position; EffectId = id; Kind = expectedKind + (if observed then ".Observed" else "") + suffix; Request = key
                                       Completed = true; Result = (if observed then JsonSerializer.Serialize {| event = payload effect result; operations = operations |} else payload effect result) }
                        // Persist a completed atomic tool even if cancellation arrived during its commit.
                        do! sink.RecordStepAsync(request.SessionId, record, CancellationToken.None)
                        let recorded = { state with Position = position; Completed = record :: state.Completed
                                                    Fresh = record :: state.Fresh; Issued = issued :: state.Issued }
                        if failed then
                            return Error({ recorded with Context = AgentCore.observeEffectResult state.Context effect result },
                                         WorkflowRunStatus.Failed, (match result with ModelFailure(_, _, detail, _) -> detail | _ -> record.Result))
                        else return Ok(result, recorded, operations)
            with
            | :? OperationCanceledException -> return Error(state, WorkflowRunStatus.Cancelled, "Workflow cancelled.")
            | error -> return Error(state, WorkflowRunStatus.Failed, error.Message)
        }

    let stagePrompt policy prompt =
        let tools = policy.Tools |> Set.toList |> String.concat ", "
        let required = policy.RequiredTools |> Set.toList |> String.concat ", "
        $"AGENT STAGE: {policy.Name}\n{policy.Instructions}\n" +
        $"Stage tool allowlist: [{tools}]. Model-turn budget: {policy.Budget.ModelTurns}; tool-call budget: {policy.Budget.ToolCalls}.\n" +
        $"Tools that must be attempted before completing this stage: {required}.\n" +
        "Use provider-native function calls for tools; put explanation only in assistant text. " +
        "Await paired tool results and correct failed arguments. Ordinary text with no native calls ends the stage.\nTASK:\n" + prompt

    let episode request (host: IWorkflowEffectHost) sink token path policy shared prompt exports validate state =
        let stage = stagePrompt policy prompt
        let sharedMode = not (String.IsNullOrWhiteSpace shared)
        let rules = if sharedMode then stagePrompt policy "" else ""
        let sharedKey = if sharedMode then Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes shared)) else ""
        let sdkInstructions =
            if Array.isEmpty exports then "" else
            match host with
            | :? IWorkflowSdkHost as sdk -> sdk.DescribeSdk exports
            | _ -> exports |> Array.map (fun tool -> tool.NativeDefinition) |> String.concat "\n"
        let prepared = state.Context |> AgentCore.ensureInstructions shared |> AgentCore.ensureInstructions rules |> AgentCore.ensureInstructions sdkInstructions
        let context, pending = AgentCore.chatStep prepared [] (UserMessage("workflow/" + path, if sharedMode then prompt else stage))
        let advance (models, tools, used, evidence, pending, state) =
            task {
                match pending with
                | [] when state.Context.Status = RunStatus.Stopped ->
                    let detail = match List.tryLast state.Context.History with
                                 | Some(HistoryEntry.ToolResult("agent-retry", detail)) -> detail
                                 | _ -> "Agent yielded control."
                    return AgentTurn.Complete(Error(state, WorkflowRunStatus.Stopped, detail))
                | [] ->
                    let missing = Set.difference policy.RequiredTools used
                    if not missing.IsEmpty then
                        return AgentTurn.Complete(Error(state, WorkflowRunStatus.Failed,
                            "AGENT_REQUIRED_TOOLS_MISSING: " + String.concat ", " (Set.toList missing)))
                    else
                        let answer = state.Context.History |> List.tryLast |> Option.bind (function
                            | HistoryEntry.AssistantReply reply -> Some reply.Text
                            | HistoryEntry.ModelResult answer -> Some answer
                            | _ -> None)
                        match answer with
                        | Some answer ->
                            let validated = try Ok(validate (evidence |> List.rev |> List.toArray) answer) with error -> Error error.Message
                            match validated with
                            | Error reason -> return AgentTurn.Complete(Error(state, WorkflowRunStatus.Failed, reason))
                            | Ok(Ok ()) ->
                                let response = JsonSerializer.Serialize {| answer = answer; operations = evidence |> List.rev |> List.toArray |}
                                return AgentTurn.Complete(Ok(response, { state with Context = { state.Context with Retry = { state.Context.Retry with Attempts = 0 } } }))
                            | Ok(Error reason) ->
                                let next, effects = AgentCore.rejectOutput (policy.Name + ": " + reason) state.Context
                                return AgentTurn.Continue(models, tools, used, evidence, effects, { state with Context = next })
                        | _ -> return AgentTurn.Complete(Error(state, WorkflowRunStatus.Failed, "AGENT_STAGE_EMPTY: no final agent reply."))
                | [effect] ->
                    let denied =
                        match effect with
                        | LlmChat _ when state.Context.Retry.Attempts = 0 && models >= policy.Budget.ModelTurns -> Some(ExhaustedBudget "AGENT_MODEL_BUDGET_EXHAUSTED")
                        | McpToolCall(id, name, _) when not (policy.Tools.Contains name) -> Some(ForbiddenTool(id, name))
                        | McpToolCall(_, name, _) when name <> "fsi" && not (exports |> Array.exists (fun tool -> tool.Name = name)) && tools >= policy.Budget.ToolCalls -> Some(ExhaustedBudget "AGENT_TOOL_BUDGET_EXHAUSTED")
                        | _ -> None
                    match denied with
                    | Some(ForbiddenTool(id, name)) ->
                        let allowed = String.concat ", " (Set.toList policy.Tools)
                        let detail = $"AGENT_TOOL_DENIED: {name}; stage '{policy.Name}' permits only [{allowed}]. Nothing was executed."
                        let next, effects = AgentCore.chatStep state.Context [] (ToolFailure(id, name, detail))
                        return AgentTurn.Continue(models, tools, used, evidence, effects, { state with Context = next })
                    | Some(ExhaustedBudget reason) -> return AgentTurn.Complete(Error(state, WorkflowRunStatus.Failed, $"{reason}: {policy.Name}"))
                    | None ->
                        let state =
                            match effect with
                            | LlmChat _ when state.Context.Retry.Attempts = 0 ->
                                let limit =
                                    match state.Replay with
                                    | recorded :: _ ->
                                        use json = JsonDocument.Parse recorded.Request
                                        json.RootElement.GetProperty("RetryLimit").GetInt32()
                                    | [] -> match host with :? IWorkflowRetryPolicyHost as policyHost -> policyHost.MaxRequestRetries | _ -> state.Context.Retry.Limit
                                { state with Context = AgentCore.withRetryLimit limit state.Context }
                            | _ -> state
                        let arguments = match effect with McpToolCall(_, _, args) -> args | _ -> ""
                        let tool = match effect with McpToolCall(_, name, _) -> name | _ -> ""
                        let contracts = exports |> Array.map (fun (tool: ExportedTool) -> tool.Identity) |> String.concat "/"
                        let key = JsonSerializer.Serialize {| Path = path; Stage = (if sharedMode then rules else stage);
                                                             Shared = sharedKey; Prompt = (if sharedMode then prompt else ""); Models = models;
                                                             RetryAttempt = state.Context.Retry.Attempts; RetryLimit = state.Context.Retry.Limit;
                                                             Tools = tools; Tool = tool; Arguments = arguments; Sdk = contracts; SdkDeclarations = WorkflowSnapshots.hashScript sdkInstructions |}
                        let scope = WorkflowEffectScope(state.Context, Some(Set.toArray policy.Tools), Some(policy.Budget.ToolCalls - tools), exports, state.Plan + "/" + path + "/" + contracts)
                        let! executed = dispatch request host sink token scope key effect state
                        match executed with
                        | Error failure -> return AgentTurn.Complete(Error failure)
                        | Ok(result, recorded, operations) ->
                            // The exact decision point used by chat owns model/tool iteration and parsing.
                            let next, effects = AgentCore.chatStep recorded.Context [] result
                            let unknown = operations |> Array.exists (fun op -> op.ErrorCode = "SDK_OPERATION_UNKNOWN")
                            let modelCount = models + (match effect with LlmChat _ when recorded.Context.Retry.Attempts = 0 -> 1 | _ -> 0)
                            let toolCount = tools + (match effect with
                                                     | McpToolCall(_, name, _) when name = "fsi" || exports |> Array.exists (fun tool -> tool.Name = name) -> operations.Length
                                                     | McpToolCall _ -> max 1 operations.Length | _ -> 0)
                            let usedTools = operations |> Array.fold (fun names op -> Set.add op.Name names) used
                            let usedTools = match effect with McpToolCall(_, name, _) -> Set.add name usedTools | _ -> usedTools
                            let evidence = (operations |> Array.rev |> Array.toList) @ evidence
                            if unknown then
                                return AgentTurn.Complete(Error({ recorded with Context = { next with Status = RunStatus.Stopped } },
                                    WorkflowRunStatus.Failed, "SDK_OPERATION_UNKNOWN: reconcile the actual resource before continuing this workflow."))
                            else
                                return AgentTurn.Continue(modelCount, toolCount, usedTools, evidence, effects, { recorded with Context = next })
                | _ -> return AgentTurn.Complete(Error(state, WorkflowRunStatus.Failed, "AGENT_EFFECT_CARDINALITY: expected one agent effect."))
            }
        AgentDriver.RunAsync((0, 0, Set.empty, [], pending, { state with Context = context }),
            Func<_, _, _>(fun cursor _ -> advance cursor), token)

    let checkpoint (request: WorkflowRunRequest) (sink: IWorkflowRunSink) (token: CancellationToken) record state = task {
        match sink with
        | :? IWorkflowCheckpointSink as durable -> do! durable.RecordCheckpointAsync(request.SessionId, record, token)
        | _ -> ()
        return { state with Checkpoints = state.Checkpoints.Add(record.Path, record) } }

    let node (request: WorkflowRunRequest) sink token command state = task {
        match command with
        | LoadNode(path, inputSchema, outputSchema, input) ->
            match state.Checkpoints.TryFind path with
            | Some previous ->
                if previous.Plan <> state.Plan || previous.InputSchema <> inputSchema || previous.OutputSchema <> outputSchema || previous.Input <> input then
                    return Error(state, WorkflowRunStatus.Failed, "WORKFLOW_CHECKPOINT_DIVERGED: " + path)
                else return Ok(defaultArg previous.Output "", state)
            | None ->
                let record = { Path = path; Plan = state.Plan; InputSchema = inputSchema; OutputSchema = outputSchema; Input = input
                               Output = None; Route = None; Position = state.Position; Context = ContextCodec.toJson state.Context }
                let! next = checkpoint request sink token record state
                return Ok("", next)
        | SaveNode(path, output) ->
            let record = { state.Checkpoints[path] with Output = Some output; Position = state.Position; Context = ContextCodec.toJson state.Context }
            let! next = checkpoint request sink token record state
            return Ok("", next)
        | SelectRoute(path, select) ->
            match state.Checkpoints.TryFind path with
            | Some { Route = Some route; Plan = plan } when plan = state.Plan -> return Ok(route, state)
            | Some _ -> return Error(state, WorkflowRunStatus.Failed, "WORKFLOW_ROUTE_DIVERGED: " + path)
            | None ->
                let route = select()
                let record = { Path = path; Plan = state.Plan; InputSchema = "route"; OutputSchema = "route"; Input = ""
                               Output = None; Route = Some route; Position = state.Position; Context = ContextCodec.toJson state.Context }
                let! next = checkpoint request sink token record state
                return Ok(route, next)
        | _ -> return Error(state, WorkflowRunStatus.Failed, "WORKFLOW_CHECKPOINT_COMMAND_INVALID") }

    let rec interpret request host sink (token: CancellationToken) program state =
        task {
            try
                token.ThrowIfCancellationRequested()
                match program with
                | Return () ->
                    if not state.Replay.IsEmpty then
                        return state, WorkflowRunStatus.Failed, "WORKFLOW_REPLAY_DIVERGED: recorded steps remain."
                    else return state, WorkflowRunStatus.Finished, "Agent workflow finished."
                | Abort reason -> return state, WorkflowRunStatus.Failed, reason
                | Request(command, continueWith) ->
                    let! response =
                        match command with
                        | LoadNode _ | SaveNode _ | SelectRoute _ -> node request sink token command state
                        | RunAgent(path, policy, shared, prompt, exports, validate) -> episode request host sink token path policy shared prompt exports validate state
                        | AwaitEvent(path, uri) ->
                            task {
                                let effect, context = AgentCore.Effects.waitRunEvent uri state.Context
                                let key = JsonSerializer.Serialize {| Path = path; Uri = uri |}
                                let! executed = dispatch request host sink token (WorkflowEffectScope(context)) key effect { state with Context = context }
                                match executed with
                                | Error failure -> return Error failure
                                | Ok(result, recorded, operations) ->
                                    return Ok(payload effect result,
                                        { recorded with Context = AgentCore.observeEffectResult recorded.Context effect result })
                            }
                    match response with
                    | Error(failed, status, detail) -> return failed, status, detail
                    | Ok(text, next) ->
                        let continuation =
                            try Ok(continueWith text)
                            with error -> Error error.Message
                        match continuation with
                        | Ok program -> return! interpret request host sink token program next
                        | Error reason -> return next, WorkflowRunStatus.Failed, reason
            with
            | :? OperationCanceledException -> return state, WorkflowRunStatus.Cancelled, "Workflow cancelled."
            | error -> return state, WorkflowRunStatus.Failed, error.Message
        }

/// Runs cold control plans over the existing agent core and host interpreter.
[<Sealed>]
type WorkflowExecutor(effectHost: IWorkflowEffectHost, runSink: IWorkflowRunSink, compiler: ScriptCompiler) =
    new(effectHost: IWorkflowEffectHost) = WorkflowExecutor(effectHost, WorkflowRunSinks.noop, ScriptCompiler())
    new(effectHost: IWorkflowEffectHost, runSink: IWorkflowRunSink) = WorkflowExecutor(effectHost, runSink, ScriptCompiler())

    member _.RunAsync(request: WorkflowRunRequest, cancellationToken: CancellationToken) : Task<WorkflowRunOutcome> =
        task {
            let retryLimit = request.Context.Retry.Limit
            let state = { Context = AgentCore.withRetryLimit retryLimit AgentCore.initial; Replay = List.ofArray request.RecordedSteps
                          Completed = []; Fresh = []; Issued = []; Position = 0; Checkpoints = Map.empty;
                          Plan = request.Snapshot.ScriptHash + "/" + typeof<AgentWorkflow>.Assembly.ManifestModule.ModuleVersionId.ToString() + "/" + typeof<Context>.Assembly.ManifestModule.ModuleVersionId.ToString() }
            let outcome state status diagnostics detail =
                let coreStatus =
                    match status with
                    | WorkflowRunStatus.Finished -> RunStatus.Finished
                    | WorkflowRunStatus.Cancelled -> RunStatus.Cancelled
                    | _ -> RunStatus.Stopped
                let core =
                    if status <> WorkflowRunStatus.Finished && state.Fresh.IsEmpty
                       && ((state.Position = 0 && state.Context.History.IsEmpty) || not state.Replay.IsEmpty) then request.Context
                    else state.Context
                { SessionId = request.SessionId; Status = status; Context = { core with Status = coreStatus }
                  Steps = Array.append (state.Completed |> List.rev |> List.toArray) (List.toArray state.Replay)
                  NewSteps = state.Fresh |> List.rev |> List.toArray; IssuedEffects = state.Issued |> List.rev |> List.toArray
                  Diagnostics = diagnostics; Detail = detail }
            let fileName = request.Snapshot.WorkflowId + ".fsx"
            if request.Snapshot.ApiVersion <> ScriptApiVersion.Current then
                let detail =
                    if request.Snapshot.ApiVersion = "patchouli.workflow.harness/6" then
                        "WORKFLOW_RETRY_PROTOCOL_CHANGED: this snapshot uses the previous retry/replay protocol. History is preserved; start a new session from the existing workflow. The script does not need to be rewritten."
                    else
                        "WORKFLOW_API_REMOVED: this snapshot uses the deleted workflow API. Rewrite the workflow and start a new session."
                return outcome state WorkflowRunStatus.Failed [||] detail
            else
                try
                    cancellationToken.ThrowIfCancellationRequested()
                    if WorkflowSnapshots.hashScript request.Snapshot.ScriptText <> request.Snapshot.ScriptHash then
                        invalidOp "WORKFLOW_SNAPSHOT_HASH_MISMATCH"
                    if System.Text.RegularExpressions.Regex.IsMatch(request.Snapshot.ScriptText, @"(?m)^\s*#(?:load|r)\b") then
                        invalidOp "WORKFLOW_DEPENDENCY_NOT_FROZEN: external #load/#r dependencies are not included in this snapshot. Use the built-in SDK or keep the cold definitions in the frozen script."
                    let! check = compiler.CheckWorkflowAsync(request.Snapshot.ScriptText, fileName, request.Snapshot.ScriptEntryPoint)
                    if not check.Succeeded then
                        return outcome state WorkflowRunStatus.Failed check.Diagnostics (ScriptDiagnostics.describeAll check.Diagnostics)
                    else
                        do! runSink.RecordScriptSnapshotAsync(request.SessionId, request.Snapshot, cancellationToken)
                        use host = ScriptHostSession.Create()
                        let evaluation = host.EvaluateScript(request.Snapshot.ScriptText, fileName)
                        if not evaluation.Succeeded then
                            return outcome state WorkflowRunStatus.Failed evaluation.Diagnostics evaluation.Failure
                        else
                            let entry = host.EvaluateExpression(request.Snapshot.ScriptEntryPoint, fileName)
                            match entry.Value with
                            | :? AgentWorkflow as plan when entry.Succeeded ->
                                Workflow.bounds plan.Shape |> ignore
                                let selection = request.Selection
                                let input = { Documents = List.ofArray selection.Documents; PageRange = selection.PageRange
                                              Parameters = request.Parameters |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq }
                                let! saved = match runSink with
                                             | :? IWorkflowCheckpointSink as durable -> durable.ReadCheckpointsAsync(request.SessionId, cancellationToken)
                                             | _ -> Task.FromResult [||]
                                if saved |> Array.exists (fun checkpoint -> checkpoint.Plan <> state.Plan) then
                                    return outcome state WorkflowRunStatus.Failed [||] "WORKFLOW_CHECKPOINT_PLAN_MISMATCH"
                                else
                                    let restored = if saved.Length = 0 then state else
                                                       let latest = saved[saved.Length - 1]
                                                       { state with Context = ContextCodec.fromJson latest.Context; Position = latest.Position
                                                                    Completed = request.RecordedSteps |> Array.filter (fun step -> step.Step <= latest.Position) |> Array.rev |> Array.toList
                                                                    Replay = request.RecordedSteps |> Array.filter (fun step -> step.Step > latest.Position) |> Array.toList
                                                                    Checkpoints = saved |> Array.map (fun item -> item.Path, item) |> Map.ofArray }
                                    let! final, status, detail = HarnessRuntime.interpret request effectHost runSink cancellationToken (plan.Build input) restored
                                    return outcome final status [||] detail
                            | _ -> return outcome state WorkflowRunStatus.Failed entry.Diagnostics
                                       "WORKFLOW_ENTRY_INVALID: the entry point must be a cold AgentWorkflow value."
                with
                | :? OperationCanceledException -> return outcome state WorkflowRunStatus.Cancelled [||] "Workflow cancelled."
                | error -> return outcome state WorkflowRunStatus.Failed [||] error.Message
        }
