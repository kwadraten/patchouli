namespace Patchouli.Workflows.Scripting

open System
open Patchouli.Agent.Sdk

/// Closed tool vocabulary. A stage's allowlist can only narrow host permissions.
[<RequireQualifiedAccess>]
type AgentTool = Find | Fetch | Cite | Put | Send | Fsi | History

[<RequireQualifiedAccess>]
module AgentTool =
    let internal name = function
        | AgentTool.Find -> "find" | AgentTool.Fetch -> "fetch" | AgentTool.Cite -> "cite"
        | AgentTool.Put -> "put" | AgentTool.Send -> "send" | AgentTool.Fsi -> "fsi"
        | AgentTool.History -> "history"

/// Positive model-turn and nonnegative tool-call limits, validated at construction.
type AgentBudget = private { Models: int; Tools: int }
    with
        member this.ModelTurns = this.Models
        member this.ToolCalls = this.Tools

[<RequireQualifiedAccess>]
module AgentBudget =
    let create modelTurns toolCalls =
        if modelTurns < 1 then invalidArg "modelTurns" "An agent needs at least one model turn."
        if toolCalls < 0 then invalidArg "toolCalls" "The tool budget cannot be negative."
        { Models = modelTurns; Tools = toolCalls }

type internal AgentPolicy =
    { Name: string
      Instructions: string
      Tools: Set<string>
      RequiredTools: Set<string>
      Budget: AgentBudget }

/// An inspectable control graph. No model call or tool call occurs during construction.
[<RequireQualifiedAccess>]
type ControlShape =
    | Pure
    | AgentStage of name: string * budget: AgentBudget * tools: Set<string> * requiredTools: Set<string>
    | Sequence of ControlShape * ControlShape
    | Branch of ControlShape * ControlShape
    | BoundedLoop of limit: int * body: ControlShape
    | AwaitEvent

type internal HarnessCommand =
    | RunAgent of path: string * policy: AgentPolicy * shared: string * prompt: string * exports: ExportedTool[] * validate: (SdkObservation[] -> string -> Result<unit, string>)
    | AwaitEvent of path: string * runUri: string
    | LoadNode of path: string * inputSchema: string * outputSchema: string * input: string
    | SaveNode of path: string * output: string
    | SelectRoute of path: string * select: (unit -> string)

