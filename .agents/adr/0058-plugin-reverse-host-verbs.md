# 0058. Plugins call host verbs over stdio (`patchouli.host.*`)

- Status: Accepted (implementation on `feature/plugin-reverse-host-verbs`).
- Date: 2026-11-12
- Product scope: dev-0.4.0 plugin interop, reverse direction (no GitHub issue).

## Context

The forward direction is settled: the host calls a plugin's tools over one stdio
JSON-RPC process (`patchouli.plugin.find` / `fetch` / `send`), and the plugin
implements verb semantics over its own data projected as a VFS. The reverse
direction was missing — a plugin that needs library data (read an item, put a
translation, cite, start a run, import a file) had no way to ask the host, so
plugins either duplicated host logic or could not do the job at all.

The host's reader loop dropped plugin-initiated requests entirely. Closing that
gap is a sensitive surface: a plugin calling host verbs that mutate the library
(`put`/`add`) must have an explicit grant, and the call graph can recurse
(plugin A → host → plugin B → host → plugin A …) or deadlock (the host awaits
a `tools/call` answer from plugin A while plugin A awaits a reverse answer
from the host). The design mirrors the forward permission model rather than
inventing a new one, and answers every refusal in the same shape as a failed
tool call so the plugin handles refusals uniformly.

## Decision

### Method family and response shape

One JSON-RPC method per host verb on the same stdio channel:
`patchouli.host.{find,fetch,put,cite,send,add}`. Params are exactly the
arguments of the matching `patchouli.{verb}` MCP tool, and the call runs
through the shared command surface (`HostServices.McpCommands` via
`McpProtocolHandler.InvokeToolAsync`), so a plugin call gets exactly the host
behavior an agent would get — same validation, same domain permissions, same
atomic write set, same error vocabulary.

Every reply is a JSON-RPC `result` carrying the MCP tool-response shape
`{content:[{type:"text",text}],isError}` — success and refusal alike. A
refusal renders as `{content:[{type:"text",text:{"tool","error":
{"code","message"}}}],isError:true}`. Only a method outside the
`patchouli.host.*` family is a JSON-RPC error (`-32601 Method not found`).
Hosts without this feature ignore reverse requests, so a plugin discovers
support by sending one with a timeout.

### Permission model: manifest request ∩ registry grant, default deny

- Manifest: the plugin requests host verbs in a `[host]` table
  (`verbs = ["find","fetch"]`). Only the six host verbs are legal; an unknown
  or duplicated verb fails the manifest. Absent means no host verbs.
- Device registry: `allowed_host_verbs` grants them explicitly. An invalid
  value rejects the whole record — never a grant. Absent means none granted.
- Effective grant = registry ∩ manifest, computed per mount exactly like the
  forward `EffectiveVerbs`.
- Community index: optional `requested_host_verbs`. The installer rejects a
  package whose manifest requests different host verbs than the signed index.
  The store records the grant on install, refuses a rollback that changes the
  requested host verbs, and shows the requested host verbs in the install
  dialog and row metadata.
- Every reverse call checks the grant first and distinguishes the two refusal
  reasons: undeclared ("does not declare … in its manifest [host] verbs")
  versus ungranted ("The device registry does not grant …").
- Tool switches still apply: `put`/`send`/`add` consult the host's live MCP
  tool switches on every call; if the live settings cannot be read, the call
  is refused (fail closed). `find`/`fetch`/`cite` have no switches, matching
  the agent path.

### Reentrancy: refuse calls into the calling plugin's own mount

A plugin must not call host verbs on its own mount. The rule is a blanket
refusal, deliberately not limited to live calls: static descriptor reads
(`plugin.yml`, `README.md`, `views/`, `files/`) are refused too, because the
plugin already owns that data and a single uniform rule is safer than a
dispatcher that re-implements the host's static/live routing (which would
drift). The refusal code is `reentrancy_refused`. The plugin reads its own
data directly; it uses reverse calls for library data and other plugins'
mounts.

The host reader loop never awaits a reverse dispatch (fire-and-forget), so a
reverse call cannot block the plugin's answers to outstanding host calls.
The primary scenario therefore works: while answering a host `find`, a plugin
may reverse-call host `find`/`fetch` on library data and the host answers
without touching the plugin process.

### Recursion budget: depth 8, propagated host-side

Reverse nesting is bounded at depth 8 (`PluginReverseHostVerbs.MaxDepth`); the
ninth nested call is refused with `depth_exceeded`.

Depth cannot travel in an `AsyncLocal` across the process boundary, so the
host propagates it explicitly: the dispatcher sets an `AsyncLocal` depth
around the verb invocation; an outbound host → plugin call made in that flow
is tagged with the depth on the target client's outstanding-call record; the
next reverse dispatch from that plugin inherits the deepest outstanding depth
plus one (zero when nothing is outstanding). Forward, agent-initiated calls
carry no depth and register nothing, so a plugin answering an agent call
starts a fresh chain at depth 0.

### What this does not do

- No forward `put`/`add`/`cite` on plugin mounts (separate work; `put` is
  being implemented on its own branch).
- No background actions, no plugin-entity library grants.
- No change to forward find/fetch/send behavior, except the shared stdio
  reader loop now routes plugin-initiated requests instead of ignoring them.
- No weakening of any existing permission check; the reverse path adds gates
  on top of the command surface, never below it.

## Consequences

- Plugin authors declare `[host] verbs` and get a refusal that names the
  missing declaration or grant; users approve the reverse grant in the same
  install/registry flow as the forward verbs.
- A plugin that needs its own mount data through the host gets
  `reentrancy_refused` and must read its own data directly; this is
  documented in the protocol doc and the plugin skill.
- The depth budget makes unbounded plugin → host → plugin cycles fail fast
  instead of hanging, at the cost of refusing chains deeper than 8 — far
  beyond any legitimate enrichment pattern (one plugin enriching its answer
  with library data is depth 0; a cross-plugin lookup is depth 1).

## Alternatives considered

- **Queue reverse calls while a call into the same plugin is outstanding.**
  Queuing does not help: a single-threaded plugin blocked awaiting its
  reverse answer cannot service the queued call either, so the queue would
  only delay the same hang. Refusal is honest and fast.
- **Per-verb static/live split for the own-mount rule** (allow static
  descriptor reads on the own mount). More precise, but it duplicates the
  host's static-routing knowledge in the dispatcher and drifts when a new
  static path is added. The blanket rule costs nothing real — the plugin owns
  the data — and stays correct by construction.
- **A separate reverse-only command service.** Rejected: it would duplicate
  validation, permission, and rendering logic and drift from the agent path.
  The reverse invoker reuses the shared command service through the
  protocol handler's `InvokeToolAsync` entry point.
