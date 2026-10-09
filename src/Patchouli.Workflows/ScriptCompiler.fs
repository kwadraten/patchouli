namespace Patchouli.Workflows

open System
open System.IO
open System.Threading.Tasks
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open Patchouli.Workflows.Scripting

/// <summary>Severity of one script diagnostic, mirroring the F# compiler's severities.</summary>
type ScriptDiagnosticSeverity =
    | Error = 0
    | Warning = 1
    | Information = 2
    | Hidden = 3

/// <summary>One structured diagnostic of a workflow script (settings page and run log).</summary>
/// <param name="FileName">The script file the diagnostic belongs to.</param>
/// <param name="Line">1-based line of the diagnostic start.</param>
/// <param name="Column">0-based column of the diagnostic start (the F# compiler's convention).</param>
/// <param name="EndLine">1-based line of the diagnostic end.</param>
/// <param name="EndColumn">0-based column of the diagnostic end.</param>
/// <param name="Severity">Error, warning or informational.</param>
/// <param name="ErrorNumber">Compiler error number text, for example <c>FS0039</c>.</param>
/// <param name="Message">Human-readable message.</param>
type ScriptDiagnostic =
    { FileName: string
      Line: int
      Column: int
      EndLine: int
      EndColumn: int
      Severity: ScriptDiagnosticSeverity
      ErrorNumber: string
      Message: string }

/// <summary>The outcome of a save-time compile check.</summary>
/// <param name="Succeeded">True when the script compiled without errors.</param>
/// <param name="Diagnostics">Every diagnostic the compiler produced, in compiler order.</param>
type ScriptCheckResult =
    { Succeeded: bool
      Diagnostics: ScriptDiagnostic[] }

/// <summary>The outcome of evaluating one script interaction or expression.</summary>
/// <param name="Succeeded">True when the evaluation completed without errors.</param>
/// <param name="Value">The produced value (null for a unit result); the executor awaits it.</param>
/// <param name="Diagnostics">Compiler diagnostics of the evaluation.</param>
/// <param name="Failure">The exception message when the script itself threw (empty otherwise).</param>
type ScriptEvaluationResult =
    { Succeeded: bool
      Value: obj | null
      Diagnostics: ScriptDiagnostic[]
      Failure: string }

/// <summary>Conversion helpers between compiler diagnostics and the structured script diagnostics.</summary>
[<RequireQualifiedAccess>]
module ScriptDiagnostics =
    let private severity (value: FSharpDiagnosticSeverity) : ScriptDiagnosticSeverity =
        match value with
        | FSharpDiagnosticSeverity.Error -> ScriptDiagnosticSeverity.Error
        | FSharpDiagnosticSeverity.Warning -> ScriptDiagnosticSeverity.Warning
        | FSharpDiagnosticSeverity.Info -> ScriptDiagnosticSeverity.Information
        | _ -> ScriptDiagnosticSeverity.Hidden

    /// <summary>Converts one compiler diagnostic, attributing it to the caller's script file.</summary>
    let ofCompilerDiagnostic (fileName: string) (diagnostic: FSharpDiagnostic) : ScriptDiagnostic =
        { FileName = fileName
          Line = diagnostic.StartLine
          Column = diagnostic.StartColumn
          EndLine = diagnostic.EndLine
          EndColumn = diagnostic.EndColumn
          Severity = severity diagnostic.Severity
          ErrorNumber = diagnostic.ErrorNumberText
          Message = diagnostic.Message }

    /// <summary>Converts a compiler diagnostic array, attributing every entry to one script file.</summary>
    let ofCompilerDiagnostics (fileName: string) (diagnostics: FSharpDiagnostic[]) : ScriptDiagnostic[] =
        if Object.ReferenceEquals(diagnostics, null) then
            [||]
        else
            [| for diagnostic in diagnostics -> ofCompilerDiagnostic fileName diagnostic |]

    /// <summary>True when any diagnostic is an error (a warning alone does not block a save).</summary>
    let hasErrors (diagnostics: ScriptDiagnostic[]) : bool =
        not (Object.ReferenceEquals(diagnostics, null))
        && diagnostics |> Array.exists (fun diagnostic -> diagnostic.Severity = ScriptDiagnosticSeverity.Error)

    /// <summary>Renders one diagnostic as a single line, for a terminal or a status message.</summary>
    let describe (diagnostic: ScriptDiagnostic) : string =
        $"{diagnostic.FileName}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.ErrorNumber}: {diagnostic.Message}"

    /// <summary>Renders every diagnostic as lines, in compiler order.</summary>
    let describeAll (diagnostics: ScriptDiagnostic[]) : string =
        if Object.ReferenceEquals(diagnostics, null) || diagnostics.Length = 0 then
            String.Empty
        else
            diagnostics |> Array.map describe |> String.concat Environment.NewLine

    /// <summary>Removes duplicate diagnostics (the parser and the checker can both report one error).</summary>
    let distinct (diagnostics: ScriptDiagnostic[]) : ScriptDiagnostic[] =
        if Object.ReferenceEquals(diagnostics, null) then
            [||]
        else
            diagnostics
            |> Array.distinctBy (fun diagnostic ->
                diagnostic.Line, diagnostic.Column, diagnostic.EndLine, diagnostic.EndColumn, diagnostic.Severity,
                diagnostic.ErrorNumber, diagnostic.Message)

