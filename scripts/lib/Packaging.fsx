#load "Common.fsx"
open System
open System.IO
open System.Runtime.InteropServices
open Common

let version = "0.4.1"
let nativeArchitecture =
    match RuntimeInformation.ProcessArchitecture with Architecture.X64 -> "x64" | Architecture.Arm64 -> "arm64" | arch -> failwith $"Unsupported architecture: {arch}"
let configuration args = choice "--configuration" "Release" ["Debug"; "Release"] args
let packageVersion args =
    let value = arg "--version" version args
    let value = if value.StartsWith("v", StringComparison.Ordinal) then value.Substring(1) else value
    if not (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[A-Za-z0-9.-]+)?$")) then failwith "Invalid package version"
    value
let suffix = if OperatingSystem.IsWindows() then ".exe" else ""

let cleanOutput path =
    let path = insideTmp "Packaging output" path
    if Directory.Exists(path) then Directory.Delete(path, true)
    mkdir path
    path

let rec copyTree source destination =
    mkdir destination
    for path in Directory.EnumerateFileSystemEntries(source) do
        let target = Path.Combine(destination, Path.GetFileName(path))
        let attributes = File.GetAttributes(path)
        if attributes.HasFlag(FileAttributes.ReparsePoint) then
            let link = FileInfo(path).LinkTarget
            if isNull link then failwith $"Unsupported reparse point: {path}"
            File.CreateSymbolicLink(target, link) |> ignore
        elif attributes.HasFlag(FileAttributes.Directory) then copyTree path target
        else
            File.Copy(path, target, true)
            if not (OperatingSystem.IsWindows()) then File.SetUnixFileMode(target, File.GetUnixFileMode(path))

let executable (path: string) =
    if not (OperatingSystem.IsWindows()) then
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)

let normalizePermissions directory =
    if not (OperatingSystem.IsWindows()) then
        let directoryMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute
        let fileMode = UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead ||| UnixFileMode.OtherRead
        let rec visit (path: string) =
            File.SetUnixFileMode(path, directoryMode)
            for child in Directory.EnumerateFileSystemEntries(path) do
                let attributes = File.GetAttributes(child)
                if not (attributes.HasFlag(FileAttributes.ReparsePoint)) then
                    if attributes.HasFlag(FileAttributes.Directory) then visit child
                    else
                        let mode = File.GetUnixFileMode(child)
                        let mask = UnixFileMode.UserExecute ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute
                        File.SetUnixFileMode(child, if mode &&& mask <> enum 0 then directoryMode else fileMode)
        visit directory

let validatePublish directory =
    for name in ["Patchouli.UI"; "patchouli-cli"; "Patchouli.Extensions.Compiler"] do
        for file in [name + suffix; name + ".dll"; name + ".deps.json"; name + ".runtimeconfig.json"] do
            if not (File.Exists(Path.Combine(directory, file))) then failwith $"Published process component not found: {file}"
    let migrations = Path.Combine(directory, "migrations")
    if not (Directory.Exists(migrations)) then failwith "Published migrations directory not found"
    for file in Directory.EnumerateFiles(migrations) do
        let name = Path.GetFileName(file)
        if name.Contains("layout", StringComparison.OrdinalIgnoreCase) || List.contains name ["005_create_pages_and_layout.sql"; "014_add_table_cell_metadata.sql"; "024_add_layout_revision_source_basis.sql"] then
            failwith $"Published migrations still contain legacy layout schema files: {name}"

let publish directory runtime configuration version buildLabel =
    let properties = if buildLabel = "" then [] else [$"-p:InformationalVersion={version}+{buildLabel}"; "-p:IncludeSourceRevisionInInformationalVersion=false"]
    run "dotnet" (["publish"; resolve "src/Patchouli.UI/Patchouli.UI.csproj"; "-m:1"; "-c"; configuration; "-r"; runtime;
                   "--self-contained"; "true"; "-p:Version=" + version; "-p:PublishSingleFile=false"; "-p:DebugType=None";
                   "-p:DebugSymbols=false"; "-p:BuildInParallel=false"; "-p:UseSharedCompilation=false"; "-o"; directory] @ properties)
    validatePublish directory

let rustHelper runtime =
    let target =
        match runtime with
        | "osx-arm64" -> "aarch64-apple-darwin" | "osx-x64" -> "x86_64-apple-darwin"
        | "linux-x64" -> "x86_64-unknown-linux-gnu" | "linux-arm64" -> "aarch64-unknown-linux-gnu"
        | "win-x64" -> "x86_64-pc-windows-msvc" | "win-arm64" -> "aarch64-pc-windows-msvc"
        | _ -> failwith $"Unsupported Rust target runtime: {runtime}"
    run "cargo" ["build"; "--release"; "--locked"; "--target"; target; "--manifest-path"; resolve "sidecar/biblatex-helper/Cargo.toml"]
    let path = resolve ($"sidecar/biblatex-helper/target/{target}/release/biblatex-helper{suffix}")
    if not (File.Exists(path)) then failwith $"Rust helper was not built: {path}"
    path
