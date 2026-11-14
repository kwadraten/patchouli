open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let utf8 = UTF8Encoding(false)
let jsonOptions = JsonSerializerOptions(WriteIndented = true)
let cancellation = new CancellationTokenSource()
let resolve (path: string) = Path.GetFullPath(path, root)
let mkdir (path: string) = Directory.CreateDirectory(path) |> ignore
let writeText path (text: string) =
    mkdir (Path.GetDirectoryName(Path.GetFullPath(path)))
    File.WriteAllText(path, text, utf8)
let jvalue value = JsonSerializer.SerializeToNode(value, jsonOptions)
let jobj (fields: (string * JsonNode) seq) =
    let result = JsonObject()
    for key, value in fields do result[key] <- value
    result
let jarray (values: seq<JsonNode>) = JsonArray(values |> Seq.toArray)
let prop (key: string) (node: JsonNode) = if isNull node then null else node[key]
let text (node: JsonNode) = if isNull node then "" else node.ToString()
let get key node = prop key node |> text
let items (node: JsonNode) = if isNull node then [] else node.AsArray() |> Seq.toList
let strings node = items node |> List.map text
let intValue (node: JsonNode) = if isNull node then 0 else node.GetValue<int>()
let readJson path = JsonNode.Parse(File.ReadAllText(path))
let writeJson path (node: JsonNode) = writeText path (node.ToJsonString(jsonOptions) + "\n")
let utcNow () = DateTimeOffset.UtcNow.ToString("o")
let sha256 path =
    use stream = File.OpenRead(path)
    SHA256.HashData(stream) |> Convert.ToHexString |> _.ToLowerInvariant()
let hashText (value: string) = SHA256.HashData(utf8.GetBytes(value)) |> Convert.ToHexString |> _.ToLowerInvariant()

let insideTmp label path =
    let result = resolve path
    let relative = Path.GetRelativePath(resolve ".tmp", result)
    if relative = "." || relative = ".." || relative.StartsWith(".." + string Path.DirectorySeparatorChar) || Path.IsPathRooted(relative) then
        failwith $"{label} must be inside the repository .tmp directory: {result}"
    // Reject existing symlink/reparse ancestors, including .tmp itself.
    let mutable current = DirectoryInfo(Path.GetDirectoryName(result))
    while not (isNull current) && current.FullName <> root do
        if current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint) then
            failwith $"{label} must not traverse a symbolic link: {current.FullName}"
        current <- current.Parent
    if File.Exists(result) || Directory.Exists(result) then
        if File.GetAttributes(result).HasFlag(FileAttributes.ReparsePoint) then failwith $"Symbolic link output refused: {result}"
    result

type Arguments = Map<string, string list>
let parse (flags: string list) (options: string list) (argv: string array) : Arguments =
    let rec loop result args =
        match args with
        | [] -> result
        | "--" :: rest -> loop result rest
        | ("--help" | "-h") :: rest -> loop (Map.add "help" ["true"] result) rest
        | key :: rest when List.contains key flags -> loop (Map.add key ["true"] result) rest
        | key :: value :: rest when List.contains key options && not (value.StartsWith("--")) ->
            let previous = Map.tryFind key result |> Option.defaultValue []
            loop (Map.add key (previous @ [value]) result) rest
        | key :: _ -> failwith $"Unknown argument or missing value: {key}. Use --help."
    loop Map.empty (Array.toList argv)
let has key args = Map.containsKey key args
let values key args = Map.tryFind key args |> Option.defaultValue []
let arg key fallback args = values key args |> List.tryLast |> Option.defaultValue fallback
let number key fallback args =
    match Int32.TryParse(arg key (string fallback) args) with
    | true, value when value >= 0 -> value
    | _ -> failwith $"{key} must be a non-negative integer"
let choice key fallback choices args =
    let value = arg key fallback args
    if not (List.contains value choices) then failwith $"Invalid {key}: {value}"
    value

