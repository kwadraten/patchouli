namespace Patchouli.Extensions.Sdk

open System
open System.Collections.Generic
open System.Globalization
open System.Text.Json
open Patchouli.Extensions.Contracts

/// A typed key whose public descriptor is extracted statically from FSX source.
[<Sealed>]
type Binding<'T> internal
    (descriptor: ConfigFieldDescriptor, decode: string -> 'T, capabilityRequest: ExtensionCapabilityRequest option) =
    member _.Descriptor = descriptor
    member internal _.Decode = decode
    member _.Key = descriptor.Key
    member _.CapabilityRequest = capabilityRequest

/// Common typed declaration accepted by the single `bind` entry point.
[<AbstractClass>]
type BindingDeclaration<'T> internal (descriptor: ConfigFieldDescriptor, decode: string -> 'T) =
    member _.Descriptor = descriptor
    member internal _.Decode = decode

/// A typed, key-independent field description which becomes a stable Binding when passed to Config.bind.
[<Sealed>]
type Field<'T> internal (descriptor: ConfigFieldDescriptor, decode: string -> 'T) =
    inherit BindingDeclaration<'T>(descriptor, decode)

[<Sealed>]
type CapabilityDeclaration<'T> internal
    (descriptor: ConfigFieldDescriptor, decode: string -> 'T, requestKind: ExtensionCapabilityKind,
     defaultDomain: string option) =
    inherit BindingDeclaration<'T>(descriptor, decode)
    member _.RequestKind = requestKind
    member _.DefaultDomain = defaultDomain

[<RequireQualifiedAccess>]
module Field =
    let private create label kind required description (defaultValue: string | null) minimum maximum choices decode =
        if String.IsNullOrWhiteSpace label then invalidArg "label" "A configuration label is required."
        Field<'T>(ConfigFieldDescriptor("", label, kind, required, description, defaultValue, minimum, maximum, choices), decode)

    let text label defaultValue = create label ConfigValueKind.Text false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id
    let multiline label defaultValue = create label ConfigValueKind.MultilineText false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id
    let language label defaultValue = create label ConfigValueKind.Language false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    let integer label (defaultValue: int) =
        create label ConfigValueKind.Integer false "" (defaultValue.ToString(CultureInfo.InvariantCulture)) (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value -> Int32.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture))

    let decimal label (defaultValue: decimal) =
        create label ConfigValueKind.Decimal false "" (defaultValue.ToString(CultureInfo.InvariantCulture)) (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value -> Decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture))

    let boolean label defaultValue =
        create label ConfigValueKind.Boolean false "" (if defaultValue then "true" else "false") (Nullable()) (Nullable()) Unchecked.defaultof<_> Boolean.Parse

    let choice label (choices: string list) defaultValue =
        if List.isEmpty choices || not (List.contains defaultValue choices) then
            invalidArg "choices" "A choice requires options and a default included in those options."
        create label ConfigValueKind.Choice false "" defaultValue (Nullable()) (Nullable()) (List.toArray choices) id

    let model label =
        create label ConfigValueKind.Model true "" Unchecked.defaultof<_> (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value ->
                match JsonSerializer.Deserialize<ExtensionModelSelection>(value, JsonSerializerOptions(PropertyNameCaseInsensitive = true)) |> Option.ofObj with
                | Some selection when not (String.IsNullOrWhiteSpace selection.ProviderId) && not (String.IsNullOrWhiteSpace selection.Model) -> selection
                | _ -> invalidArg "value" "A model selection must include providerId and model.")

    let defaultModel label =
        create label ConfigValueKind.Model false "" "" (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value ->
                if String.IsNullOrWhiteSpace value then None
                else
                    match JsonSerializer.Deserialize<ExtensionModelSelection>(value, JsonSerializerOptions(PropertyNameCaseInsensitive = true)) |> Option.ofObj with
                    | None -> None
                    | Some selection when String.IsNullOrWhiteSpace selection.ProviderId && String.IsNullOrWhiteSpace selection.Model -> None
                    | Some selection -> Some selection)

    let documents label = create label ConfigValueKind.Documents false "" "[]" (Nullable()) (Nullable()) Unchecked.defaultof<_> id
    let pageRange label defaultValue = create label ConfigValueKind.PageRange false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id
    let credentialReference label = create label ConfigValueKind.CredentialReference false "" Unchecked.defaultof<_> (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    let required description (field: Field<'T>) =
        let value = field.Descriptor
        Field<'T>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, true, description, value.DefaultValue, value.Minimum, value.Maximum, value.Choices), field.Decode)

    let optional (field: Field<'T>) =
        let value = field.Descriptor
        Field<'T>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, false, value.Description, value.DefaultValue, value.Minimum, value.Maximum, value.Choices), field.Decode)

    let describe description (field: Field<'T>) =
        let value = field.Descriptor
        Field<'T>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, value.Required, description, value.DefaultValue, value.Minimum, value.Maximum, value.Choices), field.Decode)

    let range (minimum: int) (maximum: int) (field: Field<int>) =
        if minimum > maximum then invalidArg "minimum" "The minimum cannot exceed the maximum."
        let value = field.Descriptor
        Field<int>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, value.Required, value.Description,
            value.DefaultValue, Nullable(Convert.ToDecimal(minimum)), Nullable(Convert.ToDecimal(maximum)), value.Choices), field.Decode)

    let decimalRange (minimum: decimal) (maximum: decimal) (field: Field<decimal>) =
        if minimum > maximum then invalidArg "minimum" "The minimum cannot exceed the maximum."
        let value = field.Descriptor
        Field<decimal>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, value.Required, value.Description,
            value.DefaultValue, Nullable minimum, Nullable maximum, value.Choices), field.Decode)

