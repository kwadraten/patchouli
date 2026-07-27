# Rust tools

Patchouli keeps Rust/Cargo as a supported repository toolchain for native or format-conversion helpers. Each helper belongs under `tools/<tool-name>`; Cargo build output is ignored through `tools/**/target/`.

## biblatex-helper

Locked dependency: [`biblatex` 0.12.0](https://crates.io/crates/biblatex) (`typst/biblatex`).

Build:

```pwsh
cargo build --release --manifest-path tools/biblatex-helper/Cargo.toml
```

Protocol: one JSON request on UTF-8 stdin, one JSON response on UTF-8 stdout. stderr is diagnostics only.

- Parse: `{"op":"parse","text":"..."}` → `{ok, entries?, error?}`
- Write: `{"op":"write","entries":[...]}` → `{ok, text?, error?}`

CSL rendering remains in-process through `Fsharp.Citeproc`. Packaging scripts should ship `biblatex-helper` only after the application wires a runtime path to it.
