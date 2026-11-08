#load "Support.fsx"

open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines
open BuiltInOcr.Support

[<Settings>]
type Settings = {
    [<Field("Model directory", Key = "model-path", Kind = ConfigValueKind.DirectoryPath); Default("");
      // RapidOCR v3.9.2 default ONNX set (upstream default_models.yaml). The classifier is
      // optional: only required when the use-cls orientation stage is enabled.
      ManagedFile("PP-OCRv6_det_small.onnx",
          "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx",
          ExpectedBytes = 9929594L,
          Sha256 = "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f",
          Version = "RapidOCR/v3.9.2", DirectoryKind = "rapidocr-models");
      ManagedFile("PP-OCRv6_rec_small.onnx",
          "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx",
          ExpectedBytes = 21234383L,
          Sha256 = "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884",
          Version = "RapidOCR/v3.9.2", DirectoryKind = "rapidocr-models");
      ManagedFile("ch_ppocr_mobile_v2.0_cls_mobile.onnx",
          "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv4/cls/ch_ppocr_mobile_v2.0_cls_mobile.onnx",
          ExpectedBytes = 585532L,
          Sha256 = "e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c",
          Version = "RapidOCR/v3.9.2", DirectoryKind = "rapidocr-models",
          RequiredWhenKey = "use-cls", RequiredWhenValue = "true")>]
    ModelPath: string
    [<Field("Model identifier", Key = "model-id"); Default("rapidocr-default")>]
    ModelId: string
    [<Field("Minimum text score", Key = "text-score"); Default(0.5); Range(0.0, 1.0)>]
    TextScore: double
    [<Field("Text detection", Key = "use-det"); Default(true)>]
    UseDet: bool
    [<Field("Orientation classification", Key = "use-cls"); Default(true)>]
    UseCls: bool
    [<Field("Text recognition", Key = "use-rec"); Default(true)>]
    UseRec: bool
    [<Field("Image preprocessing", Key = "use-preprocess-img"); Default(true)>]
    UsePreprocessImg: bool
    [<Field("Minimum image side length", Key = "min-side-len"); Default(30); Range(1, 100000)>]
    MinSideLen: int
    [<Field("Maximum image side length", Key = "max-side-len"); Default(2000); Range(1, 100000)>]
    MaxSideLen: int
    [<Field("Vertical line padding", Key = "use-vertical-padding"); Default(true)>]
    UseVerticalPadding: bool
    [<Field("Minimum box height", Key = "min-height"); Default(30); Range(1, 100000)>]
    MinHeight: int
    [<Field("Vertical width/height ratio", Key = "width-height-ratio"); Default(8); Range(1, 1000)>]
    WidthHeightRatio: int
    [<Field("Word-level boxes", Key = "return-word-box"); Default(false)>]
    ReturnWordBox: bool
    [<Field("Single-character boxes", Key = "return-single-char-box"); Default(false)>]
    ReturnSingleCharBox: bool
    [<Field("Recognizer dictionary path", Key = "rec-keys-path"); Default("")>]
    RecKeysPath: string
    [<Field("Intra-op threads", Key = "intra-op-num-threads"); Default(-1); Range(-1, 1024)>]
    IntraOpNumThreads: int
    [<Field("Inter-op threads", Key = "inter-op-num-threads"); Default(-1); Range(-1, 1024)>]
    InterOpNumThreads: int
    [<Field("Engine parameters (JSON)", Key = "parameters-json", Hidden = true,
        Description = "Advanced RapidOCR parameters as a JSON object; typed fields override individual keys."); Default("")>]
    ParametersJson: string
}

/// <summary>RapidOCR (ONNX) text recognition extension.</summary>
/// <patchouli-extension id="ocr.rapidocr" kind="ocr" version="1.1.0" host-api="patchouli.extensions/4">
///   <name>RapidOCR</name>
/// </patchouli-extension>
let extension =
    Extension.createOcrEngine<Settings>
        (definition NativeOcrSession.RapidOcrEngineId)
        (check NativeOcrSession.RapidOcrEngineId)
        (run NativeOcrSession.RapidOcrEngineId)
