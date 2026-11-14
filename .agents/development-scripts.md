# SDK-only repository automation

All development, test, generator, packaging and release entry points use .NET 10's
bundled F# Interactive. Run `dotnet fsi --warnaserror+ scripts/<name>.fsx -- <options>`
from any working directory (use an absolute script path outside the checkout).
Each entry point supports `--help`. There are no shell wrappers, Python/jq
dependencies, or `#r "nuget:..."` restores for this automation.

## Entry points

| Script | Purpose / important options |
| --- | --- |
| `run-tests.fsx` | Restore/build the selected project once, then run recorded sequential batches. `--suite Full`, `--project`, `--filter`, `--no-build --skip-restore`, `--expected-cases-file`, `--build-rust-helper`. |
| `summarize-test-run.fsx` | Parse TRX/JUnit, preserving Theory parameter names and duplicate rows. `--trx`, `--project`, `--expected`, `--out`, repeatable `--log`. |
| `run-perf.fsx` | `--profile smoke\|full --check`, `--ui`, repeated budget probes with `--full-budget-check` / `--ui-budget-check`. |
| `cleanup-code.fsx` | Pinned JetBrains cleanup; repeatable `--include`, `--configuration`, `--use-existing-build`. |
| `inspect-code.fsx` | Pinned JetBrains inspection and blocking-rule evaluation in .NET, without jq; repeatable `--exclude`. |
| `generate-palettes.fsx` | Radix 3.0.0 assets; cached npm tarball extracted using .NET. Semantic C# template lives in `scripts/lib/PaletteSemantics.txt`. |
| `update-agent-catalog.fsx` | ACP registry plus the single adapter table in `scripts/lib/AgentCatalog.fsx`; `--refresh`, `--check`, `--output-path`. Preserves explicit catalog-preserve blocks. |
| `prepare-ovisocr3-model.fsx` | `--model-directory`; pinned sizes, hashes and HTTP byte ranges; atomic verified installation, no overwrite of invalid existing files. |
| `oxr-conversion/plan-runpod.fsx` | Offline `--action Plan` by default. Explicit `--check-account`, `--action Create\|Get\|Stop`, `--pod-id`, `--public-key-file`. Credentials come from repository `.env`, never printed. Create allocates billable resources. |
| `oxr-conversion/prepare-release.fsx` | `--model-directory`; verify pinned OXR export and generate `manifest.json`. |
| `package-windows.fsx` | Inno Setup installer, isolated `--smoke` identity and source/package fingerprint manifests. |
| `package-linux.fsx` | Repeatable `--format deb\|rpm\|appimage\|flatpak`; default all four. `--skip-publish` reuses the complete published tree including Rust helper. |
| `package-macos.fsx` | Build native helper, assemble/sign app and DMG, run recorded native-rendering tests and verify final mounted image. |
| `verify-macos-dmg.fsx` | `--dmg`; verify ad-hoc signature, sealed resources and bound Info.plist inside the image. |
| `publish-release.fsx` | CI release upload using `gh`, one nonempty asset per platform/format plus SHA-256 sidecars. Requires explicit `--tag`. |

## Paths and cancellation

Projects, baselines, catalogs, suite manifests and automation output options resolve
against the repository root. Model directory options and standalone summarizer input
paths retain current-directory semantics. All temporary work stays under `.tmp/`.
Test run IDs must be simple filenames; existing run directories are refused so an
old TRX cannot turn a new failed invocation into a pass. Output traversal and
symlink/reparse ancestors are rejected. Ctrl+C/SIGTERM cancels child processes and
their trees; interrupted recorded runs remain `incomplete`.

Recorded runs write `run.json`, `console.log`, per-batch TRX/summary files and
`cases.txt` under `.tmp/test-runs/`. Validation rejects missing/empty results,
failed/unknown outcomes despite exit 0, truncated runs, overlapping resource
batches, missing required cases, unexpected skips and expected-multiset drift.

## External tool dependencies

FSI itself needs only the .NET SDK, not a separate F# SDK. Tools invoked by a task
remain dependencies of that task:

- Cleanup/inspection: `dotnet tool install -g JetBrains.ReSharper.GlobalTools --version 2026.1.4`.
- Native packaging and optional test helper staging: Cargo/Rust. Packaging uses
  `--locked` and an explicit Rust target matching the RID; cross-architecture macOS
  builds require the target installed with `rustup target add`.
- Windows: Inno Setup 6 in a standard Program Files directory; x64 installer.
- macOS: Xcode Command Line Tools (`clang`, Foundation), `sips`, `iconutil`,
  `plutil`, `codesign`, `hdiutil`, `file`. Default native `osx-arm64` / `osx-x64`.
  Signing is ad-hoc, not notarized or Developer ID trusted (ADR 0017).
- Linux: native x64/arm64 host, ImageMagick (`magick` or `convert`), `tar`, `file`.
  DEB: `ar`, `zstd`; RPM: `rpmbuild`; Flatpak: `flatpak` plus
  `org.freedesktop.Platform` and `org.freedesktop.Sdk` branch `26.08` for the host
  architecture. AppImage downloads/caches upstream `appimagetool` and extracts it
  to avoid a FUSE requirement, as the prior packager did.
- Release upload: authenticated GitHub CLI (`GH_TOKEN`, `GH_REPO`).

Packaging outputs go to `.tmp/installer/`. Default package version is pinned in
`scripts/lib/Packaging.fsx` and must match `Changelog.md` and build metadata;
`--version` accepts CI tags with an optional `v` prefix. CI uses these same production
entry points. Package smoke builds Windows, macOS and Linux DEB; release builds all
formats. The desktop test matrix runs SDK-only automation checks on all three OSes.

Python is retained only for the actual PyTorch/ONNX conversion/export/recognition
algorithms under `scripts/oxr-conversion/`; those inherently depend on that ML
ecosystem and are not prerequisites for ordinary development or packaging.
