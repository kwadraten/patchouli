# 0038. Chat FSI REPL and VFS permission matrix

2026-10-09 amendment: FSI is always available in chat and workflow sessions. The session grant and chat-page toggle are removed; workers remain lazy and isolated. This supersedes the earlier grant policy.

2026-10-08 amendment: the independent worker now receives an ambient typed SDK bridge
and generated callable `AgentTools` bindings. Native and FSI operations share the host invoker,
domain checks and durable primitive receipts. Frozen workflow definitions and input bindings are
rebuilt from the snapshot; worker-local variables remain temporary. Interrupted whole code blocks
are refused on restore, while settled SDK receipts remain available. This preserves the existing
independent process, scratch workspace and .NET trust boundary. See [workflow-api](../workflow-api.md).

Date: 2026-10-07

## Status

Accepted. Amends ADR 0036's built-in agent capability boundary.

## Decision

The chat agent can request one tool with a whole-response JSON object
`{"tool":"fsi","arguments":{"code":"F# source"}}`. The pure chat step records the
model response and issues the tool effect; its recorded result requests another model
turn. Ordinary prose completes the turn. The same protocol supports the existing
`find`, `fetch`, `cite` and `put` tools. Results use ordinary user messages on the
provider wire because this text protocol has no native function-call identifier.
Workflows keep their existing effect and provider-history contract.

Each session has an independent worker process, created lazily using the existing
FSharp.Compiler.Service runtime through a private application entry point and an
authenticated user-local named pipe. The worker's current directory is the session's
directory beneath the system temporary folder; relative filesystem operations and
child processes inherit that directory without changing the desktop host's cwd.
Bindings survive interactions within that worker. Each call returns its own output,
errors and compilation diagnostics. Restarting a worker loses interpreter bindings;
completed results remain in history and are not re-executed to rebuild those bindings.

Chat and workflow sessions always have FSI available; the chat page has no enable/disable
control. The worker is created lazily on the first call, including after reopening a session
on another process or device. Closing the chat tab leaves its session alive. Cancelling
an active evaluation terminates its worker process tree; the next call starts a fresh
worker. Purging retires the worker and blocks its queued evaluations.
Clearing session scratch files stops workers first and preserves conversation history.
Removing the grant does not rewrite existing sessions' instruction prefixes or recorded history; shared tool instructions describe current FSI availability.

Scratch directories use `Path.GetTempPath()` on Windows, macOS and Linux, under
`patchouli-agent-workspaces-<user-key>/<library-path-hash>/<session-id>/`. Unix
workspace namespaces are created with owner-only access. These files are device-local
and separate from the durable `agent-sessions/` journal. FSI exposes the absolute
path as `workingDirectory` and restores its default cwd before each interaction.
Local File Management lists the current library's workspaces with size, open and
clear actions. Purging a session stops its worker and drains its driver before
deleting both its scratch directory and journal; cleanup never follows links into
other directories. The OS may remove temporary files independently; the next use
recreates the directory without replaying completed agent effects.

FSI has the user's .NET privileges, including filesystem, network and process access.
The separate process isolates interpreter state and cwd; it is not a sandbox.
Its .NET capability is independent of MCP permissions: arbitrary code can perform
operations outside the VFS matrix. Host SDK calls still enforce their permissions.
FSI is a built-in trusted execution surface, not an external MCP
tool; existing MCP resource validation and transport sanitization remain in place.

MCP settings present eight rows (`/`, `items`, `texts`, `translations`, `csl-styles`,
`runs`, `workflows`, `library.toon`) and five columns (`find`, `fetch`, `put`, `cite`,
`send`). Device-local `Mcp.DomainPermissions` stores domain/verb/enabled entries;
omitted cells default to enabled for backward compatibility. Legacy tool-wide denies
remain effective. Editing a cell covered by a legacy deny first materializes that
deny into the other cells, then removes the global deny.

The shared command service enforces the matrix for built-in and external agents.
`find` checks the scope restored by a cursor and filters denied domains from root
discovery. `fetch` rejects a batch containing any denied domain before retrieving its
contents. `put` checks its target. `cite` checks referenced domains and the item and
CSL style domains it consumes. `send start` checks `workflows`; other send actions
check `runs`. Permissions grant capabilities only: they do not make unsupported
resource/verb combinations legal.

Built-in calls load device settings per operation, so a save applies to later calls.
External MCP handlers use their startup settings snapshot and require reload, as
already indicated by the settings page. A settings-load failure fails closed.
