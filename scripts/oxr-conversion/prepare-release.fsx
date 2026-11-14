#load "../lib/Common.fsx"
open System
open System.IO
open Common

let expected = Map.ofList [
    "oxr-vision-448.onnx", "21693fa5b4cc52e4b8ecdec3dfad45425d563fd938a0ad17534c66529f45562f"
    "oxr-vision-448.onnx.data", "d8e46e2e061276b18553106fbff7a4e60c374a41f11cd97204381c1833c804da"
    "oxr-language.onnx", "04d32cfe162a813fb04b9c535eb74c7989a011eedb96077ce17751857b16ade9"
    "oxr-language.onnx.data", "c4f3d40e82573811af472aeccfefa97d05881efa5463175e7d02564c5d1cc962"
    "oxr-text-embedding.onnx", "9a17f7461aff5e2682c646c64a8983d0725a3f61c800e2b7c1e1deb9a71dc2a4"
    "oxr-text-embedding.onnx.data", "066a9dbb18ff50788d8b3f9f2259ec82d0ec9577f8ba37cb4a4263a06ff9ee45"
    "oxr-layout.onnx", "f5db09a5bd81fb1e419cdfc98d5e1ca0862e6a146c3317876d7f73ac32002e48"
    "oxr-indicators.f32", "1a319aa1d58d977f63e6c7eb7e4ddcef5f82f2f180a90933f2a5110fedf1409f" ]
let required = ["LICENSE"; "NOTICE"; "README.md"; "LICENSES/Apache-2.0.txt"; "LICENSES/D-FINE-NOTICES.md"; "LICENSES/D-FINE-Apache-2.0.txt"; "tokenizer.json"; "config.json"; "preprocessor_config.json"; "oxr-prompt.json"; "layout-metadata.json"]

entry (fun argv ->
    let args = parse [] ["--model-directory"] argv
    if has "help" args then printfn "Usage: dotnet fsi scripts/oxr-conversion/prepare-release.fsx -- [--model-directory DIR]"
    else
        let directory = Path.GetFullPath(arg "--model-directory" ".tmp/oxr-validation/upload" args)
        for name in (expected |> Map.toList |> List.map fst) @ required do
            if not (File.Exists(Path.Combine(directory, name))) then failwith $"OXR release is missing '{name}'"
        let files = [
            for path in Directory.EnumerateFiles(directory, "*", EnumerationOptions(RecurseSubdirectories = true, AttributesToSkip = enum 0)) |> Seq.sort do
                if File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then failwith $"Symbolic link release file refused: {path}"
                if Path.GetFileName(path) <> "manifest.json" then
                    let name, digest = Path.GetRelativePath(directory, path).Replace('\\', '/'), sha256 path
                    match Map.tryFind name expected with Some pinned when pinned <> digest -> failwith $"OXR release file '{name}' has an unexpected SHA-256" | _ -> ()
                    jobj ["path", jvalue name; "bytes", jvalue (FileInfo(path).Length); "sha256", jvalue digest] :> System.Text.Json.Nodes.JsonNode ]
        let manifest = jobj ["schema_version", jvalue 1; "source_repository", jvalue "SoMarkAI/OXR-1.0";
                             "source_revision", jvalue "c708407f9d2b4df658f2543d9132b62bb7221df4";
                             "source_recognition_sha256", jvalue "795bb2f1faac579f58829913f2c606202531a96c1203e19ef7a70b103fb7ea51";
                             "source_layout_sha256", jvalue "dff78ccf18439955362ca44a69eb28fb0b40222a7d278860589682de0176dc17";
                             "recognition_validation", jvalue "ONNX Runtime CPU recognized HELLO OCR 123 from a 448x448 image"; "files", jarray files]
        writeJson (Path.Combine(directory, "manifest.json")) manifest
        printfn "Verified %d OXR release files and wrote manifest.json" files.Length
    0)
