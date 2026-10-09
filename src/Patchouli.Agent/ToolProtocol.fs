namespace Patchouli.Agent

open System
open System.Collections.Generic
open System.Text.Json

/// The closed set of argument types accepted at the tool boundary.
[<RequireQualifiedAccess>]
type ToolValueType =
    | Text
    | Integer
    | Boolean
    | Array of element: ToolValueType

/// A decoding failure retains the field and both sides of a type mismatch.
[<RequireQualifiedAccess>]
type ToolProtocolError =
    | MissingField of path: string * expected: ToolValueType
    | TypeMismatch of path: string * expected: ToolValueType * actual: string
    | UnknownField of path: string
    | DuplicateField of path: string
    | InvalidJson of reason: string
    | InvalidValue of path: string * reason: string

/// Schemas are shared with the published MCP contract, rather than maintained a second time.
type ToolArgumentSchema =
    { Fields: IReadOnlyDictionary<string, ToolValueType>
      Required: IReadOnlySet<string> }

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ToolProtocol =
    let rec private typeName = function
        | ToolValueType.Text -> "string"
        | ToolValueType.Integer -> "integer"
        | ToolValueType.Boolean -> "boolean"
        | ToolValueType.Array element -> "array<" + typeName element + ">"

    let describe = function
        | ToolProtocolError.MissingField(path, expected) -> $"{path} is required; expected {typeName expected}."
        | ToolProtocolError.TypeMismatch(path, expected, actual) -> $"{path}: expected {typeName expected}, received {actual}."
        | ToolProtocolError.UnknownField path -> $"Unknown field {path}."
        | ToolProtocolError.DuplicateField path -> $"Duplicate field {path}."
        | ToolProtocolError.InvalidJson reason -> "Invalid JSON: " + reason
        | ToolProtocolError.InvalidValue(path, reason) -> path + ": " + reason

    let private tryProperty name (element: JsonElement) =
        element.EnumerateObject() |> Seq.tryFind (fun field -> field.Name = name) |> Option.map (fun field -> field.Value)

    let rec private validateValue path expected (value: JsonElement) =
        let mismatch () = Error(ToolProtocolError.TypeMismatch(path, expected, value.ValueKind.ToString()))
        match expected, value.ValueKind with
        | ToolValueType.Text, JsonValueKind.String -> Ok ()
        | ToolValueType.Integer, JsonValueKind.Number ->
            match value.TryGetInt64() with true, _ -> Ok () | _ -> mismatch ()
        | ToolValueType.Boolean, (JsonValueKind.True | JsonValueKind.False) -> Ok ()
        | ToolValueType.Array element, JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.mapi (fun index item -> validateValue $"{path}[{index}]" element item)
            |> Seq.tryPick (function Error error -> Some error | Ok () -> None)
            |> function Some error -> Error error | None -> Ok ()
        | _ -> mismatch ()

    /// Decode argument types before any tool can touch a resource.
    let validateArguments (schema: ToolArgumentSchema) (text: string) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement
            if root.ValueKind <> JsonValueKind.Object then
                Error(ToolProtocolError.InvalidValue("$.arguments", "expected object"))
            else
                let fields = root.EnumerateObject() |> Seq.toList
                let missing = schema.Required |> Seq.tryFind (fun name -> tryProperty name root |> Option.isNone)
                let duplicates = fields |> Seq.countBy (fun field -> field.Name) |> Seq.tryFind (fun (_, count) -> count > 1)
                match missing, duplicates with
                | Some name, _ -> Error(ToolProtocolError.MissingField("$.arguments." + name, schema.Fields[name]))
                | _, Some(name, _) -> Error(ToolProtocolError.DuplicateField("$.arguments." + name))
                | _ ->
                    fields
                    |> Seq.tryPick (fun field ->
                        let path = "$.arguments." + field.Name
                        match schema.Fields.TryGetValue field.Name with
                        | false, _ -> Some(ToolProtocolError.UnknownField path)
                        | true, expected -> match validateValue path expected field.Value with Error error -> Some error | Ok () -> None)
                    |> function Some error -> Error error | None -> Ok ()
        with :? JsonException as error -> Error(ToolProtocolError.InvalidJson error.Message)

    let fsiSchema =
        { Fields = dict [ "code", ToolValueType.Text ] |> Dictionary :> IReadOnlyDictionary<string, ToolValueType>
          Required = HashSet<string>(["code"]) :> IReadOnlySet<string> }

