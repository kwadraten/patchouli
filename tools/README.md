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

CSL rendering remains in-process through `Fsharp.Citeproc`. Windows and macOS packaging scripts build and ship `biblatex-helper` next to the application binary.

## patchouli-shell-sidecar

Locked dependency: [`bashkit` 0.14.4](https://crates.io/crates/bashkit).

Build:

```pwsh
cargo build --release --manifest-path tools/patchouli-shell-sidecar/Cargo.toml
```

Protocol: length-prefixed (4-byte big-endian) UTF-8 JSON frames on stdin/stdout. Protocol version is exact-match `"1"`.

Handshake: sidecar sends `hello` notification → host sends `initialize` request (odd request IDs) → sidecar responds and sends `ready`.

Reverse domain RPC methods handled by .NET: `vfs.resolve`, `vfs.list`, `vfs.stat`, `vfs.read`, `search.exact`, `search.enhanced`, `evidence.resolve`, `cite.format`.

Host methods: `shell.execute`, `session.close`, `cancel`, `shutdown`.

Windows and macOS packaging scripts build and ship `patchouli-shell-sidecar` next to the application binary, similar to `biblatex-helper`.
