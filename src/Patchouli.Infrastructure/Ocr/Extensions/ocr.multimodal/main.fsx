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

open System.Threading
open System.Threading.Tasks
open Patchouli.Core.Results
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk
open Patchouli.Ocr.Engines

/// Frozen per-run configuration. The flattened keys land in the job's pinned snapshot exactly as
/// MultimodalOcrOptions reads them; "model" carries the ExtensionModelSelection JSON that
/// ResolveLlm("ocr", selection) consumes on the host side.
[<Settings>]
type Settings = {
    [<Model("OCR model", Key = "model", FollowDefault = "ocr")>]
    Model: ExtensionModelSelection option
    [<Field("OCR model id", Key = "model-id",
        Description = "Model name on the resolved OCR provider; empty uses the provider's configured OCR model.");
      Default("")>]
    ModelId: string
    [<Field("Layout model file", Key = "model-path", Kind = ConfigValueKind.FilePath,
        Description = "Local ONNX layout weights; download in Settings > Local Files."); Default("");
      // The layout-model choice selects which declared file is the weights; both are required
      // only when local layout detection is enabled and the run actually feeds full-page inputs
      // (an explicit region crop bypasses detection entirely).
      ManagedFile("inference.onnx",
          "https://huggingface.co/PaddlePaddle/PP-DocLayoutV3_onnx/resolve/46bbdf188bb0a772c08aed74882ce7e51a8f1ea6/inference.onnx",
          ExpectedBytes = 130502049L,
          Sha256 = "45bf71750b00739a41fc209f132eb104a4d6b5bb29483c9078164d8b87cf28ba",
          Version = "46bbdf188bb0a772c08aed74882ce7e51a8f1ea6",
          DirectoryKind = "pp-doclayout-v3-models",
          WhenKey = "layout-model", WhenValue = "pp-doclayout-v3",
          RequiredWhenKey = "use-local-layout", RequiredWhenValue = "true",
          RequiredScopes = "document,pages");
      ManagedFile("doclayout_yolo_docstructbench_imgsz1024.onnx",
          "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/ee7c3d744e5c47c58e267044ac825f95abe69653/doclayout_yolo_docstructbench_imgsz1024.onnx",
          ExpectedBytes = 75324598L,
          Sha256 = "fece9af02f618b603ff7921ccec6861d13e7e1f9830e091dfb7e8ad9311e5b21",
          Version = "ee7c3d744e5c47c58e267044ac825f95abe69653",
          DirectoryKind = "doclayout-yolo-models",
          WhenKey = "layout-model", WhenValue = "doclayout-yolo",
          RequiredWhenKey = "use-local-layout", RequiredWhenValue = "true",
          RequiredScopes = "document,pages")>]
    ModelPath: string
    [<Field("Local layout detection", Key = "use-local-layout",
        Description = "Detect layout regions locally, then recognize each region with the multimodal model.");
      Default(true)>]
    UseLocalLayout: bool
    [<Field("Layout model", Key = "layout-model", Kind = ConfigValueKind.Choice,
        Choices = [| "pp-doclayout-v3"; "doclayout-yolo" |]); Default("pp-doclayout-v3")>]
    LayoutModel: string
    [<Field("Region concurrency", Key = "concurrency",
        Description = "Maximum parallel region recognition requests."); Default(4); Range(1, 16)>]
    Concurrency: int
    [<Field("Reasoning effort", Key = "reasoning-effort", Kind = ConfigValueKind.Choice,
        Choices = [| "default"; "none"; "low"; "medium"; "high"; "max" |]); Default("default")>]
    ReasoningEffort: string
    [<Field("Image detail", Key = "image-detail", Kind = ConfigValueKind.Choice,
        Choices = [| "auto"; "low"; "high" |]); Default("high")>]
    ImageDetail: string
    [<Field("Advanced JSON options", Key = "parameters-json", Kind = ConfigValueKind.MultilineText,
        Hidden = true,
        Description = "Advanced multimodal OCR parameters as a JSON object; typed fields override individual keys.");
      Default("")>]
    ParametersJson: string
}

/// Non-executing, branch-neutral readiness probe: validates the frozen configuration and the OCR
/// model binding without creating an ONNX session or calling a provider. Layout weights are NOT
/// probed here — region/direct jobs run without them; a full-page input in layout mode fails
/// explicitly at first detection instead (MultimodalOcrSession.CheckLayoutReadiness is the
/// scope-aware companion for a full-capability probe).
let private check (config: ExtensionConfigSnapshot) (_settings: Settings) (resources: IExtensionResources)
                   (_cancellationToken: CancellationToken) : Task<Result> =
    Task.FromResult(MultimodalOcrSession.CheckReadiness(config, resources))

/// The task-level run: one session bound to the job's immutable configuration feeds the shared
/// page driver. The layout detector stays lazy until a full-page input needs it, explicit region
/// inputs bypass detection entirely, and disposal here deterministically ends every lazily
/// created resource once the driver's calls have exited.
let private run (_settings: Settings) (job: IOcrJob) (resources: IExtensionResources)
                (cancellationToken: CancellationToken) : Task =
    task {
        use session = new MultimodalOcrSession(job.Info.Config)
        let engine : OcrPageEngine =
            { Recognize = fun page input image token ->
                session.RecognizeAsync(page, input, image, resources, token, job.Progress) }
        do! OcrJob.runPages job engine cancellationToken
    } :> Task

/// <summary>Multimodal hosted-vision OCR extension.</summary>
/// <patchouli-extension id="ocr.multimodal" kind="ocr" version="1.0.0" host-api="patchouli.extensions/3">
///   <name>Multimodal</name>
/// </patchouli-extension>
let extension : IOcrExtension =
    Extension.createOcrEngine<Settings>
        (MultimodalOcrSession.GetDefinition())
        check
        run
