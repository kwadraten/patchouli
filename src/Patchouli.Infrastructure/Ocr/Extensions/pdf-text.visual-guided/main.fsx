open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines.PdfText

[<Settings>]
type Settings = {
    [<Field("版面识别模型", Key = "model-id", Description = "版面识别模型扩展 ID");
      Default("layout.doclayout-yolo")>]
    ModelId: string
    [<Field("Layout detection DPI", Key = "layout-dpi",
        Description = "DPI used when the page is rendered once for layout detection.");
        Default(200); Range(50, 600)>]
    LayoutDpi: int
    [<Field("Minimum native coverage", Key = "min-native-coverage",
        Description = "Fraction of a text region's area native glyphs must cover before it stays native.");
        Default(0.25); Range(0.0, 1.0)>]
    MinNativeCoverage: double
    [<Field("OCR non-text regions", Key = "recognize-non-text",
        Description = "Request image OCR for figure, table and formula regions.");
        Default(true)>]
    RecognizeNonTextRegions: bool
    [<Field("Line gap factor", Key = "line-gap-factor",
        Description = "Paragraph merge threshold inside a region, as a fraction of the median line height.");
        Default(0.45); Range(0.0, 10.0)>]
    LineGapFactor: double
    [<Field("Word gap factor", Key = "word-gap-factor",
        Description = "Inline gap that inserts a word break, as a fraction of the median glyph advance.");
        Default(0.32); Range(0.0, 5.0)>]
    WordGapFactor: double
    [<Field("Detect mixed directions", Key = "detect-mixed-directions",
        Description = "Detect per-line RTL, vertical and rotated local frames inside regions.");
        Default(true)>]
    DetectMixedDirections: bool
}

let private toOptions (settings: Settings) =
    PdfTextLayerParseOptions(
        LineGapFactor = settings.LineGapFactor,
        WordGapFactor = settings.WordGapFactor,
        DetectMixedDirections = settings.DetectMixedDirections,
        LayoutModelId = settings.ModelId,
        LayoutDpi = settings.LayoutDpi,
        MinNativeCoverage = settings.MinNativeCoverage,
        RecognizeNonTextRegions = settings.RecognizeNonTextRegions,
        // Bounded region OCR is this provider's selected policy.
        SupplementWithOcr = true)

/// <summary>Layout-guided PDF text layer: the shared detector steers native-glyph preservation and bounded region OCR.</summary>
/// <patchouli-extension id="pdf-text.visual-guided" kind="pdf-text-layer" version="1.2.0" host-api="patchouli.extensions/4">
///   <name>Visual-guided text layer</name>
/// </patchouli-extension>
let extension : IPdfTextLayerExtension =
    PdfTextLayerExtensions.CreateVisualGuided<Settings>(toOptions)