/// <summary>
///     One in-process F# Interactive session: the compilation and execution host of a workflow
///     script (ADR 0036 "trust model": scripts run in-process with host permissions and are not a
///     sandbox). A session is created per run so two runs never share F# state, and a transient
///     session serves the save-time compile check.
/// </summary>
/// <remarks>
///     <para>
///         The session references this assembly and the agent core explicitly, so a script can use the
///         versioned API without any <c>#r</c>; an explicit <c>#r</c> in the script still resolves
///         normally.
///     </para>
///     <para>
///         Script assemblies are loaded into the default load context. That is deliberate: the bound
///         API instance and the script must agree on type identity, which a collectible load context
///         would break. Scripts therefore cannot be unloaded individually, which is consistent with
///         the in-process, not-a-sandbox trust model.
///     </para>
/// </remarks>
[<Sealed>]
type ScriptHostSession internal (session: FsiEvaluationSession, output: StringWriter, failure: StringWriter) =
    /// <summary>Creates a fresh F# Interactive session referencing the workflow API surface.</summary>
    static member Create() : ScriptHostSession =
        let configuration = FsiEvaluationSession.GetDefaultConfiguration()
        let arguments =
            [| "fsi.exe"
               "--noninteractive"
               "--nologo"
               "--readline-"
               "--gui-"
               "-r:" + typeof<AgentWorkflow>.Assembly.Location
               "-r:" + typeof<Patchouli.Agent.Effect>.Assembly.Location |]

        let output = new StringWriter()
        let failure = new StringWriter()
        let session =
            FsiEvaluationSession.Create(configuration, arguments, new StringReader(String.Empty), output, failure)

        new ScriptHostSession(session, output, failure)

    /// <summary>Binds one host value into the session (the script reads it as a plain value).</summary>
    member _.BindValue(name: string, value: obj) : unit =
        if String.IsNullOrWhiteSpace name then
            nullArg "name"

        session.AddBoundValue(name, value)

    /// <summary>
    ///     Compile-checks script text without executing it: the settings page runs this before a save
    ///     and reports line/column/severity/message. Diagnostics are attributed to
    ///     <paramref name="fileName" />.
    /// </summary>
    member _.CheckScript(source: string, fileName: string) : ScriptCheckResult =
        let (parseResults, checkResults, projectResults) =
            session.ParseAndCheckInteraction(WorkflowText.orEmpty source)

        (match box projectResults with
         | :? IDisposable as disposable -> disposable.Dispose()
         | _ -> ())

        let diagnostics =
            ScriptDiagnostics.distinct (
                Array.append (ScriptDiagnostics.ofCompilerDiagnostics fileName parseResults.Diagnostics)
                    (ScriptDiagnostics.ofCompilerDiagnostics fileName checkResults.Diagnostics))

        { Succeeded = not (ScriptDiagnostics.hasErrors diagnostics)
          Diagnostics = diagnostics }

    /// <summary>Evaluates one script interaction (declarations and top-level bindings).</summary>
    member _.EvaluateScript(source: string, fileName: string) : ScriptEvaluationResult =
        let (outcome, diagnostics) =
            session.EvalInteractionNonThrowing(WorkflowText.orEmpty source, fileName)

        ScriptHostSession.Render(outcome, diagnostics, fileName)

    /// <summary>Evaluates one expression against the state the session has built so far.</summary>
    member _.EvaluateExpression(expression: string, fileName: string) : ScriptEvaluationResult =
        let (outcome, diagnostics) = session.EvalExpressionNonThrowing(WorkflowText.orEmpty expression, fileName)
        ScriptHostSession.Render(outcome, diagnostics, fileName)

    /// <summary>Interrupts an evaluation that is still running (cancellation of a live run).</summary>
    member _.Interrupt() : unit = session.Interrupt()

    /// <summary>The text a script printed to standard output (diagnostics only; never a result).</summary>
    member _.Output: string = output.ToString()

    /// <summary>The text a script wrote to standard error.</summary>
    member _.Failure: string = failure.ToString()

    /// Writers used by the host to capture a REPL interaction's Console output.
    member _.OutputWriter: TextWriter = output
    member _.FailureWriter: TextWriter = failure

    /// Clears captured output between REPL interactions without discarding bindings.
    member _.ClearOutput() : unit =
        output.GetStringBuilder().Clear() |> ignore
        failure.GetStringBuilder().Clear() |> ignore

    static member private Render(outcome: Choice<FsiValue option, exn>, diagnostics: FSharpDiagnostic[],
                                 fileName: string) : ScriptEvaluationResult =
        let converted = ScriptDiagnostics.ofCompilerDiagnostics fileName diagnostics
        match outcome with
        | Choice1Of2 value ->
            let reflected =
                match value with
                | Some produced -> produced.ReflectionValue
                | None -> null

            { Succeeded = not (ScriptDiagnostics.hasErrors converted)
              Value = reflected
              Diagnostics = converted
              Failure = String.Empty }
        | Choice2Of2 error ->
            { Succeeded = false
              Value = null
              Diagnostics = converted
              Failure = error.Message }

    interface IDisposable with
        member _.Dispose() =
            (session :> IDisposable).Dispose()
            output.Dispose()
            failure.Dispose()

/// <summary>
///     Compile-check service for workflow scripts. It is the "check on save" of the settings page
///     (ADR 0036 trust model) and the pre-flight check of the executor: a script that does not
///     compile is reported as structured diagnostics instead of a run.
/// </summary>
[<Sealed>]
type ScriptCompiler() =
    /// <summary>
    ///     Compile-checks script text without executing it. The check runs on a worker thread and
    ///     resolves the same reference set the executor uses, so a passing check means the same
    ///     script type-checks in a run.
    /// </summary>
    member _.CheckScriptAsync(source: string, fileName: string) : Task<ScriptCheckResult> =
        Task.Run(fun () ->
            use host = ScriptHostSession.Create()
            host.CheckScript(source, fileName))

    /// Check the root contract without executing the script or any author-supplied functions.
    member _.CheckWorkflowAsync(source: string, fileName: string, entryPoint: string) : Task<ScriptCheckResult> =
        Task.Run(fun () ->
            use host = ScriptHostSession.Create()
            let contract = "\nlet __patchouli_workflow_contract : Patchouli.Workflows.Scripting.AgentWorkflow = " + entryPoint
            host.CheckScript(source + contract, fileName))
