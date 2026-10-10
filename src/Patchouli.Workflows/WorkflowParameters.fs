namespace Patchouli.Workflows.Scripting

open System
open System.Globalization
open System.Text.Json

/// The editor control represented by one statically declared workflow parameter.
type WorkflowParameterValueType =
    | Text = 0
    | MultilineText = 1
    | Integer = 2
    | Decimal = 3
    | Boolean = 4
    | Choice = 5
    | Language = 6
    | Model = 7
    | Documents = 8
    | PageRange = 9
    | TextSelection = 10

/// Static metadata extracted from one SDK parameter binding. Values cross the host boundary as text.
[<CLIMutable>]
type ParameterDescriptor =
    { Key: string
      Label: string
      Type: WorkflowParameterValueType
      Required: bool
      Description: string
      HasDefault: bool
      DefaultValue: string
      Minimum: Nullable<decimal>
      Maximum: Nullable<decimal>
      Choices: string array
      ContextBinding: string
      SourceLine: int
      SourceColumn: int }

/// Strongly typed, non-parameter workflow metadata declared in the fsx file.
[<CLIMutable>]
type WorkflowDeclarationInfo =
    { Name: string
      Description: string
      EntryPoint: string
      SelectionScope: Patchouli.Workflows.WorkflowSelectionScope
      MenuPath: string
      MenuOrder: int
      ShowInMenu: bool }

/// Structured diagnostic from declaration extraction or parameter validation.
[<CLIMutable>]
type WorkflowParameterIssue =
    { Key: string
      Code: string
      Message: string
      Line: int
      Column: int }

[<CLIMutable>]
type ParameterResolutionResult =
    { Succeeded: bool
      Values: Collections.Generic.IReadOnlyDictionary<string, string>
      Issues: WorkflowParameterIssue array }

/// Provider and model selected for a workflow session.
[<CLIMutable>]
type ModelSelection =
    { ProviderId: string
      Model: string }

[<RequireQualifiedAccess>]
module ModelSelectionCodec =
    let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                                     PropertyNameCaseInsensitive = true)
    /// Stable JSON representation shared by the editor, host and script input.
    [<CompiledName("Encode")>]
    let encode (selection: ModelSelection) : string =
        if Object.ReferenceEquals(selection, null) then nullArg "selection"
        JsonSerializer.Serialize(selection, jsonOptions)

    /// Reads the stable JSON representation and rejects missing identities.
    [<CompiledName("Decode")>]
    let decode (value: string) : ModelSelection =
        if String.IsNullOrWhiteSpace value then invalidArg "value" "A model selection is required."
        let selectionObject = JsonSerializer.Deserialize<ModelSelection>(value, jsonOptions) |> box
        if isNull selectionObject then
            invalidArg "value" "A model selection must include providerId and model."
        let selection = unbox<ModelSelection> selectionObject
        if String.IsNullOrWhiteSpace selection.ProviderId
           || String.IsNullOrWhiteSpace selection.Model then
            invalidArg "value" "A model selection must include providerId and model."
        selection

/// An opaque, typed reference to one parameter descriptor. Construct descriptors with Parameter functions.
[<Sealed>]
type Parameter<'T> internal (descriptor: ParameterDescriptor, decode: string -> 'T) =
    member internal _.Descriptor = descriptor
    member internal _.Decode = decode

