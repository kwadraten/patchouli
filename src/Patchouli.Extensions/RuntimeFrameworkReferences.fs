namespace Patchouli.Extensions

open System
open System.IO
open System.Text.Json
open System.Collections.Generic
open System.Reflection

/// Compiler references come from the running framework manifest, including self-contained deployments.
/// Application dependencies in TPA are deliberately excluded.
module internal RuntimeFrameworkReferences =
    let private references = lazy (
        let runtimeDirectory =
            Path.GetDirectoryName(typeof<obj>.Assembly.Location)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "The runtime framework directory is unavailable.")
        let manifests = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        match AppContext.GetData("APP_CONTEXT_DEPS_FILES") with
        | :? string as paths ->
            for path in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) do
                manifests.Add(path) |> ignore
        | _ -> ()
        manifests.Add(Path.Combine(runtimeDirectory, "Microsoft.NETCore.App.deps.json")) |> ignore
        match Assembly.GetEntryAssembly() |> Option.ofObj with
        | Some entry -> manifests.Add(Path.ChangeExtension(entry.Location, ".deps.json") |> nonNull) |> ignore
        | None -> ()
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for manifest in manifests do
            if File.Exists manifest then
                use document = JsonDocument.Parse(File.ReadAllText manifest)
                match document.RootElement.TryGetProperty("targets") with
                | true, targets ->
                    for target in targets.EnumerateObject() do
                        for library in target.Value.EnumerateObject() do
                            if library.Name.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal)
                               || library.Name.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal) then
                                match library.Value.TryGetProperty("runtime") with
                                | true, assets ->
                                    for asset in assets.EnumerateObject() do
                                        if asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
                                            names.Add(Path.GetFileName(asset.Name) |> nonNull) |> ignore
                                | _ -> ()
                | _ -> ()
        if names.Count = 0 then
            invalidOp "The .NET runtime framework manifest is unavailable; extension compilation cannot use SDK reference packs."
        names
        |> Seq.map (fun name -> Path.Combine(runtimeDirectory, name))
        |> Seq.filter File.Exists
        |> Seq.append [ typeof<obj>.Assembly.Location; typeof<int option>.Assembly.Location ]
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray)

    let paths () = references.Value
    let compilerFlags () =
        Array.append [| "--noframework"; "--targetprofile:netcore" |]
            (paths () |> Array.map (fun path -> "--reference:" + path))
