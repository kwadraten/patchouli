# 0036. Agent Sessions, `.fsx` Workflows, and MCP Collaboration

Date: 2026-10-05

## Status

The workflow script API and procedural translation sections are superseded by
[ADR 0039](0039-typed-functional-agent-harness-workflows.md). The old API is removed;
workflows now declare typed control plans over the existing agent. Session persistence,
VFS resource contracts and independent FSI process/workspace decisions remain in force.

[ADR 0040](0040-cache-preserving-whole-prefix-compaction.md) amends the provider prefix-cache
invariant: original history remains append-only, while model-visible history is append-only
within each whole-prefix compaction generation.

Accepted. Amends ADR `0010` (read-only MCP becomes a first-class human/agent collaboration
interface controlled by user tool switches), amends ADR `0023` (its limited write set stays in
force, but write capability is now governed by those user switches and the built-in agent may
write through the same `put` contract), amends the resource tree of ADR `0024` (two further VFS
roots, `runs/` and `workflows/`, plus the new `send` verb), and supersedes only the premise of
ADR `0034` that "Patchouli embeds no translation engine": the built-in agent now translates
through the very same `put` contract. Everything else in ADR `0034` — box-derived
`translation_boxes` storage, lazy realignment, positional structure validation, and the
`translations/` root — remains in force unchanged. This ADR also follows the working/committed
revision model of ADR `0027`, the item purge boundary of ADR `0030`, and the evidence and
projection contracts of ADR `0028` and ADR `0034`.

## Context

Patchouli's MCP surface grew from read-only retrieval (ADR `0010`) to narrow atomic writes
(ADR `0023`) over one structured protocol (ADR `0024`), and translation resources were added as
derived data that an **external** agent fills by reading `texts/` and writing `translations/`
(ADR `0034`). That model works, but it keeps every useful assistant workflow outside the product:
the user must run an external agent, wire credentials twice, and reconcile two progress views.
The product plan therefore commits to a **built-in agent session platform**: a session-driven
agent that runs workflows from `.fsx` scripts, chats with the user in a chat tab, and performs
full-text translation and other library maintenance through the same MCP-visible write contract
an external agent would use.

Three product needs drive the design:

1. **Long-running, resumable work.** Translating a book is a multi-hour job with progress,
   retries and partial results. It must survive closing a tab, restarting the app, and being
   observed from outside.
2. **A real collaboration boundary.** External agents, the CLI and the built-in agent must all
   behave as peers over one contract rather than the built-in agent getting privileged access.
3. **Independence from OCR.** The existing OCR queue has its own scheduling and state machine;
   translation must not hijack it or rename it.

## Decision

### Session-driven built-in agent: F# pure step core, C# effect interpreter

The generic agent core lives in a new F# project `src/Patchouli.Agent/`. Its heart is a pure
function

```fsharp
step : Context -> Event -> Context * Effect list
```

- Model results, tool results, user messages, script progress, cancellation and resume all enter
  the same `Event` stream; the step function is the single decision point.
- **`Effect`/`Event` discriminated unions are defined only on the F# side.** The C# effect
  interpreter pattern-matches those unions to perform effects and feed results back as events.
  There is one authoritative type definition; C# never redeclares or mirrors the cases.
- The core owns model interaction, tool invocation, the event loop and history maintenance. It
  contains **no concrete business steps**; business steps live in workflows (below).
- LLM access goes through the `Patchouli.Llm` base layer (LLMTornado, all providers), tool access
  through the in-process `McpCommandService` (`fetch` source text, `put` translations and other
  allowed whole-resource replaces).

**Provider prefix-cache invariant.** Session history is append-only. System instructions, tool
definitions and existing messages stay byte-stable and in stable order; new content is only ever
appended at the end; after resume the history is rebuilt in its recorded order. Cache expiry
therefore changes only cost and latency, never behavior. The invariant is contractual: any
feature that would rewrite or reorder history must go through a future ADR, not a convenience
patch.

### `.fsx` workflows and the versioned script API

A workflow is a `.fsx` script plus metadata (stable id, name, description, script entry point,
parameter definitions, applicable selection scope, locked state, menu placement) saved with the
Library. The built-in "Full-text translation" workflow ships locked: it cannot be edited or
deleted, and it appears in the workflow menus. "Copy from built-in workflow" is the recommended
creation path for new workflows.

Scripts reach the outside world only through a **versioned F# script API** — their single
controlled side-effect surface:

- read launch parameters and the selected resources (documents, page ranges, languages);
- append instructions, request a model action, call MCP tools, wait for run events (including a
  host-side OCR completion wakeup — no polling), report progress, and finish or stop.

