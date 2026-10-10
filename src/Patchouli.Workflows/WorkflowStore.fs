namespace Patchouli.Workflows

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

/// <summary>The outcome of one workflow store mutation.</summary>
type WorkflowMutationResult =
    /// <summary>The mutation was written.</summary>
    | Applied

    /// <summary>The definition failed validation; nothing was written.</summary>
    | Invalid of problems: string[]

    /// <summary>The workflow is locked (a built-in, or a definition the user locked); nothing was written.</summary>
    | Locked of workflowId: string

    /// <summary>The workflow id has no stored definition.</summary>
    | NotFound of workflowId: string

/// <summary>
///     Durable storage for workflow definitions and their scripts: one definition document and one
///     <c>.fsx</c> script per workflow under a <c>workflows/</c> directory that travels with the
///     Library (ADR 0036, plan §3.4).
/// </summary>
/// <remarks>
///     <para>
///         Workflow definitions are <b>user configuration</b>: they are saved with the Library next to
///         the runtime database but are not part of the rebuildable index and never advance
///         <c>library_revision</c>. They are managed separately from agent session data.
///     </para>
///     <para>
///         Built-in workflows are registered in code and always locked: they can never be written or
///         deleted by this store, and a stored file for a built-in id is ignored (a stale copy can
///         never shadow the shipped definition).
///     </para>
///     <para>
///         A missing workflows directory is a normal, non-fatal condition: every read reports "not
///         found" instead of throwing, so a Library always opens. Locked workflows are refused
///         explicitly rather than silently overwritten.
///     </para>
/// </remarks>
[<Sealed>]
type WorkflowStore(root: string) =
    static let idPattern = Regex(WorkflowDefinitions.IdPattern, RegexOptions.CultureInvariant)

    /// <summary>Auxiliary Library directory that holds the workflow definitions and scripts.</summary>
    static let directoryName = "workflows"

    /// <summary>Subdirectory of the workflows root that holds the <c>.fsx</c> script files.</summary>
    static let scriptsDirectoryName = "scripts"
    static let configurationDirectoryName = "configuration"
    static let legacyBackupDirectoryName = "legacy-backups"

    static let writeAtomicAsync (path: string) (content: string) (cancellationToken: CancellationToken) : Task =
        task {
            let directory = WorkflowPaths.parentDirectory path
            Directory.CreateDirectory directory |> ignore

            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
            try
                do! File.WriteAllTextAsync(temporary, content, UTF8Encoding(false), cancellationToken)
                File.Move(temporary, path, true)
            finally
                if File.Exists temporary then
                    File.Delete temporary
        }

    static let tryReadTextAsync (path: string) (cancellationToken: CancellationToken) : Task<string option> =
        task {
            if not (File.Exists path) then
                return None
            else
                try
                    let! content = File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                    return Some content
                with
                | :? IOException -> return None
                | :? UnauthorizedAccessException -> return None
        }

    do
        if String.IsNullOrWhiteSpace root then
            nullArg "root"

    let resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)

    /// <summary>Resolves the workflows root of one Library from its runtime database path.</summary>
    static member ForLibrary(libraryDatabasePath: string) : WorkflowStore =
        if String.IsNullOrWhiteSpace libraryDatabasePath then
            nullArg "libraryDatabasePath"

        let full = Path.GetFullPath libraryDatabasePath
        match Path.GetDirectoryName full with
        | null -> invalidArg "libraryDatabasePath" "The library path must have a parent directory."
        | directory -> WorkflowStore(Path.Combine(directory, directoryName))

    /// <summary>The workflows root directory (it may not exist yet).</summary>
    member _.Root = resolvedRoot

    /// <summary>The absolute definition document path of one workflow id.</summary>
    member _.ResolveDefinitionPath(workflowId: string) : string =
        WorkflowStore.RequireId workflowId
        Path.Combine(resolvedRoot, workflowId + ".json")

    /// <summary>The absolute <c>.fsx</c> script path of one workflow id.</summary>
    member _.ResolveScriptPath(workflowId: string) : string =
        WorkflowStore.RequireId workflowId
        Path.Combine(resolvedRoot, scriptsDirectoryName, workflowId + ".fsx")

    /// <summary>The durable values file for one workflow's statically declared parameters.</summary>
    member _.ResolveConfigurationPath(workflowId: string) : string =
        WorkflowStore.RequireId workflowId
        Path.Combine(resolvedRoot, configurationDirectoryName, workflowId + ".json")

    /// <summary>The preserved pre-migration definition document for one workflow, when needed.</summary>
    member _.ResolveLegacyBackupPath(workflowId: string) : string =
        WorkflowStore.RequireId workflowId
        Path.Combine(resolvedRoot, legacyBackupDirectoryName, workflowId + ".json")

    /// <summary>Workflow ids with a stored definition, in ordinal order; empty when the root is missing.</summary>
    member _.ListIdsAsync(cancellationToken: CancellationToken) : Task<IReadOnlyList<string>> =
        task {
            if not (Directory.Exists resolvedRoot) then
                return [||] :> IReadOnlyList<string>
            else
                let ids = ResizeArray<string>()
                for path in Directory.EnumerateFiles(resolvedRoot, "*.json") do
                    match Path.GetFileNameWithoutExtension path with
                    | null -> ()
                    | id ->
                        if idPattern.IsMatch(id) then
                            ids.Add id
                ids.Sort StringComparer.Ordinal
                return ids.ToArray() :> IReadOnlyList<string>
        }

    /// <summary>
    ///     Loads one definition: built-ins first (they can never be shadowed), then the stored
    ///     document. <c>None</c> means the id has no definition at all.
    /// </summary>
    member this.TryLoadAsync(workflowId: string, cancellationToken: CancellationToken)
        : Task<WorkflowDefinition option> =
        task {
            WorkflowStore.RequireId workflowId
            match BuiltInWorkflows.tryFind workflowId with
            | Some builtIn -> return Some builtIn
            | None ->
                let! document = tryReadTextAsync (this.ResolveDefinitionPath workflowId) cancellationToken
                return
                    match document with
                    | Some json -> Some(WorkflowCodec.definitionFromJson json)
                    | None -> None
        }

    /// <summary>
    ///     Every workflow the Library can launch: the built-ins in their menu order followed by the
    ///     stored workflows in ordinal id order.
    /// </summary>
    member this.ListDefinitionsAsync(cancellationToken: CancellationToken) : Task<IReadOnlyList<WorkflowDefinition>> =
        task {
            let definitions = ResizeArray<WorkflowDefinition>()
            definitions.AddRange BuiltInWorkflows.all
            let! ids = this.ListIdsAsync cancellationToken
            for id in ids do
                let! document = tryReadTextAsync (this.ResolveDefinitionPath id) cancellationToken
                match document with
                | Some json ->
                    try
                        definitions.Add(WorkflowCodec.definitionFromJson json)
                    with
                    | :? System.Text.Json.JsonException -> ()
                | None -> ()
            return definitions.ToArray() :> IReadOnlyList<WorkflowDefinition>
        }

    /// <summary>
    ///     Saves one definition. Validation problems and locked workflows are reported instead of
    ///     writing, so a locked built-in or a locked user workflow can never be modified.
    /// </summary>
    member this.SaveAsync(definition: WorkflowDefinition, cancellationToken: CancellationToken)
        : Task<WorkflowMutationResult> =
        task {
            if Object.ReferenceEquals(definition, null) then
                return Invalid [| "The workflow definition is null." |]
            else
                let validation = WorkflowDefinitions.validate definition
                if not validation.IsValid then
                    return Invalid validation.Problems
                elif BuiltInWorkflows.isBuiltIn definition.Id then
                    return Locked definition.Id
                else
                    let! existing = this.TryLoadAsync(definition.Id, cancellationToken)
                    match existing with
                    | Some current when current.Locked -> return Locked definition.Id
                    | _ ->
                        do!
                            writeAtomicAsync (this.ResolveDefinitionPath definition.Id)
                                (WorkflowCodec.definitionToJson definition) cancellationToken
                        return Applied
        }

    /// <summary>Deletes one stored definition and its script. Locked workflows are never deleted.</summary>
    member this.DeleteAsync(workflowId: string, cancellationToken: CancellationToken) : Task<WorkflowMutationResult> =
        task {
            WorkflowStore.RequireId workflowId
            if BuiltInWorkflows.isBuiltIn workflowId then
                return Locked workflowId
            else
                let! existing = this.TryLoadAsync(workflowId, cancellationToken)
                match existing with
                | None -> return NotFound workflowId
                | Some current when current.Locked -> return Locked workflowId
                | Some _ ->
                    cancellationToken.ThrowIfCancellationRequested()
                    let definition = this.ResolveDefinitionPath workflowId
                    if File.Exists definition then
                        File.Delete definition
                    let script = this.ResolveScriptPath workflowId
                    if File.Exists script then
                        File.Delete script
                    let configuration = this.ResolveConfigurationPath workflowId
                    if File.Exists configuration then
                        File.Delete configuration
                    return Applied
        }

    /// <summary>
    ///     Reads the <c>.fsx</c> script of one workflow. A built-in without a registered script body
    ///     reports <c>None</c>: the executor turns that into a structured failure (see
    ///     <see cref="BuiltInWorkflowScripts" />).
    /// </summary>
    member this.ReadScriptAsync(workflowId: string, cancellationToken: CancellationToken) : Task<string option> =
        task {
            WorkflowStore.RequireId workflowId
            match BuiltInWorkflows.isBuiltIn workflowId with
            | true -> return BuiltInWorkflowScripts.tryGetText workflowId
            | false -> return! tryReadTextAsync (this.ResolveScriptPath workflowId) cancellationToken
        }

    /// <summary>
    ///     Writes the <c>.fsx</c> script of one stored workflow. The definition must exist and must
    ///     not be locked, so a script can never be attached to a locked built-in.
    /// </summary>
    member this.SaveScriptAsync(workflowId: string, scriptText: string, cancellationToken: CancellationToken)
        : Task<WorkflowMutationResult> =
        task {
            WorkflowStore.RequireId workflowId
            if BuiltInWorkflows.isBuiltIn workflowId then
                return Locked workflowId
            else
                let! existing = this.TryLoadAsync(workflowId, cancellationToken)
                match existing with
                | None -> return NotFound workflowId
                | Some current when current.Locked -> return Locked workflowId
                | Some _ ->
                    do!
                        writeAtomicAsync (this.ResolveScriptPath workflowId) (WorkflowText.orEmpty scriptText)
                            cancellationToken
                    return Applied
        }

    static member private RequireId(workflowId: string) : unit =
        if String.IsNullOrWhiteSpace workflowId then
            nullArg "workflowId"

        if not (idPattern.IsMatch workflowId) then
            invalidArg "workflowId"
                $"Invalid workflow id '{workflowId}': expected 1-64 characters of [a-z0-9._-]."
