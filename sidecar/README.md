# Sidecar helpers

Patchouli keeps Rust/Cargo as a supported repository toolchain for native or format-conversion helpers. Each helper belongs under `sidecar/<helper-name>`; Cargo build output is ignored through `sidecar/**/target/`.

## biblatex-helper

Locked dependency: [`biblatex` 0.12.0](https://crates.io/crates/biblatex) (`typst/biblatex`).

Build:

```pwsh
cargo build --release --manifest-path sidecar/biblatex-helper/Cargo.toml
```

Protocol: one JSON request on UTF-8 stdin, one JSON response on UTF-8 stdout. stderr is diagnostics only.

- Parse: `{"op":"parse","text":"..."}` → `{ok, entries?, error?}`
- Write: `{"op":"write","entries":[...]}` → `{ok, text?, error?}`

CSL rendering remains in-process through `Fsharp.Citeproc`. Windows and macOS packaging scripts build and ship `biblatex-helper` next to the application binary.

The former Bashkit virtual shell sidecar (`patchouli-shell-sidecar`) was removed from the repository; its implementation survives only on the `feature/mcp-ab-benchmark` branch as historical benchmark evidence.