type ProcessResult = { ExitCode: int; Stdout: string; Stderr: string }

let execute workingDirectory file (arguments: string list) (logPath: string option) quiet =
    cancellation.Token.ThrowIfCancellationRequested()
    let info = ProcessStartInfo(file, UseShellExecute = false, WorkingDirectory = workingDirectory,
                                RedirectStandardOutput = true, RedirectStandardError = true,
                                StandardOutputEncoding = utf8, StandardErrorEncoding = utf8)
    for argument in arguments do info.ArgumentList.Add(argument)
    use child = new Process(StartInfo = info)
    use log = match logPath with Some path -> new StreamWriter(path, true, utf8) | None -> null
    let gate = obj()
    let stdout, stderr = StringBuilder(), StringBuilder()
    let copy (reader: StreamReader) (buffer: StringBuilder) (error: bool) = task {
        let mutable running = true
        while running do
            let! line = reader.ReadLineAsync()
            if isNull line then running <- false
            else
                lock gate (fun () ->
                    buffer.AppendLine(line) |> ignore
                    if not quiet then (if error then Console.Error else Console.Out).WriteLine(line)
                    if not (isNull log) then
                        log.WriteLine(line)
                        log.Flush())
    }
    if not (child.Start()) then failwith $"Could not start {file}"
    let readers = [| copy child.StandardOutput stdout false; copy child.StandardError stderr true |]
    try
        child.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult()
        Task.WhenAll(readers).GetAwaiter().GetResult() |> ignore
        { ExitCode = child.ExitCode; Stdout = stdout.ToString(); Stderr = stderr.ToString() }
    finally
        if not child.HasExited then
            child.Kill(true)
            child.WaitForExit()
        Task.WhenAll(readers).GetAwaiter().GetResult() |> ignore

let runIn directory file arguments =
    let result = execute directory file arguments None false
    if result.ExitCode <> 0 then failwith $"{file} failed with exit code {result.ExitCode}"
let run file arguments = runIn root file arguments
let capture file arguments =
    let result = execute root file arguments None true
    if result.ExitCode <> 0 then failwith $"{file} failed with exit code {result.ExitCode}"
    result.Stdout.TrimEnd('\r', '\n')
let dotnetScript path arguments = run "dotnet" (["fsi"; "--warnaserror+"; resolve path; "--"] @ arguments)

let isolateTemp name =
    let directory = insideTmp "Temporary work" (".tmp/" + name)
    mkdir directory
    for variable in ["TMPDIR"; "TMP"; "TEMP"] do Environment.SetEnvironmentVariable(variable, directory)

let requireOS name =
    let supported = match name with "Windows" -> OperatingSystem.IsWindows() | "macOS" -> OperatingSystem.IsMacOS() | "Linux" -> OperatingSystem.IsLinux() | _ -> false
    if not supported then failwith $"This operation requires {name}"

let requireTools (tools: string list) =
    let path = Environment.GetEnvironmentVariable("PATH") |> Option.ofObj |> Option.defaultValue ""
    for tool in tools do
        let candidates =
            if Path.IsPathRooted(tool) then [tool]
            else [ for directory in path.Split(Path.PathSeparator) do
                       for suffix in (if OperatingSystem.IsWindows() then [""; ".exe"; ".cmd"] else [""]) do
                           Path.Combine(directory, tool + suffix) ]
        if not (List.exists File.Exists candidates) then failwith $"Required tool not found on PATH: {tool}"

let entry main =
    Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; cancellation.Cancel())
    use termination =
        if OperatingSystem.IsWindows() then null
        else System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, fun context -> context.Cancel <- true; cancellation.Cancel())
    try
        main (fsi.CommandLineArgs |> Array.skip 1) |> Environment.Exit
    with
    | :? OperationCanceledException -> eprintfn "Interrupted"; Environment.Exit(130)
    | error -> eprintfn "%s" error.Message; Environment.Exit(1)
