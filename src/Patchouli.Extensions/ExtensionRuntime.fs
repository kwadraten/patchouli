namespace Patchouli.Extensions

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Reflection
open System.Runtime.Loader
open Patchouli.Core.Ids
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

/// Storage boundary for source packages and user configuration.
type IExtensionFileStore =
    abstract ReadSourcesAsync: id: string * CancellationToken -> Task<IReadOnlyDictionary<string, string>>
    abstract WriteSourcesAsync: id: string * sources: IReadOnlyDictionary<string, string> * CancellationToken -> Task
    abstract ReadConfigAsync: id: string * CancellationToken -> Task<ExtensionConfigSnapshot>
    abstract WriteConfigAsync: id: string * config: ExtensionConfigSnapshot * CancellationToken -> Task

type IPersistentExtensionFileStore =
    inherit IExtensionFileStore
    abstract PersistSnapshotAsync: ExtensionPackageSnapshot * CancellationToken -> Task
    abstract ReadSnapshotAsync: id: string * fingerprint: string * CancellationToken -> Task<ExtensionPackageSnapshot option>
    abstract ReadActiveFingerprintAsync: id: string * CancellationToken -> Task<string option>
    abstract ReadLastKnownGoodFingerprintAsync: id: string * CancellationToken -> Task<string option>
    abstract SaveStateAsync: id: string * activeFingerprint: string option * lkgFingerprint: string option * CancellationToken -> Task

/// Optional storage support for an independently verified Last Known Good copy.
type ILastKnownGoodExtensionFileStore =
    abstract PersistLastKnownGoodSnapshotAsync: ExtensionPackageSnapshot * CancellationToken -> Task
    abstract ReadLastKnownGoodSnapshotAsync: id: string * fingerprint: string * CancellationToken -> Task<ExtensionPackageSnapshot option>

type IPackageExtensionFileStore =
    abstract ReadPackageAsync: id: string * CancellationToken -> Task<ExtensionPackageSnapshot>
    abstract WritePackageAsync: ExtensionPackageSnapshot * CancellationToken -> Task

/// One compiled extension loaded in a collectible context.
type IExtensionGeneration =
    inherit IAsyncDisposable
    abstract Translation: ITranslationExtension

/// Injectable loader to isolate evaluation and generation lifecycle from manager state transitions.
type IExtensionGenerationLoader =
    abstract LoadAsync: ExtensionPackageSnapshot * CancellationToken -> Task<IExtensionGeneration>

/// A loader that consumes the host's completed static check without evaluating declarations again.
type ICheckedExtensionGenerationLoader =
    abstract LoadCheckedAsync: ExtensionPackageSnapshot * ExtensionCheckResult * CancellationToken -> Task<IExtensionGeneration>

/// Optional compiler result hand-off that avoids checking and compiling the same package twice.
/// Optional generation loader that consumes the exact artifact produced by its source checker.
type ICompiledExtensionGenerationLoader =
    abstract LoadCompiledAsync: ExtensionPackageSnapshot * ExtensionCompilationResult * CancellationToken -> Task<IExtensionGeneration>

/// Optional newer contract carried by generations that export more than translation.
type IExtensionGenerationDefinition =
    abstract Definition: IExtensionDefinition

