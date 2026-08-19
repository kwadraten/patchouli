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
