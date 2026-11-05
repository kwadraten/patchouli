#r "lib/Patchouli.Ocr.Engines.dll"

open System.Threading
open System.Threading.Tasks
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines.OvisOcr3

[<Settings>]
type Settings = {
    [<Field("Model directory", Key = "model-path", Kind = ConfigValueKind.DirectoryPath); Default("");
      ManagedFile("ovisocr3_int4_single.onnx",
          "https://huggingface.co/arlenecc/OvisOCR3-ONNX-int4/resolve/e654df1eed2c3b4a902eac7b458bddd7cb338e1b/onnx/ovisocr3_int4_single.onnx",
          ExpectedBytes = 1216759027L,
          Sha256 = "7bafbef06e85847963d46fe520ba9767d18c7d2abdad91a25ce2f036558bcb3e",
          Version = "arlenecc/OvisOCR3-ONNX-int4@e654df1", DirectoryKind = "ovisocr3-models");
      ManagedFile("tokenizer.json",
          "https://huggingface.co/ATH-MaaS/OvisOCR3/resolve/4833d5d2afa6e769ee933f024504d67ace80a24c/tokenizer.json",
          ExpectedBytes = 12807982L,
          Sha256 = "5f9e4d4901a92b997e463c1f46055088b6cca5ca61a6522d1b9f64c4bb81cb42",
          Version = "ATH-MaaS/OvisOCR3@4833d5d", DirectoryKind = "ovisocr3-models");
      ManagedFile("document_structured.txt",
          "https://huggingface.co/ATH-MaaS/OvisOCR3/resolve/4833d5d2afa6e769ee933f024504d67ace80a24c/prompts/document_structured.txt",
          ExpectedBytes = 1110L,
          Sha256 = "1b6139ec72888e087c8af75f2e2dfb78b425e15ae1c1529940b96137613d1db1",
          Version = "ATH-MaaS/OvisOCR3@4833d5d", DirectoryKind = "ovisocr3-models")>]
    ModelPath: string
    [<Field("Maximum generated tokens", Key = "max-new-tokens"); Default(4096); Range(1.0, 8192.0)>]
    MaxNewTokens: int
}

let private check (config: ExtensionConfigSnapshot) (_settings: Settings)
                  (_resources: IExtensionResources) (_token: CancellationToken) : Task<Result> =
    Task.FromResult(OvisOcr3OnnxSession.CheckReadiness(config))

let private run (_settings: Settings) (job: IOcrJob) (_resources: IExtensionResources)
                (token: CancellationToken) : Task =
    task {
        use session = new OvisOcr3OnnxSession(job.Info.Config)
        let engine: OcrPageEngine =
            { Recognize = fun _page input image cancellationToken ->
                Task.FromResult(session.Recognize(input, image, cancellationToken)) }
        do! OcrJob.runPages job engine token
    } :> Task

/// <summary>Offline OvisOCR3 OCR through the local INT4 ONNX model.</summary>
/// <patchouli-extension id="ocr.ovisocr3" kind="ocr" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>OvisOCR3 (ONNX)</name>
/// </patchouli-extension>
let extension : IOcrExtension =
    Extension.createOcrEngine<Settings> (OvisOcr3OnnxSession.GetDefinition()) check run
