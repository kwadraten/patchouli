namespace Patchouli.Extensions

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open Patchouli.Core.Ids
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

[<Sealed>]
type ExtensionCompiler(checkRoot: string) =
    // Keep the cache-path static field free of FCS types. The lazy lambda and its FCS call are
    // reached only through CompileDllMissAsync, a NoInlining backend.
    static let sharedChecker = lazy (box (FSharpChecker.Create()))
    static let contractAssemblies =
        [| typeof<ITranslationExtension>.Assembly.Location
           typeof<DocumentInstanceId>.Assembly.Location
           typeof<ExtensionCompilationProcess>.Assembly.Location
           typeof<int option>.Assembly.Location |]

    static let frameworkReferences = lazy (RuntimeFrameworkReferences.paths())

    static let referencedDlls (package: ExtensionPackageSnapshot) =
        package.Sources
        |> Seq.collect (fun pair ->
            Regex.Matches(pair.Value, "(?m)^\\s*#r\\s+@?\"([^\"]+\\.dll)\"", RegexOptions.CultureInvariant)
            |> Seq.cast<Match>
            |> Seq.map (fun matched -> matched.Groups[1].Value.Replace('\\', '/')))
        |> Seq.distinct
        |> Seq.toArray

    static let resultFor (succeeded: bool) (fields: ConfigFieldDescriptor array) (diagnostics: ExtensionDiagnostic array) =
        ExtensionCheckResult(succeeded, fields, diagnostics)

    static let diagnostic (sourceRoot: string) (item: FSharpDiagnostic) =
        let severity =
            match item.Severity with
            | FSharpDiagnosticSeverity.Error -> ExtensionDiagnosticSeverity.Error
            | FSharpDiagnosticSeverity.Warning -> ExtensionDiagnosticSeverity.Warning
            | _ -> ExtensionDiagnosticSeverity.Information
        let file =
            try
                let candidate =
                    if String.IsNullOrWhiteSpace item.FileName then Path.Combine(sourceRoot, "main.fsx")
                    elif Path.IsPathRooted item.FileName then Path.GetFullPath item.FileName
                    else Path.GetFullPath(Path.Combine(sourceRoot, item.FileName))
                let relative = Path.GetRelativePath(sourceRoot, candidate).Replace('\\', '/')
                if relative = ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted relative then
                    "main.fsx"
                else ExtensionPackageSnapshot.NormalizeSourcePath(relative)
            with _ -> "main.fsx"
        ExtensionDiagnostic(severity, item.ErrorNumberText, item.Message,
            ExtensionSourceLocation(file, max 1 item.StartLine, item.StartColumn, max 1 item.EndLine, item.EndColumn))

    static let metadataCheck (package: ExtensionPackageSnapshot) =
        try
            let metadata = ExtensionEntryMetadata.Parse(package)
            if metadata.HostApi <> ExtensionHostApi.Current then
                Error [| ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0007",
                    "The package targets an unsupported Patchouli extension host API.",
                    ExtensionSourceLocation("main.fsx", 1, 0, 1, 1)) |]
            else Ok metadata
        with :? ExtensionMetadataException as error ->
            Error [| ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, error.Code, error.Message,
                ExtensionSourceLocation(error.FileName, error.Line, error.Column, error.Line, error.Column + 1)) |]

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member private CompileDllMissAsync(sourceRoot: string, arguments: string array, cancellationToken: CancellationToken) = task {
        cancellationToken.ThrowIfCancellationRequested()
        let checker = unbox<FSharpChecker> sharedChecker.Value
        let! rawDiagnostics, exitCode = checker.Compile(arguments) |> Async.StartAsTask
        cancellationToken.ThrowIfCancellationRequested()
        let diagnostics = rawDiagnostics |> Array.map (diagnostic sourceRoot)
        let errors = diagnostics |> Array.exists (fun issue -> issue.Severity = ExtensionDiagnosticSeverity.Error)
        let exitSucceeded = match box exitCode with | :? int as code -> code = 0 | null -> true | _ -> false
        return diagnostics, not errors && exitSucceeded
    }

    /// Manifest-only fast path; no FSharpChecker is instantiated or called.
    static member TryReadCachedCompilation(package: ExtensionPackageSnapshot, artifactRoot: string) : ExtensionCompilationResult option =
        ExtensionArtifact.tryReadCached package artifactRoot (ExtensionArtifact.runtimeToolchainIdentity())

    /// Manifest-only artifact lookup. The cache-hit path has no FCS references in its signature
    /// or implementation and never enters the no-inline compiler backend.
    static member CompileAsync(package: ExtensionPackageSnapshot, artifactRoot: string,
                               cancellationToken: CancellationToken) : Task<ExtensionCompilationResult> = backgroundTask {
        cancellationToken.ThrowIfCancellationRequested()
        let root = Path.GetFullPath artifactRoot
        let identity = ExtensionArtifact.runtimeToolchainIdentity()
        match ExtensionArtifact.tryReadCached package root identity with
        | Some cached -> return cached
        | None -> return! ExtensionCompiler.CompileMissAsync(package, root, cancellationToken)
    }

    /// Build a missing artifact. Isolated from the SDK fast path and shared by one compiler server.
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member private CompileMissAsync(package: ExtensionPackageSnapshot, artifactRoot: string,
                                           cancellationToken: CancellationToken) : Task<ExtensionCompilationResult> = backgroundTask {
        cancellationToken.ThrowIfCancellationRequested()
        match metadataCheck package with
        | Error issues -> return ExtensionCompilationResult(resultFor false [||] issues, "", "", false)
        | Ok _ ->
            let root = Path.GetFullPath artifactRoot
            let packageDlls = referencedDlls package
            let unresolved =
                packageDlls
                |> Array.filter (fun relative ->
                    let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                    not (package.BinaryFiles.ContainsKey normalized)
                    && not (package.BinaryFiles.ContainsKey(relative)))
            if unresolved.Length > 0 then
                let issues = unresolved |> Array.map (fun path ->
                    ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0030",
                        "#r DLL must resolve to a package-local binary dependency: " + path,
                        ExtensionSourceLocation("main.fsx", 1, 0, 1, 1)))
                return ExtensionCompilationResult(resultFor false [||] issues, "", "", false)
            else
                let referencePaths = Array.append frameworkReferences.Value contractAssemblies
                let sourceDependencyHash = ExtensionArtifact.sourceHash package
                let toolchainIdentity = ExtensionArtifact.runtimeToolchainIdentity()
                let artifactKey = ExtensionArtifact.artifactKey package toolchainIdentity
                let artifactDirectory = Path.Combine(root, artifactKey)
                let assemblyPath = ExtensionArtifact.assemblyPath artifactDirectory
                Directory.CreateDirectory root |> ignore
                let staging = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"))
                let sourceRoot = Path.Combine(staging, "src")
                let outputRoot = Path.Combine(staging, "bundle")
                Directory.CreateDirectory sourceRoot |> ignore
                Directory.CreateDirectory outputRoot |> ignore
                try
                    for KeyValue(relative, content) in package.Sources do
                        cancellationToken.ThrowIfCancellationRequested()
                        let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                        let destination = Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar))
                        match Path.GetDirectoryName destination with
                        | null -> invalidOp "Extension source has no parent directory."
                        | parent -> Directory.CreateDirectory(parent) |> ignore
                        File.WriteAllText(destination, content)
                    for KeyValue(relative, content) in package.BinaryFiles do
                        cancellationToken.ThrowIfCancellationRequested()
                        let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                        let destination = Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar))
                        match Path.GetDirectoryName destination with
                        | null -> invalidOp "Extension binary dependency has no parent directory."
                        | parent -> Directory.CreateDirectory(parent) |> ignore
                        File.WriteAllBytes(destination, content.ToArray())
                    let entry = Path.Combine(sourceRoot, "main.fsx")
                    if not (File.Exists entry) then
                        let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0001",
                            "Package is missing main.fsx.", ExtensionSourceLocation("main.fsx", 1, 0, 1, 1))
                        return ExtensionCompilationResult(resultFor false [||] [| issue |], artifactDirectory, assemblyPath, false)
                    else
                        let output = ExtensionArtifact.assemblyPath outputRoot
                        let checker = unbox<FSharpChecker> sharedChecker.Value
                        let! dependencyResolution = ExtensionDependencies.resolveAsync(checker, sourceRoot, outputRoot, package.Sources, cancellationToken)
                        if not dependencyResolution.Succeeded then
                            return ExtensionCompilationResult(
                                resultFor false [||] dependencyResolution.Diagnostics,
                                artifactDirectory, assemblyPath, false)
                        else
                            for index = 0 to dependencyResolution.FingerprintEntries.Length - 1 do
                                let relative, bytes = dependencyResolution.FingerprintEntries[index]
                                let fingerprintName =
                                    SHA256.HashData(Encoding.UTF8.GetBytes relative)
                                    |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
                                let fingerprintPath = Path.Combine(outputRoot, ".dependency-fingerprint", fingerprintName)
                                Directory.CreateDirectory(Path.GetDirectoryName fingerprintPath |> nonNull) |> ignore
                                File.WriteAllBytes(fingerprintPath, bytes)
                            let packageReferences =
                                packageDlls
                                |> Array.map (fun relative ->
                                    let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                                    Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar)))
                            let loadedReferences = Array.concat [ referencePaths; packageReferences; dependencyResolution.CompilerReferences ]
                            let projectCompilerOptions =
                                dependencyResolution.CompilerOptions
                                |> Array.filter (fun option ->
                                    not (option.StartsWith("-r:", StringComparison.Ordinal))
                                    && not (option.StartsWith("--reference:", StringComparison.Ordinal)))
                            let contractLine = "\nlet __patchouli_extension_contract : Patchouli.Extensions.Contracts.IExtensionDefinition = extension\n"
                            File.AppendAllText(entry, contractLine)
                            let compilerArgs =
                                Array.concat [
                                    [| "fsc.exe" |]
                                    projectCompilerOptions
                                    [| "-o:" + output; "-a"; "--noframework"; "--targetprofile:netcore"
                                       "--langversion:10.0"; "--checknulls+"; "--warnaserror+"; "--optimize+"; "--crossoptimize-"; "--debug-"; "--nologo" |]
                                    loadedReferences |> Array.map (fun path -> "-r:" + path)
                                    [| entry |]
                                ]
                            let! diagnostics, compileSucceeded = ExtensionCompiler.CompileDllMissAsync(sourceRoot, compilerArgs, cancellationToken)
                            if not compileSucceeded || not (File.Exists output) then
                                return ExtensionCompilationResult(resultFor false [||] diagnostics, artifactDirectory, assemblyPath, false)
                            else
                                let schema =
                                    try Ok(ConfigSettings.ReadSchema(output, loadedReferences) |> Seq.toArray)
                                    with error -> Error error
                                match schema with
                                | Error error ->
                                    let safeMessage = error.Message.Replace(staging, ".", StringComparison.OrdinalIgnoreCase)
                                    let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXTCFG001",
                                        "The compiled extension settings schema could not be read. Ensure the package exports one [<Settings>] settings record and every property has a supported [<Field>] or [<Model>] declaration. " + safeMessage,
                                        ExtensionSourceLocation("main.fsx", 1, 0, 1, 1))
                                    return ExtensionCompilationResult(resultFor false [||] [| issue |], artifactDirectory, assemblyPath, false)
                                | Ok fields ->
                                    let check = resultFor true fields diagnostics
                                    let assemblyHash = SHA256.HashData(File.ReadAllBytes output) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
                                    // Preserve exact package binary dependencies in the artifact bundle.
                                    for KeyValue(relative, _) in package.BinaryFiles do
                                        let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                                        let sourcePath = Path.Combine(sourceRoot, normalized.Replace('/', Path.DirectorySeparatorChar))
                                        let destination = Path.Combine(outputRoot, normalized.Replace('/', Path.DirectorySeparatorChar))
                                        match Path.GetDirectoryName destination with
                                        | null -> invalidOp "A package dependency has no output directory."
                                        | parent -> Directory.CreateDirectory(parent) |> ignore
                                        File.Copy(sourcePath, destination, true)
                                    let manifest =
                                        let dependencyFiles =
                                            Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
                                            |> Seq.filter (fun path -> not (String.Equals(path, output, StringComparison.OrdinalIgnoreCase)))
                                            |> Seq.map (fun path ->
                                                let relative = Path.GetRelativePath(outputRoot, path).Replace('\\', '/')
                                                let hash = SHA256.HashData(File.ReadAllBytes path) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
                                                relative + "|" + hash)
                                            |> Seq.toArray
                                        { SchemaVersion = 1; PackageId = package.Id; PackageFingerprint = package.Fingerprint
                                          ToolchainIdentity = toolchainIdentity; AssemblyFile = ExtensionArtifact.AssemblyFileName
                                          AssemblySha256 = assemblyHash; SourceAndDependencySha256 = sourceDependencyHash
                                          DependencyFiles = dependencyFiles
                                          CheckResultJson = JsonSerializer.Serialize(check) }
                                    ExtensionArtifact.writeManifest outputRoot manifest
                                    cancellationToken.ThrowIfCancellationRequested()
                                    if Directory.Exists artifactDirectory then Directory.Delete(artifactDirectory, true)
                                    Directory.Move(outputRoot, artifactDirectory)
                                    return ExtensionCompilationResult(check, artifactDirectory, assemblyPath, false)
                finally
                    if Directory.Exists staging then Directory.Delete(staging, true)
    }

    static member CompileAsync(package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) =
        ExtensionCompiler.CompileAsync(package, Path.Combine(AppContext.BaseDirectory, ".compiled"), cancellationToken)

    member _.CheckAsync(package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) : Task<ExtensionCheckResult> = task {
        let compileRoot =
            match Path.GetDirectoryName(Path.GetFullPath checkRoot) with
            | null -> Path.Combine(AppContext.BaseDirectory, ".compiled")
            | parent -> Path.Combine(parent, ".compiled")
        let! result = ExtensionCompiler.CompileAsync(package, compileRoot, cancellationToken)
        return result.CheckResult
    }

    interface IExtensionSourceChecker with
        member this.CheckAsync(package, cancellationToken) = this.CheckAsync(package, cancellationToken)

    new() = ExtensionCompiler(Path.Combine(AppContext.BaseDirectory, ".check"))
