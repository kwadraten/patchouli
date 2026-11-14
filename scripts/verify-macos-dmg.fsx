#load "lib/Common.fsx"
open System
open System.IO
open System.Text.RegularExpressions
open Common

entry (fun argv ->
    let args = parse [] ["--dmg"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/verify-macos-dmg.fsx -- --dmg FILE"
    else
        requireOS "macOS"
        let dmg = Path.GetFullPath(arg "--dmg" "" args)
        if not (has "--dmg" args) || not (File.Exists(dmg)) then failwith "--dmg must name an existing disk image"
        requireTools ["hdiutil"; "codesign"; "plutil"]
        isolateTemp "packaging"
        run "hdiutil" ["verify"; dmg]
        run "codesign" ["--verify"; "--strict"; "--verbose=4"; dmg]
        let mountDir = insideTmp "DMG mount" (".tmp/packaging/verify-dmg-" + Guid.NewGuid().ToString("N"))
        mkdir mountDir
        let mutable attached = false
        try
            run "hdiutil" ["attach"; dmg; "-readonly"; "-nobrowse"; "-mountpoint"; mountDir]
            attached <- true
            let app = Path.Combine(mountDir, "Patchouli.NET.app")
            let resources = Path.Combine(app, "Contents/_CodeSignature/CodeResources")
            if not (File.Exists(resources)) || FileInfo(resources).Length = 0L then failwith "Bundle signature resources are missing"
            run "plutil" ["-lint"; resources]
            run "codesign" ["--verify"; "--deep"; "--strict"; "--verbose=4"; app]
            let details = execute root "codesign" ["--display"; "--verbose=4"; app] None false
            let text = details.Stdout + details.Stderr
            if details.ExitCode <> 0 || not (Regex.IsMatch(text, "(?m)^Signature=adhoc\\r?$")) || not (Regex.IsMatch(text, "(?m)^Sealed Resources version=2 ")) || text.Contains("Info.plist=not bound") then
                failwith "Expected an ad-hoc signed bundle with bound Info.plist and sealed resources"
            printfn "Verified final disk image: %s" dmg
        finally
            if attached then
                // Cleanup must still run when cancellation has been requested.
                let info = Diagnostics.ProcessStartInfo("hdiutil", UseShellExecute = false)
                info.ArgumentList.Add("detach")
                info.ArgumentList.Add(mountDir)
                use child = Diagnostics.Process.Start(info)
                child.WaitForExit()
                if child.ExitCode <> 0 then eprintfn "Could not detach DMG mount: %s" mountDir
            if Directory.Exists(mountDir) && not (Directory.EnumerateFileSystemEntries(mountDir) |> Seq.isEmpty |> not) then Directory.Delete(mountDir)
    0)
