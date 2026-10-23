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

type private ExtensionNugetDirective =
    { PackageId: string
      Version: string
      FileName: string
      Line: int }

module ExtensionDependencies =
    // FCS does the actual hash-directive parsing and dependency resolution. This scanner only
    // records requested package identity and source locations for preflight diagnostics.
    let private directive =
        Regex("(?m)^\\s*#r\\s+@?\"nuget:\\s*(?<id>[^,\"\\r\\n]+?)(?:\\s*,\\s*(?<version>[^\"\\r\\n]*?))?\\s*\"",
              RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)

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

    let private parseDirectives (sources: IReadOnlyDictionary<string, string>) =
        sources
        |> Seq.collect (fun pair ->
            directive.Matches(pair.Value)
            |> Seq.cast<Match>
            |> Seq.map (fun matched ->
                let line = 1 + pair.Value.Substring(0, matched.Index).Split('\n').Length - 1
                { PackageId = matched.Groups["id"].Value.Trim()
                  Version = if matched.Groups["version"].Success then matched.Groups["version"].Value.Trim() else ""
                  FileName = pair.Key
                  Line = line }))
        |> Seq.toArray

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
                               token: CancellationToken) : Task<ExtensionDependencyResolution> = task {
        let entry = Path.Combine(Path.GetFullPath sourceRoot, "main.fsx")
        match sources.TryGetValue "main.fsx" with
        | false, _ ->
            return failed [| failure "EXT0031" "NuGet directives require a main.fsx entry script." "main.fsx" 1 |]
        | true, source ->
            try
                token.ThrowIfCancellationRequested()
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

    let resolveAsync(checker: FSharpChecker, sourceRoot: string, outputRoot: string,
                     sources: IReadOnlyDictionary<string, string>, token: CancellationToken) : Task<ExtensionDependencyResolution> = task {
        token.ThrowIfCancellationRequested()
        let requested = parseDirectives sources
        if requested.Length = 0 then
            return ExtensionDependencyResolution([||], [||], [||], [||], true)
        else
            let invalidVersions =
                requested
                |> Array.filter (fun item -> String.IsNullOrWhiteSpace item.PackageId || not (exactVersion item.Version))
                |> Array.map (fun item ->
                    failure "EXT0032"
                        "#r nuget requires a fixed NuGet version (for example 1.2.3); missing versions, floating versions and ranges are not supported."
                        item.FileName item.Line)
            if invalidVersions.Length > 0 then return failed invalidVersions
            else
                let! probe = NugetResolverPreflight.ensureSdkAsync token
                match probe with
                | DotnetMissing ->
                    return failed [| failure "EXT0033"
                        "#r nuget restore requires a .NET SDK, but dotnet was not found. Install the .NET SDK or include the DLL in the package and use #r \"lib/...\"."
                        requested[0].FileName requested[0].Line |]
                | RuntimeOnly _ ->
                    return failed [| failure "EXT0034"
                        "The selected dotnet host has no usable .NET SDK (runtime-only installation). Install a .NET SDK or include the DLL in the package and use #r \"lib/...\"."
                        requested[0].FileName requested[0].Line |]
                | ProbeUnavailable ->
                    return failed [| failure "EXT0035"
                        "Could not confirm an installed .NET SDK using dotnet --list-sdks. Install or repair the .NET SDK, or include the DLL in the package and use #r \"lib/...\"."
                        requested[0].FileName requested[0].Line |]
                | SdkAvailable sdkHost ->
                    // The compiler sidecar serializes requests; override only its own process so
                    // FCS invokes the same SDK host that this preflight verified, and so the
                    // ephemeral restore project does not inherit ancestor MSBuild customization.
                    Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", sdkHost)
                    NugetResolverPreflight.applyRestoreIsolationEnvironment()
                    return! resolveWithFcs(checker, sourceRoot, outputRoot, sources, requested, token)
    }