/// Typed free program. Only the runner interprets commands; continuations remain typed.
type internal HarnessProgram<'a> =
    | Return of 'a
    | Request of HarnessCommand * (string -> HarnessProgram<'a>)
    | Abort of string

module internal Program =
    let rec bind next = function
        | Return value -> next value
        | Request(command, continueWith) -> Request(command, continueWith >> bind next)
        | Abort reason -> Abort reason

/// A cold, typed agent control plan, not a Task or a host RPC facade.
[<Sealed>]
type Workflow<'input, 'output> internal
    (shape: ControlShape, build: string -> 'input -> HarnessProgram<'output>,
     ?inputCodec: ValueCodec<'input>, ?outputCodec: ValueCodec<'output>) =
    member _.Shape = shape
    member internal _.BuildBody path input = build path input
    member internal _.Build path input =
        let inputCodec = match inputCodec with Some codec -> codec | None -> ValueCodec.create<'input>()
        let outputCodec = match outputCodec with Some codec -> codec | None -> ValueCodec.create<'output>()
        Request(LoadNode(path, inputCodec.Identity, outputCodec.Identity, inputCodec.Encode input), fun saved ->
            if saved <> "" then Return(outputCodec.Decode saved)
            else
                build path input |> Program.bind (fun output ->
                    Request(SaveNode(path, outputCodec.Encode output), fun _ -> Return output)))

/// Agent input formatting and output validation are pure; inference and tools belong to AgentCore.
[<Sealed>]
type ChatAgent<'input, 'output> internal
    (policy: AgentPolicy, format: 'input -> string, parse: 'input -> SdkObservation[] -> string -> Result<'output, string>, shared: 'input -> string,
     exports: 'input -> ExportedTool list, evidence: 'input -> SdkObservation[] -> Result<unit, string>) =
    member internal _.Policy = policy
    member internal _.Format = format
    member internal _.Parse = parse
    member internal _.SharedInstructions = shared
    member internal _.Exports = exports
    member internal _.Evidence = evidence

[<RequireQualifiedAccess>]
module Agent =
    let verified name instructions format parse =
        if System.String.IsNullOrWhiteSpace name then invalidArg "name" "Give the agent stage a name."
        ChatAgent({ Name = name; Instructions = instructions; Tools = Set.empty; RequiredTools = Set.empty
                    Budget = AgentBudget.create 8 7 }, format, parse, (fun _ -> ""), (fun _ -> []), (fun _ _ -> Ok ()))

    let typed name instructions format parse = verified name instructions format (fun input _ text -> parse input text)

    let text name instructions format = typed name instructions format (fun _ text -> Ok text)

    let withTools tools (agent: ChatAgent<'i, 'o>) =
        ChatAgent({ agent.Policy with Tools = tools |> List.map AgentTool.name |> Set.ofList },
                  agent.Format, agent.Parse, agent.SharedInstructions, agent.Exports, agent.Evidence)

    let withBudget budget (agent: ChatAgent<'i, 'o>) =
        ChatAgent({ agent.Policy with Budget = budget }, agent.Format, agent.Parse, agent.SharedInstructions, agent.Exports, agent.Evidence)

    /// A final prose reply cannot silently satisfy a stage that must actually act through tools.
    /// This checks completed tool attempts, not the correctness of their business results.
    let requireToolUse tools (agent: ChatAgent<'i, 'o>) =
        ChatAgent({ agent.Policy with RequiredTools = tools |> List.map AgentTool.name |> Set.ofList },
                  agent.Format, agent.Parse, agent.SharedInstructions, agent.Exports, agent.Evidence)

    /// Shared rules are retained once in active conversation history; subsequent requests carry only their task.
    let withSharedInstructions instructions (agent: ChatAgent<'i, 'o>) =
        ChatAgent(agent.Policy, agent.Format, agent.Parse, instructions, agent.Exports, agent.Evidence)

    /// Build frozen, input-bound tools before the stage starts. Both native and FSI see these contracts.
    let withSdkTools exports (agent: ChatAgent<'i, 'o>) =
        ChatAgent(agent.Policy, agent.Format, agent.Parse, agent.SharedInstructions, exports, agent.Evidence)

    /// Final output advances the pipeline only after independently recorded operations validate it.
    let withEvidence validate (agent: ChatAgent<'i, 'o>) =
        ChatAgent(agent.Policy, agent.Format, agent.Parse, agent.SharedInstructions, agent.Exports, validate)

    let run (agent: ChatAgent<'i, 'o>) : Workflow<'i, 'o> =
        if not (Set.isSubset agent.Policy.RequiredTools agent.Policy.Tools) then
            invalidArg "agent" "Required tools must belong to the stage allowlist."
        Workflow(ControlShape.AgentStage(agent.Policy.Name, agent.Policy.Budget, agent.Policy.Tools, agent.Policy.RequiredTools),
            fun path input ->
                let exports = agent.Exports input |> List.toArray
                let names = exports |> Array.map (fun tool -> tool.Name)
                let primitives = [ AgentTool.Find; AgentTool.Fetch; AgentTool.Cite; AgentTool.Put; AgentTool.Send; AgentTool.Fsi; AgentTool.History ] |> List.map AgentTool.name |> Set.ofList
                if Array.distinct names <> names || names |> Array.exists (fun name -> Set.contains name primitives) then
                    invalidArg "exports" "SDK exports must have unique names distinct from primitive tools."
                let policy = { agent.Policy with Tools = Set.union agent.Policy.Tools (Set.ofArray names) }
                let validate operations text =
                    agent.Evidence input operations |> Result.bind (fun () -> agent.Parse input operations text |> Result.map ignore)
                Request(RunAgent(path, policy, agent.SharedInstructions input, agent.Format input, exports, validate), fun response ->
                    use json = System.Text.Json.JsonDocument.Parse response
                    let text = match json.RootElement.GetProperty("answer").GetString() with null -> "" | value -> value
                    let operations = (ValueCodec.create<SdkObservation list>()).Decode(json.RootElement.GetProperty("operations").GetRawText()) |> List.toArray
                    match agent.Parse input operations text with
                    | Ok output -> Return output
                    | Error reason -> Abort $"AGENT_OUTPUT_INVALID: {agent.Policy.Name}: {reason}"))

type WorkflowBounds =
    { ModelTurns: int64
      ToolCalls: int64
      EventWaits: int64 }

/// A root plan has a fixed launch-input contract. Output erasure happens only here, after type checking.
[<Sealed>]
type AgentWorkflow internal (shape: ControlShape, build: WorkflowInput -> HarnessProgram<unit>, sessionModelKey: string option) =
    member _.Shape = shape
    /// Stable key of the parameter that selects this session's model, when declared.
    member _.SessionModelKey = defaultArg sessionModelKey ""
    member internal _.Build input = build input

[<RequireQualifiedAccess>]
module Workflow =
    /// Binds one declared model parameter to the complete session. It does not add stage-level switching.
    let withModel (parameter: Parameter<ModelSelection>) (flow: AgentWorkflow) : AgentWorkflow =
        if Object.ReferenceEquals(parameter, null) then nullArg "parameter"
        if Object.ReferenceEquals(flow, null) then nullArg "flow"
        if parameter.Descriptor.Type <> WorkflowParameterValueType.Model then
            invalidArg "parameter" "Workflow.withModel requires a model-selection parameter."
        AgentWorkflow(flow.Shape, flow.Build, Some parameter.Descriptor.Key)

    /// Private smart-constructor types require an explicit codec that revalidates decoded values.
    let withCodecs input output (flow: Workflow<'i, 'o>) =
        Workflow(flow.Shape, flow.BuildBody, inputCodec = input, outputCodec = output)

    let identity<'a> : Workflow<'a, 'a> = Workflow(ControlShape.Pure, fun _ input -> Return input)

    /// Pure transformations only; there is intentionally no Task/Async or arbitrary-effect step.
    let map transform : Workflow<'i, 'o> =
        Workflow(ControlShape.Pure, fun _ input -> Return(transform input))

    let thenDo (next: Workflow<'a, 'o>) (previous: Workflow<'i, 'a>) : Workflow<'i, 'o> =
        Workflow(ControlShape.Sequence(previous.Shape, next.Shape), fun path input ->
            previous.Build (path + "/0") input |> Program.bind (next.Build (path + "/1")))

    let choose predicate (whenTrue: Workflow<'i, 'o>) (whenFalse: Workflow<'i, 'o>) : Workflow<'i, 'o> =
        Workflow(ControlShape.Branch(whenTrue.Shape, whenFalse.Shape), fun path input ->
            Request(SelectRoute(path + "/choice", fun () -> if predicate input then "true" else "false"), fun route ->
                if route = "true" then whenTrue.Build (path + "/true") input
                elif route = "false" then whenFalse.Build (path + "/false") input
                else Abort "WORKFLOW_ROUTE_INVALID"))

    /// A precondition loop over one immutable state type. Exhaustion is a failure, never silent success.
    let repeatUntil limit doneWhen (body: Workflow<'state, 'state>) : Workflow<'state, 'state> =
        if limit < 1 then invalidArg "limit" "A loop needs a positive iteration bound."
        Workflow(ControlShape.BoundedLoop(limit, body.Shape), fun path input ->
            let rec loop iteration state =
                Request(SelectRoute(path + "/route/" + string iteration, fun () -> if doneWhen state then "done" else "repeat"), fun route ->
                    if route = "done" then Return state
                    elif iteration = limit then Abort $"WORKFLOW_LOOP_EXHAUSTED: {path} ({limit})"
                    elif route = "repeat" then body.Build (path + "/" + string iteration) state |> Program.bind (loop (iteration + 1))
                    else Abort "WORKFLOW_ROUTE_INVALID")
            loop 0 input)

    /// The preceding stage must produce unit; the event payload establishes a new typed boundary.
    let awaitEvent runUri parse : Workflow<unit, 'event> =
        Workflow(ControlShape.AwaitEvent, fun path () ->
            Request(AwaitEvent(path, runUri), fun payload ->
                match parse payload with
                | Ok value -> Return value
                | Error reason -> Abort $"WORKFLOW_EVENT_INVALID: {reason}"))

    let define (flow: Workflow<WorkflowInput, 'o>) =
        let build input = flow.Build "root" input |> Program.bind (fun _ -> Return())
        AgentWorkflow(flow.Shape, build, None)

    /// Structural upper bounds, independent of model output. Assumes pure author-supplied functions terminate.
    let rec bounds = function
        | ControlShape.Pure -> { ModelTurns = 0L; ToolCalls = 0L; EventWaits = 0L }
        | ControlShape.AgentStage(_, budget, _, _) ->
            { ModelTurns = int64 budget.ModelTurns; ToolCalls = int64 budget.ToolCalls; EventWaits = 0L }
        | ControlShape.AwaitEvent -> { ModelTurns = 0L; ToolCalls = 0L; EventWaits = 1L }
        | ControlShape.Sequence(a, b) ->
            let x, y = bounds a, bounds b
            { ModelTurns = Checked.(+) x.ModelTurns y.ModelTurns
              ToolCalls = Checked.(+) x.ToolCalls y.ToolCalls; EventWaits = Checked.(+) x.EventWaits y.EventWaits }
        | ControlShape.Branch(a, b) ->
            let x, y = bounds a, bounds b
            { ModelTurns = max x.ModelTurns y.ModelTurns; ToolCalls = max x.ToolCalls y.ToolCalls
              EventWaits = max x.EventWaits y.EventWaits }
        | ControlShape.BoundedLoop(limit, body) ->
            let x = bounds body
            { ModelTurns = Checked.(*) (int64 limit) x.ModelTurns
              ToolCalls = Checked.(*) (int64 limit) x.ToolCalls; EventWaits = Checked.(*) (int64 limit) x.EventWaits }

type WorkflowBuilder() =
    member _.Yield(()) = Workflow.identity
    [<CustomOperation("step")>]
    member _.Step(previous: Workflow<'i, 'a>, next: Workflow<'a, 'o>) = Workflow.thenDo next previous
    [<CustomOperation("step")>]
    member _.Step(previous: Workflow<'i, 'a>, agent: ChatAgent<'a, 'o>) = Workflow.thenDo (Agent.run agent) previous
    member _.Run(flow: Workflow<'i, 'o>) = flow

[<AutoOpen>]
module WorkflowExpression =
    let workflow = WorkflowBuilder()