[<Sealed>]
type FileExtensionStore(runtimeDirectory: string) =
    let root = Path.GetFullPath runtimeDirectory
    let jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
    let validateId id =
        if String.IsNullOrWhiteSpace id || not (Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)) then
            invalidArg "id" "Extension id must be a stable alphanumeric identifier."
    let directory id = validateId id; Path.Combine(root, "extensions", id)
    let generationDirectory id fingerprint =
        if String.IsNullOrWhiteSpace fingerprint ||
           not (Regex.IsMatch(fingerprint, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) then
            invalidArg "fingerprint" "Extension fingerprints must be lowercase BLAKE3 values."
        Path.Combine(directory id, ".generations", fingerprint)
    let lastKnownGoodDirectory id fingerprint =
        if String.IsNullOrWhiteSpace fingerprint ||
           not (Regex.IsMatch(fingerprint, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) then
            invalidArg "fingerprint" "Extension fingerprints must be lowercase BLAKE3 values."
        Path.Combine(directory id, ".lkg", fingerprint)
    let statePath id = Path.Combine(directory id, "extension-state.json")
    let createParentDirectory (path: string) =
        match Path.GetDirectoryName path with
        | null -> invalidArg "path" "The target path must have a parent directory."
        | parent -> Directory.CreateDirectory(parent) |> ignore
    let isBinaryPath (path: string) = not (ExtensionPackageSnapshot.IsTextSourcePath(path))
    let readState id = task {
        let path = statePath id
        if not (File.Exists path) then return (None, None)
        else
            let! text = File.ReadAllTextAsync(path)
            match JsonSerializer.Deserialize<Dictionary<string, string>>(text, jsonOptions) |> Option.ofObj with
            | None -> return (None, None)
            | Some state ->
                let get key = match state.TryGetValue(key) with | true, value when not (String.IsNullOrWhiteSpace value) -> Some value | _ -> None
                if get "schemaVersion" <> Some "1" then
                    return (None, None)
                else return get "activeFingerprint", get "lastKnownGoodFingerprint"
    }
    let readPackageAt id fingerprint path = task {
        if not (Directory.Exists path) then return None
        else
            let sources = Dictionary<string, string>(StringComparer.Ordinal)
            let binaryFiles = Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            for filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) do
                let relative = Path.GetRelativePath(path, filePath).Replace('\\', '/')
                if isBinaryPath relative then
                    let! content = File.ReadAllBytesAsync(filePath)
                    binaryFiles[relative] <- ReadOnlyMemory<byte>(content)
                else
                    let! content = File.ReadAllTextAsync(filePath)
                    sources[relative] <- content
            let package = ExtensionPackageSnapshot(id, sources, binaryFiles)
            return if package.Fingerprint = fingerprint then Some package else None
    }
    let readPackage id fingerprint = readPackageAt id fingerprint (generationDirectory id fingerprint)
    let readLastKnownGoodPackage id fingerprint =
        readPackageAt id fingerprint (lastKnownGoodDirectory id fingerprint)
    let ensureLastKnownGoodGuidance id = task {
        let guidancePath = Path.Combine(directory id, ".lkg", "AGENTS.md")
        if not (File.Exists guidancePath) then
            createParentDirectory guidancePath
            try
                do! File.WriteAllTextAsync(guidancePath,
                    "# Last Known Good snapshots\n\nThis directory is managed by Patchouli. Do not edit or repair snapshots here. Edit extension sources in the package root and use Patchouli recovery to restore a verified snapshot.\n",
                    CancellationToken.None)
            with :? IOException when File.Exists guidancePath -> ()
    }
    interface IExtensionFileStore with
        member _.ReadSourcesAsync(id, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let basePath = directory id
            let sources = Dictionary<string, string>(StringComparer.Ordinal)
            if Directory.Exists basePath then
                for path in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories) do
                    let relative = Path.GetRelativePath(basePath, path).Replace('\\', '/')
                    if not (relative.StartsWith(".generations/", StringComparison.Ordinal))
                        && not (relative.StartsWith(".lkg/", StringComparison.OrdinalIgnoreCase))
                        && relative <> "extension-state.json" && relative <> "config.json"
                         && not (relative.EndsWith(".tmp", StringComparison.Ordinal))
                         && not (isBinaryPath relative) then
                        let! content = File.ReadAllTextAsync(path, cancellationToken)
                        sources[relative] <- content
            return sources :> IReadOnlyDictionary<string, string>
        }
        member _.WriteSourcesAsync(id, sources, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let target = directory id
            Directory.CreateDirectory target |> ignore
            let normalizedSources = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            for KeyValue(relative, content) in sources do
                let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                if normalized.StartsWith(".generations/", StringComparison.OrdinalIgnoreCase)
                   || normalized.StartsWith(".lkg/", StringComparison.OrdinalIgnoreCase)
                   || normalized.Equals("extension-state.json", StringComparison.OrdinalIgnoreCase)
                   || normalized.Equals("config.json", StringComparison.OrdinalIgnoreCase) then
                    invalidArg "sources" ("Invalid extension source path: " + relative)
                if not (normalizedSources.TryAdd(normalized, content)) then
                    invalidArg "sources" ("Duplicate extension source path after normalization: " + normalized)
            if Directory.Exists target then
                for existingPath in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories) do
                    let relative = Path.GetRelativePath(target, existingPath).Replace('\\', '/')
                    if not (relative.StartsWith(".generations/", StringComparison.Ordinal))
                        && not (relative.StartsWith(".lkg/", StringComparison.OrdinalIgnoreCase))
                        && relative <> "extension-state.json" && relative <> "config.json"
                         && not (relative.EndsWith(".tmp", StringComparison.Ordinal))
                         && not (normalizedSources.ContainsKey(relative)) && not (isBinaryPath relative) then
                        File.Delete(existingPath)
            for KeyValue(normalized, content) in normalizedSources do
                let path = Path.Combine(target, normalized.Replace('/', Path.DirectorySeparatorChar))
                createParentDirectory path
                let temporary = path + ".tmp"
                do! File.WriteAllTextAsync(temporary, content, cancellationToken)
                File.Move(temporary, path, true)
        }
        member _.ReadConfigAsync(id, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let path = Path.Combine(directory id, "config.json")
            if not (File.Exists path) then return ExtensionConfigSnapshot(Dictionary<string, string>() :> IReadOnlyDictionary<string, string>, 0L)
            else
                let! json = File.ReadAllTextAsync(path, cancellationToken)
                let parsed = JsonSerializer.Deserialize<ExtensionConfigSnapshot>(json, jsonOptions)
                return Option.ofObj parsed |> Option.defaultValue (ExtensionConfigSnapshot(Dictionary<string, string>() :> IReadOnlyDictionary<string, string>, 0L))
        }
        member _.WriteConfigAsync(id, config, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let target = directory id
            Directory.CreateDirectory target |> ignore
            let temporary = Path.Combine(target, "config.json.tmp")
            do! File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(config), cancellationToken)
            File.Move(temporary, Path.Combine(target, "config.json"), true)
        }

    interface IPersistentExtensionFileStore with
        member _.PersistSnapshotAsync(package, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let target = generationDirectory package.Id package.Fingerprint
            let! current = readPackage package.Id package.Fingerprint
            if current.IsNone then
                createParentDirectory target
                let staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp"
                let backup = target + "." + Guid.NewGuid().ToString("N") + ".bak"
                try
                    Directory.CreateDirectory staging |> ignore
                    for KeyValue(relative, content) in package.Sources do
                        cancellationToken.ThrowIfCancellationRequested()
                        let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                        let path = Path.Combine(staging, normalized.Replace('/', Path.DirectorySeparatorChar))
                        createParentDirectory path
                        do! File.WriteAllTextAsync(path, content, cancellationToken)
                    for KeyValue(relative, content) in package.BinaryFiles do
                        cancellationToken.ThrowIfCancellationRequested()
                        let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                        let path = Path.Combine(staging, normalized.Replace('/', Path.DirectorySeparatorChar))
                        createParentDirectory path
                        do! File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken)
                    let! verified = readPackageAt package.Id package.Fingerprint staging
                    if verified.IsNone then
                        invalidOp "The staged extension snapshot failed its content fingerprint check."
                    cancellationToken.ThrowIfCancellationRequested()
                    let hadTarget = Directory.Exists target
                    if hadTarget then Directory.Move(target, backup)
                    try
                        Directory.Move(staging, target)
                    with error ->
                        if hadTarget && Directory.Exists backup && not (Directory.Exists target) then
                            Directory.Move(backup, target)
                        raise error
                    if Directory.Exists backup then Directory.Delete(backup, true)
                finally
                    if Directory.Exists staging then Directory.Delete(staging, true)
        }
        member _.ReadSnapshotAsync(id, fingerprint, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            return! readPackage id fingerprint
        }
        member _.ReadActiveFingerprintAsync(id, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let! (activeFingerprint, _) = readState id
            return activeFingerprint
        }
        member _.ReadLastKnownGoodFingerprintAsync(id, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let! (_, lkgFingerprint) = readState id
            return lkgFingerprint
        }
        member _.SaveStateAsync(id, activeFingerprint, lkgFingerprint, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let target = directory id
            Directory.CreateDirectory target |> ignore
            let values = Dictionary<string, string>()
            values["schemaVersion"] <- "1"
            values["hostApi"] <- ExtensionHostApi.Current
            activeFingerprint |> Option.iter (fun value -> values["activeFingerprint"] <- value)
            lkgFingerprint |> Option.iter (fun value -> values["lastKnownGoodFingerprint"] <- value)
            let temporary = statePath id + ".tmp"
            do! File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(values), cancellationToken)
            File.Move(temporary, statePath id, true)
        }
    interface ILastKnownGoodExtensionFileStore with
        member _.PersistLastKnownGoodSnapshotAsync(package, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            do! ensureLastKnownGoodGuidance package.Id
            let target = lastKnownGoodDirectory package.Id package.Fingerprint
            if Directory.Exists target then
                let! existing = readPackageAt package.Id package.Fingerprint target
                if existing.IsNone then
                    invalidOp "The existing Last Known Good snapshot is corrupt; it was preserved."
            else
                createParentDirectory target
                let staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp"
                try
                    Directory.CreateDirectory staging |> ignore
                    for KeyValue(relative, content) in package.Sources do
                        cancellationToken.ThrowIfCancellationRequested()
                        let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                        let path = Path.Combine(staging, normalized.Replace('/', Path.DirectorySeparatorChar))
                        createParentDirectory path
                        do! File.WriteAllTextAsync(path, content, cancellationToken)
                    for KeyValue(relative, content) in package.BinaryFiles do
                        cancellationToken.ThrowIfCancellationRequested()
                        let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                        let path = Path.Combine(staging, normalized.Replace('/', Path.DirectorySeparatorChar))
                        createParentDirectory path
                        do! File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken)
                    let! verified = readPackageAt package.Id package.Fingerprint staging
                    if verified.IsNone then
                        invalidOp "The staged Last Known Good snapshot failed its content fingerprint check."
                    cancellationToken.ThrowIfCancellationRequested()
                    try
                        Directory.Move(staging, target)
                    with :? IOException when Directory.Exists target ->
                        let! raced = readPackageAt package.Id package.Fingerprint target
                        if raced.IsNone then
                            invalidOp "A concurrent Last Known Good snapshot is corrupt; it was preserved."
                finally
                    if Directory.Exists staging then Directory.Delete(staging, true)
        }
        member _.ReadLastKnownGoodSnapshotAsync(id, fingerprint, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            return! readLastKnownGoodPackage id fingerprint
        }
    interface IPackageExtensionFileStore with
        member this.WritePackageAsync(package, cancellationToken) = task {
            do! (this :> IExtensionFileStore).WriteSourcesAsync(package.Id, package.Sources, cancellationToken)
            for KeyValue(relative, content) in package.BinaryFiles do
                let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                let path = Path.Combine(directory package.Id, normalized.Replace('/', Path.DirectorySeparatorChar))
                createParentDirectory path
                let staging = path + ".tmp"
                do! File.WriteAllBytesAsync(staging, content.ToArray(), cancellationToken)
                File.Move(staging, path, true)
        }
        member _.ReadPackageAsync(id, cancellationToken) = task {
            cancellationToken.ThrowIfCancellationRequested()
            let basePath = directory id
            let sources = Dictionary<string, string>(StringComparer.Ordinal)
            let binaryFiles = Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            if Directory.Exists basePath then
                for path in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories) do
                    let relative = Path.GetRelativePath(basePath, path).Replace('\\', '/')
                    if not (relative.StartsWith(".generations/", StringComparison.Ordinal))
                       && not (relative.StartsWith(".lkg/", StringComparison.OrdinalIgnoreCase))
                       && relative <> "extension-state.json" && relative <> "config.json"
                       && not (relative.EndsWith(".tmp", StringComparison.Ordinal)) then
                        if isBinaryPath relative then
                            let! content = File.ReadAllBytesAsync(path, cancellationToken)
                            binaryFiles[relative] <- ReadOnlyMemory<byte>(content)
                        else
                            let! content = File.ReadAllTextAsync(path, cancellationToken)
                            sources[relative] <- content
            return ExtensionPackageSnapshot(id, sources, binaryFiles)
        }