[<RequireQualifiedAccess>]
module Binding =
    let get (binding: Binding<'T>) (snapshot: ExtensionConfigSnapshot) : 'T =
        if isNull (box binding) then nullArg "binding"
        if isNull (box snapshot) then nullArg "snapshot"
        match snapshot.Values.TryGetValue(binding.Key) with
        | true, value -> binding.Decode value
        | _ when binding.Descriptor.Kind = ConfigValueKind.Capability -> binding.Decode ""
        | _ ->
            match binding.Descriptor.DefaultValue with
            | null -> invalidOp ("EXTENSION_CONFIG_MISSING: " + binding.Key)
            | defaultValue -> binding.Decode defaultValue

[<RequireQualifiedAccess>]
module Config =
    let private create (key: string) (label: string) (kind: ConfigValueKind) (required: bool) (description: string)
                       (defaultValue: string | null) (minimum: Nullable<decimal>) (maximum: Nullable<decimal>)
                       (choices: string array) (decode: string -> 'T) : Binding<'T> =
        if String.IsNullOrWhiteSpace key then invalidArg "key" "A stable configuration key is required."
        if String.IsNullOrWhiteSpace label then invalidArg "label" "A configuration label is required."
        Binding<'T>(ConfigFieldDescriptor(key, label, kind, required, description, defaultValue, minimum, maximum, choices), decode, None)

    let bind (key: string) (field: BindingDeclaration<'T>) : Binding<'T> =
        if String.IsNullOrWhiteSpace key then invalidArg "key" "A stable configuration key is required."
        let value = field.Descriptor
        let request =
            match field with
            | :? CapabilityDeclaration<'T> as capability ->
                Some(ExtensionCapabilityRequest(capability.RequestKind, key, capability.DefaultDomain |> Option.toObj))
            | _ -> None
        Binding<'T>(ConfigFieldDescriptor(key, value.Label, value.Kind, value.Required, value.Description,
            value.DefaultValue, value.Minimum, value.Maximum, value.Choices), field.Decode, request)

    let text key label defaultValue =
        create key label ConfigValueKind.Text false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    let multiline key label defaultValue =
        create key label ConfigValueKind.MultilineText false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    let language key label defaultValue =
        create key label ConfigValueKind.Language false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    let integer key label (defaultValue: int) =
        create key label ConfigValueKind.Integer false "" (defaultValue.ToString(CultureInfo.InvariantCulture)) (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value -> Int32.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture))

    let decimal key label (defaultValue: decimal) =
        create key label ConfigValueKind.Decimal false "" (defaultValue.ToString(CultureInfo.InvariantCulture)) (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value -> Decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture))

    let boolean key label defaultValue =
        create key label ConfigValueKind.Boolean false "" (if defaultValue then "true" else "false") (Nullable()) (Nullable()) Unchecked.defaultof<_> Boolean.Parse

    let choice key label (choices: string list) defaultValue =
        if List.isEmpty choices || not (List.contains defaultValue choices) then
            invalidArg "choices" "A choice requires options and a default included in those options."
        create key label ConfigValueKind.Choice false "" defaultValue (Nullable()) (Nullable()) (List.toArray choices) id

    let required description (binding: Binding<'T>) =
        let value = binding.Descriptor
        Binding<'T>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, true, description, value.DefaultValue,
            value.Minimum, value.Maximum, value.Choices), binding.Decode, binding.CapabilityRequest)

    let optional (binding: Binding<'T>) =
        let value = binding.Descriptor
        Binding<'T>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, false, value.Description, value.DefaultValue,
            value.Minimum, value.Maximum, value.Choices), binding.Decode, binding.CapabilityRequest)

    let describe description (binding: Binding<'T>) =
        let value = binding.Descriptor
        Binding<'T>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, value.Required, description, value.DefaultValue,
            value.Minimum, value.Maximum, value.Choices), binding.Decode, binding.CapabilityRequest)

    let range (minimum: int) (maximum: int) (binding: Binding<int>) =
        if minimum > maximum then invalidArg "minimum" "The minimum cannot exceed the maximum."
        let value = binding.Descriptor
        Binding<int>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, value.Required, value.Description,
            value.DefaultValue, Nullable(Convert.ToDecimal(minimum)), Nullable(Convert.ToDecimal(maximum)), value.Choices), binding.Decode, binding.CapabilityRequest)

    let decimalRange (minimum: decimal) (maximum: decimal) (binding: Binding<decimal>) =
        if minimum > maximum then invalidArg "minimum" "The minimum cannot exceed the maximum."
        let value = binding.Descriptor
        Binding<decimal>(ConfigFieldDescriptor(value.Key, value.Label, value.Kind, value.Required, value.Description,
            value.DefaultValue, Nullable minimum, Nullable maximum, value.Choices), binding.Decode, binding.CapabilityRequest)

    let model key label =
        create key label ConfigValueKind.Model true "" Unchecked.defaultof<_> (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value ->
                match JsonSerializer.Deserialize<ExtensionModelSelection>(value,
                    JsonSerializerOptions(PropertyNameCaseInsensitive = true)) |> Option.ofObj with
                | Some selection when not (String.IsNullOrWhiteSpace selection.ProviderId) && not (String.IsNullOrWhiteSpace selection.Model) -> selection
                | _ -> invalidArg "value" "A model selection must include providerId and model.")

    let defaultModel key label =
        create key label ConfigValueKind.Model false "" "" (Nullable()) (Nullable()) Unchecked.defaultof<_>
            (fun value ->
                if String.IsNullOrWhiteSpace value then None
                else
                    match JsonSerializer.Deserialize<ExtensionModelSelection>(value,
                        JsonSerializerOptions(PropertyNameCaseInsensitive = true)) |> Option.ofObj with
                    | None -> None
                    | Some selection when String.IsNullOrWhiteSpace selection.ProviderId && String.IsNullOrWhiteSpace selection.Model -> None
                    | Some selection -> Some selection)

    let documents key label =
        create key label ConfigValueKind.Documents false "" "[]" (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    let pageRange key label defaultValue =
        create key label ConfigValueKind.PageRange false "" defaultValue (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    /// Selects a saved credential by opaque reference; secret bytes never enter script config.
    let credentialReference key label =
        create key label ConfigValueKind.CredentialReference false "" Unchecked.defaultof<_> (Nullable()) (Nullable()) Unchecked.defaultof<_> id

    /// Boxes a typed binding so different value types can be placed in one declaration list.
    let asField (binding: Binding<'T>) : obj =
        match box binding with
        | null -> invalidArg "binding" "A config binding cannot be null."
        | boxed -> boxed

    /// Builds the extension's runtime schema from typed bindings without exposing decoder functions.
    let fields (bindings: seq<objnull>) : ConfigFieldDescriptor array =
        bindings
        |> Seq.map (fun binding ->
            if isNull binding then invalidArg "bindings" "A config binding cannot be null."
            match binding.GetType().GetProperty("Descriptor") with
            | null -> invalidArg "bindings" "Config.fields accepts only Config bindings."
            | property ->
                match property.GetValue(binding) with
                | :? ConfigFieldDescriptor as descriptor -> descriptor
                | _ -> invalidArg "bindings" "Config.fields accepts only Config bindings.")
        |> Seq.filter (fun descriptor -> descriptor.Kind <> ConfigValueKind.Capability)
        |> Seq.toArray

[<AutoOpen>]
module ConfigBindingOperators =
    let bind = Config.bind
