# Bashkit Read-Only Virtual Shell MCP

Status: accepted; amended 2026-07-30 to record the implemented bounded shell surface; amended 2026-07-31 for relationship to ADR `0023` (limited writable MCP)

Patchouli exposes the current Library to MCP agents through one tool, `patchouli_shell`, backed by a locked Rust Bashkit sidecar. The shell presents a virtual filesystem and domain commands (`search`, `evidence`, `cite`) while .NET remains the sole domain authority. The **currently shipped** shell VFS is read-only; product-level limited writes (item `.bib` / style `.csl` whole-resource replace) are defined in ADR `0023` and must not become open-ended VFS writes.

**Decision**

- MCP registers only `patchouli_shell` with a single `command` argument. Legacy discrete tools may remain temporarily for migration, but the progressive-exploration surface is the shell.
- One Bashkit sidecar process is owned by the Patchouli application run while MCP is enabled, not by a single MCP connection and not by Library open alone.
- Communication uses length-prefixed (4-byte big-endian) UTF-8 JSON frames over stdin/stdout. Protocol version is exact-match `"1"` with no negotiation or partial enablement.
- The persisted MCP setting `Mcp.ShellCommandTimeoutSeconds` selects the command timeout, defaults to 15 seconds, and is validated to the inclusive range 1..60. The host sends it during `initialize`; the sidecar applies its compiled 1..60-second bounds and returns the effective timeout and compiled execution limits. The host requires the returned protocol metadata and limits to exactly match before declaring the sandbox ready. The compiled 60-second sidecar cap remains authoritative even if a caller bypasses settings validation.
- Each MCP connection/session owns an independent Bashkit session (cwd, variables, functions). Same-session calls are FIFO; different sessions may run concurrently.
- .NET handles Library lifecycle, SQLite, VFS resolution, BibLaTeX projection, Markdown rendering, exact/enhanced search, EvidenceRef, and CSL formatting.
- Rust handles Bashkit parse/execute, session state, pipelines, command-scoped VFS request memoization, text processing, and formatting of domain RPC results.
- VFS root is fixed: `/AGENTS.md`, `/library.yml`, `/items/`, `/texts/`, `/csl-styles/`. No host paths, `file:` URIs, network, or external processes. The shipped shell surface does not write through the VFS; any later write path on this or a successor surface follows ADR `0023` (whole-resource replace only), not arbitrary filesystem mutation.
- The VFS is resolved on demand through reverse domain RPCs; the sidecar does not preload or maintain a full in-memory Library filesystem. VFS memoized responses are cleared before and after each command and when a session is reset.
- Reverse domain RPCs are `vfs.resolve`, `vfs.list`, `vfs.walk`, `vfs.stat`, `vfs.stat_many`, `vfs.read`, `vfs.read_lines`, `vfs.read_batch`, `search.exact`, `search.enhanced`, `evidence.resolve`, `evidence.resolve_many`, and `cite.format`. Batch stat/read/evidence operations preserve request order and return independent success or error results, with at most 64 paths or URIs per call.
- Directory listing is ordinal and paged. `vfs.list` returns at most 1,000 entries plus `next_after` and a shell `continuation_command` when more entries exist; Bashkit directory reads follow continuations while rejecting repeated cursors and listings over 10,000 entries.
- `find` and `tree` are custom, bounded `vfs.walk` clients rather than recursive shell traversal. `head` and `tail` use `vfs.read_lines` for file operands, and `wc` batches file reads through `vfs.read_batch`; stdin remains in-process. These commands intentionally implement only the options shown by shell `help`.
- Compiled page Markdown is cached in .NET by immutable DocumentTreeRevision and compilation options. The successful-result LRU is bounded to 32 MiB, coalesces concurrent compilation, does not cache failures or entries larger than the limit, and remains a rebuildable projection rather than a filesystem snapshot.
- Evidence uses opaque `evref` query parameters on text-page URIs. Pinned reads never silently fall back to current.
- Sidecar crash, protocol corruption, or uncaught failures leave the sandbox `faulted` with no automatic restart. Users may force-restart the shell sandbox.
- Sidecar lifetime is bound to the host process: Windows Job Object `KILL_ON_JOB_CLOSE`, cross-platform parent-PID watchdog in the sidecar (`PATCHOULI_PARENT_PID`), stdin EOF exit, and host `ProcessExit`/dispose force-kill. Orphan sidecars after host exit are a defect.
- Library switch tears down all sessions and replaces the sidecar before accepting new commands.

**Locked dependency**

- Bashkit `=0.14.4` under `tools/patchouli-shell-sidecar`. Feature upgrades require capability review (commands, redirects, host FS, network, process, parser).

**Standing constraints**

- Extends ADR `0010` (as amended by ADR `0023`): MCP remains **text-only**; no OCR, index rebuild, secrets, images, or local paths. Absolute “MCP never writes” is relaxed only for the limited item/style whole-resource replaces in ADR `0023`.
- This ADR still describes the **implemented** Bashkit progressive-exploration surface as a read-only VFS plus domain commands. Enabling writes on shell or a v3 successor must implement ADR `0023` resource and safety rules; it is not a promise of open VFS or host writes.
- Logs may record method names, request IDs, anonymous session IDs, and internal error chains. They must not record shell commands, arguments, URIs, search terms, stdout, body text, EvidenceRefs, or bibliography text.
- Resource limits are enforced at protocol, host, Bashkit, builtin, and domain boundaries. In addition to the negotiated 1..60-second command timeout, compiled limits include 8 MiB RPC frames, 1 MiB returned terminal output, 2,000 commands, 5,000 loop iterations, depth 16 for functions/subshells/substitutions, 2 MiB strings/input, 10,000 glob or directory-walk results, and 2,000 brace-expansion results. `vfs.walk` is limited to depth 20 and 10,000 entries; batch methods accept 64 operands; `head`/`tail` accept at most 1,000 lines.
- Sidecar-detected timeout or cancellation resets the affected session state; a host watchdog timeout cancels and closes the session. Resource truncation or traversal truncation is surfaced rather than presented as complete output.

**Consequences**

- Agents explore the Library like a literature server via familiar shell composition (read path as shipped; limited assistive writes per ADR `0023` when product enables them on this or a successor surface).
- Packaging must build and ship `patchouli-shell-sidecar` beside the application binary, similar to `biblatex-helper`.
- MCP UI surfaces shell sandbox status: `starting`, `ready`, `stopping`, `stopped`, `faulted`, `protocol_incompatible`.
