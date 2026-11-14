#load "Common.fsx"
open System
open System.IO
open System.Net.Http
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Common

let registryUrl = "https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json"
let catalogPath = resolve "src/Patchouli.Core/Agents/AgentCatalog.g.cs"

// Native interactive TUI adapters, never ACP server arguments. This is the single table.
let adapters = [
    "opencode", "OpenCode", "The open source coding agent.", "opencode",
    [@"%USERPROFILE%\.opencode\bin\opencode.exe"; @"%APPDATA%\npm\opencode.cmd"],
    ["~/.opencode/bin/opencode"; "/opt/homebrew/bin/opencode"; "/usr/local/bin/opencode"], ["~/.opencode/bin/opencode"; "/usr/local/bin/opencode"]
    "claude-acp", "Claude Code", "Anthropic's Claude coding agent.", "claude",
    [@"%USERPROFILE%\.local\bin\claude.exe"; @"%APPDATA%\npm\claude.cmd"],
    ["~/.local/bin/claude"; "/opt/homebrew/bin/claude"; "/usr/local/bin/claude"], ["~/.local/bin/claude"; "/usr/local/bin/claude"]
    "codex-acp", "Codex", "OpenAI's coding agent.", "codex",
    [@"%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe"; @"%APPDATA%\npm\codex.cmd"],
    ["~/.local/bin/codex"; "/opt/homebrew/bin/codex"], ["~/.local/bin/codex"; "/usr/local/bin/codex"]
    "kimi", "Kimi Code", "Moonshot AI's coding agent.", "kimi",
    [@"%USERPROFILE%\.kimi-code\bin\kimi.exe"; @"%USERPROFILE%\.local\bin\kimi.exe"],
    ["~/.kimi-code/bin/kimi"; "~/.local/bin/kimi"; "/opt/homebrew/bin/kimi"], ["~/.kimi-code/bin/kimi"; "~/.local/bin/kimi"; "/usr/local/bin/kimi"]
    "antigravity-acp", "Google Antigravity", "Google's Antigravity agent CLI.", "agy", [@"%LOCALAPPDATA%\agy\bin\agy.exe"],
    ["~/.local/bin/agy"; "/Applications/Antigravity.app/Contents/Resources/bin/agy"], ["~/.local/bin/agy"]
    "gemini", "Gemini CLI", "Google's Gemini coding agent.", "gemini", [@"%APPDATA%\npm\gemini.cmd"],
    ["/opt/homebrew/bin/gemini"; "/usr/local/bin/gemini"; "~/.local/bin/gemini"], ["/usr/local/bin/gemini"; "~/.local/bin/gemini"]
    "github-copilot-cli", "GitHub Copilot CLI", "GitHub's Copilot command line agent.", "copilot", [@"%APPDATA%\npm\copilot.cmd"],
    ["/opt/homebrew/bin/copilot"; "/usr/local/bin/copilot"], ["/usr/local/bin/copilot"]
    "qwen-code", "Qwen Code", "Alibaba's Qwen coding assistant.", "qwen", [@"%APPDATA%\npm\qwen.cmd"],
    ["/opt/homebrew/bin/qwen"; "/usr/local/bin/qwen"], ["/usr/local/bin/qwen"]
    "cline", "Cline", "Cline's autonomous coding agent.", "cline", [@"%APPDATA%\npm\cline.cmd"],
    ["/opt/homebrew/bin/cline"; "/usr/local/bin/cline"], ["/usr/local/bin/cline"] ]

let readRegistry path =
    let document = readJson path
    if isNull document.["version"] || items document.["agents"] |> List.isEmpty then failwith "ACP registry must have a version and agents"
    let ids = items document.["agents"] |> List.map (get "id")
    if List.contains "" ids || List.distinct ids |> List.length <> ids.Length then failwith "ACP registry has missing or duplicate agent IDs"
    document

let loadRegistry refresh =
    let cache = resolve ".tmp/acp-registry/registry.json"
    if File.Exists(cache) && not refresh then readRegistry cache
    else
        mkdir (Path.GetDirectoryName(cache))
        let temporary = cache + "." + Guid.NewGuid().ToString("N") + ".download"
        try
            try
                use client = new HttpClient(Timeout = TimeSpan.FromSeconds(60.))
                use response = client.GetAsync(registryUrl, cancellation.Token).GetAwaiter().GetResult()
                response.EnsureSuccessStatusCode() |> ignore
                File.WriteAllBytes(temporary, response.Content.ReadAsByteArrayAsync(cancellation.Token).GetAwaiter().GetResult())
                let document = readRegistry temporary
                File.Move(temporary, cache, true)
                document
            with
            | :? OperationCanceledException -> reraise()
            | error when File.Exists(cache) -> eprintfn "Warning: registry download failed; reusing cache: %s" error.Message; readRegistry cache
        finally
            if File.Exists(temporary) then File.Delete(temporary)

let literal (value: string) =
    if value.Contains('\r') || value.Contains('\n') then failwith "C# string literals must not contain line breaks"
    if value.Contains('\\') then "@\"" + value.Replace("\"", "\"\"") + "\"" else "\"" + value.Replace("\"", "\\\"") + "\""