[<Sealed>]
type private ExtensionLoadContext(artifactDirectory: string) =
    inherit AssemblyLoadContext("patchouli-extension-" + Guid.NewGuid().ToString("N"), true)
    let sharedNames = HashSet<string>(StringComparer.OrdinalIgnoreCase)
    let defaultAssemblies = Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
    let managedDependencies = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    let nativeDependencies = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    do
        for frameworkReference in RuntimeFrameworkReferences.paths() do
            match Path.GetFileNameWithoutExtension(frameworkReference) |> Option.ofObj with
            | Some simpleName when not (String.IsNullOrWhiteSpace simpleName) -> sharedNames.Add(simpleName) |> ignore
            | _ -> ()
        for assembly in
            [| typeof<Patchouli.Core.Ids.DocumentInstanceId>.Assembly
               typeof<IExtensionDefinition>.Assembly
               typeof<ExtensionCompilationProcess>.Assembly
               typeof<int option>.Assembly |] do
            match Option.ofObj (assembly.GetName().Name) with
            | Some name -> sharedNames.Add(name) |> ignore
            | None -> ()
        for assembly in AssemblyLoadContext.Default.Assemblies do
            match Option.ofObj (assembly.GetName().Name) with
            | Some name -> defaultAssemblies.TryAdd(name, assembly) |> ignore
            | None -> ()
        if Directory.Exists artifactDirectory then
            for path in Directory.EnumerateFiles(artifactDirectory, "*", SearchOption.AllDirectories) |> Seq.sort do
                let extension = Path.GetExtension(path) |> Option.ofObj |> Option.defaultValue "" |> fun value -> value.ToLowerInvariant()
                let fileName = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue ""
                let simpleName = Path.GetFileNameWithoutExtension(path) |> Option.ofObj |> Option.defaultValue ""
                if extension = ".dll" && not (String.IsNullOrWhiteSpace simpleName) then
                    managedDependencies.TryAdd(simpleName, Path.GetFullPath path) |> ignore
                if extension = ".dll" || extension = ".so" || extension = ".dylib" then
                    if not (String.IsNullOrWhiteSpace fileName) then nativeDependencies.TryAdd(fileName, Path.GetFullPath path) |> ignore
                    if not (String.IsNullOrWhiteSpace simpleName) then
                        nativeDependencies.TryAdd(simpleName, Path.GetFullPath path) |> ignore
                        if simpleName.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && simpleName.Length > 3 then
                            nativeDependencies.TryAdd(simpleName.Substring(3), Path.GetFullPath path) |> ignore
    override this.Load(assemblyName: AssemblyName) =
        match Option.ofObj assemblyName.Name with
        | None -> null
        | Some name ->
            if sharedNames.Contains name then
                match defaultAssemblies.TryGetValue name with
                | true, assembly -> assembly
                | _ -> null
            else
                match managedDependencies.TryGetValue name with
                | true, path -> this.LoadFromAssemblyPath path
                | _ ->
                    match defaultAssemblies.TryGetValue name with
                    | true, assembly -> assembly
                    | _ -> null
    override this.LoadUnmanagedDll(name: string) =
        let fileName = Path.GetFileName(name) |> Option.ofObj |> Option.defaultValue name
        let withoutExtension = Path.GetFileNameWithoutExtension(fileName) |> Option.ofObj |> Option.defaultValue fileName
        match nativeDependencies.TryGetValue name with
        | true, path -> this.LoadUnmanagedDllFromPath path
        | _ ->
            match nativeDependencies.TryGetValue fileName with
            | true, path -> this.LoadUnmanagedDllFromPath path
            | _ ->
                match nativeDependencies.TryGetValue withoutExtension with
                | true, path -> this.LoadUnmanagedDllFromPath path
                | _ -> IntPtr.Zero

[<Sealed>]
type private CompiledExtensionGeneration(packageId: string, context: AssemblyLoadContext, rawExtension: IExtensionDefinition,
                                         definition: IExtensionDefinition) =
    let mutable context = Some context
    let mutable rawExtension = Some rawExtension
    let mutable definition = Some definition
    let current () =
        match definition with
        | Some value -> value
        | None -> raise (ObjectDisposedException(nameof CompiledExtensionGeneration))
    interface IExtensionGeneration with
        member _.Translation =
            match current () with
            | :? ITranslationExtension as value -> value
            | _ -> invalidOp "This generation does not contain a translation extension."
        member _.DisposeAsync() =
            ValueTask(task {
                match rawExtension with
                | Some (:? IAsyncDisposable as value) ->
                    try do! value.DisposeAsync().AsTask()
                    finally
                        rawExtension <- None
                        definition <- None
                        match context with
                        | Some value -> ExtensionUnload.unload packageId value; context <- None
                        | None -> ()
                | Some (:? IDisposable as value) ->
                    try value.Dispose()
                    finally
                        rawExtension <- None
                        definition <- None
                        match context with
                        | Some value -> ExtensionUnload.unload packageId value; context <- None
                        | None -> ()
                | _ ->
                    rawExtension <- None
                    definition <- None
                    match context with
                    | Some value -> ExtensionUnload.unload packageId value; context <- None
                    | None -> ()
            })
    interface IExtensionGenerationDefinition with
        member _.Definition = current ()

