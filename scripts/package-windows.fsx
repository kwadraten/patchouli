#load "lib/Packaging.fsx"
open System
open System.IO
open System.Text.RegularExpressions
open Common
open Packaging

let productionAppId = "{{DCBB7F21-2751-4C90-A9B4-9459523CFF70}"
let smokeAppId = "{{E3F7D819-616F-4A16-B65C-17C78FDE024E}"

entry (fun argv ->
    let args = parse ["--smoke"] ["--runtime"; "--configuration"; "--version"; "--publish-directory"; "--installer-directory"; "--output-base-name"; "--build-label"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/package-windows.fsx -- [--runtime win-x64] [--configuration Release] [--version VERSION] [--smoke] [--publish-directory .tmp/...] [--installer-directory .tmp/...] [--output-base-name NAME] [--build-label LABEL]"
    else
        requireOS "Windows"
        isolateTemp "packaging"
        let runtime = choice "--runtime" "win-x64" ["win-x64"] args
        let version, configuration = packageVersion args, configuration args
        let publishDir = insideTmp "Publish outputs" (arg "--publish-directory" ($".tmp/publish/{runtime}") args)
        let installerDir = insideTmp "Installer outputs" (arg "--installer-directory" ".tmp/installer" args)
        let buildLabel = arg "--build-label" "" args
        let defaultName = $"Patchouli.NET-{version}-{runtime}" + (if has "--smoke" args then "-smoke-setup" else "-setup")
        let outputName = arg "--output-base-name" defaultName args
        for label in [outputName; buildLabel] do
            if label <> "" && not (Regex.IsMatch(label, "^[A-Za-z0-9._-]+$")) then failwith "Output/build labels may contain only ASCII letters, digits, dots, underscores and hyphens"
        let iscc = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)] |> List.map (fun directory -> Path.Combine(directory, "Inno Setup 6", "ISCC.exe")) |> List.tryFind File.Exists
        let iscc = iscc |> Option.defaultWith (fun () -> failwith "Inno Setup 6 not found")
        requireTools ["cargo"]
        if has "--publish-directory" args && Directory.Exists(publishDir) then failwith "Explicit publish directory already exists; choose a unique .tmp output"
        cleanOutput publishDir |> ignore
        mkdir installerDir
        publish publishDir runtime configuration version buildLabel
        File.Copy(rustHelper runtime, Path.Combine(publishDir, "biblatex-helper.exe"), true)
        let appId = if has "--smoke" args then smokeAppId else productionAppId
        run iscc [$"/DSourceDir={publishDir}"; $"/DOutputDir={installerDir}"; $"/DAppVersion={version}"; $"/DAppId={appId}"; $"/DOutputBaseFilename={outputName}"; resolve "packaging/windows/Patchouli.NET.iss"]
        let installer = Path.Combine(installerDir, outputName + ".exe")
        if not (File.Exists(installer)) then failwith $"Installer not created: {installer}"
        let sourceStatus = capture "git" ["status"; "--porcelain=v1"]
        let sourceFiles = capture "git" ["ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z"]
        let sourceHashes = sourceFiles.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right)) |> Array.filter (resolve >> File.Exists) |> Array.map (fun path -> path + ":" + sha256 (resolve path)) |> String.concat "\n"
        writeJson (Path.Combine(installerDir, outputName + ".manifest.json")) (jobj [
            "version", jvalue version; "runtime", jvalue runtime; "configuration", jvalue configuration; "build_label", jvalue buildLabel
            "created_utc", jvalue (utcNow()); "source_head", jvalue (capture "git" ["rev-parse"; "HEAD"]); "source_dirty", jvalue (sourceStatus <> "")
            "source_fingerprint", jvalue (hashText sourceHashes); "installer", jvalue installer; "installer_sha256", jvalue (sha256 installer)
            "installer_bytes", jvalue (FileInfo(installer).Length); "installed_by_packaging_script", jvalue false ])
        printfn "%s" installer
    0)