**Recovery model.** Because scripts express side effects only through that API, the executor can
replay completed steps and reuse their recorded results instead of re-invoking models or tools.
Arbitrary `.NET IO` a script performs outside the API is **not** covered by the recoverability
guarantee. A call whose result was never taken is recovered by operation kind after a crash; v1
does not promise exactly-once semantics.

**Trust model.** `.fsx` runs in-process with host permissions and is **not** a sandbox. v1
targets user-trusted local scripts: compile check on save in the settings page, a prominent
permission warning, and copy-from-built-in as the primary authoring path. Locked built-in
workflows are the safe default.

**Script snapshots.** Each session stores the script snapshot it started with. Editing a workflow
definition affects later launches only; running sessions keep executing their snapshot.

### Host session service: lifecycle, inbox, Event boundaries

The host session service in `Patchouli.Host` owns run lifecycles: creation, inbox, Event-boundary
advancement, stop, cancel, resume and purge. A session is a first-class run, not a task-queue
job.

**Message append semantics.** `send` messages first enter an inbox. At the next Event boundary —
before the next tool call or effect execution — they are appended to the session **in recorded
order**. Calls not yet executed are handed back to the loop for a fresh decision; calls already
completed and their results stay in history. Messages never wait for the workflow to finish and
never interrupt the in-flight call by default.

**Revocability, per effect type.** The revocable boundary is defined per `Effect` case rather
than by ad-hoc special-casing:

| Effect kind | Revocable at an Event boundary? |
|---|---|
| LLM chat request | Not while it is in flight; a pending request that has not started may be reconsidered. A cancelled in-flight `LlmChat` is cancelled promptly, but consumed tokens are not returned. |
| MCP tool call (for example `fetch`) | Yes, until execution starts; the unexecuted call returns to the loop and is re-decided with the appended messages in context. |
| `put` whole-resource replace | No. Once the effect is issued it enters an atomic commit point; a `put` is never revoked at an Event boundary, and cancellation before that point leaves the Library unchanged while cancellation after it completes the commit or rollback (ADR `0024`). |
| Host primitive (OCR enqueue, wait-for-run-event, progress report) | Yes until execution starts, except that a wait already armed for a host event is satisfied or cancelled by a host control event, never silently dropped. |
| Finish / stop | Explicit control events; they take effect at the boundary and are not silently undone by a queued message. |

**Cancellation.** Cancel is an explicit control event handled immediately, without waiting for
the next model inference; it does not bypass an atomic commit point that has already been
entered. Completion and failure are reported through `IHostActivityTracker` and the status bar.

**Resume.** On startup the host automatically resumes sessions still marked running. Stopped,
cancelled or completed sessions are not auto-started; they resume on user or `send resume` action
and continue from recorded results.

### Persistence, physical purge, and sync

- **Sessions** are auxiliary Library data in one directory per session: message/event history,
  launch parameters, the script snapshot, current step and completed results, pending inbox
  messages and unexecuted calls, and run state. The `runs/` discovery projection is rebuildable
  from these directories; a missing session directory must never block opening a Library.
  **Physically purging a session never touches Items, original documents, translations or OCR
  results.**
- **Workflow definitions** are saved with the Library but are user configuration, not rebuildable
  index data. They are managed separately from session data.
- **Sync** uses two independent switches — one for sessions, one for workflow definitions —
  controlling what enters a Library snapshot. Synced history and resume material from another
  device **does not transfer run ownership**; an imported session is never automatically
  re-executed on the receiving device.

Progress, messages and token accounting advance through the session event sequence numbers only.
They never advance `library_revision`.

### OCR queue independence (D7)

The OCR queue keeps its own name, queue, scheduler, execution state machine, board UI and
activity tracking. Agent sessions share none of these. The `runs/` projection exposes OCR runs
and agent runs side by side **without unifying their states** into one status vocabulary.
Multimodal LLM OCR (LLMTornado) joins the existing queue as one more adapter whose page output
still normalizes to `OcrDocumentTreeCandidate` and enters the shared importer; it is not
agent-driven and never writes `document_boxes` directly.

### MCP contract: `runs/`, `workflows/`, and `send`

Two further VFS roots join the tree (root discovery becomes six directories plus one file):

```
patchouli://runs/                                  volatile runtime observation
patchouli://runs/ocr/{task-id}/status
patchouli://runs/agent/{session-id}/status
patchouli://runs/agent/{session-id}/events         monotonic sequence, incremental reads
patchouli://workflows/
patchouli://workflows/{workflow-id}                metadata + parameter definitions, readable
```

- **`runs/`** is discoverable and readable via `find`/`fetch`. It is the first deliberately
  **volatile** runtime resource in the VFS: it is not part of any snapshot, it cannot be `put`,
  and it is not a canonical resource. Agent event streams use monotonic session event sequence
  numbers so a client can read incrementally since its last position. OCR and agent runs keep
  separate status vocabularies.
