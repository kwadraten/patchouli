#load "lib/Packaging.fsx"
open System
open System.IO
open Common
open Packaging

entry (fun argv ->
    let args = parse [] ["--runtime"; "--configuration"; "--version"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/package-macos.fsx -- [--runtime osx-arm64|osx-x64] [--configuration Release] [--version VERSION]"
    else
        requireOS "macOS"
        requireTools ["clang"; "cargo"; "plutil"; "sips"; "iconutil"; "codesign"; "hdiutil"; "file"]
        isolateTemp "packaging"
        let runtime = choice "--runtime" ("osx-" + nativeArchitecture) ["osx-arm64"; "osx-x64"] args
        let architecture = if runtime = "osx-arm64" then "arm64" else "x86_64"
        let version, configuration = packageVersion args, configuration args
        let publishDir = cleanOutput ($".tmp/publish/{runtime}")
        let appDir = cleanOutput ".tmp/macos/Patchouli.NET.app"
        let contents = Path.Combine(appDir, "Contents")
        let macos, resources = Path.Combine(contents, "MacOS"), Path.Combine(contents, "Resources")
        mkdir macos
        mkdir resources
        let helperName = "libpatchouli-macos-fs.dylib"
        let helper = insideTmp "Native helper output" (".tmp/packaging/" + runtime + "/" + helperName)
        mkdir (Path.GetDirectoryName(helper))
        run "clang" ["-dynamiclib"; "-framework"; "Foundation"; "-arch"; architecture; "-install_name"; "@rpath/" + helperName; "-o"; helper; resolve "sidecar/patchouli-macos-fs/patchouli_macos_fs.m"]
        publish publishDir runtime configuration version ""
        copyTree publishDir macos
        File.Copy(helper, Path.Combine(macos, helperName), true)
        File.Copy(rustHelper runtime, Path.Combine(macos, "biblatex-helper"), true)
        for name in ["Patchouli.UI"; "patchouli-cli"; "Patchouli.Extensions.Compiler"; "biblatex-helper"] do executable (Path.Combine(macos, name))
        let defaults = Path.Combine(macos, "appsettings.json")
        if not (File.Exists(defaults)) then failwith "Published appsettings.json is missing"
        File.Move(defaults, Path.Combine(resources, "appsettings.json"))
        let plist = Path.Combine(contents, "Info.plist")
        // Not sandboxed/App Store: retain standard TCC NS*FolderUsageDescription prompts.
        writeText plist (File.ReadAllText(resolve "packaging/macos/Info.plist.template").Replace("__VERSION__", version))
        run "plutil" ["-lint"; plist]
        let iconRoot = insideTmp "Icon output" (".tmp/packaging/icons-" + Guid.NewGuid().ToString("N"))
        let iconset = Path.Combine(iconRoot, "AppIcon.iconset")
        mkdir iconset
        try
            for size in [16; 32; 128; 256; 512] do
                run "sips" ["-z"; string size; string size; resolve "logo/icon.png"; "--out"; Path.Combine(iconset, $"icon_{size}x{size}.png")]
                let doubleSize = size * 2
                run "sips" ["-z"; string doubleSize; string doubleSize; resolve "logo/icon.png"; "--out"; Path.Combine(iconset, $"icon_{size}x{size}@2x.png")]
            run "iconutil" ["-c"; "icns"; iconset; "-o"; Path.Combine(resources, "AppIcon.icns")]
        finally Directory.Delete(iconRoot, true)
        let pdfium = Path.Combine(macos, "libpdfium.dylib")
        if not (File.Exists(pdfium)) then failwith "Published PDFium native library is missing"
        let description = capture "file" [pdfium]
        if not (description.Contains("Mach-O") && description.Contains(architecture)) then failwith $"Wrong PDFium format or architecture: {description}"
        if Directory.EnumerateFiles(macos, "*", SearchOption.AllDirectories) |> Seq.exists (fun path -> Path.GetFileName(path).Contains("mupdf", StringComparison.OrdinalIgnoreCase)) then failwith "Forbidden MuPDF payload remains in bundle"
        // Real native rendering is validated on the packaging host, with recorded evidence.
        dotnetScript "scripts/run-tests.fsx" ["--configuration"; configuration; "--filter"; "FullyQualifiedName~RealPdfRendererTests|FullyQualifiedName~MinerUUploadPreparerTests.UploadAndExtract_splits_pdf_when_page_limit_would_be_exceeded"]
        // MacOS is a nested-code location: managed DLLs/data live in Resources.
        for path in Directory.GetFileSystemEntries(macos) do
            if not (File.Exists(path) && capture "file" ["-b"; path] |> _.Contains("Mach-O")) then
                let name = Path.GetFileName(path)
                let target = Path.Combine(resources, name)
                if Directory.Exists(path) then Directory.Move(path, target) else File.Move(path, target)
                File.CreateSymbolicLink(path, "../Resources/" + name) |> ignore
        let defaultsCount = Directory.EnumerateFiles(resources, "appsettings.json", SearchOption.AllDirectories) |> Seq.length
        if defaultsCount <> 1 || not (File.Exists(Path.Combine(resources, "appsettings.json"))) then failwith "appsettings.json must exist exactly once in Resources"
        // Ad-hoc integrity signing, not Developer ID trust/notarization; see ADR 0017.
        for binary in Directory.EnumerateFiles(macos) do
            if binary <> Path.Combine(macos, "Patchouli.UI") && not (File.GetAttributes(binary).HasFlag(FileAttributes.ReparsePoint)) && (capture "file" ["-b"; binary]).Contains("Mach-O") then
                run "codesign" ["--force"; "--sign"; "-"; "--preserve-metadata=entitlements"; binary]
        run "codesign" ["--force"; "--sign"; "-"; appDir]
        run "codesign" ["--verify"; "--deep"; "--strict"; "--verbose=4"; appDir]
        let dmg = insideTmp "Disk image output" ($".tmp/installer/Patchouli.NET-{version}-{runtime}.dmg")
        mkdir (Path.GetDirectoryName(dmg))
        let staging = cleanOutput (".tmp/packaging/dmg-" + Guid.NewGuid().ToString("N"))
        try
            copyTree appDir (Path.Combine(staging, "Patchouli.NET.app"))
            Directory.CreateSymbolicLink(Path.Combine(staging, "Applications"), "/Applications") |> ignore
            let mutable built = false
            for attempt in 1..5 do
                if not built then
                    cancellation.Token.ThrowIfCancellationRequested()
                    let result = execute root "hdiutil" ["create"; "-volname"; "Patchouli.NET"; "-srcfolder"; staging; "-ov"; "-format"; "UDZO"; dmg] None false
                    built <- result.ExitCode = 0
                    if not built && attempt < 5 then Threading.Tasks.Task.Delay(3000, cancellation.Token).GetAwaiter().GetResult()
            if not built then failwith "hdiutil create failed after five attempts"
            run "codesign" ["--force"; "--sign"; "-"; dmg]
            dotnetScript "scripts/verify-macos-dmg.fsx" ["--dmg"; dmg]
            printfn "%s" dmg
        finally Directory.Delete(staging, true)
    0)