[<Sealed>]
type CompiledExtensionGenerationLoader(runtimeDirectory: string) =
    let compilationProcess = ExtensionCompilationProcess(runtimeDirectory)
    let artifactRoot = Path.Combine(Path.GetFullPath runtimeDirectory, ".compiled")
    let load (package: ExtensionPackageSnapshot) (result: ExtensionCompilationResult) (token: CancellationToken) = task {
        token.ThrowIfCancellationRequested()
        if not result.CheckResult.Succeeded then invalidOp "Cannot load an extension whose static compilation failed."
        if not (File.Exists result.AssemblyPath) then
            invalidOp "The compiled extension assembly is missing from its artifact bundle."
        let context = ExtensionLoadContext(result.ArtifactDirectory)
        try
            use stream = File.OpenRead result.AssemblyPath
            let assembly = context.LoadFromStream stream
            let metadata = ExtensionEntryMetadata.Parse(package)
            let exports =
                assembly.GetTypes()
                |> Array.collect (fun candidate ->
                    candidate.GetProperties(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)
                    |> Array.filter (fun property -> property.Name = "__patchouli_extension_contract"))
            if exports.Length <> 1 then
                invalidOp "Compiled assembly must contain exactly one compiler-generated entrypoint."
            let export =
                match exports[0].GetValue(null) with
                | :? IExtensionDefinition as value -> value
                | _ -> invalidOp "Compiled assembly has no typed extension definition."
            let fields = result.CheckResult.ConfigFields
            let projected =
                match export with
                | :? ITranslationExtension as extension ->
                    { new ITranslationExtension with
                        member _.Descriptor = metadata.ToDescriptor()
                        member _.ConfigFields = fields
                        member _.ClearDerivedStateAsync(token) = extension.ClearDerivedStateAsync(token)
                        member _.RunAsync(job, resources, token) =
                            ExecutionScope.run job.Info.Options.Concurrency (Job.progress job) token (fun () -> task {
                                do! extension.RunAsync(job, resources, token)
                                return () })
                      interface IExtensionSettingsDefinition with
                        member _.SettingsType =
                            match extension with
                            | :? IExtensionSettingsDefinition as settings -> settings.SettingsType
                            | _ -> typeof<obj> } :> IExtensionDefinition
                | :? IMetadataExtension as extension ->
                    { new IMetadataExtension with
                        member _.Descriptor = metadata.ToDescriptor()
                        member _.ConfigFields = fields
                        member _.SourceDefinition = extension.SourceDefinition
                        member _.LookupAsync(identifier, config, resources, token) =
                            extension.LookupAsync(identifier, config, resources, token)
                      interface IExtensionSettingsDefinition with
                        member _.SettingsType =
                            match extension with
                            | :? IExtensionSettingsDefinition as settings -> settings.SettingsType
                            | _ -> typeof<obj> } :> IExtensionDefinition
                | :? IOcrExtension as extension ->
                    { new IOcrExtension with
                        member _.Descriptor = metadata.ToDescriptor()
                        member _.ConfigFields = fields
                        member _.RecognizeAsync(context, resources, token) = extension.RecognizeAsync(context, resources, token)
                      interface IExtensionSettingsDefinition with
                        member _.SettingsType =
                            match extension with
                            | :? IExtensionSettingsDefinition as settings -> settings.SettingsType
                            | _ -> typeof<obj> } :> IExtensionDefinition
                | _ -> invalidOp "Compiled extension kind is not supported by this host API."
            return CompiledExtensionGeneration(package.Id, context, export, projected) :> IExtensionGeneration
        with error ->
            context.Unload()
            return raise error
    }
    member _.CompileAsync(package, token) = compilationProcess.CompileAsync(package, token)
    interface ICompiledExtensionSourceChecker with
        member this.CompileAsync(package, token) = this.CompileAsync(package, token)
    interface IExtensionSourceChecker with
        member this.CheckAsync(package, token) = task {
            let! compilation = this.CompileAsync(package, token)
            return compilation.CheckResult
        }
    interface ICompiledExtensionGenerationLoader with
        member _.LoadCompiledAsync(package, compilation, token) = load package compilation token
    interface IExtensionGenerationLoader with
        member _.LoadAsync(package, token) = task {
            let! result = compilationProcess.CompileAsync(package, token)
            if not result.CheckResult.Succeeded then
                let message = result.CheckResult.Diagnostics |> Seq.map (fun issue -> issue.Message) |> String.concat Environment.NewLine
                return raise (InvalidOperationException(message))
            else return! load package result token
        }
    interface ICheckedExtensionGenerationLoader with
        member _.LoadCheckedAsync(package, checkResult, token) = task {
            if not checkResult.Succeeded then invalidOp "Cannot load an extension whose static check failed."
            let result =
                ExtensionArtifact.tryReadCached package artifactRoot (ExtensionArtifact.runtimeToolchainIdentity())
                |> Option.defaultWith (fun () -> invalidOp "The checked extension has no cached compiled artifact; recheck it before loading.")
            return! load package result token
        }

[<Sealed>]
type private ActiveGeneration(package: ExtensionPackageSnapshot, generation: IExtensionGeneration,
                              definition: IExtensionDefinition, descriptor: ExtensionDescriptor,
                              configFields: IReadOnlyList<ConfigFieldDescriptor>, config: ExtensionConfigSnapshot) =
    let gate = obj()
    let mutable leases = 0
    let mutable retired = false
    let mutable disposed = false
    let mutable disposalCompleted = false
    let mutable generationReference = Some generation
    let mutable definitionReference = Some definition
    let mutable configSnapshot = config
    let frozenConfigFields = Array.AsReadOnly(configFields |> Seq.toArray)
    let releaseGeneration () = task {
        try
            try
                match generationReference with
                | Some value ->
                    do! ExtensionDiagnostics.segmentTask "extension.generation.release" package.Id package.Fingerprint
                            (fun () -> task { do! value.DisposeAsync().AsTask() })
                | None -> ()
            with error ->
                System.Diagnostics.Trace.WriteLine(
                    "Extension resources could not be fully released (" + error.GetType().Name + "). Restart the application if old code remains active.")
        finally
            lock gate (fun () ->
                generationReference <- None
                definitionReference <- None
                disposalCompleted <- true)
    }
    member _.Package = package
    member _.Definition =
        match definitionReference with
        | Some value -> value
        | None -> raise (ObjectDisposedException(nameof ActiveGeneration))
    member _.Descriptor = descriptor
    member _.ConfigFields = frozenConfigFields
    member _.Config = lock gate (fun () -> configSnapshot)
    member _.UpdateConfig(value) = lock gate (fun () -> configSnapshot <- value)
    member _.LeaseCount = lock gate (fun () -> leases)
    member _.IsDisposed = lock gate (fun () -> disposalCompleted)
    member _.Acquire() = lock gate (fun () ->
        if retired then invalidOp "Extension generation has been retired."
        leases <- leases + 1)
    member _.RetireAsync() = task {
        let dispose = lock gate (fun () ->
            retired <- true
            if leases = 0 && not disposed then
                disposed <- true
                true
            else false)
        if dispose then
            do! releaseGeneration ()
    }
    member _.ReleaseAsync() = task {
        let dispose = lock gate (fun () ->
            leases <- max 0 (leases - 1)
            if retired && leases = 0 && not disposed then
                disposed <- true
                true
            else false)
        if dispose then
            do! releaseGeneration ()
    }

