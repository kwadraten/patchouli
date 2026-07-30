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

Handshake: sidecar sends `hello` notification -> host sends `initialize` request (odd request IDs) -> sidecar responds with the effective limits and sends `ready`. The host validates the exact protocol version and returned limits before accepting commands.

The command timeout is persisted as `Mcp.ShellCommandTimeoutSeconds`, defaults to 15, and must be from 1 through 60 seconds. It is sent as `limits.command_timeout_ms` during initialization. The Rust sidecar clamps it to compiled 1..60-second bounds, including a hard 60-second cap, and the host requires the effective value echoed in the initialize response.

Reverse domain RPC methods handled by .NET:

- VFS: `vfs.resolve`, `vfs.list`, `vfs.walk`, `vfs.stat`, `vfs.stat_many`, `vfs.read`, `vfs.read_lines`, `vfs.read_batch`.
- Domain: `search.exact`, `search.enhanced`, `evidence.resolve`, `evidence.resolve_many`, `cite.format`.

Batch stat/read/evidence methods accept at most 64 paths or URIs, preserve operand order, and report each result independently. `vfs.read_lines` supports `head` or `tail` with 0..1,000 lines. `vfs.walk` supports depth 0..20, an optional file/directory filter, and at most 10,000 returned entries.

`vfs.list` is ordinal and returns at most 1,000 entries per page. A truncated response includes machine-readable `next_after` and a shell `continuation_command`; Bashkit filesystem directory reads follow these pages, reject repeated continuations, and stop with an error above 10,000 entries.

Custom sidecar commands are:

- `head` and `tail`: bounded file reads through `vfs.read_lines`; stdin is sliced locally.
- `wc`: line, word, byte, character, and maximum-line-length counts; distinct files are read in batches of 64 through `vfs.read_batch`.
- `find`: one-path traversal with only `-maxdepth N` and `-type f|d`.
- `tree`: one-path traversal with only `-L N`.

`find` and `tree` each use one bounded `vfs.walk` RPC and visibly fail if the 10,000-entry result is truncated. Run shell `help` or `<command> --help` for the supported command subsets; these are not host OS utilities.

Host methods: `shell.execute`, `session.close`, `cancel`, `shutdown`.

The virtual filesystem is read-only and RPC-backed. It exposes only `/AGENTS.md`, `/library.yml`, `/items/`, `/texts/`, and `/csl-styles/`; it cannot access host paths, `file:` URIs, network, external processes, or mutation APIs. The sidecar does not build a full in-memory filesystem. It memoizes VFS responses only within a command and clears them at command and session-reset boundaries.

Compiled page Markdown has a separate .NET successful-result LRU keyed by immutable revision and compilation options. It is bounded to 32 MiB, coalesces concurrent compilation, and does not retain failures or an individual result larger than the cache limit.

Compiled resource limits include 8 MiB RPC frames, 1 MiB returned terminal output, 2,000 commands, 5,000 loop iterations, depth 16 for functions/subshells/substitutions, 2 MiB strings/input, 10,000 glob results, and 2,000 brace-expansion results. Sidecar-detected timeout/cancellation resets the affected session state; a host watchdog timeout cancels and closes the session. Truncation is reported rather than treated as complete output.

Windows and macOS packaging scripts build and ship `patchouli-shell-sidecar` next to the application binary, similar to `biblatex-helper`.
