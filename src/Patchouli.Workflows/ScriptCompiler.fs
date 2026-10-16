namespace Patchouli.Workflows

open System
open System.IO
open System.Threading.Tasks
open System.Globalization
open System.Text.Json
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
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

/// Result of statically analyzing a workflow declaration. User code is never evaluated.
[<CLIMutable>]
type WorkflowDeclarationAnalysis =
    { Succeeded: bool
      Info: WorkflowDeclarationInfo
      Fields: ParameterDescriptor array
      SessionModelKey: string
      Diagnostics: ScriptDiagnostic array }

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

/// Static declaration grammar. Expressions are read from FCS-checked source and only SDK calls/literals are accepted.
[<RequireQualifiedAccess>]
module WorkflowDeclarationAnalysisParser =
    type private Call = { Name: string; Head: objnull; Arguments: objnull list }
    type private Literal = | Text of string | Number of decimal | Flag of bool | Name of string | Items of Literal list

    let private checker = FSharpChecker.Create()

    let private property (value: objnull) (name: string) : objnull =
        if isNull value then null
        else
            let reflected = value.GetType().GetProperty(name, Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance) |> box
            match reflected with
            | :? Reflection.PropertyInfo as prop -> prop.GetValue(value)
            | _ -> null

    let private union (value: objnull) : string * objnull[] =
        let flags = Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
        if isNull value || not (FSharp.Reflection.FSharpType.IsUnion(value.GetType(), flags)) then "", [||]
        else
            let case, values = FSharp.Reflection.FSharpValue.GetUnionFields(value, value.GetType(), flags)
            case.Name, values

    let private sequence (value: objnull) : objnull list =
        match value with
        | :? System.Collections.IEnumerable as values when not (value :? string) -> values |> Seq.cast<objnull> |> Seq.toList
        | _ -> []

    let rec private identifierText (value: objnull) =
        match union value with
        | "Ident", fields when fields.Length > 0 ->
            match property fields[0] "idText" with :? string as text -> text | _ -> ""
        | _ ->
            match property value "idText" with
            | :? string as text -> text
            | _ ->
                let ident = property value "ident"
                if isNull ident || Object.ReferenceEquals(ident, value) then ""
                else identifierText ident

    let private canonicalName (name: string) =
        let prefix = "Patchouli.Workflows.Scripting."
        if name.StartsWith(prefix, StringComparison.Ordinal) then name.Substring(prefix.Length) else name

    let private callName (head: objnull) =
        match union head with
        | "Ident", fields when fields.Length > 0 -> identifierText fields[0]
        | "LongIdent", _ ->
            let longId = property head "longDotId"
            let identifiers =
                let primary = property longId "LongIdent"
                if not (isNull primary) then sequence primary
                else property longId "id" |> sequence
            identifiers |> List.map identifierText |> String.concat "."
        | _ -> ""

    let rec private sequenceExpressions (expression: objnull) =
        match union expression with
        | "Sequential", fields ->
            fields
            |> Array.choose (fun field ->
                if field :? FSharp.Compiler.Syntax.SynExpr then Some field else None)
            |> Array.collect sequenceExpressions
        | _ -> [| expression |]

    let rec private flattenCall (expression: objnull) : Call option =
        let caseName, fields = union expression
        if caseName = "Paren" || caseName = "Typed" then
            let inner = property expression "expr"
            flattenCall inner
        elif caseName = "App" then
            let func = property expression "funcExpr"
            let arg = property expression "argExpr"
            match flattenCall func with
            | Some { Name = "op_PipeRight"; Arguments = [ left ] } ->
                match flattenCall arg with
                | Some call -> Some { call with Arguments = call.Arguments @ [ left ] }
                | None -> None
            | Some call -> Some { call with Arguments = call.Arguments @ [ arg ] }
            | None -> None
        else
            let name = callName expression |> canonicalName
            if String.IsNullOrEmpty name then None else Some { Name = name; Head = expression; Arguments = [] }

    let rec private staticValue (expression: objnull) : Literal option =
        let caseName, fields = union expression
        match caseName with
        | "Paren" | "Typed" -> staticValue (property expression "expr")
        | "Ident" -> Some(Name(identifierText fields[0]))
        | "LongIdent" -> Some(Name(callName expression))
        | "ArrayOrList" ->
            property expression "exprs"
            |> sequence
            |> List.map staticValue
            |> List.fold (fun state item -> state |> Option.bind (fun values -> item |> Option.map (fun value -> value :: values))) (Some [])
            |> Option.map (List.rev >> Items)
        | "ArrayOrListComputed" when fields.Length > 1 ->
            let isArray = match fields[0] with :? bool as value -> value | _ -> true
            if isArray then None
            else
                sequenceExpressions fields[1]
                |> Array.toList
                |> List.map staticValue
                |> List.fold (fun state item -> state |> Option.bind (fun values -> item |> Option.map (fun value -> value :: values))) (Some [])
                |> Option.map (List.rev >> Items)
        | "Const" ->
            let constant = property expression "constant"
            let constCase, constFields = union constant
            match constCase with
            | "String" when constFields.Length > 0 ->
                match constFields[0] with
                | :? string as value -> Some(Text value)
                | _ -> None
            | "Bool" when constFields.Length > 0 -> Some(Flag(constFields[0] :?> bool))
            | ("Int32" | "Int64" | "UInt32" | "UInt64" | "Decimal" | "Double" | "Single") when constFields.Length > 0 ->
                try Some(Number(Convert.ToDecimal(constFields[0], CultureInfo.InvariantCulture))) with _ -> None
            | _ -> None
        | _ -> None

    let private getRange (value: objnull) =
        let range = property value "Range"
        if isNull range then property value "idRange" else range
    let private intProperty (value: objnull) name =
        match property value name with :? int as number -> number | _ -> 0
    let private rangeStart (value: objnull) =
        let range = getRange value
        let start = property range "Start"
        intProperty start "Line" - 1, intProperty start "Column"
    let private rangeEnd (value: objnull) =
        let range = getRange value
        let finish = property range "End"
        intProperty finish "Line" - 1, intProperty finish "Column"

    let private issue fileName line column code message =
        let sourceLine = max 0 (line - 2)
        { FileName = fileName; Line = max 1 (sourceLine + 1); Column = max 0 column; EndLine = max 1 (sourceLine + 1); EndColumn = max 0 column + 1
          Severity = ScriptDiagnosticSeverity.Error; ErrorNumber = code; Message = message }

    let private symbolAt (checkResults: FSharpCheckFileResults) (lines: string[]) (prefixLines: int) (node: objnull) =
        let lineIndex, _ = rangeStart node
        let _, endColumn = rangeEnd node
        if lineIndex < 0 || lineIndex >= lines.Length then None
        else
            let name = callName node
            let names = name.Split('.') |> Array.toList
            checkResults.GetSymbolUseAtLocation(lineIndex + prefixLines + 1, endColumn, lines[lineIndex], names)

    let private symbolIs checkResults lines prefixLines node expected =
        match symbolAt checkResults lines prefixLines node with
        | Some useAt ->
            let assembly = property (box useAt.Symbol) "Assembly"
            let simpleName = property assembly "SimpleName"
            let isSdkAssembly =
                match simpleName with
                | :? string as name -> String.Equals(name, typeof<AgentWorkflow>.Assembly.GetName().Name, StringComparison.Ordinal)
                | _ -> false
            String.Equals(useAt.Symbol.FullName, expected, StringComparison.Ordinal) && isSdkAssembly
        | None -> false

    let private isSdkParameterType (symbol: objnull) =
        match symbol with
        | :? FSharpMemberOrFunctionOrValue as memberValue ->
            let valueType = memberValue.FullType.StripAbbreviations()
            if not valueType.HasTypeDefinition then false
            else
                let definition = valueType.TypeDefinition
                let expectedAssembly = typeof<AgentWorkflow>.Assembly.GetName().Name
                // Check assembly identity before asking for the entity's qualified name. FCS can throw
                // for FullName on framework entities such as System.String; TryFullName is safe here.
                String.Equals(definition.Assembly.SimpleName, expectedAssembly, StringComparison.Ordinal) &&
                definition.TryFullName = Some "Patchouli.Workflows.Scripting.Parameter`1"
        | _ -> false

    let private bindingName (binding: objnull) =
        let pat = property binding "headPat"
        let rec findNamedPattern (pattern: objnull) =
            let caseName, fields = union pattern
            if caseName = "Named" && fields.Length > 0 then
                let ident = property fields[0] "ident"
                match property ident "idText" with
                | :? string as name -> Some(name, ident)
                | _ -> None
            elif caseName = "Typed" || caseName = "Paren" || caseName = "Attrib" then
                fields |> Array.tryPick findNamedPattern
            else None
        findNamedPattern pat

    let private parseSelectionScope (expression: objnull) =
        match union expression with
        | "LongIdent", _ ->
            let segments = callName expression |> fun name -> name.Split('.')
            if segments.Length < 2 || segments[segments.Length - 2] <> "WorkflowSelectionScope" then None
            else
                match Enum.TryParse<Patchouli.Workflows.WorkflowSelectionScope>(segments[segments.Length - 1], false) with
                | true, scope when Enum.IsDefined(typeof<Patchouli.Workflows.WorkflowSelectionScope>, scope) -> Some scope
                | _ -> None
        | _ -> None

    let private topLevelBindings (tree: objnull) =
        let bindings = ResizeArray<objnull>()
        let rec visitDecl (decl: objnull) =
            let caseName, fields = union decl
            if caseName = "Let" && fields.Length > 1 then
                for binding in sequence fields[1] do bindings.Add binding
            elif caseName = "NestedModule" || caseName = "NamespaceFragment" then
                for field in fields do
                    if isNull field then ()
                    elif field.GetType().Name = "SynModuleOrNamespace" then visitModule field
                    else
                        for nested in sequence field do
                            if not (isNull nested) && nested.GetType().Name = "SynModuleDecl" then visitDecl nested
                            elif not (isNull nested) && nested.GetType().Name = "SynModuleOrNamespace" then visitModule nested
        and visitModule (moduleNode: objnull) =
            property moduleNode "decls" |> sequence |> List.iter visitDecl
        let caseName, fields = union tree
        if caseName = "ImplFile" && fields.Length > 0 then
            let impl = fields[0]
            property impl "contents" |> sequence |> List.iter visitModule
        bindings |> Seq.toList

    let private literalText = function Text value -> Some value | _ -> None
    let private literalNumber = function Number value -> Some value | _ -> None
    let private literalBool = function Flag value -> Some value | _ -> None

    let private staticArgs (call: Call) = call.Arguments |> List.map staticValue

    let private declaredEntryPoint (bindings: objnull list) (fallback: string) =
        let infoBinding = bindings |> List.tryFind (fun binding -> bindingName binding |> Option.exists (fun (name, _) -> name = "info"))
        let rec readEntryPoint expression =
            match flattenCall expression with
            | Some call ->
                match call.Name, staticArgs call, call.Arguments with
                | "WorkflowInfo.create", _, _ -> Some "run"
                | "WorkflowInfo.entryPoint", Some(Text name) :: _, _ -> Some name
                | "WorkflowInfo.selectionScope", _ :: _, _ :: info :: _ -> readEntryPoint info
                | "WorkflowInfo.menu", _ :: _ :: _, _ :: _ :: info :: _ -> readEntryPoint info
                | _ -> None
            | None -> None
        infoBinding
        |> Option.bind (fun binding -> readEntryPoint (property binding "expr"))
        |> Option.defaultValue fallback

    let private analyzeParameter call sourceLine sourceColumn =
        match call.Name, staticArgs call with
        | "Parameter.text", [ Some(Text key); Some(Text label); Some(Text value) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.Text; Required = false; Description = ""; HasDefault = true; DefaultValue = value; Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.multilineText", [ Some(Text key); Some(Text label); Some(Text value) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.MultilineText; Required = false; Description = ""; HasDefault = true; DefaultValue = value; Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.language", [ Some(Text key); Some(Text label); Some(Text value) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.Language; Required = false; Description = ""; HasDefault = true; DefaultValue = value; Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.pageRange", [ Some(Text key); Some(Text label); Some(Text value) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.PageRange; Required = false; Description = ""; HasDefault = true; DefaultValue = value; Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.textSelection", [ Some(Text key); Some(Text label) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.TextSelection; Required = true; Description = ""; HasDefault = false; DefaultValue = ""; Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.integer", [ Some(Text key); Some(Text label); Some(Number value) ] when value >= decimal Int32.MinValue && value <= decimal Int32.MaxValue ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.Integer; Required = false; Description = ""; HasDefault = true; DefaultValue = Decimal.ToInt32(value).ToString(CultureInfo.InvariantCulture); Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.decimal", [ Some(Text key); Some(Text label); Some(Number value) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.Decimal; Required = false; Description = ""; HasDefault = true; DefaultValue = value.ToString(CultureInfo.InvariantCulture); Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.boolean", [ Some(Text key); Some(Text label); Some(Flag value) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.Boolean; Required = false; Description = ""; HasDefault = true; DefaultValue = value.ToString().ToLowerInvariant(); Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.model", [ Some(Text key); Some(Text label) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.Model; Required = true; Description = ""; HasDefault = false; DefaultValue = ""; Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.documents", [ Some(Text key); Some(Text label) ] ->
            Some { Key = key; Label = label; Type = WorkflowParameterValueType.Documents; Required = false; Description = ""; HasDefault = true; DefaultValue = "[]"; Minimum = Nullable(); Maximum = Nullable(); Choices = [||]; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | "Parameter.choice", [ Some(Text key); Some(Text label); Some(Items choices); Some(Text value) ] ->
            let options = choices |> List.choose literalText |> List.toArray
            if options.Length <> choices.Length || not (Array.contains value options) then None
            else Some { Key = key; Label = label; Type = WorkflowParameterValueType.Choice; Required = false; Description = ""; HasDefault = true; DefaultValue = value; Minimum = Nullable(); Maximum = Nullable(); Choices = options; ContextBinding = ""; SourceLine = sourceLine + 1; SourceColumn = sourceColumn }
        | _ -> None

    let private analyzeParameterExpression (checkResults: FSharpCheckFileResults) (lines: string[]) prefixLines (fileName: string)
                                          (diagnostics: ResizeArray<ScriptDiagnostic>) (expression: objnull) sourceLine sourceColumn =
        let rec analyze (expression: objnull) =
            match flattenCall expression with
            | None -> None
            | Some call when call.Name.StartsWith("Parameter.", StringComparison.Ordinal) ->
                let line, column = rangeStart call.Head
                if not (symbolIs checkResults lines prefixLines call.Head ("Patchouli.Workflows.Scripting." + call.Name)) then
                    None
                else
                    if [ "Parameter.text"; "Parameter.multilineText"; "Parameter.language"; "Parameter.pageRange"
                         "Parameter.textSelection"; "Parameter.integer"; "Parameter.decimal"; "Parameter.boolean"
                         "Parameter.model"; "Parameter.documents"; "Parameter.choice" ] |> List.contains call.Name then
                        analyzeParameter call sourceLine sourceColumn
                    elif [ "Parameter.required"; "Parameter.describe"; "Parameter.intRange"; "Parameter.decimalRange"; "Parameter.bindContext" ] |> List.contains call.Name
                         && call.Arguments.Length >= 2 then
                        match analyze (List.last call.Arguments) with
                        | None -> None
                        | Some field ->
                            let updated =
                                match call.Name, staticArgs call with
                                | "Parameter.required", [ Some(Text value); _ ] ->
                                    Some { field with Required = true; Description = value }
                                | "Parameter.describe", [ Some(Text value); _ ] ->
                                    Some { field with Description = value }
                                | "Parameter.intRange", [ Some(Number minimum); Some(Number maximum); _ ] ->
                                    Some { field with Minimum = Nullable minimum; Maximum = Nullable maximum }
                                | "Parameter.decimalRange", [ Some(Number minimum); Some(Number maximum); _ ] ->
                                    Some { field with Minimum = Nullable minimum; Maximum = Nullable maximum }
                                | "Parameter.bindContext", [ Some(Text binding); _ ]
                                    when [ "documents"; "documentId"; "pageRange"; "textSelection" ] |> List.contains binding ->
                                    Some { field with ContextBinding = binding }
                                | _ -> None
                            match updated with
                            | Some value -> Some value
                            | None ->
                                diagnostics.Add(issue fileName line column "WORKFLOW_DECLARATION_STATIC"
                                    "Parameter modifiers require supported literal values and a supported context binding.")
                                None
                    else
                        diagnostics.Add(issue fileName line column "WORKFLOW_DECLARATION_STATIC" "Parameter modifiers accept only supported SDK calls and literal values.")
                        None
            | _ -> None
        analyze expression

    let analyze (source: string) (fileName: string) (entryPoint: string) : WorkflowDeclarationAnalysis =
        let source = WorkflowText.orEmpty source
        let scriptAssembly = typeof<AgentWorkflow>.Assembly.Location
        let agentAssembly = typeof<Patchouli.Agent.Effect>.Assembly.Location
        let prefix = "#r @\"" + scriptAssembly.Replace("\"", "\"\"") + "\"\n#r @\"" + agentAssembly.Replace("\"", "\"\"") + "\"\n"
        let sourceOnly = prefix + source
        let sourceText = SourceText.ofString sourceOnly
        let sourceOptions, _ = checker.GetProjectOptionsFromScript(fileName, sourceText, assumeDotNetFramework = false) |> Async.RunSynchronously
        let sourceParseResults, _ = checker.ParseAndCheckFileInProject(fileName, 0, sourceText, sourceOptions) |> Async.RunSynchronously
        let declared = declaredEntryPoint (topLevelBindings (box sourceParseResults.ParseTree)) entryPoint
        let checkedSource = sourceOnly + "\nlet __patchouli_workflow_contract : Patchouli.Workflows.Scripting.AgentWorkflow = " + declared
        let checkedLines = checkedSource.Replace("\r\n", "\n").Split('\n')
        let sourceText = SourceText.ofString checkedSource
        let options, optionDiagnostics = checker.GetProjectOptionsFromScript(fileName, sourceText, assumeDotNetFramework = false) |> Async.RunSynchronously
        let parseResults, checkAnswer = checker.ParseAndCheckFileInProject(fileName, 0, sourceText, options) |> Async.RunSynchronously
        let typedResults = match checkAnswer with FSharpCheckFileAnswer.Succeeded results -> Some results | _ -> None
        let compileDiagnostics =
            List.concat [ optionDiagnostics; Array.toList parseResults.Diagnostics
                          typedResults |> Option.map (fun results -> Array.toList results.Diagnostics) |> Option.defaultValue [] ]
            |> List.map (fun diagnostic -> ScriptDiagnostics.ofCompilerDiagnostic fileName diagnostic)
            |> List.map (fun diagnostic ->
                if diagnostic.Line > 2 then { diagnostic with Line = diagnostic.Line - 2; EndLine = max 1 (diagnostic.EndLine - 2) }
                else diagnostic)
            |> List.toArray
            |> ScriptDiagnostics.distinct
        let diagnostics = ResizeArray<ScriptDiagnostic>(compileDiagnostics)
        let tree = parseResults.ParseTree
        let bindings = topLevelBindings (box tree)
        let parameterBindings = Collections.Generic.Dictionary<obj, ParameterDescriptor>(HashIdentity.Structural)
        let fields = ResizeArray<ParameterDescriptor>()
        let fieldKeys = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let getBoundSymbol (ident: objnull) =
            let line, _ = rangeStart ident
            let _, col = rangeEnd ident
            if line < 0 || line >= checkedLines.Length then None
            else
                let text = identifierText ident
                typedResults |> Option.bind (fun result -> result.GetSymbolUseAtLocation(line + 1, col, checkedLines[line], text.Split('.') |> Array.toList))
        for binding in bindings do
            match bindingName binding with
            | None -> ()
            | Some(variableName, ident) ->
                let expression = property binding "expr"
                let line, column = rangeStart expression
                let symbol = getBoundSymbol ident
                match symbol, typedResults with
                | Some bindingSymbol, Some checkResults when not (isNull expression) ->
                    match analyzeParameterExpression checkResults checkedLines 0 fileName diagnostics expression (line - 2) column with
                    | Some field ->
                        if fieldKeys.Add field.Key then
                            fields.Add field
                            parameterBindings[bindingSymbol.Symbol] <- field
                        else diagnostics.Add(issue fileName line column "WORKFLOW_PARAMETER_DUPLICATE" ("Duplicate parameter key '" + field.Key + "'."))
                    | None ->
                        match flattenCall expression with
                        | Some call when call.Name.StartsWith("Parameter.", StringComparison.Ordinal) ->
                            if symbolIs checkResults checkedLines 0 call.Head ("Patchouli.Workflows.Scripting." + call.Name) then
                                diagnostics.Add(issue fileName line column "WORKFLOW_DECLARATION_STATIC" "Parameter declarations accept only supported SDK calls and literal values.")
                            else diagnostics.Add(issue fileName line column "WORKFLOW_DECLARATION_SYMBOL" "Parameter declarations must resolve to a Patchouli SDK function.")
                        | _ ->
                            match staticValue expression with
                            | Some(Name alias) ->
                                let _, symbolColumn = rangeEnd expression
                                match checkResults.GetSymbolUseAtLocation(line + 1, symbolColumn, checkedLines[line], alias.Split('.') |> Array.toList) with
                                | Some aliasUse when parameterBindings.ContainsKey aliasUse.Symbol ->
                                    parameterBindings[bindingSymbol.Symbol] <- parameterBindings[aliasUse.Symbol]
                                | _ when isSdkParameterType bindingSymbol.Symbol ->
                                    diagnostics.Add(issue fileName line column "WORKFLOW_DECLARATION_STATIC"
                                        "Top-level Parameter values must be a supported static SDK declaration or an alias of one.")
                                | _ -> ()
                            | _ when isSdkParameterType bindingSymbol.Symbol ->
                                diagnostics.Add(issue fileName line column "WORKFLOW_DECLARATION_STATIC"
                                    "Top-level Parameter values must be a supported static SDK declaration or an alias of one.")
                            | _ -> ()
                | Some bindingSymbol, _ ->
                    ()
                | _ -> ()
                ignore variableName
        let mutable info = { Name = ""; Description = ""; EntryPoint = entryPoint; SelectionScope = Patchouli.Workflows.WorkflowSelectionScope.Nothing; MenuPath = ""; MenuOrder = 0; ShowInMenu = false }
        match bindings |> List.tryFind (fun binding -> bindingName binding |> Option.exists (fun (name, _) -> name = "info")) with
        | Some binding ->
            match flattenCall (property binding "expr"), typedResults with
            | Some call, Some checkResults when call.Name.StartsWith("WorkflowInfo.", StringComparison.Ordinal) ->
                let rec readInfo call =
                    if not (symbolIs checkResults checkedLines 0 call.Head ("Patchouli.Workflows.Scripting." + call.Name)) then None
                    else
                        match call.Name, staticArgs call with
                        | "WorkflowInfo.create", [ Some(Text name); Some(Text description) ] ->
                            Some { Name = name; Description = description; EntryPoint = "run"; SelectionScope = Patchouli.Workflows.WorkflowSelectionScope.Nothing; MenuPath = ""; MenuOrder = 0; ShowInMenu = false }
                        | "WorkflowInfo.selectionScope", _ :: _ ->
                            match parseSelectionScope call.Arguments[0] with
                            | Some scope ->
                                readInfoFromArgument call.Arguments[1]
                                |> Option.map (fun current -> { current with SelectionScope = scope })
                            | None -> None
                        | "WorkflowInfo.menu", Some(Text path) :: Some(Number order) :: _
                            when order >= 0M && order <= decimal Int32.MaxValue && order = Decimal.Truncate order ->
                            readInfoFromArgument call.Arguments[2]
                            |> Option.map (fun current -> { current with MenuPath = path; MenuOrder = Decimal.ToInt32 order; ShowInMenu = not (String.IsNullOrWhiteSpace path) })
                        | "WorkflowInfo.entryPoint", Some(Text name) :: _ ->
                            readInfoFromArgument call.Arguments[1] |> Option.map (fun current -> { current with EntryPoint = name })
                        | _ -> None
                and readInfoFromArgument argument =
                    match flattenCall argument with Some nested when nested.Name.StartsWith("WorkflowInfo.", StringComparison.Ordinal) -> readInfo nested | _ -> None
                match readInfo call with
                | Some value -> info <- value
                | None ->
                    let line, column = rangeStart call.Head
                    diagnostics.Add(issue fileName line column "WORKFLOW_INFO_STATIC" "WorkflowInfo accepts only supported SDK calls with literal values.")
            | _ -> diagnostics.Add(issue fileName 1 0 "WORKFLOW_INFO_MISSING" "Declare workflow metadata in a top-level info binding using WorkflowInfo SDK functions.")
        | None -> diagnostics.Add(issue fileName 1 0 "WORKFLOW_INFO_MISSING" "Declare workflow metadata in a top-level info binding using WorkflowInfo SDK functions.")
        let entryBinding = bindings |> List.tryFind (fun binding -> bindingName binding |> Option.exists (fun (name, _) -> name = info.EntryPoint))
        let modelKey =
            match entryBinding, typedResults with
            | Some binding, Some checkResults ->
                let rec findModel expr =
                    match flattenCall expr with
                    | Some call when call.Name = "Workflow.withModel" ->
                        let line, _ = rangeStart call.Head
                        if not (symbolIs checkResults checkedLines 0 call.Head "Patchouli.Workflows.Scripting.Workflow.withModel") then
                            diagnostics.Add(issue fileName line 0 "WORKFLOW_MODEL_BINDING" "Workflow.withModel must resolve to the Patchouli SDK function.")
                            ""
                        elif call.Arguments.Length < 2 then ""
                        else
                            match staticValue call.Arguments[0] with
                            | Some(Name variable) ->
                                let row, _ = rangeStart call.Arguments[0]
                                let _, col = rangeEnd call.Arguments[0]
                                let token = checkedLines[row]
                                match checkResults.GetSymbolUseAtLocation(row + 1, col, token, variable.Split('.') |> Array.toList) with
                                | Some useAt when parameterBindings.ContainsKey useAt.Symbol ->
                                    let field = parameterBindings[useAt.Symbol]
                                    if field.Type = WorkflowParameterValueType.Model then field.Key
                                    else diagnostics.Add(issue fileName row col "WORKFLOW_MODEL_PARAMETER" "Workflow.withModel must reference a Parameter.model declaration."); ""
                                | _ -> diagnostics.Add(issue fileName row col "WORKFLOW_MODEL_PARAMETER" "Workflow.withModel must reference a top-level Parameter.model declaration."); ""
                            | _ -> diagnostics.Add(issue fileName line 0 "WORKFLOW_MODEL_PARAMETER" "Workflow.withModel requires a static parameter reference."); ""
                    | Some call -> call.Arguments |> List.map findModel |> List.tryFind (String.IsNullOrWhiteSpace >> not) |> Option.defaultValue ""
                    | None -> ""
                findModel (property binding "expr")
            | _ -> ""
        let allDiagnostics = ScriptDiagnostics.distinct (diagnostics.ToArray())
        { Succeeded = not (ScriptDiagnostics.hasErrors allDiagnostics)
          Info = info
          Fields = fields.ToArray()
          SessionModelKey = modelKey
          Diagnostics = allDiagnostics }

/// Shared value resolution and validation used by desktop, MCP and direct workflow callers.
[<AbstractClass; Sealed>]
type WorkflowParameterResolver private () =
    static member Validate(analysis: WorkflowDeclarationAnalysis, values: Collections.Generic.IReadOnlyDictionary<string, string>) : WorkflowParameterIssue[] =
        let result = ResizeArray<WorkflowParameterIssue>()
        let get key =
            if Object.ReferenceEquals(values, null) then None
            else
                match values.TryGetValue key with
                | true, value -> Some value
                | _ -> None
        for field in analysis.Fields do
            let found = get field.Key
            let value = found |> Option.defaultValue ""
            let add code message =
                result.Add({ Key = field.Key; Code = code; Message = message; Line = field.SourceLine; Column = field.SourceColumn })
            if field.Required && String.IsNullOrWhiteSpace value then
                add "WORKFLOW_PARAMETER_REQUIRED" (field.Label + " is required.")
            elif found.IsSome then
                match field.Type with
                | WorkflowParameterValueType.Integer ->
                    match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, number ->
                        let decimalNumber = Convert.ToDecimal number
                        if field.Minimum.HasValue && decimalNumber < field.Minimum.Value then add "WORKFLOW_PARAMETER_RANGE" (field.Label + " is below its minimum.")
                        if field.Maximum.HasValue && decimalNumber > field.Maximum.Value then add "WORKFLOW_PARAMETER_RANGE" (field.Label + " is above its maximum.")
                    | _ -> add "WORKFLOW_PARAMETER_TYPE" (field.Label + " must be an integer.")
                | WorkflowParameterValueType.Decimal ->
                    match Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture) with
                    | true, number ->
                        if field.Minimum.HasValue && number < field.Minimum.Value then add "WORKFLOW_PARAMETER_RANGE" (field.Label + " is below its minimum.")
                        if field.Maximum.HasValue && number > field.Maximum.Value then add "WORKFLOW_PARAMETER_RANGE" (field.Label + " is above its maximum.")
                    | _ -> add "WORKFLOW_PARAMETER_TYPE" (field.Label + " must be a number.")
                | WorkflowParameterValueType.Boolean ->
                    match Boolean.TryParse value with
                    | true, _ -> ()
                    | _ -> add "WORKFLOW_PARAMETER_TYPE" (field.Label + " must be true or false.")
                | WorkflowParameterValueType.Choice when not (Array.contains value field.Choices) ->
                    add "WORKFLOW_PARAMETER_CHOICE" (field.Label + " is not one of the available choices.")
                | WorkflowParameterValueType.Model ->
                    try ModelSelectionCodec.decode value |> ignore
                    with _ -> add "WORKFLOW_MODEL_UNAVAILABLE" (field.Label + " must select a provider and model.")
                | WorkflowParameterValueType.Documents ->
                    try
                        let documentsObject = JsonSerializer.Deserialize<string array>(value) |> box
                        if isNull documentsObject then
                            add "invalid_documents" (field.Label + " must be a document list, not null.")
                        else
                            let documents = unbox<string array> documentsObject
                            if documents |> Array.exists (fun (document: string) -> String.IsNullOrWhiteSpace document) then
                                add "invalid_documents" (field.Label + " must contain only non-empty document identifiers.")
                            elif field.Required && documents.Length = 0 then
                                add "WORKFLOW_PARAMETER_REQUIRED" (field.Label + " is required.")
                    with _ -> add "invalid_documents" (field.Label + " must be a valid document list.")
                | _ -> ()
        result.ToArray()

    static member ResolveParameters(analysis: WorkflowDeclarationAnalysis,
                                    explicitValues: Collections.Generic.IReadOnlyDictionary<string, string>,
                                    savedValues: Collections.Generic.IReadOnlyDictionary<string, string>,
                                    contextValues: Collections.Generic.IReadOnlyDictionary<string, string>) : ParameterResolutionResult =
        let values = Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let tryValue (source: Collections.Generic.IReadOnlyDictionary<string, string>) key =
            if Object.ReferenceEquals(source, null) then None
            else
                match source.TryGetValue key with
                | true, value -> Some value
                | _ -> None
        for field in analysis.Fields do
            let selected =
                match tryValue explicitValues field.Key with
                | Some value -> Some value
                | None when not (String.IsNullOrWhiteSpace field.ContextBinding) -> tryValue contextValues field.ContextBinding
                | None -> None
            let selected =
                match selected with
                | Some value -> Some value
                | None -> tryValue savedValues field.Key
            let selected =
                match selected with
                | Some value -> Some value
                | None when field.HasDefault -> Some field.DefaultValue
                | None -> None
            match selected with
            | Some value -> values[field.Key] <- value
            | None -> ()
        let valuesView = values :> Collections.Generic.IReadOnlyDictionary<string, string>
        let issues = WorkflowParameterResolver.Validate(analysis, valuesView)
        { Succeeded = issues.Length = 0 && analysis.Succeeded
          Values = valuesView
          Issues = issues }

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

    /// Extracts only SDK declaration expressions from a checked syntax tree; this method never evaluates the script.
    member _.AnalyzeWorkflowAsync(source: string, fileName: string, entryPoint: string) : Task<WorkflowDeclarationAnalysis> =
        Task.Run(fun () -> WorkflowDeclarationAnalysisParser.analyze source fileName entryPoint)
