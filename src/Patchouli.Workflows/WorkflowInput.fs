namespace Patchouli.Workflows.Scripting

open System

[<RequireQualifiedAccess>]
module ScriptApiVersion =
    [<Literal>]
    let Current = "patchouli.workflow.harness/7"

/// Persisted launch selection. Scripts receive its immutable list-based projection.
type WorkflowSelection =
    { Documents: string[]
      PageRange: string
      TextSelection: string }

[<RequireQualifiedAccess>]
module WorkflowSelections =
    let empty =
        { Documents = [||]
          PageRange = String.Empty
          TextSelection = String.Empty }

/// Immutable input to an agent harness; this value has no execution capabilities.
type WorkflowInput =
    { Documents: string list
      PageRange: string
      Parameters: Map<string, string> }

[<RequireQualifiedAccess>]
module WorkflowInput =
    let parameter key fallback input =
        input.Parameters |> Map.tryFind key |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue fallback
