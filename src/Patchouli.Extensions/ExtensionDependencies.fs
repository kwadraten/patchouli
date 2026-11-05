namespace Patchouli.Extensions

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax
open Patchouli.Extensions.Contracts

[<Sealed>]
type ExtensionDependencyResolution(compilerReferences: string array, compilerOptions: string array,
                                  diagnostics: ExtensionDiagnostic array, fingerprintEntries: (string * byte[]) array,
                                  succeeded: bool) =
    member _.CompilerReferences = compilerReferences
    member _.CompilerOptions = compilerOptions
    member _.Diagnostics = diagnostics
    member _.FingerprintEntries = fingerprintEntries
    member _.Succeeded = succeeded

type internal ExtensionNugetDirective =
    { PackageId: string
      Version: string
      Reference: string
      FileName: string
      Line: int }

type internal ExtensionNugetInputs =
    { Packages: ExtensionNugetDirective array
      Sources: string array }

module ExtensionDependencies =
    let private diagnostic (item: FSharpDiagnostic) =
        let severity =
            match item.Severity with
            | FSharpDiagnosticSeverity.Error -> ExtensionDiagnosticSeverity.Error
            | FSharpDiagnosticSeverity.Warning -> ExtensionDiagnosticSeverity.Warning
            | _ -> ExtensionDiagnosticSeverity.Information
        let file =
            if String.IsNullOrWhiteSpace item.FileName then "main.fsx"
            else Path.GetFileName item.FileName |> Option.ofObj |> Option.defaultValue "main.fsx"
        ExtensionDiagnostic(severity, item.ErrorNumberText, item.Message,
            ExtensionSourceLocation(file, max 1 item.StartLine, item.StartColumn, max 1 item.EndLine, item.EndColumn))

    let private failure code message file line =
        ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, code, message,
            ExtensionSourceLocation(file, line, 0, line, 1))

    let private failed issues =
        ExtensionDependencyResolution([||], [||], issues, [||], false)

    let rec private declarations items = seq {
            for item in items do
                match item with
                | SynModuleDecl.HashDirective(hashDirective = value) -> yield value
                | SynModuleDecl.NestedModule(decls = nested) -> yield! declarations nested
                | SynModuleDecl.NamespaceFragment fragment -> yield! moduleDirectives fragment
                | _ -> ()
    }
    and private moduleDirectives (SynModuleOrNamespace(decls = items)) = declarations items

    /// Parse with FCS's compiler conditions; comments, strings and inactive branches never count.
    let internal scanNugetDirectives(checker: FSharpChecker, sources: IReadOnlyDictionary<string, string>,
                                    token: CancellationToken) = task {
        let found = ResizeArray<ExtensionNugetDirective>()
        let packageSources = ResizeArray<string>()
        let visited = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let virtualRoot = Path.GetFullPath(".")
        let queue = Queue<string>()
        queue.Enqueue("main.fsx")
        while queue.Count > 0 do
            let file = queue.Dequeue()
            token.ThrowIfCancellationRequested()
            match sources.TryGetValue file with
            | true, source when visited.Add file ->
                let options, _ = checker.GetParsingOptionsFromCommandLineArgs(
                    ["--targetprofile:netcore"; "--langversion:10.0"; file])
                let! parsed = Async.StartAsTask(
                    checker.ParseFile(file, SourceText.ofString source, options), cancellationToken = token)
                let directives =
                    match parsed.ParseTree with
                    | ParsedInput.ImplFile input ->
                        Seq.append input.HashDirectives (input.Contents |> Seq.collect moduleDirectives)
                    | _ -> Seq.empty
                for ParsedHashDirective(name, arguments, location) in directives do
                    for argument in arguments do
                        match argument with
                        | ParsedHashDirectiveArgument.String(value, _, _) when name = "r" &&
                            value.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) ->
                            let parts = value.Substring("nuget:".Length).Split(',') |> Array.map (fun item -> item.Trim())
                            let named name =
                                parts |> Array.tryPick (fun part ->
                                    let prefix = name + "="
                                    if part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                                        Some(part.Substring(prefix.Length).Trim())
                                    else None)
                            found.Add { PackageId = named "Include" |> Option.defaultValue parts[0]
                                        Version = named "Version" |> Option.defaultValue (
                                            if parts.Length > 1 then parts[1] else "")
                                        Reference = value
                                        FileName = file; Line = location.StartLine }
                        | ParsedHashDirectiveArgument.String(value, _, _) when name = "load" ->
                            let target = Path.GetFullPath(Path.Combine(virtualRoot,
                                (Path.GetDirectoryName(file) |> Option.ofObj |> Option.defaultValue ""), value))
                            let relative = Path.GetRelativePath(virtualRoot, target).Replace('\\', '/')
                            if sources.ContainsKey relative then queue.Enqueue(relative)
                        | ParsedHashDirectiveArgument.String(value, _, _) when
                            String.Equals(name, "i", StringComparison.OrdinalIgnoreCase)
                            && value.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) ->
                            packageSources.Add(value)
                        | _ -> ()
            | _ -> ()
        return { Packages = found.ToArray(); Sources = packageSources |> Seq.distinct |> Seq.toArray }
    }

    let private exactVersion (version: string) =
        Regex.IsMatch(version,
            "^[0-9]+(?:\\.[0-9]+){0,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?(?:\\+[0-9A-Za-z][0-9A-Za-z.-]*)?$",
            RegexOptions.CultureInvariant)

    let private findPackageRoot (path: string) =
        let rec ascend (directory: DirectoryInfo option) =
            match directory with
            | None -> None
            | Some current ->
                if File.Exists(Path.Combine(current.FullName, ".nupkg.metadata"))
                   || (Directory.EnumerateFiles(current.FullName, "*.nuspec", SearchOption.TopDirectoryOnly) |> Seq.isEmpty |> not) then
                    let id = current.Parent |> Option.ofObj |> Option.map (fun parent -> parent.Name)
                    id |> Option.map (fun packageId -> current.FullName, packageId, current.Name)
                else current.Parent |> Option.ofObj |> ascend
        (Path.GetDirectoryName path |> Option.ofObj |> Option.map (fun parent -> DirectoryInfo(parent))) |> ascend

    // FCS may emit forward-slash reference paths on Windows; DirectoryInfo roots use the
    // platform separator. Compare full normalized paths or package assets silently drop out.
    let internal packageAssetPaths (packageRoot: string) (references: string array) =
        let root = Path.GetFullPath packageRoot
        references
        |> Array.filter (fun path ->
            let normalized = Path.GetFullPath path
            normalized.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(root + string Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))

    let private resolveWithFcs(checker: FSharpChecker, sourceRoot: string, outputRoot: string,
                               sources: IReadOnlyDictionary<string, string>, directives: ExtensionNugetDirective array,
                               packageSources: string array, token: CancellationToken) : Task<ExtensionDependencyResolution> = task {
        let entry = Path.Combine(Path.GetFullPath sourceRoot, ".nuget-resolve.fsx")
        match sources.TryGetValue "main.fsx" with
        | false, _ ->
            return failed [| failure "EXT0031" "NuGet directives require a main.fsx entry script." "main.fsx" 1 |]
        | true, _ ->
            try
                token.ThrowIfCancellationRequested()
                // Resolve only the compiler-active directives already parsed above. Parsing the
                // original entry as an FSI script would select a different #if INTERACTIVE branch.
                let source =
                    Array.append
                        (packageSources |> Array.map (fun value -> "#i " + System.Text.Json.JsonSerializer.Serialize(value)))
                        (directives |> Array.map (fun item -> "#r " + System.Text.Json.JsonSerializer.Serialize(item.Reference)))
                    |> String.concat "\n"
                let dependencyManager = Path.Combine(AppContext.BaseDirectory, "FSharp.DependencyManager.Nuget.dll")
                if not (File.Exists dependencyManager) then
                    raise (FileNotFoundException("F# NuGet dependency manager sidecar is missing.", dependencyManager))
                let projectOptions, optionDiagnostics =
                    checker.GetProjectOptionsFromScript(entry, SourceText.ofString source,
                        assumeDotNetFramework = false, useSdkRefs = false,
                        otherFlags = [| "--noframework"; "--targetprofile:netcore"; "--compilertool:" + AppContext.BaseDirectory |])
                    |> Async.RunSynchronously
                token.ThrowIfCancellationRequested()
                let diagnostics = optionDiagnostics |> List.map diagnostic |> List.toArray
                let options = projectOptions.OtherOptions
                let references =
                    options
                    |> Array.choose (fun option ->
                        if option.StartsWith("-r:", StringComparison.Ordinal) then Some(option.Substring(3))
                        elif option.StartsWith("--reference:", StringComparison.Ordinal) then Some(option.Substring(12))
                        else None)
                    |> Array.filter File.Exists
                    |> Array.map Path.GetFullPath
                    |> Array.distinct
                let packageRoots = references |> Array.choose findPackageRoot |> Array.distinctBy (fun (root, _, _) -> root)
                // A pre-existing global cache directory is not evidence that this resolver call
                // produced a reference. FCS can otherwise return success with no restored DLLs.
                let resolvedPackages = packageRoots |> Array.map (fun (_, packageId, version) -> packageId, version)
                let unresolved =
                    directives
                    |> Array.filter (fun item -> not (NugetResolverPreflight.isPackageRestored(item.PackageId, item.Version, resolvedPackages)))
                    |> Array.map (fun item ->
                        failure "EXT0036"
                            ($"NuGet package '{item.PackageId}' version '{item.Version}' did not produce a confirmed restored package or usable reference. Check package sources/cache and target compatibility, or include a DLL and use #r \"lib/...\".")
                            item.FileName item.Line)
                let allDiagnostics = Array.append diagnostics unresolved
                if unresolved.Length > 0 || (diagnostics |> Array.exists (fun item -> item.Severity = ExtensionDiagnosticSeverity.Error)) then
                    return failed allDiagnostics
                else
                    let packageAssets =
                        packageRoots
                        |> Array.collect (fun (packageRoot, packageId, packageVersion) ->
                            let packageReferences = packageAssetPaths packageRoot references
                            let frameworkDirectories =
                                packageReferences
                                |> Array.choose (fun path ->
                                    let directory = Path.GetDirectoryName path |> Option.ofObj
                                    match directory with
                                    | None -> None
                                    | Some value when value.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])
                                                          |> Array.exists (fun part -> String.Equals(part, "ref", StringComparison.OrdinalIgnoreCase)) ->
                                        Path.GetFileName value |> Option.ofObj |> Option.map (fun tfm -> Path.Combine(packageRoot, "lib", tfm))
                                    | Some value -> Some value)
                                |> Array.filter Directory.Exists
                            let runtimeRoot = Path.Combine(packageRoot, "runtimes")
                            let runtimeDirectories =
                                [| System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier; "any" |]
                                |> Array.collect (fun rid ->
                                    let root = Path.Combine(runtimeRoot, rid)
                                    let native = Path.Combine(root, "native")
                                    let runtimeLib =
                                        frameworkDirectories
                                        |> Array.choose (fun framework ->
                                            Path.GetFileName framework |> Option.ofObj |> Option.map (fun tfm -> Path.Combine(root, "lib", tfm)))
                                    Array.append runtimeLib [| native |])
                                |> Array.filter Directory.Exists
                            Array.concat [ frameworkDirectories; runtimeDirectories ]
                            |> Array.distinct
                            |> Array.collect (fun directory -> Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                            |> Array.distinct
                            |> Array.map (fun path -> packageRoot, packageId, packageVersion, path))
                    let libDirectory = Path.Combine(Path.GetFullPath outputRoot, "lib")
                    Directory.CreateDirectory libDirectory |> ignore
                    for packageRoot, packageId, packageVersion, path in packageAssets do
                        token.ThrowIfCancellationRequested()
                        let relative = Path.GetRelativePath(packageRoot, path)
                        let destination = Path.Combine(libDirectory, packageId, packageVersion, relative.Replace('/', Path.DirectorySeparatorChar))
                        let parent = Path.GetDirectoryName destination |> Option.ofObj |> Option.defaultWith (fun () -> invalidOp "NuGet runtime asset destination has no parent directory.")
                        Directory.CreateDirectory parent |> ignore
                        File.Copy(path, destination, true)
                    let fingerprints =
                        packageAssets
                        |> Array.map (fun (packageRoot, packageId, packageVersion, path) ->
                            let relative = Path.GetRelativePath(packageRoot, path).Replace('\\', '/')
                            "nuget/" + packageId + "/" + packageVersion + "/" + relative, File.ReadAllBytes path)
                    return ExtensionDependencyResolution(references, options, diagnostics, fingerprints, true)
            with
            | :? OperationCanceledException as error when token.IsCancellationRequested -> return raise error
            | :? FileNotFoundException ->
                return failed [| failure "EXT0031" "The F# NuGet dependency manager is missing from the compiler sidecar." "main.fsx" 1 |]
            | _ ->
                return failed [| failure "EXT0035" "NuGet resolution failed. Verify the installed .NET SDK and configured package sources/cache." "main.fsx" 1 |]
    }

    let internal resolveWithSdkProbeAsync(checker: FSharpChecker, sourceRoot: string, outputRoot: string,
                     sources: IReadOnlyDictionary<string, string>,
                     sdkProbe: Func<CancellationToken, Task<NugetSdkProbeResult>>,
                     token: CancellationToken) : Task<ExtensionDependencyResolution> = task {
        token.ThrowIfCancellationRequested()
        let! inputs = scanNugetDirectives(checker, sources, token)
        let requested = inputs.Packages
        if requested.Length = 0 then
            return ExtensionDependencyResolution([||], [||], [||], [||], true)
        else
            let invalidVersions =
                requested
                |> Array.filter (fun item -> not (Regex.IsMatch(item.PackageId, "^[A-Za-z0-9_.-]+$"))
                                            || not (exactVersion item.Version))
                |> Array.map (fun item ->
                    failure "EXT0032"
                        "#r nuget requires a fixed NuGet version (for example 1.2.3); missing versions, floating versions and ranges are not supported."
                        item.FileName item.Line)
            if invalidVersions.Length > 0 then return failed invalidVersions
            else
                let! probe = sdkProbe.Invoke(token)
                match probe with
                | DotnetMissing ->
                    return failed [| failure "EXT0033"
                        "此扩展通过 NuGet 引用依赖，但未找到 dotnet。请安装可用的 .NET SDK 后重试：https://dotnet.microsoft.com/download"
                        requested[0].FileName requested[0].Line |]
                | RuntimeOnly _ ->
                    return failed [| failure "EXT0034"
                        "此扩展通过 NuGet 引用依赖，本机仅有 runtime，没有可用的 .NET SDK。请安装 SDK 后重试：https://dotnet.microsoft.com/download"
                        requested[0].FileName requested[0].Line |]
                | ProbeUnavailable ->
                    return failed [| failure "EXT0035"
                        "无法通过 dotnet --list-sdks 确认可用 SDK。请安装或修复 .NET SDK 后重试：https://dotnet.microsoft.com/download"
                        requested[0].FileName requested[0].Line |]
                | SdkIncompatible _ ->
                    return failed [| failure "EXT0037"
                        ("此扩展通过 NuGet 引用依赖，现有 SDK 无法支持当前宿主。请安装 .NET SDK "
                         + string Environment.Version.Major
                         + " 或更新版本后重试：https://dotnet.microsoft.com/download")
                        requested[0].FileName requested[0].Line |]
                | SdkAvailable sdkHost ->
                    // The compiler sidecar serializes requests; override only its own process so
                    // FCS invokes the same SDK host that this preflight verified, and so the
                    // ephemeral restore project does not inherit ancestor MSBuild customization.
                    Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", sdkHost)
                    NugetResolverPreflight.applyRestoreIsolationEnvironment()
                    return! resolveWithFcs(checker, sourceRoot, outputRoot, sources, requested, inputs.Sources, token)
    }

    let resolveAsync(checker: FSharpChecker, sourceRoot: string, outputRoot: string,
                     sources: IReadOnlyDictionary<string, string>, token: CancellationToken) =
        resolveWithSdkProbeAsync(checker, sourceRoot, outputRoot, sources,
            Func<_, _>(NugetResolverPreflight.ensureSdkAsync), token)
