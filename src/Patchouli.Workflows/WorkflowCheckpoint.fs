namespace Patchouli.Workflows

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Patchouli.Agent.Sdk

/// Continuations remain in the frozen plan; only immutable node data and the core cursor persist.
type WorkflowCheckpoint =
    { Path: string
      Plan: string
      InputSchema: string
      OutputSchema: string
      Input: string
      Output: string option
      Route: string option
      Position: int
      Context: string }

/// Frozen identity for one Agent.run node. Function closures are rebuilt from the frozen script,
/// while this contract proves the rebuilt request, tools and binding still match the recorded run.
type WorkflowActivationCheckpoint =
    { Id: string
      ContextId: string
      Contract: string
      Instructions: string
      ModelSelection: string
      IsMain: bool
      Request: string
      Policy: string
      AllowedTools: string[]
      PrimitiveLimit: int
      Exports: string[] }

/// Complete workflow continuation state stored in each node checkpoint.
type WorkflowRuntimeCheckpoint =
    { MainContextId: string
      Contexts: IReadOnlyDictionary<string, string>
      Activations: WorkflowActivationCheckpoint[] }

[<RequireQualifiedAccess>]
module WorkflowRuntimeCheckpointCodec =
    let private required (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value -> value
        | _ -> raise (JsonException($"The workflow runtime checkpoint is missing '{name}'."))

    let private text (element: JsonElement) (name: string) =
        let value = required element name
        if value.ValueKind <> JsonValueKind.String then
            raise (JsonException($"The workflow runtime checkpoint field '{name}' must be a string."))
        match Option.ofObj (value.GetString()) with
        | Some text -> text
        | None -> raise (JsonException($"The workflow runtime checkpoint field '{name}' must be a string."))

    let private texts (element: JsonElement) (name: string) =
        let value = required element name
        if value.ValueKind <> JsonValueKind.Array then
            raise (JsonException($"The workflow runtime checkpoint field '{name}' must be an array."))
        value.EnumerateArray()
        |> Seq.map (fun item ->
            if item.ValueKind <> JsonValueKind.String then
                raise (JsonException($"The workflow runtime checkpoint field '{name}' contains a non-string."))
            match Option.ofObj (item.GetString()) with
            | Some text -> text
            | None -> raise (JsonException("Null string.")))
        |> Seq.toArray

    let private boolValue (element: JsonElement) (name: string) =
        match (required element name).ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> raise (JsonException($"The workflow runtime checkpoint field '{name}' must be a boolean."))

    let private intValue (element: JsonElement) (name: string) =
        let value = required element name
        if value.ValueKind <> JsonValueKind.Number then
            raise (JsonException($"The workflow runtime checkpoint field '{name}' must be a number."))
        value.GetInt32()

    let encode (checkpoint: WorkflowRuntimeCheckpoint) =
        if Object.ReferenceEquals(checkpoint, null) then nullArg "checkpoint"
        if String.IsNullOrWhiteSpace checkpoint.MainContextId ||
           Object.ReferenceEquals(checkpoint.Contexts, null) || Object.ReferenceEquals(checkpoint.Activations, null) ||
           not (checkpoint.Contexts.ContainsKey checkpoint.MainContextId) then
            invalidArg "checkpoint" "The runtime checkpoint must include its main context and activation registry."
        let contexts = JsonObject()
        for KeyValue(id, context) in checkpoint.Contexts |> Seq.sortBy (fun pair -> pair.Key) do
            if String.IsNullOrWhiteSpace id || String.IsNullOrWhiteSpace context then
                invalidArg "checkpoint" "Runtime context ids and encoded contexts are required."
            contexts[id] <- JsonValue.Create context
        let activations = JsonArray()
        for activation in checkpoint.Activations |> Array.sortBy (fun item -> item.Id) do
            let tools = JsonArray()
            for tool in activation.AllowedTools do tools.Add(JsonValue.Create tool)
            let exports = JsonArray()
            for export in activation.Exports do exports.Add(JsonValue.Create export)
            let value = JsonObject()
            value["id"] <- JsonValue.Create activation.Id
            value["contextId"] <- JsonValue.Create activation.ContextId
            value["contract"] <- JsonValue.Create activation.Contract
            value["instructions"] <- JsonValue.Create activation.Instructions
            value["modelSelection"] <- JsonValue.Create activation.ModelSelection
            value["isMain"] <- JsonValue.Create activation.IsMain
            value["request"] <- JsonValue.Create activation.Request
            value["policy"] <- JsonValue.Create activation.Policy
            value["allowedTools"] <- tools
            value["primitiveLimit"] <- JsonValue.Create activation.PrimitiveLimit
            value["exports"] <- exports
            activations.Add value
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create 1
        root["mainContextId"] <- JsonValue.Create checkpoint.MainContextId
        root["contexts"] <- contexts
        root["activations"] <- activations
        root.ToJsonString()

    let decode (json: string) =
        use document = JsonDocument.Parse json
        let root = document.RootElement
        if intValue root "schemaVersion" <> 1 then
            raise (JsonException("Unsupported workflow runtime checkpoint version."))
        let mainContextId = text root "mainContextId"
        let contexts = Dictionary<string, string>(StringComparer.Ordinal)
        let contextObject = required root "contexts"
        if contextObject.ValueKind <> JsonValueKind.Object then
            raise (JsonException("The workflow runtime checkpoint contexts must be an object."))
        for property in contextObject.EnumerateObject() do
            if property.Value.ValueKind <> JsonValueKind.String then
                raise (JsonException("A workflow runtime context must be encoded as a string."))
            match Option.ofObj (property.Value.GetString()) with
            | None -> raise (JsonException("Null workflow context."))
            | Some value -> contexts.Add(property.Name, value)
        let activationsElement = required root "activations"
        if activationsElement.ValueKind <> JsonValueKind.Array then
            raise (JsonException("The workflow runtime checkpoint activations must be an array."))
        let activations = activationsElement.EnumerateArray() |> Seq.map (fun value ->
            { Id = text value "id"
              ContextId = text value "contextId"
              Contract = text value "contract"
              Instructions = text value "instructions"
              ModelSelection = text value "modelSelection"
              IsMain = boolValue value "isMain"
              Request = text value "request"
              Policy = text value "policy"
              AllowedTools = texts value "allowedTools"
              PrimitiveLimit = intValue value "primitiveLimit"
              Exports = texts value "exports" }) |> Seq.toArray
        let invalidActivation = activations |> Array.exists (fun activation ->
            String.IsNullOrWhiteSpace activation.Id || String.IsNullOrWhiteSpace activation.ContextId ||
            not (contexts.ContainsKey activation.ContextId))
        let duplicateActivation =
            (activations |> Array.map (fun activation -> activation.Id) |> Array.distinct |> Array.length) <> activations.Length
        if String.IsNullOrWhiteSpace mainContextId || not (contexts.ContainsKey mainContextId) ||
           invalidActivation || duplicateActivation then
            raise (JsonException("The workflow runtime checkpoint has missing or duplicate identities."))
        { MainContextId = mainContextId
          Contexts = contexts :> IReadOnlyDictionary<string, string>
          Activations = activations }

type IWorkflowCheckpointSink =
    abstract ReadCheckpointsAsync: sessionId: string * cancellationToken: CancellationToken -> Task<WorkflowCheckpoint[]>
    abstract RecordCheckpointAsync: sessionId: string * checkpoint: WorkflowCheckpoint * cancellationToken: CancellationToken -> Task

[<RequireQualifiedAccess>]
module WorkflowCheckpoints =
    let codec = ValueCodec.create<WorkflowCheckpoint>()
