#load "lib/Packaging.fsx"
open System
open System.IO
open System.Net.Http
open Common
open Packaging

entry (fun argv ->
    let args = parse ["--skip-publish"] ["--format"; "--runtime"; "--configuration"; "--version"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/package-linux.fsx -- [--format deb|rpm|appimage|flatpak]... [--runtime linux-x64|linux-arm64] [--configuration Release] [--version VERSION] [--skip-publish]"
    else
        requireOS "Linux"
        isolateTemp "packaging"
        let runtime = choice "--runtime" ("linux-" + nativeArchitecture) ["linux-x64"; "linux-arm64"] args
        if runtime <> "linux-" + nativeArchitecture then failwith "Linux native packaging must run on the target architecture"
        let debArch, unixArch = if runtime = "linux-arm64" then "arm64", "aarch64" else "amd64", "x86_64"
        let formats = match values "--format" args with [] -> ["deb"; "rpm"; "appimage"; "flatpak"] | values -> List.distinct values
        for format in formats do if not (List.contains format ["deb"; "rpm"; "appimage"; "flatpak"]) then failwith $"Unknown package format: {format}"
        let wants format = List.contains format formats
        let version, configuration = packageVersion args, configuration args
        let imageTool =
            let check = execute root "which" ["magick"] None true
            if check.ExitCode = 0 then "magick" else "convert"
        requireTools ([imageTool; "tar"; "file"] @ (if has "--skip-publish" args then [] else ["cargo"])
                      @ (if wants "deb" then ["ar"; "zstd"] else []) @ (if wants "rpm" then ["rpmbuild"] else []) @ (if wants "flatpak" then ["flatpak"] else []))
        let publishDir = insideTmp "Publish outputs" ($".tmp/publish/{runtime}")
        if not (has "--skip-publish" args) then
            cleanOutput publishDir |> ignore
            publish publishDir runtime configuration version ""
            File.Copy(rustHelper runtime, Path.Combine(publishDir, "biblatex-helper"), true)
        validatePublish publishDir
        if not (File.Exists(Path.Combine(publishDir, "biblatex-helper"))) then failwith "Missing published biblatex-helper; run without --skip-publish"
        let stage, output = cleanOutput ".tmp/linux-stage", insideTmp "Installer outputs" ".tmp/installer"
        mkdir output
        let appId = "io.github.kwadraten.patchouli"
        let library = Path.Combine(stage, "usr/lib/patchouli")
        copyTree publishDir library
        let bin = Path.Combine(stage, "usr/bin")
        mkdir bin
        File.CreateSymbolicLink(Path.Combine(bin, "patchouli"), "../lib/patchouli/Patchouli.UI") |> ignore
        File.CreateSymbolicLink(Path.Combine(bin, "patchouli-cli"), "../lib/patchouli/patchouli-cli") |> ignore
        for name in ["Patchouli.UI"; "patchouli-cli"; "Patchouli.Extensions.Compiler"; "biblatex-helper"] do executable (Path.Combine(library, name))
        let icon source (destination: string) size =
            mkdir (Path.GetDirectoryName(destination))
            run imageTool [source; "-resize"; $"{size}x{size}"; destination]
        for size in [16; 32; 48; 64; 128; 256; 512] do icon (resolve "logo/icon.png") (Path.Combine(stage, $"usr/share/icons/hicolor/{size}x{size}/apps/{appId}.png")) size
        let desktop = File.ReadAllText(resolve ($"packaging/linux/assets/{appId}.desktop"))
        writeText (Path.Combine(stage, $"usr/share/applications/{appId}.desktop")) (desktop.Replace("__EXEC__", "/usr/bin/patchouli"))
        let metadata = Path.Combine(stage, "usr/share/metainfo")
        mkdir metadata
        File.Copy(resolve ($"packaging/linux/assets/{appId}.metainfo.xml"), Path.Combine(metadata, appId + ".metainfo.xml"))
        File.Copy(resolve "LICENSE", Path.Combine(library, "LICENSE"))
        normalizePermissions stage
        if wants "deb" then
            let work = cleanOutput ".tmp/deb-work"
            let control = Path.Combine(work, "control")
            mkdir control
            let installedSize = Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories) |> Seq.sumBy (fun path -> FileInfo(path).Length) |> fun bytes -> (bytes + 1023L) / 1024L
            writeText (Path.Combine(control, "control")) $"""Package: patchouli
Version: {version}
Section: office
Priority: optional
Architecture: {debArch}
Maintainer: kwadraten <schild0van0man@duck.com>
Installed-Size: {installedSize}
Depends: fontconfig, libx11-6, libice6, libsm6, libicu76 | libicu74 | libicu72, libssl3t64 | libssl3, libgssapi-krb5-2
Description: Personal literature manager for large PDF libraries
 Patchouli is an experimental desktop literature manager for managing,
 digitizing, continuously correcting, and recalling large PDF libraries.
"""
            normalizePermissions control
            for name, directory in ["data", stage; "control", control] do
                let tar = Path.Combine(work, name + ".tar")
                run "tar" ["-cf"; tar; "-C"; directory; "."]
                run "zstd" ["-q"; tar; "-o"; tar + ".zst"]
            writeText (Path.Combine(work, "debian-binary")) "2.0\n"
            let package = Path.Combine(output, $"patchouli-{version}-{runtime}.deb")
            if File.Exists(package) then File.Delete(package)
            runIn work "ar" ["r"; package; "debian-binary"; "control.tar.zst"; "data.tar.zst"]
        if wants "rpm" then
            let top = cleanOutput ".tmp/rpmbuild"
            for name in ["BUILD"; "RPMS"; "SOURCES"; "SPECS"; "SRPMS"] do mkdir (Path.Combine(top, name))
            let spec = Path.Combine(top, "SPECS/patchouli.spec")
            File.Copy(resolve "packaging/linux/rpm/patchouli.spec", spec)
            let source = cleanOutput ".tmp/rpm-source"
            copyTree (Path.Combine(stage, "usr")) (Path.Combine(source, "usr"))
            File.Copy(resolve "LICENSE", Path.Combine(source, "LICENSE"))
            run "tar" ["-czf"; Path.Combine(top, $"SOURCES/patchouli-{version}.tar.gz"); "-C"; source; "usr"; "LICENSE"]
            run "rpmbuild" ["-bb"; "--target"; unixArch; "--define"; "_topdir " + top; "--define"; "app_version " + version; spec]
            for path in Directory.EnumerateFiles(Path.Combine(top, "RPMS"), "*.rpm", SearchOption.AllDirectories) do File.Copy(path, Path.Combine(output, Path.GetFileName(path)), true)
        if wants "appimage" then
            let appDir = cleanOutput ".tmp/Patchouli.AppDir"
            let bin = Path.Combine(appDir, "usr/bin")
            copyTree publishDir bin
            writeText (Path.Combine(appDir, appId + ".desktop")) (desktop.Replace("__EXEC__", "Patchouli.UI"))
            icon (resolve "logo/icon.png") (Path.Combine(appDir, appId + ".png")) 256
            File.CreateSymbolicLink(Path.Combine(appDir, ".DirIcon"), appId + ".png") |> ignore
            // Installed runtime launcher, not repository automation.
            let launcher = Path.Combine(appDir, "AppRun")
            writeText launcher "#!/bin/sh\nHERE=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd)\"\nexport PATH=\"$HERE/usr/bin:$PATH\"\nexec \"$HERE/usr/bin/Patchouli.UI\" \"$@\"\n"
            executable launcher
            normalizePermissions appDir
            let tool = insideTmp "AppImage tool" ($".tmp/appimagetool-{unixArch}.AppImage")
            if not (File.Exists(tool)) then
                use client = new HttpClient(Timeout = TimeSpan.FromMinutes(5.))
                File.WriteAllBytes(tool, client.GetByteArrayAsync($"https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-{unixArch}.AppImage", cancellation.Token).GetAwaiter().GetResult())
                executable tool
            let extracted = cleanOutput ".tmp/appimagetool-extract"
            runIn extracted tool ["--appimage-extract"]
            let previous = Environment.GetEnvironmentVariable("ARCH")
            try
                Environment.SetEnvironmentVariable("ARCH", unixArch)
                runIn extracted (Path.Combine(extracted, "squashfs-root/AppRun")) [appDir; Path.Combine(output, $"Patchouli.NET-{version}-{runtime}.AppImage")]
            finally Environment.SetEnvironmentVariable("ARCH", previous)
        if wants "flatpak" then
            let buildDir, repository = cleanOutput ".tmp/flatpak-manual", cleanOutput ".tmp/flatpak-repo"
            // build-init requires a non-existing build directory.
            Directory.Delete(buildDir)
            run "flatpak" ["build-init"; "--arch=" + unixArch; buildDir; appId; "org.freedesktop.Sdk"; "org.freedesktop.Platform"; "26.08"]
            copyTree (Path.Combine(stage, "usr")) (Path.Combine(buildDir, "files"))
            let entry = Path.Combine(buildDir, $"files/share/applications/{appId}.desktop")
            writeText entry (File.ReadAllText(entry).Replace("Exec=/usr/bin/patchouli", "Exec=patchouli"))
            run "flatpak" ["build-finish"; "--no-exports"; buildDir; "--share=ipc"; "--share=network"; "--socket=x11"; "--socket=pulseaudio"; "--device=dri"; "--filesystem=home"; "--talk-name=org.freedesktop.Notifications"; "--command=patchouli"]
            let metadata = File.ReadAllText(Path.Combine(buildDir, "metadata"))
            if not (metadata.Contains("command=patchouli") && metadata.Contains("[Context]")) then failwith "Flatpak metadata lacks command or permissions"
            for relative in ["share/applications"; "share/icons"] do copyTree (Path.Combine(buildDir, "files", relative)) (Path.Combine(buildDir, "export", relative))
            run "flatpak" ["build-export"; repository; buildDir]
            run "flatpak" ["build-bundle"; repository; Path.Combine(output, $"{appId}-{version}-{unixArch}.flatpak"); appId]
        printfn "Packages: %s" output
    0)
