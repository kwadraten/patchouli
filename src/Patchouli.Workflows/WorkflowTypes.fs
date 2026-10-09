namespace Patchouli.Workflows

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions

/// <summary>Null-handling helpers shared by the workflow surface (F# has no null-coalescing operator).</summary>
[<RequireQualifiedAccess>]
module WorkflowText =
    /// <summary>The value itself, or the empty string when it is null.</summary>
    let orEmpty (value: string | null) : string =
        match value with
        | null -> String.Empty
        | text -> text

/// <summary>Path helpers shared by the workflow store and the run sink.</summary>
[<RequireQualifiedAccess>]
module WorkflowPaths =
    /// <summary>The parent directory of one path (the current directory when it has none).</summary>
    let parentDirectory (path: string) : string =
        match Path.GetDirectoryName path with
        | null -> "."
        | directory -> directory

/// <summary>
///     Declared type of one workflow launch parameter. The type is advisory metadata for the
///     settings page, the launch form and the future <c>MCP workflows/</c> projection: the value
///     itself always travels as text in the launch parameters.
/// </summary>
type WorkflowParameterType =
    | Text = 0
    | DocumentId = 1
    | PageRange = 2
    | Language = 3
    | Integer = 4
    | Boolean = 5

/// <summary>
///     The Library selection a workflow may be launched against (ADR 0036). The value is a bit mask
///     so a workflow can accept several shapes of selection (for example documents or page ranges).
/// </summary>
[<Flags>]
type WorkflowSelectionScope =
    | Nothing = 0

    /// <summary>Whole selected documents.</summary>
    | Documents = 1

    /// <summary>Page ranges inside the selected documents.</summary>
    | Pages = 2

    /// <summary>The current text selection of the reading view.</summary>
    | Selection = 4

    /// <summary>Documents or page ranges.</summary>
    | DocumentsAndPages = 3

    /// <summary>Any of the supported selections.</summary>
    | All = 7

/// <summary>One declared launch parameter of a workflow.</summary>
/// <param name="Name">Stable parameter name used as the launch-parameter key.</param>
/// <param name="Type">Advisory parameter type (drives the launch form and the MCP projection).</param>
/// <param name="Required">True when a launch must supply a value.</param>
/// <param name="Description">Human-readable description shown in the launch form.</param>
/// <param name="DefaultValue">Text default used when the launch omits the parameter.</param>
type WorkflowParameter =
    { Name: string
      Type: WorkflowParameterType
      Required: bool
      Description: string
      DefaultValue: string }

/// <summary>Where a workflow appears in the workflow menus.</summary>
/// <param name="MenuPath">Slash-separated menu path (for example <c>Tools/Workflows</c>).</param>
/// <param name="Order">Ascending sort order inside the menu.</param>
/// <param name="ShowInMenu">False keeps the workflow launchable but out of the menus.</param>
type WorkflowMenuPlacement =
    { MenuPath: string
      Order: int
      ShowInMenu: bool }

/// <summary>
///     One workflow: a <c>.fsx</c> script plus its metadata (ADR 0036, plan §3.2). Workflow
///     definitions are user configuration saved with the Library; they are never part of the
///     rebuildable index and never advance <c>library_revision</c>.
/// </summary>
/// <param name="Id">Stable id (lower case, path-safe) used by sessions, menus and the MCP surface.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Description shown in the settings page and the launch form.</param>
/// <param name="ScriptEntryPoint">
///     Name of the top-level function the executor calls, for example <c>run</c>. The script is
///     evaluated in-process by F# Interactive, so the entry point is a script-level function
///     binding rather than a module-qualified name.
/// </param>
/// <param name="Parameters">Declared launch parameters, in declaration order.</param>
/// <param name="SelectionScope">Selection shapes the workflow accepts.</param>
/// <param name="Locked">True when the definition can neither be edited nor deleted.</param>
/// <param name="Menu">Menu placement.</param>
/// <param name="BuiltIn">True for definitions shipped with Patchouli (always locked in v1).</param>
type WorkflowDefinition =
    { Id: string
      Name: string
      Description: string
      ScriptEntryPoint: string
      Parameters: WorkflowParameter[]
      SelectionScope: WorkflowSelectionScope
      Locked: bool
      Menu: WorkflowMenuPlacement
      BuiltIn: bool }

