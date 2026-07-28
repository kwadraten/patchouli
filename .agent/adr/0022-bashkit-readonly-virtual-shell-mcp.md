# Bashkit Read-Only Virtual Shell MCP

Status: accepted

Patchouli exposes the current Library to MCP agents through one tool, `patchouli_shell`, backed by a locked Rust Bashkit sidecar. The shell presents a read-only virtual filesystem and domain commands (`search`, `evidence`, `cite`) while .NET remains the sole domain authority.

**Decision**

- MCP registers only `patchouli_shell` with a single `command` argument. Legacy discrete tools may remain temporarily for migration, but the progressive-exploration surface is the shell.
- One Bashkit sidecar process is owned by the Patchouli application run while MCP is enabled, not by a single MCP connection and not by Library open alone.
- Communication uses length-prefixed (4-byte big-endian) UTF-8 JSON frames over stdin/stdout. Protocol version is exact-match `"1"` with no negotiation or partial enablement.
- Each MCP connection/session owns an independent Bashkit session (cwd, variables, functions). Same-session calls are FIFO; different sessions may run concurrently.
- .NET handles Library lifecycle, SQLite, VFS resolution, BibLaTeX projection, Markdown rendering, exact/enhanced search, EvidenceRef, and CSL formatting.
- Rust handles Bashkit parse/execute, session state, pipelines, pure in-memory text tools, and formatting of domain RPC results.
- VFS root is fixed: `/AGENTS.md`, `/library.yml`, `/items/`, `/texts/`, `/csl-styles/`. No host paths, `file:` URIs, network, external processes, or writes.
- Evidence uses opaque `evref` query parameters on text-page URIs. Pinned reads never silently fall back to current.
- Sidecar crash, protocol corruption, or uncaught failures leave the sandbox `faulted` with no automatic restart. Users may force-restart the shell sandbox.
- Sidecar lifetime is bound to the host process: Windows Job Object `KILL_ON_JOB_CLOSE`, cross-platform parent-PID watchdog in the sidecar (`PATCHOULI_PARENT_PID`), stdin EOF exit, and host `ProcessExit`/dispose force-kill. Orphan sidecars after host exit are a defect.
- Library switch tears down all sessions and replaces the sidecar before accepting new commands.

**Locked dependency**

- Bashkit `=0.14.4` under `tools/patchouli-shell-sidecar`. Feature upgrades require capability review (commands, redirects, host FS, network, process, parser).

**Standing constraints**

- Extends ADR `0010`: MCP remains read-only and text-only; no OCR, index rebuild, secrets, images, or local paths.
- Logs may record method names, request IDs, anonymous session IDs, and internal error chains. They must not record shell commands, arguments, URIs, search terms, stdout, body text, EvidenceRefs, or bibliography text.
- Resource limits are fixed internal constants in v1 (15s command deadline, 1 MiB terminal output, command/loop/depth caps).
- Future write capability requires a separate ADR and task; this decision is not a write-compatibility promise.

**Consequences**

- Agents explore the Library like a read-only literature server via familiar shell composition.
- Packaging must build and ship `patchouli-shell-sidecar` beside the application binary, similar to `biblatex-helper`.
- MCP UI surfaces shell sandbox status: `starting`, `ready`, `stopping`, `stopped`, `faulted`, `protocol_incompatible`.
