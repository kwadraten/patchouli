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
open FSharp.Compiler.Interactive.Shell
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

/// One evaluated FSI generation. Its session is disposed only after it is retired and its leases drain.
type IExtensionGeneration =
    inherit IAsyncDisposable
    abstract Translation: ITranslationExtension

/// Injectable loader to isolate evaluation and generation lifecycle from manager state transitions.
type IExtensionGenerationLoader =
    abstract LoadAsync: ExtensionPackageSnapshot * CancellationToken -> Task<IExtensionGeneration>

/// A loader that consumes the host's completed static check without evaluating declarations again.
type ICheckedExtensionGenerationLoader =
    abstract LoadCheckedAsync: ExtensionPackageSnapshot * ExtensionCheckResult * CancellationToken -> Task<IExtensionGeneration>

/// Optional newer contract carried by generations that export more than translation.
type IExtensionGenerationDefinition =
    abstract Definition: IExtensionDefinition

[<Sealed>]
type private FsiInitializationException(diagnostics: ExtensionDiagnostic array) =
    inherit Exception(String.Join(Environment.NewLine,
        diagnostics |> Array.map (fun issue ->
            let location = issue.Location
            sprintf "%s(%d,%d): %s %s" location.FileName location.Line location.Column
                issue.Code issue.Message)))
    member _.Diagnostics = diagnostics

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
            for filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) do
                let relative = Path.GetRelativePath(path, filePath).Replace('\\', '/')
                let! content = File.ReadAllTextAsync(filePath)
                sources[relative] <- content
            let package = ExtensionPackageSnapshot(id, sources)
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
                        && not (relative.EndsWith(".tmp", StringComparison.Ordinal)) then
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
                        && not (normalizedSources.ContainsKey(relative)) then
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