[<Sealed>]
type private ExtensionLease(active: ActiveGeneration) =
    let mutable disposed = 0
    let config = active.Config
    let mutable generation = Some active
    let current () =
        match generation with
        | Some value -> value
        | None -> raise (ObjectDisposedException(nameof ExtensionLease))
    do active.Acquire()
    interface IExtensionLease with
        member _.Descriptor = (current ()).Descriptor
        member _.Fingerprint = (current ()).Package.Fingerprint
        member _.Config = config
        member _.ConfigFields = (current ()).ConfigFields
        member _.Definition = (current ()).Definition
        member _.Translation =
            match (current ()).Definition with
            | :? ITranslationExtension as extension -> extension
            | _ -> invalidOp "This lease does not contain a translation extension."
        member _.Metadata =
            match (current ()).Definition with
            | :? IMetadataExtension as extension -> Some extension
            | _ -> None
            |> Option.toObj
        member _.Ocr =
            match (current ()).Definition with
            | :? IOcrExtension as extension -> Some extension
            | _ -> None
            |> Option.toObj
        member _.DisposeAsync() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                let value = current ()
                generation <- None
                ValueTask(value.ReleaseAsync())
            else ValueTask.CompletedTask

[<Sealed>]
type ExtensionManager(runtimeDirectory: string, capabilities: IExtensionResources,
                      fileStore: IExtensionFileStore, generationLoader: IExtensionGenerationLoader,
                      sourceChecker: IExtensionSourceChecker) =
    let gate = new SemaphoreSlim(1, 1)
    let active = Dictionary<string, ActiveGeneration>(StringComparer.Ordinal)
    let lastKnownGood = Dictionary<string, string>(StringComparer.Ordinal)
    let staged = Dictionary<string, ActiveGeneration>(StringComparer.Ordinal)
    let draining = ResizeArray<ActiveGeneration>()
    let diagnostics = ConcurrentDictionary<string, IReadOnlyList<ExtensionDiagnostic>>(StringComparer.Ordinal)
    let keyedGates = ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal)
    let checkSync = obj()
    let mutable checkStopping = false
    let mutable activeOperations = 0
    let mutable operationDrain: TaskCompletionSource<unit> option = None
    let mutable compiledCleanupRequested = false
    let mutable disposed = false

    let cleanUnretainedCompiledArtifacts () = task {
        try
            let compiledRoot = Path.Combine(Path.GetFullPath runtimeDirectory, ".compiled")
            if Directory.Exists compiledRoot then
                let identity = ExtensionArtifact.runtimeToolchainIdentity()
                let manifests =
                    Directory.EnumerateDirectories(compiledRoot)
                    |> Seq.choose (fun path -> ExtensionArtifact.tryRead path |> Option.map (fun manifest -> path, manifest))
                    |> Seq.toArray
                let persistedLastKnownGood = HashSet<string>(StringComparer.Ordinal)
                let failedLastKnownGoodReads = HashSet<string>(StringComparer.Ordinal)
                match fileStore with
                | :? IPersistentExtensionFileStore as persistent ->
                    for packageId in manifests |> Array.map (fun (_, manifest) -> manifest.PackageId) |> Array.distinct do
                        try
                            let! fingerprint = persistent.ReadLastKnownGoodFingerprintAsync(packageId, CancellationToken.None)
                            fingerprint |> Option.iter (fun value -> persistedLastKnownGood.Add(value) |> ignore)
                        with error ->
                            failedLastKnownGoodReads.Add(packageId) |> ignore
                            System.Diagnostics.Trace.TraceWarning(
                                "Could not read the retained extension fingerprint ({0}); leaving its artifact untouched.",
                                error.GetType().Name)
                | _ -> ()

                let cleanupCompleted = lock checkSync (fun () ->
                    if activeOperations <> 0 then
                        compiledCleanupRequested <- true
                        false
                    elif not (gate.Wait(0)) then
                        compiledCleanupRequested <- true
                        false
                    else
                        try
                            let protectedFingerprints = HashSet<string>(persistedLastKnownGood, StringComparer.Ordinal)
                            let protectedDirectories = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            let protectPackageArtifact (package: ExtensionPackageSnapshot) =
                                let key = ExtensionArtifact.artifactKey package identity
                                protectedDirectories.Add(Path.GetFullPath(Path.Combine(compiledRoot, key))) |> ignore
                            try
                                for generation in active.Values do protectPackageArtifact generation.Package
                                for generation in staged.Values do protectPackageArtifact generation.Package
                                for generation in draining do
                                    if not generation.IsDisposed then protectPackageArtifact generation.Package
                                for KeyValue(_, fingerprint) in lastKnownGood do protectedFingerprints.Add(fingerprint) |> ignore
                                for manifestPath, manifest in manifests do
                                    if failedLastKnownGoodReads.Contains(manifest.PackageId) then
                                        protectedFingerprints.Add(manifest.PackageFingerprint) |> ignore
                                    if manifest.ToolchainIdentity = identity
                                       && protectedFingerprints.Contains(manifest.PackageFingerprint) then
                                        protectedDirectories.Add(Path.GetFullPath manifestPath) |> ignore
                            finally gate.Release() |> ignore

                            let mutable deletionFailed = false
                            for path, _ in manifests do
                                if not (protectedDirectories.Contains(Path.GetFullPath path)) then
                                    try Directory.Delete(path, true)
                                    with error ->
                                        deletionFailed <- true
                                        System.Diagnostics.Trace.TraceWarning(
                                            "Could not remove an unretained compiled extension artifact ({0}); it will be retried later.",
                                            error.GetType().Name)
                            if deletionFailed then compiledCleanupRequested <- true
                            true
                        with error ->
                            compiledCleanupRequested <- true
                            System.Diagnostics.Trace.TraceWarning(
                                "Compiled extension cache cleanup failed ({0}); the active extension remains unchanged.",
                                error.GetType().Name)
                            false)
                if not cleanupCompleted then ()
        with error ->
            lock checkSync (fun () -> compiledCleanupRequested <- true)
            System.Diagnostics.Trace.TraceWarning(
                "Compiled extension cache cleanup failed ({0}); the active extension remains unchanged.",
                error.GetType().Name)
    }

    let beginCheck () =
        lock checkSync (fun () ->
            if checkStopping then false
            else
                if activeOperations = 0 then
                    operationDrain <- Some(TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
                activeOperations <- activeOperations + 1
                true)

    let endCheck () =
        let completed, queueCleanup = lock checkSync (fun () ->
            activeOperations <- activeOperations - 1
            if activeOperations = 0 then
                let drain = operationDrain
                operationDrain <- None
                let queue = compiledCleanupRequested
                compiledCleanupRequested <- false
                drain, queue
            else None, false)
        if queueCleanup then
            Task.Run(Func<Task>(fun () -> cleanUnretainedCompiledArtifacts () :> Task)) |> ignore
        completed |> Option.iter (fun drain -> drain.TrySetResult(()) |> ignore)

    let loadFailure (error: exn) =
        match error with
        | _ -> ExtensionCheckResult(false, [||], [| ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error,
                "EXT0099", "Extension initialization failed: " + error.Message,
                ExtensionSourceLocation("main.fsx", 1, 0, 1, 1)) |])

    let runTrackedOperation (closedResult: 'T) (operation: unit -> Task<'T>) : Task<'T> = task {
        if not (beginCheck ()) then
            return closedResult
        else
            try return! operation ()
            finally endCheck ()
    }

    let rememberFailure packageId (result: ExtensionCheckResult) =
        diagnostics[packageId] <- result.Diagnostics
        result

    let resolveDefinition (generation: IExtensionGeneration) = task {
        try
            let definition =
                match generation with
                | :? IExtensionGenerationDefinition as typed -> typed.Definition
                | _ -> generation.Translation :> IExtensionDefinition
            return definition, definition.Descriptor
        with error ->
            do! generation.DisposeAsync().AsTask()
            return raise error
    }

    let loadCoreWhenAlive (package: ExtensionPackageSnapshot) (cancellationToken: CancellationToken) = task {
        let! (check, compilation) = task {
            try
                return! ExtensionDiagnostics.segmentTaskResult "extension.manager.check" package.Id package.Fingerprint
                            (fun (value: ExtensionCheckResult * ExtensionCompilationResult option) ->
                                let check, _ = value
                                seq { "extension.check.succeeded", (if check.Succeeded then "true" else "false") })
                            (fun () -> task {
                                match sourceChecker with
                                | :? ICompiledExtensionSourceChecker as compiledChecker ->
                                    let! result = compiledChecker.CompileAsync(package, cancellationToken)
                                    return result.CheckResult, Some result
                                | _ ->
                                    let! result = sourceChecker.CheckAsync(package, cancellationToken)
                                    return result, None
                            })
            with
            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested -> return raise error
            | error -> return (loadFailure error |> rememberFailure package.Id), None
        }
        if not check.Succeeded then
            diagnostics[package.Id] <- check.Diagnostics
            return check
        else
            let! capacityAvailable = task {
                do! ExtensionDiagnostics.segmentTask "extension.manager.gate" package.Id package.Fingerprint
                        (fun () -> task { do! gate.WaitAsync(cancellationToken) })
                try
                    draining.RemoveAll(Predicate<ActiveGeneration>(fun value -> value.IsDisposed)) |> ignore
                    // Staged generations have no leases; release their process before reserving a replacement.
                    match staged.TryGetValue(package.Id) with
                    | true, previous ->
                        do! previous.RetireAsync()
                        staged.Remove(package.Id) |> ignore
                    | _ -> ()
                    // The per-package keyed gate reserves capacity before a new process is started.
                    // A retired generation may continue running under outstanding leases. It
                    // does not prevent staging or atomically activating its replacement.
                    return true
                finally gate.Release() |> ignore
            }
            if not capacityAvailable then
                return loadFailure (InvalidOperationException("Extension activation capacity is unavailable.")) |> rememberFailure package.Id
            else
              let! persistError = task {
                  try
                      match fileStore with
                      | :? IPersistentExtensionFileStore as persistent ->
                          do! ExtensionDiagnostics.segmentTask "extension.manager.persist" package.Id package.Fingerprint
                                  (fun () -> task { do! persistent.PersistSnapshotAsync(package, cancellationToken) })
                      | _ -> ()
                      return None
                  with
                  | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested -> return raise error
                  | error -> return Some error
              }
              match persistError with
              | Some error -> return loadFailure error |> rememberFailure package.Id
              | None ->
                  let! generationResult = task {
                      try
                          let! value =
                              ExtensionDiagnostics.segmentTask "extension.manager.load" package.Id package.Fingerprint
                                  (fun () ->
                                       match compilation, generationLoader with
                                       | Some compiled, (:? ICompiledExtensionGenerationLoader as compiledLoader) ->
                                           compiledLoader.LoadCompiledAsync(package, compiled, cancellationToken)
                                       | Some compiled, (:? ICheckedExtensionGenerationLoader as checkedLoader) ->
                                           checkedLoader.LoadCheckedAsync(package, compiled.CheckResult, cancellationToken)
                                       | None, (:? ICheckedExtensionGenerationLoader as checkedLoader) ->
                                           checkedLoader.LoadCheckedAsync(package, check, cancellationToken)
                                       | _ -> generationLoader.LoadAsync(package, cancellationToken))
                          return Choice1Of2 value
                      with
                      | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested -> return raise error
                      | error -> return Choice2Of2 error
                  }
                  match generationResult with
                  | Choice2Of2 error -> return loadFailure error |> rememberFailure package.Id
                  | Choice1Of2 generation ->
                      let! (definition, descriptor) =
                          ExtensionDiagnostics.segmentTask "extension.manager.descriptor" package.Id package.Fingerprint
                              (fun () -> resolveDefinition generation)
                      let metadata = ExtensionEntryMetadata.Parse(package)
                      let expectedKind = metadata.Kind
                      let kindMatches =
                          descriptor.Kind = expectedKind
                          && (expectedKind <> ExtensionKind.Metadata || definition :? IMetadataExtension)
                          && (expectedKind <> ExtensionKind.Ocr || definition :? IOcrExtension)
                          && (expectedKind <> ExtensionKind.Translation || definition :? ITranslationExtension)
                      if descriptor.Id <> metadata.Id || descriptor.HostApi <> metadata.HostApi
                          || descriptor.Name <> metadata.Name || descriptor.Description <> metadata.Summary
                          || descriptor.Version <> metadata.Version || not kindMatches then
                          do! generation.DisposeAsync().AsTask()
                          let code, message, file =
                              if descriptor.Id <> metadata.Id then "EXT0002", "The exported extension id must match entry metadata.", "main.fsx"
                              elif descriptor.HostApi <> metadata.HostApi then "EXT0007", "The extension targets an unsupported Patchouli host API.", "main.fsx"
                              elif not kindMatches then "EXT0009", "The extension implementation kind must match entry metadata.", "main.fsx"
                              else "EXT0008", "The exported descriptor must be projected from entry metadata.", "main.fsx"
                          let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, code, message,
                              ExtensionSourceLocation(file, 1, 0, 1, 1))
                          let failed = ExtensionCheckResult(false, check.ConfigFields,
                              Seq.append check.Diagnostics [ issue ] |> Seq.toArray)
                          return rememberFailure package.Id failed
                      else
                          let! configResult = task {
                              try
                                  let! value =
                                      ExtensionDiagnostics.segmentTask "extension.manager.config" package.Id package.Fingerprint
                                          (fun () -> fileStore.ReadConfigAsync(package.Id, cancellationToken))
                                  return Choice1Of2 value
                              with
                              | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                                  do! generation.DisposeAsync().AsTask()
                                  return raise error
                              | error ->
                                  do! generation.DisposeAsync().AsTask()
                                  return Choice2Of2 error
                          }
                          match configResult with
                          | Choice2Of2 error -> return loadFailure error |> rememberFailure package.Id
                          | Choice1Of2 config ->
                              let candidate = ActiveGeneration(package, generation, definition, descriptor, check.ConfigFields, config)
                              let! gateResult = task {
                                  try
                                      do! ExtensionDiagnostics.segmentTask "extension.manager.gate" package.Id package.Fingerprint
                                              (fun () -> task { do! gate.WaitAsync(cancellationToken) })
                                      return Ok ()
                                  with error -> return Error error
                              }
                              match gateResult with
                              | Error error ->
                                  do! candidate.RetireAsync()
                                  return raise error
                              | Ok () ->
                                  try
                                      if disposed then
                                          do! candidate.RetireAsync()
                                          return loadFailure (ObjectDisposedException(nameof ExtensionManager))
                                      else
                                          match staged.TryGetValue(package.Id) with
                                          | true, previous -> do! previous.RetireAsync()
                                          | _ -> ()
                                          staged[package.Id] <- candidate
                                          diagnostics[package.Id] <- check.Diagnostics
                                          return check
                                  finally gate.Release() |> ignore
      }
    let loadCoreUnsafe package cancellationToken = task {
        if disposed then return loadFailure (ObjectDisposedException(nameof ExtensionManager))
        else return! loadCoreWhenAlive package cancellationToken
    }

    let loadCore package cancellationToken = task {
        try
            return! loadCoreUnsafe package cancellationToken
        with
        | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested -> return raise error
        | error -> return loadFailure error |> rememberFailure package.Id
    }

    new(runtimeDirectory: string, capabilities: IExtensionResources,
        fileStore: IExtensionFileStore, generationLoader: IExtensionGenerationLoader) =
        let sourceChecker =
            match generationLoader with
            | :? IExtensionSourceChecker as checker -> checker
            | _ -> ExtensionCompilationProcess(runtimeDirectory) :> IExtensionSourceChecker
        ExtensionManager(runtimeDirectory, capabilities, fileStore, generationLoader, sourceChecker)

    new(runtimeDirectory: string, capabilities: IExtensionResources) =
        let loader = CompiledExtensionGenerationLoader(runtimeDirectory)
        ExtensionManager(runtimeDirectory, capabilities, FileExtensionStore(runtimeDirectory) :> IExtensionFileStore,
            loader :> IExtensionGenerationLoader, loader :> IExtensionSourceChecker)

    member _.CheckAsync(package, cancellationToken) =
        runTrackedOperation (loadFailure (ObjectDisposedException(nameof ExtensionManager)))
            (fun () -> sourceChecker.CheckAsync(package, cancellationToken))

    member _.LoadAsync(package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) =
        runTrackedOperation (loadFailure (ObjectDisposedException(nameof ExtensionManager))) (fun () -> task {
            let keyedGate = keyedGates.GetOrAdd(package.Id, fun _ -> new SemaphoreSlim(1, 1))
            do! ExtensionDiagnostics.segmentTask "extension.manager.keyed-gate" package.Id package.Fingerprint
                    (fun () -> task { do! keyedGate.WaitAsync(cancellationToken) })
            try return! loadCore package cancellationToken
            finally keyedGate.Release() |> ignore
        })

    member _.ActivateAsync(id: string, package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) =
      runTrackedOperation (loadFailure (ObjectDisposedException(nameof ExtensionManager))) (fun () -> task {
        if id <> package.Id then invalidArg "id" "Activation id must match the package id."
        let keyedGate = keyedGates.GetOrAdd(id, fun _ -> new SemaphoreSlim(1, 1))
        do! ExtensionDiagnostics.segmentTask "extension.manager.keyed-gate" id package.Fingerprint
                (fun () -> task { do! keyedGate.WaitAsync(cancellationToken) })
        try
            let! result = loadCore package cancellationToken
            if not result.Succeeded then return result
            else
                do! gate.WaitAsync(cancellationToken)
                try
                    if disposed then return loadFailure (ObjectDisposedException(nameof ExtensionManager))
                    else
                        match staged.TryGetValue(id) with
                        | true, candidate when candidate.Package.Fingerprint = package.Fingerprint ->
                            let! savedLkg =
                                match fileStore with
                                | :? IPersistentExtensionFileStore as persistent ->
                                    persistent.ReadLastKnownGoodFingerprintAsync(id, cancellationToken)
                                | _ -> Task.FromResult(None)
                            let lkg =
                                match lastKnownGood.TryGetValue(id), savedLkg with
                                | (true, value), _ -> Some value
                                | _, Some value -> Some value
                                | _ -> None
                            match fileStore with
                            | :? IPersistentExtensionFileStore as persistent ->
                                // Persisting the pointer is the activation commit point; finish the in-memory swap after it.
                                do! ExtensionDiagnostics.segmentTask "extension.manager.persist" id package.Fingerprint
                                        (fun () -> task { do! persistent.SaveStateAsync(id, Some candidate.Package.Fingerprint, lkg, CancellationToken.None) })
                            | _ -> ()
                            savedLkg |> Option.iter (fun value -> lastKnownGood[id] <- value)
                            let previous = match active.TryGetValue(id) with | true, value -> Some value | _ -> None
                            active[id] <- candidate
                            staged.Remove(id) |> ignore
                            lock checkSync (fun () -> compiledCleanupRequested <- true)
                            match previous with
                            | Some value ->
                                draining.Add(value)
                                do! value.RetireAsync()
                            | None -> ()
                            return result
                        | _ -> return loadFailure (InvalidOperationException("The staged extension generation changed before activation."))
                finally gate.Release() |> ignore
        finally keyedGate.Release() |> ignore
      })

    member _.SaveSourceAsync(id, sources, cancellationToken) = fileStore.WriteSourcesAsync(id, sources, cancellationToken)
    member _.SavePackageAsync(package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) = task {
        match fileStore with
        | :? IPackageExtensionFileStore as packageStore -> do! packageStore.WritePackageAsync(package, cancellationToken)
        | _ when package.BinaryFiles.Count = 0 -> do! fileStore.WriteSourcesAsync(package.Id, package.Sources, cancellationToken)
        | _ -> invalidOp "This extension store cannot persist binary package dependencies."
    }
    member _.ReadSourceAsync(id, cancellationToken) = task {
        match fileStore with
        | :? IPackageExtensionFileStore as packageStore -> return! packageStore.ReadPackageAsync(id, cancellationToken)
        | _ ->
            let! sources = fileStore.ReadSourcesAsync(id, cancellationToken)
            return ExtensionPackageSnapshot(id, sources)
    }
    member _.SaveConfigAsync(id: string, config: ExtensionConfigSnapshot, cancellationToken: CancellationToken) = task {
        if isNull (box config) then nullArg "config"
        if config.Values.Keys |> Seq.exists (fun key -> key.StartsWith("__host.", StringComparison.Ordinal)) then
            invalidArg "config" "The __host.* configuration namespace is reserved for host-managed values."
        let frozen = ExtensionConfigSnapshot(config.Values, config.Revision)
        let keyedGate = keyedGates.GetOrAdd(id, fun _ -> new SemaphoreSlim(1, 1))
        do! keyedGate.WaitAsync(cancellationToken)
        try
            do! fileStore.WriteConfigAsync(id, frozen, cancellationToken)
            do! gate.WaitAsync(CancellationToken.None)
            try
                match active.TryGetValue(id) with
                | true, current -> current.UpdateConfig(frozen)
                | _ -> ()
            finally gate.Release() |> ignore
        finally keyedGate.Release() |> ignore
    }
    member _.ReadConfigAsync(id, cancellationToken) = fileStore.ReadConfigAsync(id, cancellationToken)
    member this.RestoreAsync(id: string, cancellationToken: CancellationToken) = task {
        match fileStore with
        | :? IPersistentExtensionFileStore as persistent ->
            let! activeFingerprint = persistent.ReadActiveFingerprintAsync(id, cancellationToken)
            let! activePackage =
                match activeFingerprint with
                | Some value -> persistent.ReadSnapshotAsync(id, value, cancellationToken)
                | None -> Task.FromResult(None)
            let! activeRestored =
                match activePackage with
                | None -> Task.FromResult(false)
                | Some saved -> task {
                    let! result = this.ActivateAsync(id, saved, cancellationToken)
                    return result.Succeeded
                }
            if activeRestored then return true
            else
                let! lkgFingerprint = persistent.ReadLastKnownGoodFingerprintAsync(id, cancellationToken)
                match lkgFingerprint with
                | None -> return false
                | Some value ->
                    let! package =
                        match fileStore with
                        | :? ILastKnownGoodExtensionFileStore as lastKnownGoodStore ->
                            lastKnownGoodStore.ReadLastKnownGoodSnapshotAsync(id, value, cancellationToken)
                        | _ -> persistent.ReadSnapshotAsync(id, value, cancellationToken)
                    match package with
                    | None -> return false
                    | Some saved ->
                        let! result = this.ActivateAsync(id, saved, cancellationToken)
                        return result.Succeeded
        | _ -> return false
    }
    member _.PromoteLastKnownGoodAsync(id: string, cancellationToken: CancellationToken) = task {
        do! gate.WaitAsync(cancellationToken)
        try
            match active.TryGetValue(id), fileStore with
            | (true, current), (:? IPersistentExtensionFileStore as persistent) ->
                match fileStore with
                | :? ILastKnownGoodExtensionFileStore as lastKnownGoodStore ->
                    do! lastKnownGoodStore.PersistLastKnownGoodSnapshotAsync(current.Package, cancellationToken)
                | _ -> ()
                do! persistent.SaveStateAsync(id, Some current.Package.Fingerprint,
                    Some current.Package.Fingerprint, CancellationToken.None)
                lastKnownGood[id] <- current.Package.Fingerprint
                return true
            | _ -> return false
        finally gate.Release() |> ignore
    }
    member this.RollbackAsync(id: string, cancellationToken: CancellationToken) = task {
        match fileStore with
        | :? IPersistentExtensionFileStore as persistent ->
            let! fingerprint = persistent.ReadLastKnownGoodFingerprintAsync(id, cancellationToken)
            match fingerprint with
            | None -> return false
            | Some value ->
                let! package =
                    match fileStore with
                    | :? ILastKnownGoodExtensionFileStore as lastKnownGoodStore ->
                        lastKnownGoodStore.ReadLastKnownGoodSnapshotAsync(id, value, cancellationToken)
                    | _ -> persistent.ReadSnapshotAsync(id, value, cancellationToken)
                match package with
                | None -> return false
                | Some saved ->
                    let! result = this.ActivateAsync(id, saved, cancellationToken)
                    return result.Succeeded
        | _ -> return false
    }
    member _.AcquireAsync(id: string, cancellationToken: CancellationToken) = ValueTask<IExtensionLease>(task {
        do! gate.WaitAsync(cancellationToken)
        try
            match disposed, active.TryGetValue(id) with
            | true, _ -> return Unchecked.defaultof<IExtensionLease>
            | false, (true, generation) -> return ExtensionLease(generation) :> IExtensionLease
            | _ -> return Unchecked.defaultof<IExtensionLease>
        finally gate.Release() |> ignore
    })
    member _.List() =
        gate.Wait()
        try
            active
            |> Seq.map (fun pair ->
                let generation = pair.Value
                let isLastKnownGood =
                    match lastKnownGood.TryGetValue(pair.Key) with
                    | true, fingerprint -> fingerprint = generation.Package.Fingerprint
                    | _ -> false
                ExtensionGenerationInfo(pair.Key, generation.Package.Fingerprint, true,
                    isLastKnownGood,
                    generation.LeaseCount, (match diagnostics.TryGetValue(pair.Key) with | true, items -> items | _ -> [||])))
            |> Seq.toArray
        finally
            gate.Release() |> ignore

    interface IExtensionManager with
        member this.CheckAsync(package, token) = this.CheckAsync(package, token)
        member this.LoadAsync(package, token) = this.LoadAsync(package, token)
        member this.ActivateAsync(id, package, token) = this.ActivateAsync(id, package, token)
        member this.SaveSourceAsync(id, sources, token) = this.SaveSourceAsync(id, sources, token)
        member this.SavePackageAsync(package, token) = this.SavePackageAsync(package, token)
        member this.ReadSourceAsync(id, token) = this.ReadSourceAsync(id, token)
        member this.AcquireAsync(id, token) = this.AcquireAsync(id, token)
        member this.List() = this.List()
        member this.SaveConfigAsync(id, config, token) = this.SaveConfigAsync(id, config, token)
        member this.ReadConfigAsync(id, token) = this.ReadConfigAsync(id, token)
        member this.RestoreAsync(id, token) = this.RestoreAsync(id, token)
        member this.PromoteLastKnownGoodAsync(id, token) = this.PromoteLastKnownGoodAsync(id, token)
        member this.RollbackAsync(id, token) = this.RollbackAsync(id, token)
        member _.DisposeAsync() =
            task {
                let operationDrainTask =
                    lock checkSync (fun () ->
                        checkStopping <- true
                        if activeOperations = 0 then None
                        else operationDrain |> Option.map (fun drain -> drain.Task))
                match operationDrainTask with
                | Some pending -> do! pending
                | None -> ()
                do! gate.WaitAsync()
                let generations =
                    try
                        if disposed then [||]
                        else
                            disposed <- true
                            Array.concat [ active.Values |> Seq.toArray; staged.Values |> Seq.toArray; draining |> Seq.toArray ]
                    finally gate.Release() |> ignore
                for generation in generations do do! generation.RetireAsync()
                // The manager owns its loader. Shutdown ends worker processes even if an external lease is still held;
                // ordinary reload retirement above continues to wait for leases.
                match generationLoader with
                | :? IAsyncDisposable as ownedLoader -> do! ownedLoader.DisposeAsync().AsTask()
                | _ -> ()
            } |> ValueTask
