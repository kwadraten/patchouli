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
      // ndlocr-lite release 1.3.1, pinned to immutable upstream commit 6feccb29.
      ManagedFile("model/deim-s-1024x1024.onnx",
          "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src/model/deim-s-1024x1024.onnx",
          ExpectedBytes = 40256763L,
          Sha256 = "c156ce0c4e704bc3bf7e4016d0a87b949cffa8b3724f4b4cc696b8284c3c7373",
          Version = "6feccb29e33c2467ea48894e66502a7788ae2b2d", DirectoryKind = "ndlocr-lite-models");
      ManagedFile("model/parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx",
          "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src/model/parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx",
          ExpectedBytes = 36457393L,
          Sha256 = "9e651bae4c1a4d5254da1127e86e82e21ef62d5339b37e62d4a3d3d30831772d",
          Version = "6feccb29e33c2467ea48894e66502a7788ae2b2d", DirectoryKind = "ndlocr-lite-models");
      ManagedFile("model/parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx",
          "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src/model/parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx",
          ExpectedBytes = 37808553L,
          Sha256 = "49cea9db4552f19eb05c8ee202fcf74714977749b2f4c9376b127fde41b07a99",
          Version = "6feccb29e33c2467ea48894e66502a7788ae2b2d", DirectoryKind = "ndlocr-lite-models");
      ManagedFile("model/parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx",
          "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src/model/parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx",
          ExpectedBytes = 42588187L,
          Sha256 = "06462b0dbd5b0b8508545c8c3d485cf20dbf4ffa652fe145e69c9e7457080602",
          Version = "6feccb29e33c2467ea48894e66502a7788ae2b2d", DirectoryKind = "ndlocr-lite-models");
      ManagedFile("config/ndl.yaml",
          "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src/config/ndl.yaml",
          ExpectedBytes = 299L,
          Sha256 = "0c2a6a184dd322375b76f2ce3842f8ac555d53edad0ab63655c013f4c471c5a0",
          Version = "6feccb29e33c2467ea48894e66502a7788ae2b2d", DirectoryKind = "ndlocr-lite-models");
      ManagedFile("config/NDLmoji.yaml",
          "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src/config/NDLmoji.yaml",
          ExpectedBytes = 42434L,
          Sha256 = "f6ad5a2de444b495155866af811cf1a98309dcae3225db802767ea531a2dc529",
          Version = "6feccb29e33c2467ea48894e66502a7788ae2b2d", DirectoryKind = "ndlocr-lite-models")>]
    ModelPath: string
    [<Field("Model identifier", Key = "model-id"); Default("ndlocr-lite-default")>]
    ModelId: string
    [<Field("Engine parameters (JSON)", Key = "parameters-json", Hidden = true,
        Description = "Advanced NDLOCR-Lite parameters as a JSON object."); Default("")>]
    ParametersJson: string
}

/// <summary>NDLOCR-Lite (ONNX) modern Japanese text recognition extension.</summary>
/// <patchouli-extension id="ocr.ndlocr-lite" kind="ocr" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>NDLOCR-Lite</name>
/// </patchouli-extension>
let extension =
    Extension.createOcrEngine<Settings>
        (definition NativeOcrSession.NdlOcrLiteEngineId)
        (check NativeOcrSession.NdlOcrLiteEngineId)
        (run NativeOcrSession.NdlOcrLiteEngineId)