[<RequireQualifiedAccess>]
module Parameter =
    let private descriptor key label valueType required description hasDefault defaultValue =
        if String.IsNullOrWhiteSpace key then invalidArg "key" "A stable parameter key is required."
        if String.IsNullOrWhiteSpace label then invalidArg "label" "A parameter display name is required."
        { Key = key
          Label = label
          Type = valueType
          Required = required
          Description = description
          HasDefault = hasDefault
          DefaultValue = defaultValue
          Minimum = Nullable()
          Maximum = Nullable()
          Choices = [||]
          ContextBinding = ""
          SourceLine = 0
          SourceColumn = 0 }

    let private textParameter valueType key label defaultValue required description =
        Parameter<string>(descriptor key label valueType required description true defaultValue, id)

    let text key label defaultValue = textParameter WorkflowParameterValueType.Text key label defaultValue false ""
    let multilineText key label defaultValue = textParameter WorkflowParameterValueType.MultilineText key label defaultValue false ""
    let language key label defaultValue = textParameter WorkflowParameterValueType.Language key label defaultValue false ""
    let pageRange key label defaultValue = textParameter WorkflowParameterValueType.PageRange key label defaultValue false ""
    let textSelection key label = textParameter WorkflowParameterValueType.TextSelection key label "" true ""

    let integer key label (defaultValue: int) =
        Parameter<int>(descriptor key label WorkflowParameterValueType.Integer false "" true (defaultValue.ToString(CultureInfo.InvariantCulture)),
                      fun value -> Int32.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture))

    let decimal key label (defaultValue: decimal) =
        Parameter<decimal>(descriptor key label WorkflowParameterValueType.Decimal false "" true (defaultValue.ToString(CultureInfo.InvariantCulture)),
                           fun value -> System.Decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture))

    let boolean key label defaultValue =
        let defaultText = if defaultValue then "true" else "false"
        Parameter<bool>(descriptor key label WorkflowParameterValueType.Boolean false "" true defaultText,
                       fun (value: string) -> Boolean.Parse value)

    let choice key label choices defaultValue =
        let items = choices |> List.toArray
        if items.Length = 0 || items |> Array.exists (fun (item: string) -> String.IsNullOrWhiteSpace item) then
            invalidArg "choices" "A choice parameter needs non-empty options."
        if not (Array.contains defaultValue items) then invalidArg "defaultValue" "The default must be one of the declared choices."
        let item = { descriptor key label WorkflowParameterValueType.Choice false "" true defaultValue with Choices = items }
        Parameter<string>(item, id)

    let model key label =
        Parameter<ModelSelection>(descriptor key label WorkflowParameterValueType.Model true "" false "", ModelSelectionCodec.decode)

    let documents key label =
        Parameter<string list>(descriptor key label WorkflowParameterValueType.Documents false "" true "[]",
                               fun value ->
                                   let parsedObject = JsonSerializer.Deserialize<string array>(value) |> box
                                   if isNull parsedObject then
                                       invalidArg "value" "A document list must contain non-empty document identifiers."
                                   let parsed = unbox<string array> parsedObject
                                   if parsed |> Array.exists String.IsNullOrWhiteSpace then
                                       invalidArg "value" "A document list must contain non-empty document identifiers."
                                   parsed |> Array.toList)

    let required description (parameter: Parameter<'T>) =
        Parameter<'T>({ parameter.Descriptor with Required = true; Description = description }, parameter.Decode)

    let describe description (parameter: Parameter<'T>) =
        Parameter<'T>({ parameter.Descriptor with Description = description }, parameter.Decode)

    let intRange (minimum: int) (maximum: int) (parameter: Parameter<int>) =
        if minimum > maximum then invalidArg "minimum" "The minimum cannot exceed the maximum."
        let minimumValue = Nullable<decimal>(System.Convert.ToDecimal(minimum))
        let maximumValue = Nullable<decimal>(System.Convert.ToDecimal(maximum))
        Parameter<int>({ parameter.Descriptor with Minimum = minimumValue; Maximum = maximumValue }, parameter.Decode)

    let decimalRange (minimum: decimal) (maximum: decimal) (parameter: Parameter<decimal>) =
        if minimum > maximum then invalidArg "minimum" "The minimum cannot exceed the maximum."
        Parameter<decimal>({ parameter.Descriptor with Minimum = Nullable<decimal>(minimum); Maximum = Nullable<decimal>(maximum) }, parameter.Decode)

    let bindContext binding (parameter: Parameter<'T>) =
        if String.IsNullOrWhiteSpace binding then invalidArg "binding" "A context binding name is required."
        Parameter<'T>({ parameter.Descriptor with ContextBinding = binding }, parameter.Decode)

    /// Reads this parameter from a frozen workflow input using its declared type.
    let get (parameter: Parameter<'T>) (input: WorkflowInput) : 'T =
        if Object.ReferenceEquals(parameter, null) then nullArg "parameter"
        if Object.ReferenceEquals(input, null) then nullArg "input"
        match input.Parameters |> Map.tryFind parameter.Descriptor.Key with
        | Some value -> parameter.Decode value
        | None when parameter.Descriptor.HasDefault -> parameter.Decode parameter.Descriptor.DefaultValue
        | None -> invalidOp ("WORKFLOW_PARAMETER_MISSING: " + parameter.Descriptor.Key)

/// SDK declaration for workflow-level descriptive and selection metadata.
[<Sealed>]
type WorkflowInfo internal (value: WorkflowDeclarationInfo) =
    member internal _.Value = value

[<RequireQualifiedAccess>]
module WorkflowInfo =
    let create name description =
        if String.IsNullOrWhiteSpace name then invalidArg "name" "A workflow display name is required."
        WorkflowInfo({ Name = name; Description = description
                       EntryPoint = "run"; SelectionScope = Patchouli.Workflows.WorkflowSelectionScope.Nothing
                       MenuPath = ""; MenuOrder = 0; ShowInMenu = false })

    let entryPoint name (info: WorkflowInfo) =
        WorkflowInfo({ info.Value with EntryPoint = name })

    let selectionScope scope (info: WorkflowInfo) =
        WorkflowInfo({ info.Value with SelectionScope = scope })

    let menu path order (info: WorkflowInfo) =
        if order < 0 then invalidArg "order" "Menu order cannot be negative."
        let menuPath = path
        WorkflowInfo({ info.Value with MenuPath = menuPath; MenuOrder = order; ShowInMenu = not (String.IsNullOrWhiteSpace path) })
