# Native RapidOCR ONNX Port

Status: accepted

## Context

Patchouli needed a general-purpose local OCR engine alongside the classical-Japanese
NDL Koten and NDLOCR-Lite ports. An earlier attempt shelled out to a checked-in Python
runner that imported the user-installed `rapidocr` package. That approach was rejected:
it required a user-managed Python runtime, spawned a process per page, depended on a
non-deterministic `pip install "rapidocr>=3.9.0"`, and could not be shipped as a single
cross-platform binary.

## Decision

Integrate RapidOCR as a **managed .NET ONNX Runtime adapter** (`local_library` kind in
`IRealOcrAdapter`), reusing the `Microsoft.ML.OnnxRuntime` and `SkiaSharp` dependencies
already present for the NDL Koten port.

- **Pinned model set.** The adapter uses the upstream default ONNX models of RapidOCR
  v3.9.2: `PP-OCRv6_det_small.onnx` (DB detection), `PP-OCRv6_rec_small.onnx` (CTC
  recognition) and `ch_ppocr_mobile_v2.0_cls_mobile.onnx` (PP-LCNet 0/180
  classification). `RapidOcrModelFiles` records each file's URL, byte length and SHA256;
  downloads are size- and hash-verified before an atomic rename.
- **In-process pipeline.** `RapidOcrPipeline` reproduces upstream `main.py`: global
  min/max-side resize, vertical letterbox padding, DB detection, perspective line crops
  (cubic, border replicate, CCW rotation at `h/w >= 1.5`), optional classification, and
  aspect-ratio batching (6 lines, `max_wh_ratio`, `int(48 * max_wh_ratio)` width) for
  recognition. CTC decoding drops index-0 blanks and repeated tokens, uses the model's
  embedded `character` metadata (blank at index 0, trailing space), and rounds the mean
  confidence to five decimals.
- **No native CV dependency.** OpenCV `findContours`/`minAreaRect`/`fillPoly` and
  pyclipper round-unclip are replaced by managed connected components, convex hulls,
  rotating-calipers minimum-area rectangles, scanline scoring and a sampled round
  offset. The Emgu.CV path used by the upstream C# reference is avoided because its
  runtime package is Windows-only.
- **Storage and provisioning.** Models live under `{DataDirectory}/models/rapidocr` and
  the (currently unused) work directory under `{CacheDirectory}/ocr-work/rapidocr`. The
  settings "Local Files" section can download, re-download, open and clear the model
  directory; readiness reports `missing_model_path` with a `rebind_model_path` action
  until the required files are present. The classifier is only required when `useCls`
  is true, so a `useCls=false` preset can run with the detector and recognizer alone.
- **Preset surface.** Presets keep the upstream `Global.*` options
  (`textScore`, `useDet`, `useCls`, `useRec`, `usePreprocessImg`, `minSideLen`,
  `maxSideLen`, `useVerticalPadding`, `minHeight`, `widthHeightRatio`), `modelRootDir`,
  `recKeysPath` and the ONNX Runtime thread counts. `useDet`/`useRec` must both be true,
  and `returnWordBox`/`returnSingleCharBox` are rejected because word boxes are not
  implemented. Upstream `modelType`/`langType`/`ocrVersion` keys (and their model-routing
  behavior) are intentionally not honored: the pinned v3.9.2 model set is fixed, so
  those keys are ignored rather than silently selecting a model.

## Consequences

- The `tools/rapidocr-runner` Python bridge, `RapidOcrRunner` process plumbing and the
  corresponding `.csproj` copy rules are removed. No Python runtime is required.
- Model downloads are ~32 MB and are pinned to a specific upstream release instead of
  following whatever the package index currently ships.
- RapidOCR models are Apache-2.0 PaddleOCR derivatives; attribution is shown in the
  "About" third-party list.

## Known deviations from upstream

These are deliberate, bounded differences from RapidOCR v3.9.2; the pipeline is
upstream-equivalent "within interpolation rounding", not bit-exact:

- **N1 — unclip tessellation.** The round offset is a 64-direction sampled Minkowski
  hull, whereas pyclipper uses Clipper's default `arcTolerance = 0.25` (~10 steps per
  full circle). Pre-truncation boxes can differ by up to roughly 0.5 px on curved
  corners.
- **N2 — box score rasterization.** `box_score_fast` uses an even-odd ray cast where
  upstream calls `cv2.fillPoly` (a boundary-inclusive scanline). The mean probability,
  and therefore the `box_thresh` decision, can differ by a fraction of a percent on
  marginal regions.
- **N3 — contour order.** Connected components plus holes replace
  `findContours(RETR_LIST)`, so enumeration order differs; the `max_candidates = 1000`
  truncation can therefore keep a different subset when a page yields more than 1000
  regions.
- **N9 — detection score order.** Patchouli keeps scores attached to boxes when applying
  `sorted_boxes`; upstream reorders only the boxes and leaves the detection-score array
  in its original order. Upstream discards detection scores from the final output, so
  this is not observable through the API.
- **N17 — ignored preset keys.** `modelType`, `langType`, `ocrVersion` and the routing
  they drive are not implemented (see the preset-surface decision above).
