namespace Patchouli.Workflows.Scripting

open System

[<RequireQualifiedAccess>]
module ScriptApiVersion =
    [<Literal>]
    let Current = "patchouli.workflow.harness/5"

/// Persisted launch selection. Scripts receive its immutable list-based projection.
type WorkflowSelection =
    { Documents: string[]
      PageRange: string
      TargetLanguage: string
      Languages: string[] }

[<RequireQualifiedAccess>]
module WorkflowSelections =
    let empty =
        { Documents = [||]
          PageRange = String.Empty
          TargetLanguage = String.Empty
          Languages = [||] }

/// Immutable input to an agent harness; this value has no execution capabilities.
type WorkflowInput =
    { Documents: string list
      PageRange: string
      TargetLanguage: string
      Languages: string list
      Parameters: Map<string, string> }

[<RequireQualifiedAccess>]
module WorkflowInput =
    let parameter key fallback input =
        input.Parameters |> Map.tryFind key |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue fallback
