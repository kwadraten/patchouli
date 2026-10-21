namespace Patchouli.Extensions

open System
open System.Globalization
open System.IO
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open Patchouli.Core.Ids
open Patchouli.Extensions.Contracts

/// Source-only validation boundary used by the manager and deterministic lifecycle tests.
type IExtensionSourceChecker =
    abstract CheckAsync: ExtensionPackageSnapshot * CancellationToken -> Task<ExtensionCheckResult>

/// FCS-based source checking and declaration inspection. It parses and typechecks source; it never evaluates it.
[<NoEquality; NoComparison>]
type private ScriptCall = { Name: string; Args: objnull array }

[<Sealed>]
type private TemporaryCheckDirectory(path: string) =
    do Directory.CreateDirectory(path) |> ignore
    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists path then Directory.Delete(path, true)

[<Sealed>]
type ExtensionCompiler(checkRoot: string) =
    let checker = FSharpChecker.Create()
    let contractAssembly = typeof<ITranslationExtension>.Assembly.Location
    let coreAssembly = typeof<DocumentInstanceId>.Assembly.Location
    let sdkAssembly = typeof<ExtensionCompiler>.Assembly.Location
    let resolvedCheckRoot = Path.GetFullPath checkRoot
    let createCheckParentDirectory (path: string) =
        match Path.GetDirectoryName path with
        | null -> invalidArg "path" "The check file must have a parent directory."
        | parent -> Directory.CreateDirectory(parent) |> ignore

    let diagnostic checkRoot fileName lineOffset (diagnostic: FSharpDiagnostic) =
        let severity =
            if diagnostic.ErrorNumberText = "FS0025" || diagnostic.ErrorNumberText = "FS3261" then ExtensionDiagnosticSeverity.Error
            else
                match diagnostic.Severity with
                | FSharpDiagnosticSeverity.Error -> ExtensionDiagnosticSeverity.Error
                | FSharpDiagnosticSeverity.Warning -> ExtensionDiagnosticSeverity.Warning
                | _ -> ExtensionDiagnosticSeverity.Information
        let reportedFile, isCurrentSource =
            if String.IsNullOrWhiteSpace diagnostic.FileName then fileName, false
            else
                try
                    let fullPath =
                        if Path.IsPathRooted diagnostic.FileName then Path.GetFullPath diagnostic.FileName
                        else Path.GetFullPath(Path.Combine(checkRoot, diagnostic.FileName))
                    let relative = Path.GetRelativePath(checkRoot, fullPath).Replace('\\', '/')
                    if not (Path.IsPathRooted relative)
                       && relative <> ".."
                       && not (relative.StartsWith("../", StringComparison.Ordinal)) then
                        relative, String.Equals(relative, fileName, StringComparison.OrdinalIgnoreCase)
                    else
                        match Path.GetFileName diagnostic.FileName with
                        | null -> fileName, false
                        | fallback -> fallback, false
                with :? ArgumentException -> fileName, false
        let offset = if isCurrentSource then lineOffset else 0
        ExtensionDiagnostic(severity, diagnostic.ErrorNumberText, diagnostic.Message,
                            ExtensionSourceLocation(reportedFile, max 1 (diagnostic.StartLine - offset), diagnostic.StartColumn,
                                max 1 (diagnostic.EndLine - offset), diagnostic.EndColumn))

    let union (value: objnull) : string * objnull array =
        if isNull value || not (FSharpType.IsUnion(value.GetType(), BindingFlags.Public ||| BindingFlags.NonPublic)) then "", [||]
        else
            let case, values = FSharpValue.GetUnionFields(value, value.GetType(), BindingFlags.Public ||| BindingFlags.NonPublic)
            case.Name, values

    let property (value: objnull) name : objnull =
        if isNull value then null
        else
            let properties = value.GetType().GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            match properties |> Array.tryFind (fun item -> String.Equals(item.Name, name, StringComparison.Ordinal)) with
            | Some prop -> prop.GetValue(value)
            | None ->
                match properties |> Array.tryFind (fun item -> String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) with
                | Some prop -> prop.GetValue(value)
                | None -> null

    let sequence (value: objnull) =
        match value with
        | :? System.Collections.IEnumerable as values when not (value :? string) -> values |> Seq.cast<objnull> |> Seq.toArray
        | _ -> [||]

    let identText (value: objnull) =
        match property value "idText" with
        | :? string as text -> text
        | _ -> ""

    let callName (value: objnull) =
        let name, fields = union value
        match name with
        | "Ident" when fields.Length > 0 -> identText fields[0]
        | "LongIdent" ->
            let longDot = property value "longDotId"
            let ids = sequence (property longDot "LongIdent")
            ids |> Array.map identText |> String.concat "."
        | _ -> ""

    let rec flattenCall (expression: objnull) =
        let caseName, fields = union expression
        match caseName with
        | "Paren" | "Typed" -> flattenCall (property expression "expr")
        | "App" ->
            let func = property expression "funcExpr"
            let arg = property expression "argExpr"
            let arguments =
                match union arg with
                | "Tuple", _ -> property arg "exprs" |> sequence
                | _ -> [| arg |]
            match flattenCall func with
            | Some call -> Some { call with Args = Array.append call.Args arguments }
            | None -> None
        | _ ->
            let name = callName expression
            if String.IsNullOrWhiteSpace name then None else Some { Name = name; Args = [||] }

    let rec literal (expression: objnull) : objnull option =
        let caseName, fields = union expression
        match caseName with
        | "Paren" | "Typed" -> literal (property expression "expr")
        | "Const" ->
            let constant = property expression "constant"
            let constantCase, values = union constant
            if values.Length = 0 then None
            else
                match constantCase, values[0] with
                | "String", (:? string as value) -> Some(box value)
                | "Int32", (:? int as value) -> Some(box value)
                | "Int64", (:? int64 as value) -> Some(box (decimal value))
                | "Decimal", (:? decimal as value) -> Some(box value)
                | "Double", (:? double as value) -> Some(box (decimal value))
                | "Single", (:? single as value) -> Some(box (decimal value))
                | "Bool", (:? bool as value) -> Some(box value)
                | _ -> None
        | "ArrayOrList" ->
            Some(box (property expression "exprs" |> sequence |> Array.map literal |> Array.map (Option.defaultValue null)))
        | _ -> None

    let stringValue (value: objnull) =
        match value with
        | :? string as text -> Some text
        | _ -> None

    let stringArrayValue (value: objnull) =
        match value with
        | :? (objnull array) as items ->
            let converted = items |> Array.map stringValue
            if converted |> Array.exists Option.isNone then None
            else
                let readOnly = converted |> Array.map Option.get |> Array.AsReadOnly :> IReadOnlyList<string>
                Some readOnly
        | _ -> None

    let configKind name =
        match name with
        | "Config.text" -> Some ConfigValueKind.Text
        | "Config.multiline" -> Some ConfigValueKind.MultilineText
        | "Config.language" -> Some ConfigValueKind.Language
        | "Config.integer" -> Some ConfigValueKind.Integer
        | "Config.decimal" -> Some ConfigValueKind.Decimal
        | "Config.boolean" -> Some ConfigValueKind.Boolean
        | "Config.choice" -> Some ConfigValueKind.Choice
        | "Config.model" -> Some ConfigValueKind.Model
        | "Config.defaultModel" -> Some ConfigValueKind.Model
        | "Config.documents" -> Some ConfigValueKind.Documents
        | "Config.pageRange" -> Some ConfigValueKind.PageRange
        | "Config.credentialReference" -> Some ConfigValueKind.CredentialReference
        | _ -> None

    let fieldKind name =
        match name with
        | "Field.text" -> Some ConfigValueKind.Text
        | "Field.multiline" -> Some ConfigValueKind.MultilineText
        | "Field.language" -> Some ConfigValueKind.Language
        | "Field.integer" -> Some ConfigValueKind.Integer
        | "Field.decimal" -> Some ConfigValueKind.Decimal
        | "Field.boolean" -> Some ConfigValueKind.Boolean
        | "Field.choice" -> Some ConfigValueKind.Choice
        | "Field.model" | "Field.defaultModel" -> Some ConfigValueKind.Model
        | "Field.documents" -> Some ConfigValueKind.Documents
        | "Field.pageRange" -> Some ConfigValueKind.PageRange
        | "Field.credentialReference" -> Some ConfigValueKind.CredentialReference
        | _ -> None

    let sourceLocation fileName (node: objnull) lineOffset =
        let range = property node "Range"
        let start = property range "Start"
        let finish = property range "End"
        let integer name value = match property value name with | :? int as number -> number | _ -> 0
        ExtensionSourceLocation(fileName, max 1 (integer "Line" start - lineOffset), integer "Column" start,
            max 1 (integer "Line" finish - lineOffset), integer "Column" finish)

    let isResolvedSdkCall (typedUses: FSharpSymbolUse array) (name: string) (application: objnull) =
        let rec target expression =
            match union expression with
            | "App", _ -> target (property expression "funcExpr")
            | "Paren", _ | "Typed", _ -> target (property expression "expr")
            | _ -> expression
        let targetRange = property (target application) "Range"
        let start = property targetRange "Start"
        let finish = property targetRange "End"
        let line = match property start "Line" with | :? int as value -> value | _ -> 0
        let startColumn = match property start "Column" with | :? int as value -> value | _ -> 0
        let endColumn = match property finish "Column" with | :? int as value -> value | _ -> 0
        let expected =
            if name = "bind" then
                set [ "Patchouli.Extensions.Sdk.Config.bind"; "Patchouli.Extensions.Sdk.ConfigBindingOperators.bind" ]
            else set [ "Patchouli.Extensions.Sdk." + name ]
        typedUses
        |> Array.exists (fun symbolUse ->
            expected.Contains symbolUse.Symbol.FullName
            && symbolUse.Range.StartLine = line
            && symbolUse.Range.StartColumn >= startColumn
            && symbolUse.Range.EndColumn <= endColumn)

    let manifestDiagnostics (package: ExtensionPackageSnapshot) =
        match package.Sources.TryGetValue("extension.yml") with
        | false, _ -> [||]
        | true, source ->
            let values = Dictionary<string, string>(StringComparer.Ordinal)
            let issues = ResizeArray<ExtensionDiagnostic>()
            let lines = source.Replace("\r\n", "\n").Split('\n')
            for index = 0 to lines.Length - 1 do
                let line = lines[index].Trim()
                if line.Length > 0 && not (line.StartsWith("#", StringComparison.Ordinal)) then
                    let separator = line.IndexOf(':')
                    if separator <= 0 then
                        issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0006",
                            "The extension manifest must use simple key: value entries.",
                            ExtensionSourceLocation("extension.yml", index + 1, 0, index + 1, lines[index].Length)))
                    else
                        let key = line.Substring(0, separator).Trim()
                        let value = line.Substring(separator + 1).Trim().Trim('"', '\'')
                        if values.ContainsKey key then
                            issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0006",
                                "Manifest keys must be unique: " + key,
                                ExtensionSourceLocation("extension.yml", index + 1, 0, index + 1, lines[index].Length)))
                        else values[key] <- value
            let require key =
                match values.TryGetValue key with
                | true, value when not (String.IsNullOrWhiteSpace value) -> Some value
                | _ ->
                    issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0006",
                        "The extension manifest requires " + key + ".",
                        ExtensionSourceLocation("extension.yml", 1, 0, 1, 1)))
                    None
            let id = require "id"
            let version = require "version"
            let hostApi = require "host_api"
            match values.TryGetValue "kind" with
            | true, value when not ([| "translation"; "metadata"; "ocr"; "retrieval" |]
                                    |> Array.contains (value.ToLowerInvariant())) ->
                issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0006",
                    "Manifest kind must be translation, metadata, ocr, or retrieval.",
                    ExtensionSourceLocation("extension.yml", 1, 0, 1, 1)))
            | _ -> ()
            let entry = require "entry"
            if id |> Option.exists (fun value -> value <> package.Id) then
                issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0006",
                    "Manifest id must match the package id.", ExtensionSourceLocation("extension.yml", 1, 0, 1, 1)))
            if version |> Option.exists (fun value -> String.IsNullOrWhiteSpace value) then
                issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0006",
                    "Manifest version is invalid.", ExtensionSourceLocation("extension.yml", 1, 0, 1, 1)))
            if hostApi |> Option.exists (fun value -> value <> ExtensionHostApi.Current) then
                issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0007",
                    "The package targets an unsupported Patchouli extension host API.", ExtensionSourceLocation("extension.yml", 1, 0, 1, 1)))
            entry |> Option.iter (fun value ->
                let normalized = value.Replace('\\', '/')
                if Path.IsPathRooted normalized || Array.contains ".." (normalized.Split('/'))
                   || not (package.Sources.ContainsKey normalized)
                   || not (String.Equals(normalized, "main.fsx", StringComparison.Ordinal)) then
                    issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0006",
                        "Manifest entry must name the package-local main.fsx script.", ExtensionSourceLocation("extension.yml", 1, 0, 1, 1))))
            issues.ToArray()

    let packageKind (package: ExtensionPackageSnapshot) =
        match package.Sources.TryGetValue("extension.yml") with
        | true, source ->
            let matched = Regex.Match(source, "(?m)^\\s*kind\\s*:\\s*['\"]?([^'\"#\\s]+)", RegexOptions.CultureInvariant)
            if matched.Success then
                match matched.Groups[1].Value.ToLowerInvariant() with
                | "metadata" -> ExtensionKind.Metadata
                | "ocr" -> ExtensionKind.Ocr
                | "retrieval" -> ExtensionKind.Retrieval
                | _ -> ExtensionKind.Translation
            else ExtensionKind.Translation
        | _ -> ExtensionKind.Translation

    let executionPolicyDiagnostics fileName lineOffset (typedUses: FSharpSymbolUse array) (tree: objnull) =
        let mustCheckSymbols = set [
            "Patchouli.Extensions.Contracts.IHttpCapability.SendAsync"
            "Patchouli.Extensions.Contracts.ILlmCapability.CompleteAsync"
            "Patchouli.Extensions.Sdk.Batch.map"
        ]
        let isMustCheckCall expression =
            let expressionRange = property expression "Range"
            let start = property expressionRange "Start"
            let finish = property expressionRange "End"
            let line = match property start "Line" with | :? int as value -> value | _ -> 0
            let startColumn = match property start "Column" with | :? int as value -> value | _ -> 0
            let endLine = match property finish "Line" with | :? int as value -> value | _ -> line
            let endColumn = match property finish "Column" with | :? int as value -> value | _ -> 0
            typedUses
            |> Array.exists (fun symbolUse ->
                mustCheckSymbols.Contains symbolUse.Symbol.FullName
                && symbolUse.IsFromUse
                && (symbolUse.Range.StartLine, symbolUse.Range.StartColumn) >= (line, startColumn)
                && (symbolUse.Range.StartLine, symbolUse.Range.StartColumn) <= (endLine, endColumn)
                && (symbolUse.Range.EndLine, symbolUse.Range.EndColumn) <= (endLine, endColumn))
        let unionField (node: objnull) (fieldName: string) : objnull =
            if isNull node then null
            else
                let caseName, values = union node
                let case =
                    FSharpType.GetUnionCases(node.GetType(), BindingFlags.Public ||| BindingFlags.NonPublic)
                    |> Array.find (fun item -> item.Name = caseName)
                let fields = case.GetFields()
                match fields |> Array.tryFindIndex (fun field -> field.Name = fieldName) with
                | Some index when index < values.Length -> values[index]
                | _ -> null
        let rec isDiscardPattern (pattern: objnull) depth =
            if isNull pattern || depth > 12 then
                false
            else
                let caseName, fields = union pattern
                match caseName with
                | "Wild" -> true
                | "Named" ->
                    let identifier = unionField pattern "ident"
                    match property identifier "idText" with
                    | :? string as name -> name = "_"
                    | _ -> false
                | _ ->
                    fields
                    |> Array.exists (fun field ->
                        not (isNull field)
                        && field.GetType().Name.StartsWith("SynPat", StringComparison.Ordinal)
                        && isDiscardPattern field (depth + 1))
        let diagnostics = ResizeArray<ExtensionDiagnostic>()
        let seen = HashSet<struct (int * int)>()
        let add node =
            let range = property node "Range"
            let start = property range "Start"
            let finish = property range "End"
            let line = match property start "Line" with | :? int as value -> value | _ -> 0
            let column = match property start "Column" with | :? int as value -> value | _ -> 0
            let endColumn = match property finish "Column" with | :? int as value -> value | _ -> column
            if seen.Add(struct (line, column)) then
                diagnostics.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0021",
                    "The result of an SDK HTTP, LLM, or Batch.map call must be checked or handled.",
                    ExtensionSourceLocation(fileName, max 1 (line - lineOffset), column,
                        max 1 (line - lineOffset), max column endColumn)))
        let directCall expression = isMustCheckCall expression
        let rec visit node depth =
            if not (isNull node) && depth < 100 then
                let caseName, caseFields = union node
                if caseName = "DoBang" then
                    let expression = unionField node "expr"
                    if not (isNull expression) && directCall expression then add expression
                elif caseName = "LetOrUse" then
                    let letOrUse = unionField node "Item"
                    let bindings = property letOrUse "Bindings" |> sequence
                    for binding in bindings do
                        let pattern = property binding "HeadPat"
                        let expression = property binding "Expr"
                        if not (isNull pattern) && not (isNull expression)
                           && isDiscardPattern pattern 0 && directCall expression then add expression
                        visit expression (depth + 1)
                    let body = property letOrUse "Body"
                    visit body (depth + 1)
                elif caseName = "LetOrUseBang" then
                    let pattern = unionField node "pat"
                    let rhs = unionField node "rhs"
                    if not (isNull pattern) && not (isNull rhs)
                       && isDiscardPattern pattern 0 && directCall rhs then add rhs
                elif caseName = "App" then
                    match flattenCall node with
                    | Some call when call.Name = "ignore" ->
                        for argument in call.Args do
                            if directCall argument then add argument
                    | Some call when call.Name = "op_PipeRight" && call.Args.Length = 2
                                     && callName call.Args[1] = "ignore" ->
                        if directCall call.Args[0] then add call.Args[0]
                    | Some call when call.Name = "op_PipeLeft" && call.Args.Length = 2
                                     && callName call.Args[0] = "ignore" ->
                        if directCall call.Args[1] then add call.Args[1]
                    | _ -> ()
                for child in caseFields do
                    match child with
                    | :? string | :? Type | :? MemberInfo -> ()
                    | :? System.Collections.IEnumerable as children when not (child :? string) ->
                        for nested in children do visit nested (depth + 1)
                    | _ -> visit child (depth + 1)
        visit tree 0
        diagnostics.ToArray()

    let executionPolicyDiagnosticsLegacy fileName lineOffset (typedUses: FSharpSymbolUse array) =
        typedUses
        |> Array.choose (fun symbolUse ->
            let fullName = symbolUse.Symbol.FullName
            let blocked =
                fullName.StartsWith("System.IO.File.", StringComparison.Ordinal)
                || fullName.StartsWith("System.IO.Directory.", StringComparison.Ordinal)
                || fullName = "System.Diagnostics.Process.Start"
                || fullName.StartsWith("System.Diagnostics.Process.Start", StringComparison.Ordinal)
                || fullName.StartsWith("System.Threading.Thread.Start", StringComparison.Ordinal)
                || fullName.StartsWith("System.Threading.Tasks.Task.Run", StringComparison.Ordinal)
                || fullName.StartsWith("System.Threading.Tasks.TaskFactory.StartNew", StringComparison.Ordinal)
                || fullName.Contains("FSharpAsync.Start", StringComparison.Ordinal)
                || fullName.Contains("Operators.Unchecked.defaultof", StringComparison.Ordinal)
                || fullName.Contains("Operators.unbox", StringComparison.Ordinal)
                || fullName.StartsWith("System.Net.Http.HttpClient.", StringComparison.Ordinal)
                || fullName.StartsWith("System.Net.WebRequest.", StringComparison.Ordinal)
            if not blocked || not symbolUse.IsFromUse then None
            else
                let range = symbolUse.Range
                let line = max 1 (range.StartLine - lineOffset)
                let column = range.StartColumn
                let diagnostic = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0020",
                    "Direct process, filesystem, network, thread, unchecked-default, or unchecked-cast operations are forbidden; use managed extension capabilities.",
                    ExtensionSourceLocation(fileName, line, column, line, max column range.EndColumn))
                Some diagnostic)

    let extractFields fileName lineOffset (typedUses: FSharpSymbolUse array) (tree: objnull) =
        let fields = ResizeArray<ConfigFieldDescriptor>()
        let capabilityRequests = ResizeArray<ExtensionCapabilityRequest>()
        let issues = ResizeArray<ExtensionDiagnostic>()
        let seen = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let arity name =
            match name with
            | "Config.choice" -> 4
            | "Config.model" | "Config.defaultModel" | "Config.documents" | "Config.credentialReference" -> 2
            | "Config.text" | "Config.multiline" | "Config.language" | "Config.integer"
            | "Config.decimal" | "Config.boolean" | "Config.pageRange" -> 3
            | _ -> 0
        let updateLast (transform: ConfigFieldDescriptor -> ConfigFieldDescriptor) =
            if fields.Count > 0 then
                fields[fields.Count - 1] <- transform fields[fields.Count - 1]
        let wrapperNames = set [ "Config.range"; "Config.decimalRange"; "Config.required"; "Config.optional"; "Config.describe"; "Config.asField" ]
        let fieldArity name =
            match name with
            | "Field.choice" -> 3
            | "Field.model" | "Field.defaultModel" | "Field.documents" | "Field.credentialReference" | "Field.pageRange" -> 1
            | "Field.text" | "Field.multiline" | "Field.language" | "Field.integer" | "Field.decimal" | "Field.boolean" -> 2
            | _ -> 0
        let rec parseField (key: string) (expression: objnull) : ConfigFieldDescriptor option =
            match flattenCall expression with
            | Some call when call.Name = "op_PipeRight" && call.Args.Length = 2 ->
                match parseField key call.Args[0], flattenCall call.Args[1] with
                | Some descriptor, Some wrapper -> applyFieldWrapper descriptor wrapper.Name wrapper.Args
                | _ -> None
            | Some call when call.Name = "Field.range" || call.Name = "Field.decimalRange"
                            || call.Name = "Field.required" || call.Name = "Field.optional" || call.Name = "Field.describe" ->
                if not (isResolvedSdkCall typedUses call.Name expression) then None
                elif call.Args.Length = 3 then
                    parseField key call.Args[2] |> Option.bind (fun descriptor -> applyFieldWrapper descriptor call.Name call.Args[0..1])
                elif call.Args.Length = 2 then
                    parseField key call.Args[1] |> Option.bind (fun descriptor -> applyFieldWrapper descriptor call.Name call.Args[0..0])
                else None
            | Some call when call.Name.StartsWith("Field.", StringComparison.Ordinal)
                            && isResolvedSdkCall typedUses call.Name expression
                            && Option.isSome (fieldKind call.Name)
                            && call.Args.Length = fieldArity call.Name ->
                let args = call.Args |> Array.map literal
                if args |> Array.exists Option.isNone then None
                else
                    let values = args |> Array.map Option.get
                    let kind = Option.get (fieldKind call.Name)
                    let label = stringValue values[0] |> Option.defaultValue ""
                    let isChoice = kind = ConfigValueKind.Choice
                    let defaultValue: string | null =
                        if isChoice then Convert.ToString(values[2], CultureInfo.InvariantCulture)
                        elif values.Length > 1 then Convert.ToString(values[1], CultureInfo.InvariantCulture)
                        elif call.Name = "Field.defaultModel" || call.Name = "Field.documents" then ""
                        elif call.Name = "Field.pageRange" then ""
                        else null
                    let choices: IReadOnlyList<string> | null =
                        if isChoice then
                            match stringArrayValue values[1] with
                            | Some values -> values
                            | None -> null
                        else null
                    let required = call.Name = "Field.model"
                    Some(ConfigFieldDescriptor(key, label, kind, required, "", defaultValue, Nullable(), Nullable(), choices))
            | _ -> None
        and applyFieldWrapper (descriptor: ConfigFieldDescriptor) name args =
            let withValues required description minimum maximum =
                ConfigFieldDescriptor(descriptor.Key, descriptor.Label, descriptor.Kind,
                    defaultArg required descriptor.Required, defaultArg description descriptor.Description,
                    descriptor.DefaultValue, defaultArg minimum descriptor.Minimum, defaultArg maximum descriptor.Maximum,
                    descriptor.Choices)
            match name with
            | "Field.required" ->
                let description = args |> Array.tryHead |> Option.bind literal |> Option.map string
                Some(withValues (Some true) description None None)
            | "Field.optional" -> Some(withValues (Some false) None None None)
            | "Field.describe" ->
                let description = args |> Array.tryHead |> Option.bind literal |> Option.map string
                Some(withValues None description None None)
            | "Field.range" | "Field.decimalRange" ->
                if args.Length < 2 then None
                else
                    match literal args[0], literal args[1] with
                    | Some (:? IConvertible as minimum), Some (:? IConvertible as maximum) ->
                        let lower = Convert.ToDecimal(minimum, CultureInfo.InvariantCulture)
                        let upper = Convert.ToDecimal(maximum, CultureInfo.InvariantCulture)
                        Some(withValues None None (Some(Nullable lower)) (Some(Nullable upper)))
                    | _ -> None
            | _ -> None
        let resolvedSdkValue (fullName: string) (expression: objnull) =
            let range = property expression "Range"
            let start = property range "Start"
            let finish = property range "End"
            let line = match property start "Line" with | :? int as value -> value | _ -> 0
            let startColumn = match property start "Column" with | :? int as value -> value | _ -> 0
            let endColumn = match property finish "Column" with | :? int as value -> value | _ -> 0
            typedUses
            |> Array.exists (fun symbolUse ->
                symbolUse.Symbol.FullName = fullName
                && symbolUse.Range.StartLine = line
                && symbolUse.Range.StartColumn >= startColumn
                && symbolUse.Range.EndColumn <= endColumn)
        let parseCapability (key: string) (expression: objnull) =
            let domainOf (expression: objnull) =
                match callName expression with
                | "Domains.translation" when resolvedSdkValue "Patchouli.Extensions.Sdk.Domains.translation" expression -> Some "translation"
                | "Domains.metadata" when resolvedSdkValue "Patchouli.Extensions.Sdk.Domains.metadata" expression -> Some "metadata"
                | _ -> None
            match flattenCall expression with
            | Some pipe when pipe.Name = "op_PipeRight" && pipe.Args.Length = 2 ->
                match flattenCall pipe.Args[0], flattenCall pipe.Args[1] with
                | Some capability, Some follow when capability.Name = "Capability.llm"
                                                  && follow.Name = "Capability.followDefault"
                                                  && isResolvedSdkCall typedUses capability.Name pipe.Args[0]
                                                  && isResolvedSdkCall typedUses follow.Name pipe.Args[1]
                                                  && capability.Args.Length = 1
                                                  && follow.Args.Length = 1 ->
                    match literal capability.Args[0], domainOf follow.Args[0] with
                    | Some (:? string as label), Some domain ->
                        Some(ConfigFieldDescriptor(key, label, ConfigValueKind.Model, false, "", "", Nullable(), Nullable(), null),
                             ExtensionCapabilityRequest(ExtensionCapabilityKind.Llm, key, domain))
                    | _ -> None
                | _ -> None
            | Some call when call.Name = "Capability.llm" && call.Args.Length = 1
                             && isResolvedSdkCall typedUses call.Name expression ->
                match literal call.Args[0] with
                | Some (:? string as label) ->
                    Some(ConfigFieldDescriptor(key, label, ConfigValueKind.Model, false, "", "", Nullable(), Nullable(), null),
                         ExtensionCapabilityRequest(ExtensionCapabilityKind.Llm, key))
                | _ -> None
            | Some call when call.Name = "Capability.http" && call.Args.Length = 1
                             && isResolvedSdkCall typedUses call.Name expression ->
                match literal call.Args[0] with
                | Some (:? string) -> Some(ConfigFieldDescriptor(key, "", ConfigValueKind.Capability, false, "", null, Nullable(), Nullable(), null),
                                            ExtensionCapabilityRequest(ExtensionCapabilityKind.Http, key))
                | _ -> None
            | _ -> None
        let rec visit (node: objnull) depth =
            if not (isNull node) && depth < 80 then
                match node with
                | :? string | :? Type | :? MemberInfo -> ()
                | :? System.Collections.IEnumerable as values ->
                    for child in values do visit child (depth + 1)
                | _ ->
                    let caseName, caseFields = union node
                    if caseName = "App" then
                        match flattenCall node with
                        | Some call when (call.Name.StartsWith("Config.", StringComparison.Ordinal)
                                          && isResolvedSdkCall typedUses call.Name node)
                                         || (call.Name = "bind" && isResolvedSdkCall typedUses "bind" node) ->
                            if call.Name = "Config.bind" || isResolvedSdkCall typedUses "bind" node then
                                if call.Args.Length <> 2 then
                                    issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0011",
                                        "Config.bind requires a static key and one Field declaration.", sourceLocation fileName node lineOffset))
                                else
                                    match literal call.Args[0] with
                                    | Some (:? string as key) ->
                                        if key.StartsWith("__host.", StringComparison.Ordinal) then
                                            issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0013",
                                                "The __host.* configuration namespace is reserved for the host.", sourceLocation fileName node lineOffset))
                                        elif not (seen.Add key) then
                                            issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0012",
                                                "Configuration and capability binding keys must be unique: " + key, sourceLocation fileName node lineOffset))
                                        else
                                            match parseCapability key call.Args[1] with
                                            | Some(descriptor, request) ->
                                                if descriptor.Kind <> ConfigValueKind.Capability then fields.Add descriptor
                                                capabilityRequests.Add request
                                            | None ->
                                                match parseField key call.Args[1] with
                                                | Some descriptor -> fields.Add descriptor
                                                | None -> issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0011",
                                                    "Config.bind keys and declarations must use static literals and supported SDK builders.",
                                                    sourceLocation fileName node lineOffset))
                                    | _ -> issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0011",
                                        "Config.bind requires a static stable key.", sourceLocation fileName node lineOffset))
                            elif wrapperNames.Contains call.Name then
                                let bindingArgument = call.Args |> Array.tryLast
                                match bindingArgument with
                                | Some bindingNode ->
                                    let startCount = fields.Count
                                    visit bindingNode (depth + 1)
                                    if fields.Count > startCount then
                                        match call.Name with
                                        | "Config.required" ->
                                            let description = if call.Args.Length > 1 then literal call.Args[0] |> Option.map string else None
                                            updateLast (fun field -> ConfigFieldDescriptor(field.Key, field.Label, field.Kind, true,
                                                description |> Option.defaultValue field.Description, field.DefaultValue, field.Minimum, field.Maximum, field.Choices))
                                        | "Config.optional" ->
                                            updateLast (fun field -> ConfigFieldDescriptor(field.Key, field.Label, field.Kind, false,
                                                field.Description, field.DefaultValue, field.Minimum, field.Maximum, field.Choices))
                                        | "Config.describe" ->
                                            let description = if call.Args.Length > 1 then literal call.Args[0] |> Option.map string else None
                                            updateLast (fun field -> ConfigFieldDescriptor(field.Key, field.Label, field.Kind, field.Required,
                                                description |> Option.defaultValue field.Description, field.DefaultValue, field.Minimum, field.Maximum, field.Choices))
                                        | "Config.range" | "Config.decimalRange" ->
                                            if call.Args.Length >= 3 then
                                                match literal call.Args[0], literal call.Args[1] with
                                                | Some (:? IConvertible as minimum), Some (:? IConvertible as maximum) ->
                                                    let minValue = Convert.ToDecimal(minimum, CultureInfo.InvariantCulture)
                                                    let maxValue = Convert.ToDecimal(maximum, CultureInfo.InvariantCulture)
                                                    updateLast (fun field -> ConfigFieldDescriptor(field.Key, field.Label, field.Kind,
                                                        field.Required, field.Description, field.DefaultValue, Nullable minValue,
                                                        Nullable maxValue, field.Choices))
                                                | _ -> issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0011",
                                                    "Configuration range bounds must be static numeric literals.", sourceLocation fileName node lineOffset))
                                        | _ -> ()
                                | None -> ()
                            elif call.Name = "Config.fields" then
                                for child in call.Args do visit child (depth + 1)
                            else
                                match configKind call.Name with
                                | None ->
                                    if call.Args.Length >= 2 then
                                        issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0010",
                                            "Unsupported configuration declaration. Use a Config constructor.", sourceLocation fileName node lineOffset))
                                | Some kind when call.Args.Length = arity call.Name ->
                                    let args = call.Args |> Array.map literal
                                    if args.Length < 2 || args[0].IsNone || args[1].IsNone
                                       || not (args[0].Value :? string) || not (args[1].Value :? string)
                                       || args |> Array.exists Option.isNone then
                                        issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0011",
                                            "Configuration keys, labels, defaults and choices must be static literals.", sourceLocation fileName node lineOffset))
                                    else
                                        let values = args |> Array.map Option.get
                                        let key = stringValue values[0] |> Option.defaultValue ""
                                        if key.StartsWith("__host.", StringComparison.Ordinal) then
                                            issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0013",
                                                "The __host.* configuration namespace is reserved for the host.", sourceLocation fileName node lineOffset))
                                        elif not (seen.Add key) then
                                            issues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0012",
                                                "Configuration keys must be unique: " + key, sourceLocation fileName node lineOffset))
                                        else
                                            let isChoice = kind = ConfigValueKind.Choice
                                            let defaultValue: string | null =
                                                if isChoice then Convert.ToString(values[3], CultureInfo.InvariantCulture)
                                                elif values.Length > 2 then Convert.ToString(values[2], CultureInfo.InvariantCulture)
                                                else null
                                            let choices: IReadOnlyList<string> | null =
                                                if isChoice then
                                                    match stringArrayValue values[2] with
                                                    | Some values -> values
                                                    | None -> null
                                                else null
                                            let required = call.Name = "Config.model"
                                            let label = stringValue values[1] |> Option.defaultValue ""
                                            fields.Add(ConfigFieldDescriptor(key, label, kind, required, "", defaultValue,
                                                Nullable(), Nullable(), choices))
                                | Some _ -> ()
                                for child in caseFields do visit child (depth + 1)
                        | _ -> for child in caseFields do visit child (depth + 1)
                    else
                        for child in caseFields do visit child (depth + 1)
        visit tree 0
        fields.ToArray(), capabilityRequests.ToArray(), issues.ToArray()

    new() = ExtensionCompiler(Path.Combine(Path.GetTempPath(), "patchouli-extension-check"))

    member _.CheckAsync(package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) : Task<ExtensionCheckResult> = backgroundTask {
        cancellationToken.ThrowIfCancellationRequested()
        match manifestDiagnostics package, package.Sources.TryGetValue("main.fsx") with
        | issues, _ when issues.Length > 0 -> return ExtensionCheckResult(false, [||], issues)
        | _, (false, _) ->
            let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0001", "Package is missing main.fsx.",
                ExtensionSourceLocation("main.fsx", 1, 0, 1, 1))
            return ExtensionCheckResult(false, [||], [| issue |])
        | _, (true, source) ->
            let checkRoot = Path.Combine(resolvedCheckRoot, package.Id,
                package.Fingerprint, Guid.NewGuid().ToString("N"))
            use _checkDirectory = new TemporaryCheckDirectory(checkRoot)
            for KeyValue(relative, content) in package.Sources do
                let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                let path = Path.Combine(checkRoot, normalized.Replace('/', Path.DirectorySeparatorChar))
                createCheckParentDirectory path
                File.WriteAllText(path, content)
            let fileName = Path.Combine(checkRoot, "main.fsx")
            let references = sprintf "#r @\"%s\"\n#r @\"%s\"\n#r @\"%s\"\n" contractAssembly coreAssembly sdkAssembly
            let prefixLines = 3
            let directiveIssues = ResizeArray<ExtensionDiagnostic>()
            let knownFiles = HashSet<string>(package.Sources.Keys, StringComparer.OrdinalIgnoreCase)
            for KeyValue(path, text) in package.Sources do
                let lines = text.Replace("\r\n", "\n").Split('\n')
                for index = 0 to lines.Length - 1 do
                    let matched = Regex.Match(lines[index], "^\\s*#\\s*([A-Za-z]+)(.*)$", RegexOptions.CultureInvariant)
                    if matched.Success then
                        let directive = matched.Groups[1].Value.ToLowerInvariant()
                        let location = ExtensionSourceLocation(path, index + 1, max 0 (lines[index].IndexOf('#')), index + 1, lines[index].Length)
                        if directive = "load" then
                            let argument = Regex.Match(matched.Groups[2].Value, "^\\s*@?\"([^\"]+)\"", RegexOptions.CultureInvariant)
                            if not argument.Success then
                                directiveIssues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0003", "#load must name a package source file literal.", location))
                            else
                                let relative = argument.Groups[1].Value.Replace('\\', '/')
                                let sourcePath = Path.Combine(checkRoot, path.Replace('/', Path.DirectorySeparatorChar))
                                let resolved =
                                    try
                                        match Path.GetDirectoryName sourcePath with
                                        | null -> ""
                                        | sourceDirectory ->
                                            let fullPath = Path.GetFullPath(Path.Combine(sourceDirectory,
                                                relative.Replace('/', Path.DirectorySeparatorChar)))
                                            ExtensionPackageSnapshot.NormalizeSourcePath(Path.GetRelativePath(checkRoot, fullPath))
                                    with :? ArgumentException -> ""
                                if String.IsNullOrEmpty resolved || not (knownFiles.Contains(resolved)) then
                                    directiveIssues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0004", "#load may reference only files inside this immutable package.", location))
                        else
                            directiveIssues.Add(ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0005",
                                "Only package-local #load directives are supported. Compiler warnings cannot be disabled.", location))
            if directiveIssues.Count > 0 then
                return ExtensionCheckResult(false, [||], directiveIssues.ToArray())
            else
                let contractCheck = "\nlet __patchouli_extension_contract : Patchouli.Extensions.Contracts.IExtensionDefinition = extension\n"
                let sourceText = SourceText.ofString (references + source + contractCheck)
                let options, optionDiagnostics =
                    checker.GetProjectOptionsFromScript(fileName, sourceText, assumeDotNetFramework = false,
                        useSdkRefs = false, otherFlags = RuntimeFrameworkReferences.compilerFlags())
                    |> Async.RunSynchronously
                let options = { options with OtherOptions = Array.append options.OtherOptions [| "--langversion:10.0"; "--checknulls+" |] }
                cancellationToken.ThrowIfCancellationRequested()
                let parseResults, checkAnswer = checker.ParseAndCheckFileInProject(fileName, 0, sourceText, options) |> Async.RunSynchronously
                let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)
                let typedResults =
                    match checkAnswer with
                    | FSharpCheckFileAnswer.Succeeded results -> Some results
                    | _ -> None
                let typedDiagnostics = typedResults |> Option.map (fun results -> results.Diagnostics) |> Option.defaultValue [||]
                let allDiagnostics = Seq.concat [ optionDiagnostics :> seq<_>; parseResults.Diagnostics :> seq<_>; typedDiagnostics :> seq<_> ] |> Seq.toArray
                let diagnostics = allDiagnostics |> Array.map (diagnostic checkRoot "main.fsx" prefixLines)
                let mainUses = typedResults |> Option.map (fun results -> results.GetAllUsesOfAllSymbolsInFile() |> Seq.toArray) |> Option.defaultValue [||]
                let mainFields, mainRequests, mainExtractionDiagnostics = extractFields "main.fsx" prefixLines mainUses (box parseResults.ParseTree)
                let mainPolicyDiagnostics =
                    Array.append
                        (executionPolicyDiagnostics "main.fsx" prefixLines mainUses (box parseResults.ParseTree))
                        (executionPolicyDiagnosticsLegacy "main.fsx" prefixLines mainUses)
                let helperResults =
                    package.Sources
                    |> Seq.filter (fun pair ->
                        let relative = pair.Key.Replace('\\', '/')
                        not (String.Equals(relative, "main.fsx", StringComparison.OrdinalIgnoreCase))
                        && (relative.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase)
                            || relative.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)))
                    |> Seq.map (fun pair ->
                        let relative = pair.Key.Replace('\\', '/')
                        let helperPath = Path.Combine(checkRoot, relative.Replace('/', Path.DirectorySeparatorChar))
                        let helperSource = SourceText.ofString (references + pair.Value)
                        let helperOptions, helperOptionDiagnostics =
                            checker.GetProjectOptionsFromScript(helperPath, helperSource, assumeDotNetFramework = false,
                                useSdkRefs = false, otherFlags = RuntimeFrameworkReferences.compilerFlags())
                            |> Async.RunSynchronously
                        let helperOptions = { helperOptions with OtherOptions = Array.append helperOptions.OtherOptions [| "--langversion:10.0"; "--checknulls+" |] }
                        let helperParse, helperCheck = checker.ParseAndCheckFileInProject(helperPath, 0, helperSource, helperOptions) |> Async.RunSynchronously
                        let helperUses =
                            match helperCheck with
                            | FSharpCheckFileAnswer.Succeeded results -> results.GetAllUsesOfAllSymbolsInFile() |> Seq.toArray
                            | _ -> [||]
                        let helperTypedDiagnostics =
                            match helperCheck with
                            | FSharpCheckFileAnswer.Succeeded results -> results.Diagnostics
                            | _ -> [||]
                        let helperSourceDiagnostics =
                            Seq.concat [ helperOptionDiagnostics :> seq<_>; helperParse.Diagnostics :> seq<_>; helperTypedDiagnostics :> seq<_> ]
                            |> Seq.toArray
                            |> Array.map (diagnostic checkRoot relative prefixLines)
                        let helperFields, helperRequests, helperDiagnostics = extractFields relative prefixLines helperUses (box helperParse.ParseTree)
                        let helperPolicyDiagnostics =
                            Array.append
                                (executionPolicyDiagnostics relative prefixLines helperUses (box helperParse.ParseTree))
                                (executionPolicyDiagnosticsLegacy relative prefixLines helperUses)
                        helperFields, helperRequests, helperDiagnostics, helperPolicyDiagnostics, helperSourceDiagnostics)
                    |> Seq.toArray
                let fields = Array.concat (Array.append [| mainFields |] (helperResults |> Array.map (fun (fields, _, _, _, _) -> fields)))
                let capabilityRequests = Array.concat (Array.append [| mainRequests |] (helperResults |> Array.map (fun (_, requests, _, _, _) -> requests)))
                let helperDiagnostics = helperResults |> Array.collect (fun (_, _, issues, _, _) -> issues)
                let helperPolicyDiagnostics = helperResults |> Array.collect (fun (_, _, _, issues, _) -> issues)
                let helperSourceDiagnostics = helperResults |> Array.collect (fun (_, _, _, _, issues) -> issues)
                let duplicateDiagnostics =
                    fields
                    |> Array.groupBy (fun field -> field.Key)
                    |> Array.collect (fun (key, duplicates) ->
                        if duplicates.Length < 2 then [||]
                        else [| ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0012",
                                   "Configuration keys must be unique across package files: " + key,
                                   ExtensionSourceLocation("main.fsx", 1, 0, 1, 1)) |])
                let capabilityKeys = capabilityRequests |> Array.map (fun request -> request.Key) |> Set.ofArray
                let declaredKeys =
                    Array.append
                        (fields |> Array.map (fun field -> field.Key) |> Array.filter (fun key -> not (capabilityKeys.Contains key)))
                        (capabilityRequests |> Array.map (fun request -> request.Key))
                let crossKindDuplicateDiagnostics =
                    declaredKeys
                    |> Array.groupBy id
                    |> Array.collect (fun (key, duplicates) ->
                        if duplicates.Length < 2 then [||]
                        else [| ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0012",
                                   "Configuration and capability keys must be unique across package files: " + key,
                                   ExtensionSourceLocation("main.fsx", 1, 0, 1, 1)) |])
                let allDiagnostics = Array.concat [ diagnostics; helperSourceDiagnostics; mainExtractionDiagnostics; helperDiagnostics;
                    mainPolicyDiagnostics; helperPolicyDiagnostics; duplicateDiagnostics; crossKindDuplicateDiagnostics ]
                let succeeded = not (allDiagnostics |> Array.exists (fun issue -> issue.Severity = ExtensionDiagnosticSeverity.Error))
                return ExtensionCheckResult(succeeded, fields, allDiagnostics, capabilityRequests)
    }

    interface IExtensionSourceChecker with
        member this.CheckAsync(package, cancellationToken) = this.CheckAsync(package, cancellationToken)
