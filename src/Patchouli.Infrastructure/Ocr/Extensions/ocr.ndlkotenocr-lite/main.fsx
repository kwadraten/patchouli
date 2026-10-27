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
/// <patchouli-extension id="ocr.ndlkotenocr-lite" kind="ocr" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>NDL古典籍OCR-Lite</name>
/// </patchouli-extension>
let extension =
    Extension.createOcrEngine<Settings>
        (definition NativeOcrSession.NdlKotenLiteEngineId)
        (check NativeOcrSession.NdlKotenLiteEngineId)
        (run NativeOcrSession.NdlKotenLiteEngineId)