/// <summary>The outcome of validating one workflow definition.</summary>
/// <param name="IsValid">True when the definition may be saved and launched.</param>
/// <param name="Problems">Human-readable problems, empty when the definition is valid.</param>
type WorkflowValidation =
    { IsValid: bool
      Problems: string[] }

/// <summary>Stable ids of the workflows Patchouli ships.</summary>
[<RequireQualifiedAccess>]
module WorkflowIds =
    /// <summary>The locked built-in "Full-text translation" workflow (ADR 0036, plan §3.5).</summary>
    [<Literal>]
    let FullTextTranslation = "builtin.full-text-translation"

/// <summary>Validation and construction helpers for workflow definitions.</summary>
[<RequireQualifiedAccess>]
module WorkflowDefinitions =
    /// <summary>Accepted workflow ids: lower case, digits, dot, dash and underscore.</summary>
    [<Literal>]
    let IdPattern = "^[a-z0-9][a-z0-9._-]{0,63}$"

    /// <summary>Accepted entry-point names: an F# value identifier.</summary>
    [<Literal>]
    let EntryPointPattern = "^[A-Za-z_][A-Za-z0-9_']{0,63}$"

    /// <summary>Accepted parameter names: an F#-style identifier with dashes allowed.</summary>
    [<Literal>]
    let ParameterNamePattern = "^[A-Za-z_][A-Za-z0-9_-]{0,63}$"

    /// <summary>The default menu path of a new user workflow.</summary>
    [<Literal>]
    let DefaultMenuPath = "Tools/Workflows"

    let private regexFor (text: string) = Regex(text, RegexOptions.CultureInvariant)

    /// <summary>
    ///     Validates one definition. Every problem is reported (never just the first), because the
    ///     settings page shows the complete list next to the editor.
    /// </summary>
    let validate (definition: WorkflowDefinition) : WorkflowValidation =
        if Object.ReferenceEquals(definition, null) then
            { IsValid = false
              Problems = [| "The workflow definition is null." |] }
        else
            let problems = ResizeArray<string>()
            if String.IsNullOrWhiteSpace definition.Id then
                problems.Add("The workflow id is required.")
            elif not ((regexFor IdPattern).IsMatch(definition.Id)) then
                problems.Add(
                    $"Invalid workflow id '{definition.Id}': expected 1-64 characters of [a-z0-9._-] starting with a letter or digit.")

            if String.IsNullOrWhiteSpace definition.Name then
                problems.Add("The workflow name is required.")

            if String.IsNullOrWhiteSpace definition.ScriptEntryPoint then
                problems.Add("The script entry point is required.")
            elif not ((regexFor EntryPointPattern).IsMatch(definition.ScriptEntryPoint)) then
                problems.Add(
                    $"Invalid script entry point '{definition.ScriptEntryPoint}': expected an F# value name such as 'run'.")

            if definition.Menu.ShowInMenu && String.IsNullOrWhiteSpace definition.Menu.MenuPath then
                problems.Add("A menu path is required when the workflow appears in the menus.")

            if not (Object.ReferenceEquals(definition.Parameters, null)) then
                let seen = HashSet<string>(StringComparer.Ordinal)
                for parameter in definition.Parameters do
                    if Object.ReferenceEquals(parameter, null) then
                        problems.Add("A parameter definition is null.")
                    else
                        if String.IsNullOrWhiteSpace parameter.Name then
                            problems.Add("A parameter name is required.")
                        elif not ((regexFor ParameterNamePattern).IsMatch(parameter.Name)) then
                            problems.Add(
                                $"Invalid parameter name '{parameter.Name}': expected 1-64 characters of [A-Za-z0-9_-].")
                        elif not (seen.Add parameter.Name) then
                            problems.Add($"Duplicate parameter name '{parameter.Name}'.")

            { IsValid = problems.Count = 0
              Problems = problems.ToArray() }

    /// <summary>Creates an unlocked user workflow with no declared parameters.</summary>
    let create (id: string) (name: string) (description: string) (entryPoint: string) : WorkflowDefinition =
        { Id = id
          Name = name
          Description = description
          ScriptEntryPoint = entryPoint
          Parameters = [||]
          SelectionScope = WorkflowSelectionScope.DocumentsAndPages
          Locked = false
          Menu =
            { MenuPath = DefaultMenuPath
              Order = 100
              ShowInMenu = true }
          BuiltIn = false }