[<Sealed>]
type FsiExtensionGenerationLoader(runtimeDirectory: string) =
    let createParentDirectory (path: string) =
        match Path.GetDirectoryName path with
        | null -> invalidArg "path" "The target path must have a parent directory."
        | parent -> Directory.CreateDirectory(parent) |> ignore
    let disposeExtensionAndSession (extension: obj) (session: FsiEvaluationSession) =
        ValueTask(task {
            try
                match extension with
                | :? IAsyncDisposable as asyncDisposable -> do! asyncDisposable.DisposeAsync().AsTask()
                | :? IDisposable as disposable -> disposable.Dispose()
                | _ -> ()
            finally
                (session :> IDisposable).Dispose()
        })
    new() = FsiExtensionGenerationLoader(Path.Combine(Path.GetTempPath(), "patchouli-extension-generations"))
    interface IExtensionGenerationLoader with
        member _.LoadAsync(package, cancellationToken) = backgroundTask {
            cancellationToken.ThrowIfCancellationRequested()
            match package.Sources.TryGetValue("main.fsx") with
            | false, _ -> return raise (InvalidOperationException("Extension package is missing main.fsx."))
            | true, source ->
                let generationPath = Path.Combine(Path.GetFullPath runtimeDirectory, package.Id, package.Fingerprint)
                Directory.CreateDirectory generationPath |> ignore
                for KeyValue(relative, content) in package.Sources do
                    let normalized = ExtensionPackageSnapshot.NormalizeSourcePath(relative)
                    let path = Path.Combine(generationPath, normalized.Replace('/', Path.DirectorySeparatorChar))
                    createParentDirectory path
                    if not (File.Exists path) then do! File.WriteAllTextAsync(path, content, cancellationToken)
                    else
                        let! existing = File.ReadAllTextAsync(path, cancellationToken)
                        if not (String.Equals(existing, content, StringComparison.Ordinal)) then
                            invalidOp "A generation staging path contains different source content."
                let output = new StringWriter()
                let error = new StringWriter()
                let configuration = FsiEvaluationSession.GetDefaultConfiguration()
                let arguments =
                    Array.append
                        [| "fsi.exe"; "--noninteractive"; "--nologo"; "--readline-"; "--gui-"
                           "--langversion:10.0"; "--checknulls+"; "--multiemit-"
                           "--reference:" + typeof<ITranslationExtension>.Assembly.Location
                           "--reference:" + typeof<DocumentInstanceId>.Assembly.Location
                           "--reference:" + typeof<ExtensionCompiler>.Assembly.Location
                           "--usesdkrefs-" |]
                        (RuntimeFrameworkReferences.compilerFlags())
                // FSI's default multi-emit path loads byte arrays into the non-collectible ALC;
                // single-emit honors the collectible setting passed to Create below.
                let session = FsiEvaluationSession.Create(configuration, arguments, new StringReader(""), output, error, collectible = true)
                let raiseFsiDiagnostics (items: FSharp.Compiler.Diagnostics.FSharpDiagnostic array) =
                    let errors = items |> Array.filter (fun item -> item.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
                    if errors.Length > 0 then
                        let issues = errors |> Array.map (fun item ->
                            let file =
                                if String.IsNullOrWhiteSpace item.FileName then "main.fsx"
                                else
                                    try
                                        let fullPath =
                                            if Path.IsPathRooted item.FileName then Path.GetFullPath item.FileName
                                            else Path.GetFullPath(Path.Combine(generationPath, item.FileName))
                                        let relative = Path.GetRelativePath(generationPath, fullPath).Replace('\\', '/')
                                        if Path.IsPathRooted relative || relative = ".."
                                           || relative.StartsWith("../", StringComparison.Ordinal) then "main.fsx"
                                        else relative
                                    with :? ArgumentException -> "main.fsx"
                            ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, item.ErrorNumberText,
                                item.Message, ExtensionSourceLocation(file, max 1 item.StartLine, item.StartColumn,
                                    max 1 item.EndLine, item.EndColumn)))
                        raise (FsiInitializationException issues)
                try
                    let mainPath = Path.Combine(generationPath, "main.fsx")
                    let includePath = sprintf "#I @\"%s\"" generationPath
                    let includeOutcome, includeDiagnostics = session.EvalInteractionNonThrowing(includePath, mainPath)
                    raiseFsiDiagnostics includeDiagnostics
                    match includeOutcome with
                    | Choice2Of2 failure -> raise failure
                    | Choice1Of2 _ -> ()
                    let outcome, diagnostics = session.EvalInteractionNonThrowing(source, mainPath)
                    raiseFsiDiagnostics diagnostics
                    match outcome with
                    | Choice2Of2 failure -> raise failure
                    | Choice1Of2 _ -> ()
                    let value, expressionDiagnostics = session.EvalExpressionNonThrowing("extension", mainPath)
                    raiseFsiDiagnostics expressionDiagnostics
                    let reflected =
                        match value with
                        | Choice1Of2 (Some result) -> result.ReflectionValue
                        | Choice1Of2 None -> null
                        | Choice2Of2 failure -> raise failure
                    match reflected with
                    | :? ITranslationExtension as extension ->
                        let scopedExtension =
                            { new ITranslationExtension with
                                member _.Descriptor = extension.Descriptor
                                member _.ConfigFields = extension.ConfigFields
                                member _.ClearDerivedStateAsync(token) = extension.ClearDerivedStateAsync(token)
                                member _.TranslateAsync(context, capabilities, token) =
                                    let concurrency =
                                        match context.Config.Values.TryGetValue("concurrency") with
                                        | true, configured ->
                                            match Int32.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                                            | true, value when value >= 1 && value <= 32 -> value
                                            | _ -> invalidOp "The frozen extension concurrency setting must be an integer from 1 through 32."
                                        | false, _ ->
                                            let fallback =
                                                match Option.ofObj context.Options with
                                                | Some options -> options.Concurrency
                                                | None -> 4
                                            if fallback < 1 || fallback > 32 then
                                                invalidOp "The extension concurrency option must be from 1 through 32."
                                            fallback
                                    ExecutionScope.run concurrency context.Progress token (fun () ->
                                        extension.TranslateAsync(context, capabilities, token)) }
                        return
                            { new IExtensionGeneration with
                                member _.Translation = scopedExtension
                                member _.DisposeAsync() = disposeExtensionAndSession (extension :> obj) session
                              interface IExtensionGenerationDefinition with
                                member _.Definition = scopedExtension }
                    | :? IMetadataExtension as extension ->
                        return
                            { new IExtensionGeneration with
                                member _.Translation = invalidOp "This generation exports a metadata extension."
                                member _.DisposeAsync() = disposeExtensionAndSession (extension :> obj) session
                              interface IExtensionGenerationDefinition with
                                member _.Definition = extension }
                    | :? IOcrExtension as extension ->
                        return
                            { new IExtensionGeneration with
                                member _.Translation = invalidOp "This generation exports an OCR extension."
                                member _.DisposeAsync() = disposeExtensionAndSession (extension :> obj) session
                              interface IExtensionGenerationDefinition with
                                member _.Definition = extension }
                    | :? IRetrievalExtension as extension ->
                        return
                            { new IExtensionGeneration with
                                member _.Translation = invalidOp "This generation exports a retrieval extension."
                                member _.DisposeAsync() = disposeExtensionAndSession (extension :> obj) session
                              interface IExtensionGenerationDefinition with
                                member _.Definition = extension }
                    | _ -> return raise (InvalidOperationException("The script must declare an extension value implementing IExtensionDefinition."))
                with error ->
                    (session :> IDisposable).Dispose()
                    return raise error
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
    let mutable configSnapshot = config
    let frozenConfigFields = Array.AsReadOnly(configFields |> Seq.toArray)
    member _.Package = package
    member _.Generation = generation
    member _.Definition = definition
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
            do! generation.DisposeAsync().AsTask()
            lock gate (fun () -> disposalCompleted <- true)
    }
    member _.ReleaseAsync() = task {
        let dispose = lock gate (fun () ->
            leases <- max 0 (leases - 1)
            if retired && leases = 0 && not disposed then
                disposed <- true
                true
            else false)
        if dispose then
            do! generation.DisposeAsync().AsTask()
            lock gate (fun () -> disposalCompleted <- true)
    }

