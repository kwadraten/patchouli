namespace Patchouli.Extensions

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Patchouli.Extensions.Contracts

/// Immutable, content-addressed output of the extension compiler process.
[<CLIMutable>]
type ExtensionArtifactManifest =
    { SchemaVersion: int
      PackageId: string
      PackageFingerprint: string
      ToolchainIdentity: string
      AssemblyFile: string
      AssemblySha256: string
      SourceAndDependencySha256: string
      DependencyFiles: string array | null
      CheckResultJson: string }

/// Result shared by static compilation, package activation, and the schema generator.
[<Sealed>]
type ExtensionCompilationResult(checkResult: ExtensionCheckResult, artifactDirectory: string,
                               assemblyPath: string, cacheHit: bool) =
    member _.CheckResult = checkResult
    member _.ArtifactDirectory = artifactDirectory
    member _.AssemblyPath = assemblyPath
    member _.CacheHit = cacheHit

/// Public artifact layout shared by runtime compilation and the built-in schema generator.
/// A bundle directory contains extension.dll, extension.pdb (optional), and check.json.
module ExtensionArtifact =
    [<Literal>]
    let ManifestFileName = "check.json"

    [<Literal>]
    let AssemblyFileName = "extension.dll"

    let manifestPath (directory: string) = Path.Combine(directory, ManifestFileName)
    let assemblyPath (directory: string) = Path.Combine(directory, AssemblyFileName)

    /// The snapshot fingerprint already covers source text and binary package files.
    let sourceHash (package: ExtensionPackageSnapshot) = package.Fingerprint

    /// Content-address one compiled artifact by immutable source and compiler/ABI identity.
    let artifactKey (package: ExtensionPackageSnapshot) (toolchainIdentity: string) =
        let material = sourceHash package + "|" + toolchainIdentity
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes material)
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Runtime-visible identity shared with the compiler and built-in extension installer.
    let runtimeToolchainIdentity () = RuntimeFrameworkReferences.toolchainIdentity()

    let tryRead (directory: string) =
        let path = manifestPath directory
        if not (File.Exists path) then None
        else
            try
                match JsonSerializer.Deserialize<ExtensionArtifactManifest>(File.ReadAllText path) with
                | null -> None
                | manifest when manifest.SchemaVersion <> 1 -> None
                | manifest -> Some manifest
            with :? JsonException -> None

    /// Fast local-cache validation. Trust content-addressed local artifacts, but verify metadata and
    /// that every referenced file still exists; full hashes are recorded at write time, not repeated
    /// on both check and load.
    let tryReadCached (package: ExtensionPackageSnapshot) (artifactRoot: string)
                      (toolchainIdentity: string) : ExtensionCompilationResult option =
        let metadataValid =
            try
                let metadata = ExtensionEntryMetadata.Parse(package)
                metadata.HostApi = ExtensionHostApi.Current
            with _ -> false
        if not metadataValid then None
        else
            let root = Path.GetFullPath artifactRoot
            let key = artifactKey package toolchainIdentity
            let compiledDirectory = Path.Combine(root, key)
            let bundledDirectory =
                match Path.GetDirectoryName root with
                | null -> ""
                | parent -> Path.Combine(parent, ".bundled", key)
            let tryAt directory =
                match tryRead directory with
                | Some manifest when manifest.PackageId = package.Id
                                     && manifest.PackageFingerprint = sourceHash package
                                     && manifest.SourceAndDependencySha256 = sourceHash package
                                     && manifest.ToolchainIdentity = toolchainIdentity
                                     && manifest.AssemblyFile = AssemblyFileName
                                     && not (String.IsNullOrWhiteSpace manifest.CheckResultJson) ->
                    let assembly = assemblyPath directory
                    let dependenciesExist =
                        match manifest.DependencyFiles with
                        | null -> false
                        | dependencies ->
                            let fullRoot = Path.GetFullPath directory + string Path.DirectorySeparatorChar
                            dependencies
                            |> Array.forall (fun entry ->
                                let separator = entry.IndexOf('|')
                                if separator <= 0 then false
                                else
                                    let relative = entry.Substring(0, separator).Replace('/', Path.DirectorySeparatorChar)
                                    let path = Path.GetFullPath(Path.Combine(directory, relative))
                                    path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && File.Exists path)
                    if not (File.Exists assembly) || not dependenciesExist then None
                    else
                        match JsonSerializer.Deserialize<ExtensionCheckResult>(manifest.CheckResultJson) with
                        | null -> None
                        | check when check.Succeeded ->
                            Some(ExtensionCompilationResult(check, directory, assembly, true))
                        | _ -> None
                | _ -> None
            match tryAt compiledDirectory with
            | Some result -> Some result
            | None -> tryAt bundledDirectory

    let writeManifest (directory: string) (manifest: ExtensionArtifactManifest) =
        Directory.CreateDirectory directory |> ignore
        let temporary = manifestPath directory + ".tmp"
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, JsonSerializerOptions(WriteIndented = true)))
        File.Move(temporary, manifestPath directory, true)
