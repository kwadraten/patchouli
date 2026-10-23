namespace Patchouli.Extensions

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts

/// One reusable sidecar process and one serialization gate per installed compiler path.
[<Sealed>]
type private CompilerServerLease(compilerPath: string) =
    let gate = new SemaphoreSlim(1, 1)
    let syncRoot = obj ()
    let mutable serverProcess: Process option = None

    let executableAndArguments () =
        let directory = Path.GetDirectoryName compilerPath |> Option.ofObj |> Option.defaultValue AppContext.BaseDirectory
        let appHost = Path.Combine(directory, if OperatingSystem.IsWindows() then "Patchouli.Extensions.Compiler.exe" else "Patchouli.Extensions.Compiler")
        if File.Exists appHost then appHost, [| "--server" |]
        else "dotnet", [| compilerPath; "--server" |]

    let start () =
        let executable, arguments = executableAndArguments ()
        let info = ProcessStartInfo(executable)
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for argument in arguments do info.ArgumentList.Add argument
        let child = new Process(StartInfo = info)
        if not (child.Start()) then
            child.Dispose()
            invalidOp "The extension compiler sidecar could not start."
        child.StandardInput.AutoFlush <- true
        serverProcess <- Some child
        // Drain stderr as bytes, never retaining unbounded compiler or package-source output.
        let drain = task {
            try
                let buffer = Array.zeroCreate<byte> 4096
                let mutable reading = true
                while reading do
                    let! count = child.StandardError.BaseStream.ReadAsync(buffer.AsMemory(), CancellationToken.None)
                    if count = 0 then reading <- false
            with
            | :? IOException -> ()
            | :? ObjectDisposedException -> ()
        }
        drain |> ignore
        child

    let current () =
        lock syncRoot (fun () ->
            match serverProcess with
            | Some child when not child.HasExited -> child
            | Some child ->
                child.Dispose()
                serverProcess <- None
                start ()
            | None -> start ())

    let stop (child: Process) =
        lock syncRoot (fun () ->
            match serverProcess with
            | Some current when Object.ReferenceEquals(current, child) -> serverProcess <- None
            | _ -> ())
        try
            if not child.HasExited then child.Kill(entireProcessTree = true)
            child.WaitForExit()
        with _ -> ()
        child.Dispose()

    member _.CompileAsync(sourceDirectory: string, packageId: string, artifactRoot: string,
                          cancellationToken: CancellationToken) : Task<ExtensionCompilationResult> = task {
        do! gate.WaitAsync(cancellationToken)
        try
            let requestOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)
            let request = JsonSerializer.Serialize({| SourceDir = sourceDirectory; Id = packageId; ArtifactRoot = artifactRoot |}, requestOptions)
            let mutable attempt = 0
            let mutable completed = false
            let mutable result = Unchecked.defaultof<ExtensionCompilationResult>
            while not completed && attempt < 2 do
                cancellationToken.ThrowIfCancellationRequested()
                attempt <- attempt + 1
                let child = current ()
                use registration = cancellationToken.Register(fun () -> stop child)
                try
                    do! child.StandardInput.WriteLineAsync(request).WaitAsync(cancellationToken)
                    let! responseLine = child.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
                    let line = responseLine |> Option.ofObj |> Option.defaultWith (fun () ->
                        raise (EndOfStreamException("Compiler server closed its response pipe.")))
                    use document = JsonDocument.Parse line
                    let value = document.RootElement
                    let check = value.GetProperty("CheckResult").Deserialize<ExtensionCheckResult>() |> nonNull
                    result <- ExtensionCompilationResult(check,
                        value.GetProperty("ArtifactDirectory").GetString() |> nonNull,
                        value.GetProperty("AssemblyPath").GetString() |> nonNull,
                        value.GetProperty("CacheHit").GetBoolean())
                    cancellationToken.ThrowIfCancellationRequested()
                    completed <- true
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    stop child
                    raise (OperationCanceledException cancellationToken)
                | :? IOException
                | :? InvalidOperationException
                | :? JsonException when attempt < 2 -> stop child
            if not completed then invalidOp "The extension compiler server failed after one reconnect attempt."
            return result
        finally
            gate.Release() |> ignore
    }

    member _.Dispose() =
        let child = lock syncRoot (fun () ->
            match serverProcess with
            | Some child -> serverProcess <- None; Some child
            | None -> None)
        child |> Option.iter stop