/// <summary>The workflow definitions Patchouli ships. Built-ins are registered in code, not on disk.</summary>
[<RequireQualifiedAccess>]
module BuiltInWorkflows =
    /// <summary>
    ///     The locked built-in "Full-text translation" workflow. This task registers its metadata,
    ///     menu placement and locked state; its script body is registered by the next task through
    ///     <see cref="BuiltInWorkflowScripts" />.
    /// </summary>
    let fullTextTranslation: WorkflowDefinition =
        { Id = WorkflowIds.FullTextTranslation
          Name = "全文翻译"
          Description =
            "Controls the built-in agent to translate selected documents with its existing find/fetch/put tools, " +
            "bounded inference and tool budgets, preserving structure and reporting failed pages."
          ScriptEntryPoint = "run"
          Parameters =
            [| { Name = "documentId"
                 Type = WorkflowParameterType.DocumentId
                 Required = true
                 Description = "Document to translate."
                 DefaultValue = "" }
               { Name = "pageRange"
                 Type = WorkflowParameterType.PageRange
                 Required = false
                 Description = "Pages to translate; empty means the whole document."
                 DefaultValue = "" }
               { Name = "targetLanguage"
                 Type = WorkflowParameterType.Language
                 Required = false
                 Description = "Target language; empty means the configured default."
                 DefaultValue = "" } |]
          SelectionScope = WorkflowSelectionScope.DocumentsAndPages
          Locked = true
          Menu =
            { MenuPath = "Tools/Workflows"
              Order = 10
              ShowInMenu = true }
          BuiltIn = true }

    /// <summary>All built-in definitions in menu order.</summary>
    let all: WorkflowDefinition[] = [| fullTextTranslation |]

    /// <summary>Looks up a built-in definition by id.</summary>
    let tryFind (workflowId: string) : WorkflowDefinition option =
        if String.IsNullOrWhiteSpace workflowId then
            None
        else
            all |> Array.tryFind (fun candidate -> String.Equals(candidate.Id, workflowId, StringComparison.Ordinal))

    /// <summary>True when the id belongs to a built-in (and therefore locked) workflow.</summary>
    let isBuiltIn (workflowId: string) : bool = (tryFind workflowId).IsSome