[<Sealed>]
type private ExtensionLease(active: ActiveGeneration) =
    let mutable disposed = 0
    let config = active.Config
    do active.Acquire()
    interface IExtensionLease with
        member _.Descriptor = active.Descriptor
        member _.Fingerprint = active.Package.Fingerprint
        member _.Config = config
        member _.ConfigFields = active.ConfigFields
        member _.Definition = active.Definition
        member _.Translation =
            match active.Definition with
            | :? ITranslationExtension as extension -> extension
            | _ -> invalidOp "This lease does not contain a translation extension."
        member _.Metadata =
            match active.Definition with
            | :? IMetadataExtension as extension -> Some extension
            | _ -> None
            |> Option.toObj
        member _.Ocr =
            match active.Definition with
            | :? IOcrExtension as extension -> Some extension
            | _ -> None
            |> Option.toObj
        member _.Retrieval =
            match active.Definition with
            | :? IRetrievalExtension as extension -> Some extension
            | _ -> None
            |> Option.toObj
        member _.DisposeAsync() =
            if Interlocked.Exchange(&disposed, 1) = 0 then ValueTask(active.ReleaseAsync())
            else ValueTask.CompletedTask

[<Sealed>]
type ExtensionManager(runtimeDirectory: string, capabilities: IExtensionCapabilities,
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
    let mutable disposed = false

    let beginCheck () =
        lock checkSync (fun () ->
            if checkStopping then false
            else
                if activeOperations = 0 then
                    operationDrain <- Some(TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
                activeOperations <- activeOperations + 1
                true)

    let endCheck () =
        let completed =
            lock checkSync (fun () ->
                activeOperations <- activeOperations - 1
                if activeOperations = 0 then
                    let drain = operationDrain
                    operationDrain <- None
                    drain
                else None)
        completed |> Option.iter (fun drain -> drain.TrySetResult(()) |> ignore)

    let loadFailure (error: exn) =
        match error with
        | :? FsiInitializationException as fsiError ->
            let issues = fsiError.Diagnostics |> Array.map (fun issue ->
                ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0099",
                    "Extension initialization failed: " + issue.Code + " " + issue.Message, issue.Location))
            ExtensionCheckResult(false, [||], issues)
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

    let packageKind (package: ExtensionPackageSnapshot) =
        match package.Sources.TryGetValue("extension.yml") with
        | true, text ->
            let matched = Regex.Match(text, "(?m)^\\s*kind\\s*:\\s*['\"]?([^'\"#\\s]+)")
            if matched.Success then
                match matched.Groups[1].Value.ToLowerInvariant() with
                | "metadata" -> ExtensionKind.Metadata
                | "ocr" -> ExtensionKind.Ocr
                | "retrieval" -> ExtensionKind.Retrieval
                | _ -> ExtensionKind.Translation
            else ExtensionKind.Translation
        | _ -> ExtensionKind.Translation

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
        let! check = task {
            try return! sourceChecker.CheckAsync(package, cancellationToken)
            with
            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested -> return raise error
            | error -> return loadFailure error |> rememberFailure package.Id
        }
        if not check.Succeeded then
            diagnostics[package.Id] <- check.Diagnostics
            return check
        else
            let! capacityAvailable = task {
                do! gate.WaitAsync(cancellationToken)
                try
                    draining.RemoveAll(Predicate<ActiveGeneration>(fun value -> value.IsDisposed)) |> ignore
                    // Staged generations have no leases; release their process before reserving a replacement.
                    match staged.TryGetValue(package.Id) with
                    | true, previous ->
                        do! previous.RetireAsync()
                        staged.Remove(package.Id) |> ignore
                    | _ -> ()
                    // The per-package keyed gate reserves capacity before a new process is started.
                    return not (draining |> Seq.exists (fun value -> value.Package.Id = package.Id))
                finally gate.Release() |> ignore
            }
            if not capacityAvailable then
                return loadFailure (InvalidOperationException("An older extension generation is still draining; release its leases before loading another generation.")) |> rememberFailure package.Id
            else
              let! persistError = task {
                  try
                      match fileStore with
                      | :? IPersistentExtensionFileStore as persistent -> do! persistent.PersistSnapshotAsync(package, cancellationToken)
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
                              match generationLoader with
                              | :? ICheckedExtensionGenerationLoader as checkedLoader -> checkedLoader.LoadCheckedAsync(package, check, cancellationToken)
                              | _ -> generationLoader.LoadAsync(package, cancellationToken)
                          return Choice1Of2 value
                      with
                      | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested -> return raise error
                      | error -> return Choice2Of2 error
                  }
                  match generationResult with
                  | Choice2Of2 error -> return loadFailure error |> rememberFailure package.Id
                  | Choice1Of2 generation ->
                      let! (definition, descriptor) = resolveDefinition generation
                      let expectedKind = packageKind package
                      let manifestVersion =
                          match package.Sources.TryGetValue("extension.yml") with
                          | true, text ->
                              let matched = Regex.Match(text, "(?m)^\\s*version\\s*:\\s*['\"]?([^'\"#\\s]+)")
                              if matched.Success then Some matched.Groups[1].Value else None
                          | _ -> None
                      let kindMatches =
                          descriptor.Kind = expectedKind
                          && (expectedKind <> ExtensionKind.Metadata || definition :? IMetadataExtension)
                          && (expectedKind <> ExtensionKind.Ocr || definition :? IOcrExtension)
                          && (expectedKind <> ExtensionKind.Retrieval || definition :? IRetrievalExtension)
                          && (expectedKind <> ExtensionKind.Translation || definition :? ITranslationExtension)
                      if descriptor.Id <> package.Id || descriptor.HostApi <> ExtensionHostApi.Current
                         || not kindMatches || (manifestVersion |> Option.exists (fun version -> version <> descriptor.Version)) then
                          do! generation.DisposeAsync().AsTask()
                          let code, message, file =
                              if descriptor.Id <> package.Id then "EXT0002", "The extension descriptor id must match the package id.", "main.fsx"
                              elif descriptor.HostApi <> ExtensionHostApi.Current then "EXT0007", "The extension descriptor targets an unsupported Patchouli host API.", "main.fsx"
                              elif not kindMatches then "EXT0009", "The extension implementation kind must match extension.yml.", "extension.yml"
                              else "EXT0008", "The extension descriptor version must match extension.yml.", "extension.yml"
                          let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, code, message,
                              ExtensionSourceLocation(file, 1, 0, 1, 1))
                          let failed = ExtensionCheckResult(false, check.ConfigFields,
                              Seq.append check.Diagnostics [ issue ] |> Seq.toArray)
                          return rememberFailure package.Id failed
                      else
                          let! configResult = task {
                              try
                                  let! value = fileStore.ReadConfigAsync(package.Id, cancellationToken)
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
                              let requests: IReadOnlyList<ExtensionCapabilityRequest> =
                                  Option.ofObj check.CapabilityRequests |> Option.defaultValue Array.empty
                              let descriptor = descriptor.WithCapabilityRequests(requests)
                              let candidate = ActiveGeneration(package, generation, definition, descriptor, check.ConfigFields, config)
                              let! gateResult = task {
                                  try
                                      do! gate.WaitAsync(cancellationToken)
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

    new(runtimeDirectory: string, capabilities: IExtensionCapabilities,
        fileStore: IExtensionFileStore, generationLoader: IExtensionGenerationLoader) =
        ExtensionManager(runtimeDirectory, capabilities, fileStore, generationLoader,
            ExtensionCompiler(Path.Combine(Path.GetFullPath runtimeDirectory, ".check")) :> IExtensionSourceChecker)

    new(runtimeDirectory: string, capabilities: IExtensionCapabilities) =
        ExtensionManager(runtimeDirectory, capabilities, FileExtensionStore(runtimeDirectory) :> IExtensionFileStore,
            (let workerType = Type.GetType("Patchouli.Extensions.Worker.WorkerGenerationLoader, Patchouli.Extensions.Worker", throwOnError = true) |> nonNull
             Activator.CreateInstance(workerType, [| box runtimeDirectory |]) |> nonNull :?> IExtensionGenerationLoader))

    member _.CheckAsync(package, cancellationToken) =
        runTrackedOperation (loadFailure (ObjectDisposedException(nameof ExtensionManager)))
            (fun () -> sourceChecker.CheckAsync(package, cancellationToken))

    member _.LoadAsync(package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) =
        runTrackedOperation (loadFailure (ObjectDisposedException(nameof ExtensionManager))) (fun () -> task {
            let keyedGate = keyedGates.GetOrAdd(package.Id, fun _ -> new SemaphoreSlim(1, 1))
            do! keyedGate.WaitAsync(cancellationToken)
            try return! loadCore package cancellationToken
            finally keyedGate.Release() |> ignore
        })

    member _.ActivateAsync(id: string, package: ExtensionPackageSnapshot, cancellationToken: CancellationToken) =
      runTrackedOperation (loadFailure (ObjectDisposedException(nameof ExtensionManager))) (fun () -> task {
        if id <> package.Id then invalidArg "id" "Activation id must match the package id."
        let keyedGate = keyedGates.GetOrAdd(id, fun _ -> new SemaphoreSlim(1, 1))
        do! keyedGate.WaitAsync(cancellationToken)
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
                                do! persistent.SaveStateAsync(id, Some candidate.Package.Fingerprint, lkg, CancellationToken.None)
                            | _ -> ()
                            savedLkg |> Option.iter (fun value -> lastKnownGood[id] <- value)
                            let previous = match active.TryGetValue(id) with | true, value -> Some value | _ -> None
                            active[id] <- candidate
                            staged.Remove(id) |> ignore
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
    member _.ReadSourceAsync(id, cancellationToken) = task {
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
