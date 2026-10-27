// lib/Patchouli.Ocr.Engines.dll contains GapTreeTextLayerAnalyzer.cs, a direction-aware
// port of the GapTree sort algorithm, ported from upstream Python source (gap_tree.py)
// and adapted to Patchouli's WritingFrame coordinate model.
//
// Upstream: 【间隙·树·排序算法】 GapTree_Sort_Algorithm
// Author:   hiroi-sora
// Source:   https://github.com/hiroi-sora/GapTree_Sort_Algorithm
// Commit:   281987caf36a9f5a7b40e8fcb690a7634502cab4
//
// MIT License
//
// Copyright (c) 2024 hiroi-sora
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

#r "lib/Patchouli.Ocr.Engines.dll"

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
/// <patchouli-extension id="pdf-text.native-xycut" kind="pdf-text-layer" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Native text layer (XY Cut)</name>
/// </patchouli-extension>
let extension : IPdfTextLayerExtension =
    PdfTextLayerExtensions.CreateXyCut<Settings>(toOptions)