let comment (value: string) = value.Replace('\r', ' ').Replace('\n', ' ').Trim()
let parts (value: JsonNode) = [
    let arguments = strings (prop "args" value)
    if not arguments.IsEmpty then "args " + String.concat " " arguments
    let environment = prop "env" value
    if not (isNull environment) then
        let pairs = environment.AsObject() |> Seq.sortBy _.Key |> Seq.map (fun pair -> pair.Key + "=" + text pair.Value) |> String.concat ", "
        if pairs <> "" then "env " + pairs ]
let registryComments (agent: JsonNode) = [
    "// Registry distribution (ACP server form, not TUI launch args); never read at run time."
    let version = match get "version" agent with "" -> "unknown" | value -> value
    let repository = get "repository" agent
    "//   " + comment ("agent version " + version + (if repository = "" then "" else " - " + repository))
    "//   icon: " + (match get "icon" agent with "" -> "not published" | value -> comment value + " (not embedded yet)")
    let distribution = prop "distribution" agent
    if isNull distribution then "//   distribution: not published"
    else
        for kind in ["npx"; "uvx"] do
            let value = distribution.[kind]
            if isNull value then $"//   {kind}: not published"
            else
                let package = match get "package" value with "" -> "(package missing)" | value -> value
                let suffix = parts value
                $"//   {kind}: " + comment (package + (if suffix.IsEmpty then "" else " (" + String.concat "; " suffix + ")"))
        let binary = distribution.["binary"]
        if isNull binary || binary.AsObject().Count = 0 then "//   binary: not published"
        else
            for pair in binary.AsObject() |> Seq.sortBy _.Key do
                let archive = match get "archive" pair.Value with "" -> "(archive missing)" | value -> value
                $"//   binary {pair.Key}: {archive}"
                let suffix = [for key in ["sha256"; "cmd"] do let value = get key pair.Value in if value <> "" then key + " " + value] @ parts pair.Value
                if not suffix.IsEmpty then "//     " + comment (String.concat "; " suffix) ]
let arrayLines name limit values =
    let values = List.map literal values
    if values.Length <= limit then [name + ": [" + String.concat ", " values + "],"]
    else [name + ":"; "["] @ (values |> List.mapi (fun index value -> "    " + value + (if index < values.Length - 1 then "," else ""))) @ ["],"]

let generate (registry: JsonNode) existing =
    let ids = adapters |> List.map (fun (id, _, _, _, _, _, _) -> id)
    let agents = items registry.["agents"] |> List.map (fun agent -> get "id" agent, agent) |> Map.ofList
    for id in ids do if not (Map.containsKey id agents) then failwith $"Curated agent ID missing from registry: {id}"
    let blocks = Regex.Matches(existing, "(?ms)^        // <catalog-preserve id=\"([^\"\r\n]+)\">\r?\n.*?^        // </catalog-preserve>\r?\n") |> Seq.toList
    if Regex.Matches(existing, "// </?catalog-preserve\\b").Count <> 2 * blocks.Length then failwith "Malformed catalog-preserve markers; refusing to overwrite catalog"
    let preserved = Collections.Generic.Dictionary<string, string list>(StringComparer.Ordinal)
    for block in blocks do
        let id = block.Groups[1].Value
        if not (List.contains id ids) || preserved.ContainsKey(id) then failwith $"Unknown or duplicate catalog-preserve ID: {id}"
        preserved[id] <- block.Value.TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n') |> Array.toList
    let lines = [
        "// <auto-generated>"; "// Generated from the Agent Client Protocol registry"
        $"""// ({registryUrl}, registry version {get "version" registry})"""
        "// plus the curated TUI adapter table in scripts/lib/AgentCatalog.fsx."
        "// Regenerate with: dotnet fsi scripts/update-agent-catalog.fsx -- --refresh"
        "// Do not edit by hand. Icons are not embedded yet, so IconResourceName keeps its null default."
        "// </auto-generated>"; ""; "namespace Patchouli.Core.Agents;"; ""; "public static partial class AgentCatalog"; "{"
        "    /// <summary>Every catalogued agent, in display order.</summary>"; "    public static readonly AgentCatalogEntry[] All ="; "    ["
        for index, (id, name, description, command, windows, mac, linux) in List.indexed adapters do
            if preserved.ContainsKey(id) then yield! preserved[id]
            else
                yield! registryComments agents[id] |> List.map (fun line -> "        " + line)
                "        new("
                for key, value in ["Id", id; "DisplayName", name; "Description", description] do $"            {key}: {literal value},"
                "            Kind: AgentLaunchKind.NativeTui,"
                $"            CommandName: {literal command},"
                let properties = [
                    yield! arrayLines "LaunchArguments" 3 []
                    yield! arrayLines "WindowsPaths" 1 windows
                    yield! arrayLines "MacPaths" 3 mac
                    yield! arrayLines "LinuxPaths" 3 linux
                    yield! arrayLines "VersionArguments" 3 ["--version"]
                    if id = "opencode" then "IsDefault: true," ]
                for propertyIndex, line in List.indexed properties do
                    "            " + (if propertyIndex < properties.Length - 1 then line else line.TrimEnd(',') + (if index < adapters.Length - 1 then ")," else ")"))
        "    ];"; "}"; "" ]
    String.concat "\n" lines
