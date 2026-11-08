open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines.PdfText

[<Settings>]
type Settings = {
    [<Field("Line gap factor", Key = "line-gap-factor",
        Description = "Paragraph/column block-axis cut threshold as a fraction of the median line height.");
        Default(0.45); Range(0.0, 10.0)>]
    LineGapFactor: double
    [<Field("Column gap factor", Key = "column-gap-factor",
        Description = "Inline-axis cut threshold for layout columns as a fraction of the median line height.");
        Default(1.2); Range(0.0, 20.0)>]
    ColumnGapFactor: double
    [<Field("Word gap factor", Key = "word-gap-factor",
        Description = "Inline gap that inserts a word break, as a fraction of the median glyph advance.");
        Default(0.32); Range(0.0, 5.0)>]
    WordGapFactor: double
    [<Field("Detect mixed directions", Key = "detect-mixed-directions",
        Description = "Detect per-line RTL, vertical and rotated local frames under the dominant frame.");
        Default(true)>]
    DetectMixedDirections: bool
    [<Field("OCR untrusted glyphs", Key = "ocr-untrusted",
        Description = "Request bounded image OCR for native characters that are not Unicode-convertible. Off by default: damaged native text is a parse failure, not silent partial content.");
        Default(false)>]
    OcrUntrusted: bool
}

let private toOptions (settings: Settings) =
    PdfTextLayerParseOptions(
        LineGapFactor = settings.LineGapFactor,
        ColumnGapFactor = settings.ColumnGapFactor,
        WordGapFactor = settings.WordGapFactor,
        DetectMixedDirections = settings.DetectMixedDirections,
        SupplementWithOcr = settings.OcrUntrusted)

/// <summary>Native PDFium text layer segmented with recursive XY cuts — pure text, no rendering or models.</summary>
/// <patchouli-extension id="pdf-text.native-xycut" kind="pdf-text-layer" version="1.1.0" host-api="patchouli.extensions/4">
///   <name>Native text layer (XY Cut)</name>
/// </patchouli-extension>
let extension : IPdfTextLayerExtension =
    PdfTextLayerExtensions.CreateXyCut<Settings>(toOptions)
