#load "lib/Common.fsx"
open System
open System.IO
open Common

entry (fun argv ->
    let args = parse [] ["--tag"; "--assets-root"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/publish-release.fsx -- --tag TAG [--assets-root .tmp/release-assets]"
    else
        let tag = arg "--tag" "" args
        if String.IsNullOrWhiteSpace(tag) || tag.StartsWith("-") then failwith "A release tag is required"
        requireTools ["gh"]
        let directory = insideTmp "Release assets" (arg "--assets-root" ".tmp/release-assets" args)
        let extensions = [".exe"; ".dmg"; ".deb"; ".rpm"; ".AppImage"; ".flatpak"]
        let assets = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) |> Seq.filter (fun path -> List.contains (Path.GetExtension(path)) extensions) |> Seq.sort |> Seq.toList
        for extension in extensions do
            let matches = assets |> List.filter (fun path -> Path.GetExtension(path) = extension)
            if matches.Length <> 1 || FileInfo(matches.Head).Length = 0L then failwith $"Expected one non-empty {extension} release asset"
        let checksums = [for asset in assets do
                            let checksum = asset + ".sha256"
                            writeText checksum (sha256 asset + "  " + Path.GetFileName(asset) + "\n")
                            checksum]
        let exists = (execute root "gh" ["release"; "view"; tag] None true).ExitCode = 0
        let command = if exists then ["release"; "upload"; tag] @ assets @ checksums @ ["--clobber"] else ["release"; "create"; tag] @ assets @ checksums @ ["--title"; "Patchouli.NET " + tag; "--generate-notes"]
        run "gh" command
    0)
