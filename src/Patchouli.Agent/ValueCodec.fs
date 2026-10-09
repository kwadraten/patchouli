namespace Patchouli.Agent.Sdk

open System
open System.Collections
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.FSharp.Reflection

/// A frozen contract for immutable values shared by tools, stages and checkpoints.
[<Sealed>]
type ValueCodec<'value>(schema: string, encode: 'value -> string, decode: string -> 'value) =
    member _.Schema = schema
    member _.Identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes schema))
    member _.Encode value = encode value
    member _.Decode json = decode json

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ValueCodec =
    let private generic (definition: Type) (value: Type) =
        value.IsGenericType && value.GetGenericTypeDefinition() = definition

    let private present (value: obj | null) =
        match value with null -> invalidOp "Unexpected null value." | value -> value

    let private scalar (t: Type) =
        t = typeof<string> || t = typeof<bool> || t = typeof<int> || t = typeof<int64> ||
        t = typeof<float> || t = typeof<decimal> || t = typeof<Guid> || t = typeof<DateTimeOffset>

    let rec private shape (parents: Type list) (t: Type) : JsonNode =
        let obj fields = JsonObject(fields |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v)) :> seq<_>) :> JsonNode
        let str (s: string) =
            match JsonValue.Create(s) with null -> invalidOp "Null schema literal." | value -> value :> JsonNode
        if List.contains t parents then invalidArg "valueType" $"Recursive state requires an explicit codec: {t}."
        let parents = t :: parents
        if t = typeof<unit> then obj ["type", str "null"]
        elif scalar t then
            let kind = if t = typeof<bool> then "boolean" elif t = typeof<int> || t = typeof<int64> then "integer"
                       elif t = typeof<float> || t = typeof<decimal> then "number" else "string"
            obj ["type", str kind]
        elif generic typedefof<option<_>> t then
            obj ["anyOf", JsonArray([| shape parents (t.GetGenericArguments()[0]); obj ["type", str "null"] |])]
        elif generic typedefof<list<_>> t || generic typedefof<Set<_>> t then
            obj ["type", str "array"; "items", shape parents (t.GetGenericArguments()[0])]
        elif generic typedefof<Map<_, _>> t then
            let args = t.GetGenericArguments()
            obj ["type", str "array"; "items", obj ["type", str "array"; "prefixItems", JsonArray([| shape parents args[0]; shape parents args[1] |])]]
        elif FSharpType.IsTuple t then
            obj ["type", str "array"; "prefixItems", JsonArray(FSharpType.GetTupleElements t |> Array.map (shape parents))]
        elif FSharpType.IsRecord t then
            let fields = FSharpType.GetRecordFields t
            if fields |> Array.exists (fun field -> field.SetMethod <> null) then
                invalidArg "valueType" $"Mutable record fields cannot be checkpointed: {t}."
            obj ["type", str "object"; "properties", obj (fields |> Array.map (fun f -> f.Name, shape parents f.PropertyType) |> Array.toList)
                 "required", JsonArray(fields |> Array.map (fun f -> str f.Name)); "additionalProperties", JsonValue.Create(false)]
        elif FSharpType.IsUnion t then
            let cases = FSharpType.GetUnionCases t
            if cases |> Array.forall (fun c -> c.GetFields().Length = 0) then
                obj ["type", str "string"; "enum", JsonArray(cases |> Array.map (fun c -> str c.Name))]
            else
                obj ["oneOf", JsonArray(cases |> Array.map (fun c ->
                    obj ["type", str "object"; "properties", obj ["case", obj ["type", str "string"; "enum", JsonArray([| str c.Name |])];
                          "fields", obj ["type", str "array"; "prefixItems", JsonArray(c.GetFields() |> Array.map (fun f -> shape parents f.PropertyType))]]
                         "required", JsonArray([| str "case"; str "fields" |]); "additionalProperties", JsonValue.Create(false)]))]
        else invalidArg "valueType" $"Unsupported immutable value type: {t}. Supply an explicit validated codec."

    let private objectFields (names: string[]) (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Object then raise (JsonException("Expected an object."))
        let actual = value.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.sort |> Seq.toArray
        if actual <> Array.sort names then raise (JsonException("Unexpected, missing or duplicate fields."))

    let rec private write (t: Type) (value: obj | null) : JsonNode | null =
        if t = typeof<unit> then null
        elif isNull value && generic typedefof<option<_>> t then null
        elif isNull value then raise (JsonException("Null is only valid for option/unit."))
        elif scalar t then JsonSerializer.SerializeToNode(value, t)
        elif generic typedefof<option<_>> t then
            let _, fields = FSharpValue.GetUnionFields(present value, t)
            write (t.GetGenericArguments()[0]) fields[0]
        elif generic typedefof<list<_>> t || generic typedefof<Set<_>> t then
            JsonArray((present value :?> IEnumerable) |> Seq.cast<obj> |> Seq.map (write (t.GetGenericArguments()[0])) |> Seq.toArray)
        elif generic typedefof<Map<_, _>> t then
            let args = t.GetGenericArguments()
            JsonArray((present value :?> IEnumerable) |> Seq.cast<obj> |> Seq.map (fun pair ->
                let pt = (present pair).GetType()
                JsonArray([| write args[0] ((match pt.GetProperty("Key") with null -> invalidOp "Missing map key." | p -> p.GetValue(present pair))); write args[1] ((match pt.GetProperty("Value") with null -> invalidOp "Missing map value." | p -> p.GetValue(present pair))) |]) :> JsonNode) |> Seq.toArray)
        elif FSharpType.IsTuple t then
            JsonArray(Array.map2 write (FSharpType.GetTupleElements t) (FSharpValue.GetTupleFields(present value)))
        elif FSharpType.IsRecord t then
            let json = JsonObject()
            Array.iter2 (fun (field: PropertyInfo) v -> json[field.Name] <- write field.PropertyType v)
                (FSharpType.GetRecordFields t) (FSharpValue.GetRecordFields(present value))
            json
        elif FSharpType.IsUnion t then
            let case, fields = FSharpValue.GetUnionFields(present value, t)
            if FSharpType.GetUnionCases t |> Array.forall (fun c -> c.GetFields().Length = 0) then JsonValue.Create(case.Name)
            else
                let json = JsonObject()
                json["case"] <- JsonValue.Create(case.Name)
                json["fields"] <- JsonArray(Array.map2 (fun (field: PropertyInfo) v -> write field.PropertyType v) (case.GetFields()) fields)
                json
        else invalidArg "valueType" $"Unsupported immutable value type: {t}."

    let rec private read (t: Type) (value: JsonElement) : obj | null =
        if t = typeof<unit> then
            if value.ValueKind <> JsonValueKind.Null then raise (JsonException("Expected unit/null."))
            box ()
        elif scalar t then
            if value.ValueKind = JsonValueKind.Null then raise (JsonException("Null is only valid for option/unit."))
            JsonSerializer.Deserialize(value.GetRawText(), t)
        elif generic typedefof<option<_>> t then
            let cases = FSharpType.GetUnionCases t
            if value.ValueKind = JsonValueKind.Null then FSharpValue.MakeUnion(cases[0], [||])
            else FSharpValue.MakeUnion(cases[1], [| read (t.GetGenericArguments()[0]) value |])
        elif generic typedefof<list<_>> t then
            let cases = FSharpType.GetUnionCases t
            let element = t.GetGenericArguments()[0]
            (value.EnumerateArray() |> Seq.toArray, FSharpValue.MakeUnion(cases[0], [||]))
            ||> Array.foldBack (fun v tail -> FSharpValue.MakeUnion(cases[1], [| read element v; tail |]))
        elif generic typedefof<Set<_>> t || generic typedefof<Map<_, _>> t then
            let args = t.GetGenericArguments()
            let element = if args.Length = 1 then args[0] else FSharpType.MakeTupleType args
            let values = value.EnumerateArray() |> Seq.map (read element) |> Seq.toArray
            let array = Array.CreateInstance(element, values.Length)
            values |> Array.iteri (fun i v -> array.SetValue(v, i))
            Activator.CreateInstance(t, [| array :> obj |])
        elif FSharpType.IsTuple t then
            let elements = value.EnumerateArray() |> Seq.toArray
            let types = FSharpType.GetTupleElements t
            if elements.Length <> types.Length then raise (JsonException("Tuple arity differs."))
            FSharpValue.MakeTuple(Array.map2 read types elements, t)
        elif FSharpType.IsRecord t then
            let fields = FSharpType.GetRecordFields t
            objectFields (fields |> Array.map (fun f -> f.Name)) value
            FSharpValue.MakeRecord(t, fields |> Array.map (fun f -> read f.PropertyType (value.GetProperty f.Name)))
        elif FSharpType.IsUnion t then
            let cases = FSharpType.GetUnionCases t
            let simple = cases |> Array.forall (fun c -> c.GetFields().Length = 0)
            if not simple then objectFields [| "case"; "fields" |] value
            let name =
                match (if simple then value.GetString() else value.GetProperty("case").GetString()) with
                | null -> raise (JsonException("Union case cannot be null."))
                | name -> name
            let case = cases |> Array.tryFind (fun c -> c.Name = name) |> Option.defaultWith (fun () -> raise (JsonException("Unknown union case.")))
            let fields = if simple then [||] else value.GetProperty("fields").EnumerateArray() |> Seq.toArray
            let types = case.GetFields()
            if fields.Length <> types.Length then raise (JsonException("Union payload arity differs."))
            FSharpValue.MakeUnion(case, Array.map2 (fun (p: PropertyInfo) v -> read p.PropertyType v) types fields)
        else raise (JsonException($"Unsupported value type: {t}."))

    /// Private representations and mutable/opaque objects require an explicit validated codec.
    let create<'value> () =
        let schema = shape [] typeof<'value> |> fun node -> node.ToJsonString()
        ValueCodec<'value>(schema,
            (fun value -> match write typeof<'value> (box value) with null -> "null" | node -> node.ToJsonString()),
            (fun json -> use doc = JsonDocument.Parse json
                         read typeof<'value> doc.RootElement |> unbox<'value>))
