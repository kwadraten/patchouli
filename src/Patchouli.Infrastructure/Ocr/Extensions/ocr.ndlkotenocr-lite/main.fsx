#load "Support.fsx"

open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines
open BuiltInOcr.Support

[<Settings>]
type Settings = {
    [<Field("Model directory", Key = "model-path", Kind = ConfigValueKind.DirectoryPath); Default("");
      // ndlkotenocr-lite's published manifest pins byte sizes only; the pinned digests stay
      // honestly absent rather than invented.
      ManagedFile("model/rtmdet-s-1280x1280.onnx",
          "https://raw.githubusercontent.com/ndl-lab/ndlkotenocr-lite/master/src/model/rtmdet-s-1280x1280.onnx",
          ExpectedBytes = 40188733L, DirectoryKind = "ndl-koten-models");
      ManagedFile("model/parseq-ndl-32x384-tiny-10.onnx",
          "https://raw.githubusercontent.com/ndl-lab/ndlkotenocr-lite/master/src/model/parseq-ndl-32x384-tiny-10.onnx",
          ExpectedBytes = 42442247L, DirectoryKind = "ndl-koten-models");
      ManagedFile("config/ndl.yaml",
          "https://raw.githubusercontent.com/ndl-lab/ndlkotenocr-lite/master/src/config/ndl.yaml",
          ExpectedBytes = 276L, DirectoryKind = "ndl-koten-models");
      ManagedFile("config/NDLmoji.yaml",
          "https://raw.githubusercontent.com/ndl-lab/ndlkotenocr-lite/master/src/config/NDLmoji.yaml",
          ExpectedBytes = 42426L, DirectoryKind = "ndl-koten-models")>]
    ModelPath: string
    [<Field("Model identifier", Key = "model-id"); Default("ndl-koten-default")>]
    ModelId: string
    [<Field("Engine parameters (JSON)", Key = "parameters-json", Hidden = true,
        Description = "Advanced NDLKotenOCR-Lite parameters as a JSON object."); Default("")>]
    ParametersJson: string
}

/// <summary>NDLKotenOCR-Lite (ONNX) classical Japanese text recognition extension.</summary>
/// <patchouli-extension id="ocr.ndlkotenocr-lite" kind="ocr" version="1.1.0" host-api="patchouli.extensions/4">
///   <name>NDL古典籍OCR-Lite</name>
/// </patchouli-extension>
let extension =
    Extension.createOcrEngine<Settings>
        (definition NativeOcrSession.NdlKotenLiteEngineId)
        (check NativeOcrSession.NdlKotenLiteEngineId)
        (run NativeOcrSession.NdlKotenLiteEngineId)