module private CompilerServerLeases =
    let private leases = ConcurrentDictionary<string, CompilerServerLease>(StringComparer.OrdinalIgnoreCase)
    let get compilerPath = leases.GetOrAdd(Path.GetFullPath compilerPath, fun path -> CompilerServerLease(path))

[<Sealed>]
type ExtensionCompilationProcess(runtimeDirectory: string) =
    let root = Path.GetFullPath runtimeDirectory
    let artifacts = Path.Combine(root, ".compiled")

    member _.CompileAsync(package: ExtensionPackageSnapshot, token: CancellationToken) : Task<ExtensionCompilationResult> = task {
        token.ThrowIfCancellationRequested()
        let identity = ExtensionArtifact.runtimeToolchainIdentity()
        match ExtensionArtifact.tryReadCached package artifacts identity with
        | Some cached -> return cached
        | None ->
            let bundledDirectory = Path.Combine(root, ".bundled", ExtensionArtifact.artifactKey package identity)
            let compiler = Path.Combine(AppContext.BaseDirectory, "extension-compiler", "Patchouli.Extensions.Compiler.dll")
            if Directory.Exists bundledDirectory then
                let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0040",
                    "The bundled extension artifact is incomplete or incompatible. Rebuild or repair the application; bundled code will not be silently recompiled at runtime.",
                    ExtensionSourceLocation("main.fsx", 1, 0, 1, 1))
                return ExtensionCompilationResult(ExtensionCheckResult(false, [||], [| issue |]), "", "", false)
            elif not (File.Exists compiler) then
                let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0032",
                    "The bundled extension compiler is unavailable. Repair the application installation; the current extension remains active.",
                    ExtensionSourceLocation("main.fsx", 1, 0, 1, 1))
                return ExtensionCompilationResult(ExtensionCheckResult(false, [||], [| issue |]), "", "", false)
            else
                let staging = Path.Combine(root, ".compilation", Guid.NewGuid().ToString("N"))
                let sources = Path.Combine(staging, "source")
                Directory.CreateDirectory sources |> ignore
                try
                    for KeyValue(relative, content) in package.Sources do
                        let path = Path.Combine(sources, relative.Replace('/', Path.DirectorySeparatorChar))
                        let parent = Path.GetDirectoryName path |> Option.ofObj |> Option.defaultWith (fun () -> invalidOp "Extension source path has no parent directory.")
                        Directory.CreateDirectory parent |> ignore
                        do! File.WriteAllTextAsync(path, content, token)
                    for KeyValue(relative, content) in package.BinaryFiles do
                        let path = Path.Combine(sources, relative.Replace('/', Path.DirectorySeparatorChar))
                        let parent = Path.GetDirectoryName path |> Option.ofObj |> Option.defaultWith (fun () -> invalidOp "Extension binary path has no parent directory.")
                        Directory.CreateDirectory parent |> ignore
                        do! File.WriteAllBytesAsync(path, content.ToArray(), token)
                    return! (CompilerServerLeases.get compiler).CompileAsync(sources, package.Id, artifacts, token)
                finally
                    if Directory.Exists staging then Directory.Delete(staging, true)
    }

    interface IExtensionSourceChecker with
        member this.CheckAsync(package, token) = task {
            let! result = this.CompileAsync(package, token)
            return result.CheckResult
        }

    interface ICompiledExtensionSourceChecker with
        member this.CompileAsync(package, token) = this.CompileAsync(package, token)
