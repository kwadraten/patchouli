open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines.Vision

[<Settings>]
type Settings = {
    [<Field("模型文件", Key = "model-path", Kind = ConfigValueKind.FilePath); Default("");
      ManagedFile("doclayout_yolo_docstructbench_imgsz1024.onnx",
          "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/ee7c3d744e5c47c58e267044ac825f95abe69653/doclayout_yolo_docstructbench_imgsz1024.onnx",
          ExpectedBytes = 75324598L,
          Sha256 = "fece9af02f618b603ff7921ccec6861d13e7e1f9830e091dfb7e8ad9311e5b21",
          Version = "ee7c3d744e5c47c58e267044ac825f95abe69653",
          DirectoryKind = "doclayout-yolo-models") >]
    ModelPath: string
}

let private run (settings: Settings) (job: ILayoutRecognitionJob) (_resources: IExtensionResources)
                (token: CancellationToken) : Task =
    task {
        use session = new LayoutRecognitionSession("doclayout-yolo", settings.ModelPath)
        let mutable more = true
        while more do
            let! next = job.NextInputAsync(token)
            match Option.ofObj next with
            | None -> more <- false
            | Some input ->
                let! regions = session.DetectAsync(input.Image, token)
                do! job.SubmitAsync(input, regions, token)
    } :> Task

/// <summary>DocLayout-YOLO image layout recognition.</summary>
/// <patchouli-extension id="layout.doclayout-yolo" kind="layout-recognition" version="1.0.0" host-api="patchouli.extensions/4">
///   <name>DocLayout-YOLO</name>
/// </patchouli-extension>
let extension : ILayoutRecognitionExtension = Extension.createLayoutRecognition<Settings> run
