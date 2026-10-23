namespace Patchouli.Extensions.Compiler

// Application compiler sidecar; extension implementations themselves always execute in the host.

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions
open Patchouli.Extensions.Contracts

[<CLIMutable>]
type CompilationReply =
    { CheckResult: ExtensionCheckResult
      ArtifactDirectory: string
      AssemblyPath: string
      CacheHit: bool }

[<CLIMutable>]
type CompilationRequest =
    { SourceDir: string
      Id: string
      ArtifactRoot: string }

module Program =
    let private usage () =
        eprintfn "Usage: Patchouli.Extensions.Compiler --source <snapshotDirectory> --id <id> --artifacts <artifactRoot> --result <jsonPath>"
        2

    let private parseArguments (arguments: string array) =
        if arguments.Length <> 8 then None
        else
            let options = Dictionary<string, string>(StringComparer.Ordinal)
            let mutable valid = true
            for index in 0 .. 2 .. arguments.Length - 1 do
                let key = arguments[index]
                if not ([| "--source"; "--id"; "--artifacts"; "--result" |] |> Array.contains key)
                   || options.ContainsKey key then valid <- false
                else options.Add(key, arguments[index + 1])
            if not valid || options.Count <> 4 || (options.Values |> Seq.exists String.IsNullOrWhiteSpace) then None
            else Some(options["--source"], options["--id"], options["--artifacts"], options["--result"])

    let private loadSnapshot (directory: string) (id: string) =
        let root = Path.GetFullPath directory
        if not (Directory.Exists root) then invalidArg "source" "Snapshot directory does not exist."
        let sources = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let binaries = Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase)
        for path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
            let relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')
            if ExtensionPackageSnapshot.IsTextSourcePath(relative) then
                sources.Add(relative, File.ReadAllText path)
            else binaries.Add(relative, ReadOnlyMemory<byte>(File.ReadAllBytes path))
        ExtensionPackageSnapshot(id, sources, binaries)

    let private compile source id artifacts = task {
        let package = loadSnapshot source id
        let! compiled = ExtensionCompiler.CompileAsync(package, Path.GetFullPath artifacts, CancellationToken.None)
        return
            { CheckResult = compiled.CheckResult
              ArtifactDirectory = compiled.ArtifactDirectory
              AssemblyPath = compiled.AssemblyPath
              CacheHit = compiled.CacheHit }
    }

    let private failedReply message =
        let issue = ExtensionDiagnostic(ExtensionDiagnosticSeverity.Error, "EXT0032", message,
            ExtensionSourceLocation("main.fsx", 1, 0, 1, 1))
        { CheckResult = ExtensionCheckResult(false, [||], [| issue |])
          ArtifactDirectory = ""
          AssemblyPath = ""
          CacheHit = false }

    let private run source id artifacts resultPath = task {
        let resultFullPath = Path.GetFullPath resultPath
        match Path.GetDirectoryName resultFullPath with
        | null -> ()
        | parent -> Directory.CreateDirectory parent |> ignore
        use cancellation = new CancellationTokenSource()
        Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; cancellation.Cancel())
        let package = loadSnapshot source id
        let! compiled = ExtensionCompiler.CompileAsync(package, Path.GetFullPath artifacts, cancellation.Token)
        let reply = { CheckResult = compiled.CheckResult; ArtifactDirectory = compiled.ArtifactDirectory
                      AssemblyPath = compiled.AssemblyPath; CacheHit = compiled.CacheHit }
        let json = JsonSerializer.Serialize(reply, JsonSerializerOptions(WriteIndented = true))
        File.WriteAllText(resultFullPath, json)
        return if compiled.CheckResult.Succeeded then 0 else 1
    }

    let private runServer () = task {
        let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        let mutable running = true
        while running do
            use idle = new CancellationTokenSource(TimeSpan.FromMinutes 5.0)
            let! line = task {
                try return! Console.In.ReadLineAsync(idle.Token).AsTask()
                with :? OperationCanceledException -> return null
            }
            if isNull line then
                running <- false
            elif String.IsNullOrWhiteSpace line then
                Console.Out.WriteLine(JsonSerializer.Serialize(failedReply "Compiler request was empty."))
                do! Console.Out.FlushAsync()
            else
                let requestLine = nonNull line
                let! reply = task {
                    try
                        match JsonSerializer.Deserialize<CompilationRequest>(requestLine, options) with
                        | null ->
                            return failedReply "Compiler request is missing required paths or package id."
                        | request when String.IsNullOrWhiteSpace request.SourceDir
                                       || String.IsNullOrWhiteSpace request.Id || String.IsNullOrWhiteSpace request.ArtifactRoot ->
                            return failedReply "Compiler request is missing required paths or package id."
                        | request ->
                            return! compile request.SourceDir request.Id request.ArtifactRoot
                    with _ ->
                        return failedReply "Compiler request failed. Verify the snapshot files, SDK and NuGet package sources."
                }
                Console.Out.WriteLine(JsonSerializer.Serialize(reply))
                do! Console.Out.FlushAsync()
        return 0
    }

    [<EntryPoint>]
    let main arguments =
        System.Globalization.CultureInfo.CurrentUICulture <- System.Globalization.CultureInfo.InvariantCulture
        // Compute once inside the compiler process, so a development-only rebuild cannot use a
        // stale copied stamp. Normal host startup only reads the build-generated stamp.
        RuntimeFrameworkReferences.computeApiIdentity() |> RuntimeFrameworkReferences.setCompilerIdentity
        // Scope NuGet restore isolation to this compiler process before any FCS restore child runs.
        NugetResolverPreflight.applyRestoreIsolationEnvironment()
        if arguments.Length = 1 && arguments[0] = "--server" then
            try runServer () |> fun work -> work.GetAwaiter().GetResult()
            with _ -> 1
        else
            match parseArguments arguments with
            | None -> usage ()
            | Some(source, id, artifacts, resultPath) ->
                try run source id artifacts resultPath |> fun work -> work.GetAwaiter().GetResult()
                with _ ->
                    // Error details may contain user-controlled paths or source text; the caller gets only the exit code.
                    1
