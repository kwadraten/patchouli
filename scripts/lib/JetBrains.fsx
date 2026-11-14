#load "Common.fsx"
open System
open System.IO
open System.Text.RegularExpressions
open Common

let version = "2026.1.4"
let arguments command args =
    let configuration = choice "--configuration" "Debug" ["Debug"; "Release"] args
    let sdk = capture "dotnet" ["--version"]
    let lines = capture "dotnet" ["--list-sdks"]
    let pattern = @"(?m)^" + Regex.Escape(sdk) + @"\s+\[(.+)\]\s*$"
    let found = Regex.Match(lines, pattern)
    if not found.Success then failwith $"Could not locate selected .NET SDK {sdk}"
    let msbuild = Path.Combine(found.Groups[1].Value.Trim(), sdk, "MSBuild.dll")
    if not (File.Exists(msbuild)) then failwith $"MSBuild.dll not found: {msbuild}"
    // jb apphosts need DOTNET_ROOT when the SDK was installed outside a standard location.
    if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_ROOT")) then
        Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetDirectoryName(found.Groups[1].Value.Trim()))
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    let tools = Path.Combine(home, ".dotnet", "tools")
    Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("PATH") + string Path.PathSeparator + tools)
    requireTools ["jb"]
    let actual = capture "jb" [command; "--version"]
    let actualVersion = Regex.Match(actual, @"(?m)^Version:\s*(\S+)").Groups[1].Value
    if actualVersion <> version then failwith $"JetBrains {version} required; found '{actualVersion}'. Install: dotnet tool install -g JetBrains.ReSharper.GlobalTools --version {version}"
    let caches = insideTmp "JetBrains caches" (arg "--caches-path" ($".tmp/build/{command}-caches") args)
    let common = ["--toolset-path=" + msbuild; "--no-updates"; "--caches-home=" + caches]
    if has "--use-existing-build" args then common @ ["--no-build"; $"--properties=Configuration={configuration};PatchouliUseBuiltExtensionAssembly=true"]
    else
        let artifacts = arg "--artifacts-path" ($".tmp/build/{command}") args
        if artifacts = "" && command = "cleanupcode" then common
        else
            let outputs = insideTmp "JetBrains build outputs" artifacts
            common @ [$"--properties=Configuration={configuration};ArtifactsPath={outputs};UseArtifactsOutput=true"]

let globs key (fallback: string list) args =
    let input = values key args
    let input = if input.IsEmpty then fallback else input
    input |> List.collect (fun value -> value.Split([|','; ';'|], StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim() |> Array.toList) |> String.concat ";"

let blocking (result: System.Text.Json.Nodes.JsonNode) =
    let id = get "ruleId" result
    get "level" result = "error" || id.StartsWith("CSharpWarnings::", StringComparison.Ordinal)
    || List.contains id [".XAMLErrors"; "Xaml.PossibleNullReferenceException"; "AccessToDisposedClosure"; "AccessToModifiedClosure"
                         "AsyncVoidMethod"; "AsyncVoidLambda"; "EmptyGeneralCatchClause"; "PossibleMultipleEnumeration"; "ObjectDisposed"; "SYSLIB1054"]