- **`workflows/`** lets an agent discover available workflows and their parameter schemas and
  start one through `send`.
- **`send`** is a new verb (`readOnlyHint=false`, gated by the user tool switches) carrying typed
  instructions:
  - `start`: workflow URI + parameters → creates a session and returns its
    `runs/agent/{session-id}` URI;
  - `message`: adds requirements to a running agent;
  - `cancel`: stops a run; `resume`: continues a stopped or interrupted session.
  - Responses distinguish **accepted** from **processed**; message ids are deduplicated so a retry
    cannot append the same message twice.
- **Data boundary.** None of these return images, local paths, file URLs or provider
  secrets/configuration; the ADR `0010` text-only boundary holds for the built-in agent too.
- **CLI parity.** `patchouli-cli` grows a `send` command that maps to the same request as a thin
  client.

### User tool switches and the amended MCP write boundary

The MCP surface — including the built-in agent's MCP tool calls — is a first-class human/agent
collaboration interface whose **capability** boundary is user-controlled: settings switches can
disable `put` and `send` and return the surface to read-only. The **data** boundary (text-only;
no images, paths, secrets) is a developer invariant that users cannot weaken. The narrow atomic
whole-resource writes of ADR `0023` (item `.bib`, style `.csl`, translation pages under ADR
`0034`) remain the only writable kinds; this ADR adds `send` as a control verb and the volatile
`runs/` root, neither of which is a new writable resource.

### Built-in "Full-text translation" workflow

The locked built-in workflow translates a page range of a document into a target language
(defaulting to the settings default, overridable at launch): it reads each page's source markdown
via MCP `fetch`, assembles the sliding-window context (±N source pages plus the previous window's
translation as a coherence anchor) into the deterministic prefix, instructs the model to
translate completely while preserving the Markdown block structure, and commits each page with an
atomic MCP `put` to `patchouli://translations/{document-id}/page-{N}.md`. ADR `0034`'s structural
validation applies unchanged; a failure returns the structured mismatch list as an `Event` fed
back to the agent for bounded self-healing. Success advances the cursor and reports progress;
failures are retried a bounded number of times, recorded per page, and the run continues. Every
new task fully covers its requested range: it does not skip pages based on existing translation
state, and it adds no source-matching or quality gate.

## Consequences

- External and built-in agents are now peers: both translate and maintain the library through the
  same `put` contract, and product docs must stop describing Patchouli as having no translation
  path at all. ADR `0034`'s storage, realignment and validation design is untouched and stays
  authoritative.
- Chat tabs are disposable views over durable sessions: closing a tab removes only the frontend;
  the run continues and can be reopened from the session list.
- Recovery is at-most-once for completed effects and best-effort for un-taken results; users and
  docs must not claim exactly-once workflow execution.
- `.fsx` scripts carry host privileges. The settings compile check, permission warning and
  copy-from-built-in path are part of the security story, not optional polish.
- `runs/` is volatile by contract: clients must not persist it as library content, snapshot code
  must exclude it, and `put` to it returns `PERMISSION_DENIED`.
- Session history is append-only for provider prefix caching; session growth is bounded by the
  physical purge action and the independent sync switches rather than by silent truncation.
- The OCR queue is untouched: renaming it to a generic "task queue" is explicitly out of scope,
  and translation progress lives in the chat tab and `runs/` only.
- `library_revision` is not a progress counter; run observation uses session event sequence
  numbers so long runs never fight with Library writes for one revision counter.

## Relationship to earlier ADRs

| Prior decision | After this ADR |
|---|---|
| `0010`: MCP read-only | **Amended:** read-only becomes a user-controlled capability state, not a permanent property; when `put`/`send` are enabled MCP is a first-class collaboration interface |
| `0010`: text-only, no paths/secrets/images/OCR/index | **Unchanged**, and binding on the built-in agent as well |
| `0023`: limited writable MCP | **Amended:** the write set stays narrow and atomic, but enabling/disabling `put` (and the new `send`) is a user tool switch, and the built-in agent writes through exactly these contracts |
| `0024`: four VFS roots + `library.toon` | **Amended:** two more roots, `runs/` and `workflows/`, plus the `send` verb; `runs/` is the first volatile runtime root and is excluded from snapshots and from `put` |
| `0034`: "Patchouli embeds no translation engine" | **Superseded premise:** the built-in agent translates through the same `put` contract. The box-derived storage, lazy realignment and positional structure validation of `0034` remain authoritative |
| `0027`: working/committed revisions | Unchanged; session and workflow data are not DocumentTree revisions and never advance `library_revision` |
| `0030`: item purge | Unchanged; session purge is a separate physical cleanup that never touches item, document or OCR payload |
