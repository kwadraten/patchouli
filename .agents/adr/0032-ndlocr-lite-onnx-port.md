# Native NDLOCR-Lite ONNX Port

Status: accepted

## Context

`ndl-lab/ndlocr-lite` is the modern NDL Lab Japanese OCR engine and is a **different
project from `ndlkotenocr-lite`** (ADR 0025): it uses a DEIM-S detector, a three-tier
PARSeq recognition cascade (24x256 / 24x384 / 24x768) and an XY-cut reading-order
solver, not the RTMDet/PARSeq classical-text pair. Patchouli previously listed it only
as an exploratory engine in the PRD; it needed a real local OCR implementation that
ships without a Python runtime, alongside NDL Koten (ADR 0025) and RapidOCR (ADR 0031).

## Decision

Integrate NDLOCR-Lite as a **managed .NET ONNX Runtime adapter** (`local_library` kind in
`IRealOcrAdapter`) with engine id `ndlocr-lite`, reusing the existing
`Microsoft.ML.OnnxRuntime` and `SkiaSharp` dependencies.

- **Pinned upstream.** Models, `config/ndl.yaml` and `config/NDLmoji.yaml` are taken from
  `ndl-lab/ndlocr-lite` release `1.3.1`, pinned to commit
  `6feccb29e33c2467ea48894e66502a7788ae2b2d`. Six manifest entries are downloaded at
  runtime: the DEIM-S detector, the three PARSeq checkpoints, the class list and the
  character set. Integrities are byte-length and (per the model manifest) hash checked
  during download; already-present files are re-verified.
- **Detection.** `NdlLiteDetector` pads to a black square, resizes, normalizes with
  ImageNet mean/std, feeds the model's declared input size (800x800 from metadata, not
  the misleading `1024` filename) plus the second `orig_target_sizes`/`input_shape`
  tensor, keeps scores strictly above `0.25`, and resolves the one-based DEIM label to a
  zero-based `ndl.yaml` class index.
- **Recognition cascade.** `NdlLiteRecognitionCascade` reproduces upstream `ocr.py`: the
  detector's `PRED_CHAR_CNT` selects the entry tier, each tier escalates when the decoded
  text reaches `25` / `45`, and long horizontal lines at `>= 98` characters are split in
  half and re-read with the 24x768 model. Tiers run in parallel with Python's
  `ThreadPoolExecutor` default `min(32, cpu + 4)`.
- **Layout and reading order.** `NdlLiteLayoutOrderer` ports `convert_to_xml_string3`
  plus `reading_order/order/reorder.py`: text blocks adopt their contained lines, nested
  blocks fold into their parent, page children are ranked by the XY-cut solver and each
  text block sorts its own lines top-to-bottom or right-to-left.
- **Storage and provisioning.** Models live under `{DataDirectory}/models/ndlocr-lite`
  and the work directory under `{CacheDirectory}/ocr-work/ndlocr-lite`. The settings
  "Local Files" section can download, re-download, open and clear the model directory.
  Readiness validates the model files and reports `missing_model_path` with a
  `rebind_model_path` action.
- **Attribution.** Models and configs are CC-BY-4.0; the "About" third-party list shows
  `NDLOCR-Lite` -> `https://github.com/ndl-lab/ndlocr-lite`, matching ADR 0025's
  Koten entry.

## Consequences

- NDLOCR-Lite runs entirely in-process; no Python runtime, package index or sidecar is
  involved, and the engine participates in the shared OCR preset/queue/readiness
  pipeline like the other local engines.
- The model download is the initial cost; files are never bundled in the installer.
- `NdlLiteWorkDirectory` is currently a managed placeholder, mirroring the pre-existing
  NDL Koten work-directory behavior.

## Known deviations from upstream

The port targets functional parity, not bit-exact output. The following differences are
intentional or accepted and are recorded so the parity claim stays honest:

- **M2 — DEIM downscale is not Pillow-antialiased.** The detector resizes with Skia's
  cubic sampler (`SKCubicResampler.Mitchell`, no mipmap), while upstream pads and calls
  Pillow's default `Image.resize`, whose bicubic support is scaled by the minification
  factor. Aliased strokes can therefore change detector boxes/scores. This mirrors the
  accepted NDL Koten `RtmdetDetector` approximation (and upstream `rtmdet.py` also uses
  Pillow); it is a pre-existing systemic approximation, not a new regression, and the
  manifest/ADR wording must not be read as pixel parity.
- **M3 — `GroupWarichu` and `smooth_order` omitted.** The WARICHUBLOCK grouping in
  `sort_lines` and the optional `smooth_order` Hamiltonian refinement are not reproduced.
  The deterministic XY-cut order plus the local line sort (stable, top-to-bottom or
  right-to-left) are used instead.
- **N11 — premultiplied alpha used as-is.** Crops and padding use
  `SKColorType.Bgra8888`/`SKAlphaType.Premul` and composite over black, whereas upstream
  copies raw BGR channels and ignores alpha entirely. Transparent PNGs will differ; this
  is inconsistent with the RapidOCR port, which unpremultiplies.
- **N12 — DEIM label 0 is dropped.** The port skips `label < 1`, but upstream's
  `classes[int(label) - 1]` maps label `0` to `classes[-1]` (`line_title`). Filtering
  label 0 explicitly is a deliberate hardening over that quirk.
- **Zero-LINE fallback simplified.** When a container has no LINE detections, the port
  synthesizes a line at the container box; this is a simplified version of upstream's
  no-LINE fallback path.
