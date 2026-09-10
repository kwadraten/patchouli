# Integrate ndlkotenocr-lite via C# ONNX Runtime Port

Status: accepted

## Context

`ndl-lab/ndlkotenocr-lite` is a lightweight Japanese classical-text OCR pipeline based on two ONNX models:
- `rtmdet-s-1280x1280.onnx` for text-line detection
- `parseq-ndl-32x384-tiny-10.onnx` for recognition

The repository also provides `src/reading_order`, configuration YAML files (`src/config/ndl.yaml`, `src/config/NDLmoji.yaml`), and a Python reference implementation. Patchouli needs a local OCR option alongside the existing cloud-based MinerU provider.

## Decision

Integrate `ndlkotenocr-lite` as a **C# native ONNX Runtime** adapter (`local_library` kind in `IRealOcrAdapter`), rather than bundling a Python sidecar.

Reasons:
- Avoid packaging and versioning a full Python runtime.
- Keep the provider inside the existing `Patchouli.Infrastructure` / `Patchouli.Ocr` boundaries.
- Reuse the already-present `SkiaSharp` dependency for image decode/crop/resize.
- Models and config files are downloaded on demand from upstream GitHub raw URLs instead of being bundled in the installer, reducing initial package size and avoiding a hundred-megabyte binary in version control.
- Model outputs must be normalized into `OcrDocumentTreeCandidate` and imported through the existing shared importer, consistent with ADR `0014`.

## Consequences

- A new `Microsoft.ML.OnnxRuntime` package dependency is added to `Directory.Packages.props`.
- The first-time user experience requires a network download (about 82 MB total) from the settings page.
- The adapter must implement detection NMS, reading-order sorting, and PARSeq decoding in C#, matching the upstream Python behavior.
- Model files are stored under `{DataDirectory}/models/ndl-koten/`; temporary OCR working files go under `{CacheDirectory}/ocr-work/ndl-koten/`.
- The MinerU working root moves from `%TEMP%/patchouli/mineru` to `{CacheDirectory}/ocr-work/mineru/` so that both local and cloud OCR temp files are managed from the same settings section.
- The upstream models and configs are licensed under CC-BY-4.0; the UI must display attribution and license notice before download/use.


## Implemented Model And Local-file Management Contract

Recorded from completed PRD V3-T9 (2026-09-10). The `ndl-koten` adapter supports page/region images and vertical text. Detection, right-to-left vertical reading order and recognition feed the shared working/commit importer; providers never write `document_boxes` directly.

The fixed four-file manifest includes URLs and expected sizes. Downloads use temporary files followed by atomic rename, support progress/cancellation, and do not promise resume. Missing models produce `missing_model_path` with a settings recovery path. Model-dependent end-to-end checks require downloaded models; synthetic reading-order/download tests do not certify full upstream-output parity.

“OCR 引擎” settings select registered adapters independently for document/page/region operations, with default fallback for absent or invalid selections. “本地文件管理” is an action-only settings section: it reports model/MinerU/NDL working-directory usage, offers model download/re-download and directory actions, and confirms deletion/cleanup through danger-mode `ConfirmDialog`. Cleanup is limited to application-managed contents, refreshes displayed usage, and never deletes user originals. Old OS-temp MinerU leftovers remain the operating system's responsibility.

Attribution and license notice for the NDL Koten OCR Lite models and configs are shown once, in the “关于” page third-party components list (`NDL Koten OCR Lite`, CC-BY-4.0). The local-file management page and the model download confirmation dialog intentionally do not repeat the attribution; `NdlKotenModelFiles.Attribution`/`LicenseName` remain available for capability reporting.

Detection filtering is deliberately stricter than upstream. The exported RTMDet model embeds a permissive NMS (score > 0.001, IoU 0.65, at most 200 boxes per class), so the runtime gate is the true filter: the detector defaults to the upstream runtime confidence threshold of 0.3, and `NdlKotenOcrPipeline.FilterDetections` then uniformly drops every box narrower or shorter than 5 px and de-duplicates near-duplicates by keeping the higher-confidence box of any pair with IoU ≥ 0.7, returning survivors in their original order. Upstream `ndlkotenocr-lite` has no line-level IoU de-duplication (its `NMSBoxes` call is commented out; it relies on the embedded NMS), and its `min_bbox_size=5` applies only to the `text_block`-derived line export path, not to standalone line detections. Applying both filters to all detected boxes is an intentional hardening over the upstream behavior, justified by the embedded NMS being so loose that overlapping and tiny noise boxes otherwise survive into recognition.

A page where the engine succeeds but finds no text is not an error: per-page engines (such as this adapter) behave the same as the MinerU whole-document path and emit a `DocumentBoxType.LogicalPage` placeholder (payload `Blank page (OCR returned no content).`, diagnostic `blank_page_placeholder`). The page result is `Succeeded`, so one blank physical page no longer fails the whole batch or blocks commit/import of the successful pages.