/// <summary>
///     Registration point for the script bodies of the built-in workflows. A body ships as an
///     embedded <c>.fsx</c> next to this assembly, so an unregistered built-in is reported by the
///     executor as a structured failure instead of silently running nothing, and the settings page
///     can still offer "copy from built-in" from the same text.
/// </summary>
[<RequireQualifiedAccess>]
module BuiltInWorkflowScripts =
    /// <summary>The embedded script file of the built-in "Full-text translation" workflow.</summary>
    [<Literal>]
    let private translationScriptFile = "full-text-translation.fsx"

    /// <summary>
    ///     Reads one embedded script by file name. Only the file name is part of the contract, so
    ///     moving the script inside the project does not break the registration.
    /// </summary>
    let private readEmbeddedScript (fileName: string) : string option =
        let assembly = System.Reflection.Assembly.GetExecutingAssembly()

        let readStream (stream: Stream | null) : string option =
            match stream with
            | null -> None
            | available ->
                use reader = new StreamReader(available)
                Some(reader.ReadToEnd())

        match
            assembly.GetManifestResourceNames()
            |> Array.tryFind (fun name -> name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        with
        | None -> None
        | Some name -> readStream (assembly.GetManifestResourceStream name)

    /// <summary>Script bodies registered so far, keyed by workflow id.</summary>
    let private registered: IReadOnlyDictionary<string, string> =
        let texts = Dictionary<string, string>(StringComparer.Ordinal)

        match readEmbeddedScript translationScriptFile with
        | Some text when not (String.IsNullOrWhiteSpace text) -> texts[WorkflowIds.FullTextTranslation] <- text
        | _ -> ()

        texts :> IReadOnlyDictionary<string, string>

    /// <summary>Returns the registered script text of one built-in workflow, when it exists yet.</summary>
    let tryGetText (workflowId: string) : string option =
        match registered.TryGetValue workflowId with
        | true, text -> Some text
        | _ -> None

    /// <summary>True when the built-in workflow has a script body registered.</summary>
    let hasText (workflowId: string) : bool = (tryGetText workflowId).IsSome

/// <summary>
///     The immutable script a session started with (ADR 0036 "script snapshots"). Editing a
///     workflow definition affects later launches only: a running session keeps executing the
///     snapshot it stored, which is why the executor never re-reads the workflow definition while
///     a session is live.
/// </summary>
/// <param name="WorkflowId">Id of the workflow the snapshot was taken from.</param>
/// <param name="WorkflowName">Display name at capture time (kept so a renamed workflow stays readable).</param>
/// <param name="ScriptEntryPoint">Entry-point function name at capture time.</param>
/// <param name="ScriptText">The exact script text the session executes.</param>
/// <param name="ScriptHash">SHA-256 (lower-case hex) of <paramref name="ScriptText" />, for change detection.</param>
/// <param name="ApiVersion">The versioned script API the snapshot was captured against.</param>
/// <param name="CapturedAt">Capture time as a round-trip ISO 8601 UTC string.</param>
type WorkflowScriptSnapshot =
    { WorkflowId: string
      WorkflowName: string
      ScriptEntryPoint: string
      ScriptText: string
      ScriptHash: string
      ApiVersion: string
      CapturedAt: string }

/// <summary>
///     One recorded step of a workflow run: the script API call (or terminal request) the executor
///     performed, in recorded order. The step log is the replay material of a session: every step
///     that completed carries the result the script saw, so a resumed run reuses the payload
///     instead of re-invoking the model or a tool (ADR 0036 recovery model).
/// </summary>
/// <param name="Step">
///     1-based position in the session's step log. Positions are the replay key: the script is
///     deterministic, so the n-th side-effecting API call of a resumed run corresponds to the
///     n-th recorded step.
/// </param>
/// <param name="EffectId">
///     The effect id the step issued, minted by <c>AgentCore</c> (0 when the step issued no
///     effect, for example an appended instruction). Every issued effect therefore carries an id
///     from the core's own mint sequence, which skips ids already present in the call ledger.
/// </param>
/// <param name="Kind">Agent effect kind (LlmChat, McpToolCall or WaitRunEvent).</param>
/// <param name="Request">The request as issued, for diagnostics and replay mismatch detection.</param>
/// <param name="Completed">True once a result was taken; only completed steps are reused on replay.</param>
/// <param name="Result">The payload handed back to the script.</param>
type WorkflowStepRecord =
    { Step: int
      EffectId: int64
      Kind: string
      Request: string
      Completed: bool
      Result: string }

/// <summary>Snapshot construction helpers.</summary>
[<RequireQualifiedAccess>]
module WorkflowSnapshots =
    let private hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()

    /// <summary>Computes the stable content hash of one script text.</summary>
    let hashScript (scriptText: string) : string =
        use sha = System.Security.Cryptography.SHA256.Create()
        hex (sha.ComputeHash(Text.Encoding.UTF8.GetBytes(WorkflowText.orEmpty scriptText)))

    /// <summary>
    ///     Captures the snapshot a session will run: the definition's identity and entry point plus
    ///     the exact script text. The definition is not referenced afterwards.
    /// </summary>
    let capture (definition: WorkflowDefinition) (scriptText: string) (apiVersion: string)
        (capturedAt: DateTimeOffset) : WorkflowScriptSnapshot =
        if Object.ReferenceEquals(definition, null) then
            nullArg "definition"

        { WorkflowId = definition.Id
          WorkflowName = definition.Name
          ScriptEntryPoint = definition.ScriptEntryPoint
          ScriptText = WorkflowText.orEmpty scriptText
          ScriptHash = hashScript scriptText
          ApiVersion = apiVersion
          CapturedAt = capturedAt.ToUniversalTime().ToString("O", Globalization.CultureInfo.InvariantCulture) }
