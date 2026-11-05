open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines.Vision

[<Settings>]
type Settings = {
    [<Field("模型文件", Key = "model-path", Kind = ConfigValueKind.FilePath); Default("");
      ManagedFile("inference.onnx",
          "https://huggingface.co/PaddlePaddle/PP-DocLayoutV3_onnx/resolve/46bbdf188bb0a772c08aed74882ce7e51a8f1ea6/inference.onnx",
          ExpectedBytes = 130502049L,
          Sha256 = "45bf71750b00739a41fc209f132eb104a4d6b5bb29483c9078164d8b87cf28ba",
          Version = "46bbdf188bb0a772c08aed74882ce7e51a8f1ea6",
          DirectoryKind = "pp-doclayout-v3-models") >]
    ModelPath: string
}

let private run (settings: Settings) (job: ILayoutRecognitionJob) (_resources: IExtensionResources)
                (token: CancellationToken) : Task =
    task {
        use session = new LayoutRecognitionSession("pp-doclayout-v3", settings.ModelPath)
        let mutable more = true
        while more do
            let! next = job.NextInputAsync(token)
            match Option.ofObj next with
            | None -> more <- false
            | Some input ->
                let! regions = session.DetectAsync(input.Image, token)
                do! job.SubmitAsync(input, regions, token)
    } :> Task

/// <summary>PP-DocLayoutV3 image layout recognition.</summary>
/// <patchouli-extension id="layout.pp-doclayout-v3" kind="layout-recognition" version="1.0.0" host-api="patchouli.extensions/4">
///   <name>PP-DocLayoutV3</name>
/// </patchouli-extension>
let extension : ILayoutRecognitionExtension = Extension.createLayoutRecognition<Settings> run
