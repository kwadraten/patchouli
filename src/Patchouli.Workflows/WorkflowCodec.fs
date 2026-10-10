namespace Patchouli.Workflows

open System
open System.IO
open System.Text
open System.Text.Json

/// <summary>
///     Explicit JSON codec for workflow definitions, script snapshots and recorded run steps. Every
///     field is written and read by hand so the durable format is a documented contract (the
///     settings page, the MCP <c>workflows/</c> projection and the session log all read it) rather
///     than a reflection artefact. Workflow files are user configuration saved with the Library;
///     they are never part of the rebuildable index and never advance <c>library_revision</c>.
/// </summary>
[<RequireQualifiedAccess>]
module WorkflowCodec =
    /// <summary>Durable schema version of the workflow definition document.</summary>
    [<Literal>]
    let SchemaVersion = 1

    let private indented =
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        options

    let private compact =
        let mutable options = JsonWriterOptions()
        options.Indented <- false
        options

    let private render (options: JsonWriterOptions) (write: Utf8JsonWriter -> unit) : string =
        use stream = new MemoryStream()
        do
            use writer = new Utf8JsonWriter(stream, options)
            write writer
            writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private require (element: JsonElement) (name: string) : JsonElement =
        match element.TryGetProperty name with
        | true, value -> value
        | _ -> raise (JsonException($"The workflow document is missing the '{name}' property."))

    let private text (element: JsonElement) (name: string) : string =
        let value = require element name
        match value.ValueKind with
        | JsonValueKind.Null -> String.Empty
        | JsonValueKind.String -> WorkflowText.orEmpty (value.GetString())
        | _ -> raise (JsonException($"The workflow document property '{name}' must be a string."))

    let private textOr (element: JsonElement) (name: string) (fallback: string) : string =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> WorkflowText.orEmpty (value.GetString())
        | _ -> fallback

    let private boolean (element: JsonElement) (name: string) : bool =
        let value = require element name
        if value.ValueKind = JsonValueKind.True then true
        elif value.ValueKind = JsonValueKind.False then false
        else raise (JsonException($"The workflow document property '{name}' must be a boolean."))

    let private integer (element: JsonElement) (name: string) (fallback: int) : int =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
        | _ -> fallback

    let private int64Value (element: JsonElement) (name: string) (fallback: int64) : int64 =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt64()
        | _ -> fallback

    let private parseParameterType (text: string) : WorkflowParameterType =
        match text with
        | "Text" -> WorkflowParameterType.Text
        | "DocumentId" -> WorkflowParameterType.DocumentId
        | "PageRange" -> WorkflowParameterType.PageRange
        | "Language" -> WorkflowParameterType.Language
        | "Integer" -> WorkflowParameterType.Integer
        | "Boolean" -> WorkflowParameterType.Boolean
        | "MultilineText" -> WorkflowParameterType.MultilineText
        | "Decimal" -> WorkflowParameterType.Decimal
        | "Choice" -> WorkflowParameterType.Choice
        | "Model" -> WorkflowParameterType.Model
        | "Documents" -> WorkflowParameterType.Documents
        | "TextSelection" -> WorkflowParameterType.TextSelection
        | _ -> raise (JsonException($"Unknown workflow parameter type '{text}'."))

    let private writeParameter (writer: Utf8JsonWriter) (parameter: WorkflowParameter) =
        writer.WriteStartObject()
        writer.WriteString("name", parameter.Name)
        writer.WriteString("type", parameter.Type.ToString())
        writer.WriteBoolean("required", parameter.Required)
        writer.WriteString("description", parameter.Description)
        writer.WriteString("defaultValue", parameter.DefaultValue)
        writer.WriteEndObject()

    let private readParameter (element: JsonElement) : WorkflowParameter =
        { Name = text element "name"
          Type = parseParameterType (text element "type")
          Required = boolean element "required"
          Description = textOr element "description" String.Empty
          DefaultValue = textOr element "defaultValue" String.Empty }

    let private writeStringMap (writer: Utf8JsonWriter) (name: string)
                               (values: Collections.Generic.IReadOnlyDictionary<string, string>) =
        writer.WriteStartObject(name)
        if not (Object.ReferenceEquals(values, null)) then
            for KeyValue(key, value) in values do
                writer.WriteString(key, value)
        writer.WriteEndObject()

    let private readStringMap (element: JsonElement) (name: string) : Collections.Generic.IReadOnlyDictionary<string, string> =
        let values = Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        match element.TryGetProperty name with
        | true, map when map.ValueKind = JsonValueKind.Object ->
            for property in map.EnumerateObject() do
                if property.Value.ValueKind = JsonValueKind.String then
                    values[property.Name] <- WorkflowText.orEmpty (property.Value.GetString())
        | _ -> ()
        values :> Collections.Generic.IReadOnlyDictionary<string, string>

    /// <summary>Serializes one workflow definition as an indented, hand-readable document.</summary>
    let definitionToJson (definition: WorkflowDefinition) : string =
        if Object.ReferenceEquals(definition, null) then
            nullArg "definition"

        render indented (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("schemaVersion", SchemaVersion)
            writer.WriteString("id", definition.Id)
            writer.WriteString("name", definition.Name)
            writer.WriteString("description", definition.Description)
            writer.WriteString("scriptEntryPoint", definition.ScriptEntryPoint)
            writer.WriteNumber("selectionScope", int definition.SelectionScope)
            writer.WriteBoolean("locked", definition.Locked)
            writer.WriteBoolean("builtIn", definition.BuiltIn)
            writer.WriteStartObject("menu")
            writer.WriteString("menuPath", definition.Menu.MenuPath)
            writer.WriteNumber("order", definition.Menu.Order)
            writer.WriteBoolean("showInMenu", definition.Menu.ShowInMenu)
            writer.WriteEndObject()
            writer.WriteStartArray("parameters")
            if not (Object.ReferenceEquals(definition.Parameters, null)) then
                for parameter in definition.Parameters do
                    writeParameter writer parameter
            writer.WriteEndArray()
            writer.WriteEndObject())

    /// <summary>Reads one workflow definition written by <see cref="definitionToJson" />.</summary>
    let definitionFromJson (json: string) : WorkflowDefinition =
        use document = JsonDocument.Parse(json)
        let root = document.RootElement
        let menu = require root "menu"
        let parameters =
            match root.TryGetProperty "parameters" with
            | true, array when array.ValueKind = JsonValueKind.Array ->
                [| for entry in array.EnumerateArray() -> readParameter entry |]
            | _ -> [||]

        { Id = text root "id"
          Name = text root "name"
          Description = textOr root "description" String.Empty
          ScriptEntryPoint = text root "scriptEntryPoint"
          Parameters = parameters
          SelectionScope = enum<WorkflowSelectionScope> (integer root "selectionScope" 0)
          Locked = boolean root "locked"
          BuiltIn = boolean root "builtIn"
          Menu =
            { MenuPath = text menu "menuPath"
              Order = integer menu "order" 100
              ShowInMenu = boolean menu "showInMenu" } }

    /// <summary>Serializes the script snapshot a session started with.</summary>
    let snapshotToJson (snapshot: WorkflowScriptSnapshot) : string =
        if Object.ReferenceEquals(snapshot, null) then
            nullArg "snapshot"

        render indented (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("schemaVersion", SchemaVersion)
            writer.WriteString("workflowId", snapshot.WorkflowId)
            writer.WriteString("workflowName", snapshot.WorkflowName)
            writer.WriteString("scriptEntryPoint", snapshot.ScriptEntryPoint)
            writer.WriteString("scriptHash", snapshot.ScriptHash)
            writer.WriteString("apiVersion", snapshot.ApiVersion)
            writer.WriteString("capturedAt", snapshot.CapturedAt)
            writer.WriteString("scriptText", snapshot.ScriptText)
            writer.WriteString("declarationFingerprint", snapshot.DeclarationFingerprint)
            writeStringMap writer "parameterValues" snapshot.ParameterValues
            writer.WriteString("modelSelection", snapshot.ModelSelection)
            writer.WriteEndObject())

    /// <summary>Reads one script snapshot written by <see cref="snapshotToJson" />.</summary>
    let snapshotFromJson (json: string) : WorkflowScriptSnapshot =
        use document = JsonDocument.Parse(json)
        let root = document.RootElement
        { WorkflowId = text root "workflowId"
          WorkflowName = textOr root "workflowName" String.Empty
          ScriptEntryPoint = text root "scriptEntryPoint"
          ScriptText = textOr root "scriptText" String.Empty
          ScriptHash = textOr root "scriptHash" String.Empty
          ApiVersion = textOr root "apiVersion" String.Empty
          CapturedAt = textOr root "capturedAt" String.Empty
          DeclarationFingerprint = textOr root "declarationFingerprint" String.Empty
          ParameterValues = readStringMap root "parameterValues"
          ModelSelection = textOr root "modelSelection" String.Empty }

    /// <summary>Serializes one recorded run step as a single compact log line payload.</summary>
    let stepToJson (step: WorkflowStepRecord) : string =
        if Object.ReferenceEquals(step, null) then
            nullArg "step"

        render compact (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("step", step.Step)
            writer.WriteNumber("effectId", step.EffectId)
            writer.WriteString("kind", step.Kind)
            writer.WriteString("request", step.Request)
            writer.WriteBoolean("completed", step.Completed)
            writer.WriteString("result", step.Result)
            writer.WriteEndObject())

    /// <summary>Reads one recorded run step written by <see cref="stepToJson" />.</summary>
    let stepFromJson (json: string) : WorkflowStepRecord =
        use document = JsonDocument.Parse(json)
        let root = document.RootElement
        { Step = integer root "step" 0
          EffectId = int64Value root "effectId" 0L
          Kind = text root "kind"
          Request = textOr root "request" String.Empty
          Completed = boolean root "completed"
          Result = textOr root "result" String.Empty }
