# 0058. In-process application use cases by default, explicit remote mode as a second implementation

- Status: Accepted by user direction for issue #84 (2026-11-19). Only the foundation is delivered:
  this ADR and a compiled-dependency ratchet for business UI. No use-case entry, Host session type,
  remote adapter or migrated view model exists yet; everything under "Decision" beyond the ratchet
  is target contract, not shipped behaviour.
- Date: 2026-11-19
- Product scope: [issue #84](https://github.com/kwadraten/patchouli/issues/84). Compatible with
  ADR `0024` (one Host per Library), ADR `0033` (UI reactivity) and the #46 execution boundary,
  which this ADR amends only for migrated entries (see "Background boundary").

## Context

Business view models obtain the whole `HostServices` graph, own per-Library runtime resources
(`MainWindowViewModel.AttachHostServices` creates the MCP listener, citation interop host, root
watcher and import orchestrator; `HeadlessRuntimeHost` repeats that sequence), and touch
Infrastructure, MCP, OCR and LLM types directly. The user has decided that #84 closes only when all
business UI is migrated, delivered in stages, and that a remote client will exist. The same
decision requires the local desktop to stay performance-first: making the UI remotable must not put
a transport on the local path.

## Decision

### Local in-process is the default and the performance baseline

- The desktop UI calls Host application use cases **directly, in process**. The local path never
  goes through HTTP or loopback, JSON or other serialization round trips, remote authentication,
  upload staging or media transport. Snapshots and results are passed as the in-process objects the
  use case produced; no per-call proxy, copy or re-validation layer is added "for uniformity".
- Local performance is measured on this path. A remote implementation is never used as, or
  substituted for, the local implementation, including in tests that claim local behaviour.
- Local functionality is not reduced. Device-local capabilities (credentials, updates, PTY/Agent
  terminal, plugin and extension management, MCP/CAYW listeners, protocol registration, opening
  local folders) remain fully available to the local desktop.

### Remote is an explicit, optional mode

- Remote is a second implementation of the same application contracts, used only when the
  Avalonia remote mode explicitly connects to a remote Host. It is the first remote client; a
  separate WebUI is not part of this decision.
- The remote listener binds loopback only by default. Reaching it from another device requires an
  explicit tunnel (for example SSH); Patchouli does not open a LAN/Internet listener by default.
- Remote UI clients use their own revocable credentials. They do not inherit MCP tokens, MCP
  permissions or the MCP surface, and MCP remains the restricted Agent interface (ADR `0024`).
- Initial remote scope is ordinary Library read/write with version preconditions, eventually
  including PDF reading and page editing. Host administration, credentials, update, PTY and similar
  device capabilities are not remotely exposed by default; when unavailable they fail with an
  explicit error rather than an empty success.
- File import from a remote client is an explicit, bounded, cancellable upload, distinct from
  importing a path that the Host already owns.
- Remote adapter assemblies use the reserved `Patchouli.Remote` / `Patchouli.Remote.*` names.
  `Patchouli.Host` and the local desktop do not reference them; the Avalonia remote-mode entry
  that does will be an explicit, reviewed composition exception.

### Application use-case entries

- Business UI depends on use-case entries grouped by user interaction (catalog, document, media,
  processing, settings, changes), not on `HostServices`, Infrastructure, MCP, OCR or LLM
  implementation types. Names and grouping are settled per migration slice.
- An entry is not a one-to-one proxy of an existing service. #46's prohibition of generated or
  hand-written `HostServices` proxies stands; an entry that merely forwards a service table is
  rejected in review.
- Each domain rule keeps a single execution point inside the Host. The local desktop, the remote
  adapter, MCP and CLI reuse it; none re-implements it. Infrastructure keeps its transactions and
  in-transaction concurrency checks.
- Queries are bounded and paged, and aggregate one meaningful interaction so that remote calls do
  not multiply round trips. The in-process implementation returns the same results without
  imposing remote costs.

### Resource ownership

- Per-Library runtime resources (runtime-host lease, `HostServices`, MCP and citation interop
  listeners, root watchers, revision subscriptions, import orchestration) are owned by a Host-side
  Library session with explicit start, stop-admission, drain and dispose. View models subscribe to
  and render its state; they do not create or dispose these resources. Desktop and headless hosts
  use the same session implementation.
- A client disconnecting or releasing its connection does not stop a Host that others may use.

### Completion, versions and consistency

- Results distinguish *accepted*, *business-complete*, *background drained* and, on the client,
  *applied to the view*. A single `RefreshAsync` must not stand for all of them.
- Cancellation requests, business terminal states and drained work are separate facts. A lost
  connection does not prove a write did not happen; clients re-read rather than assume.
- Library revision is an observation watermark. It does not replace item versions
  (`ExpectedUpdatedAt` today), page revisions or edit-session preconditions. Writes through
  use-case entries carry an explicit version precondition and fail with a stable conflict error.
- A composite query returns one snapshot taken at one semantic watermark, not several independent
  reads labelled afterwards with a revision. Snapshot plus change subscription must not leave a gap:
  changes after the watermark are delivered, bounded replay is used, or `resync_required` is
  returned. Progress, settings and layout use their own sequences and do not bump the Library
  revision.
- Edit-session ownership, reconnect, abandon and recovery are decided by the Host. A disconnect is
  not a discard: drafts survive until the owner explicitly abandons them or Host policy recovers
  them.
- Remote payloads never contain Host paths, credentials, database connections, native handles or
  render-buffer leases. Media is returned as authorized binary resources with size, rotation and
  coordinate basis; local in-process media may keep its direct buffers.

### Background boundary (amends #46 placement for migrated entries only)

- Unmigrated calls keep the current rule in `.agents/domain.md`: the view model captures immutable
  inputs and explicitly offloads Host work from the Dispatcher. Existing `Task.Run` calls are not
  removed in bulk.
- A migrated use-case entry owns its background boundary: whichever thread calls it, SQLite and
  inference work do not run on the Dispatcher. Its callers then only capture inputs, await,
  validate the current Library/session context and apply the snapshot; they do not wrap it again.
- There is still no dedicated database thread, and whole async commands are not wrapped in
  `Task.Run`. SQLite read concurrency, write leases and atomic transactions do not regress.
  `ModalOperationRunner` blocking, progress, cancellation and real-work draining are preserved.

### Enforcement

- `tests/Patchouli.Tests/Architecture/UiBusinessBoundaryTests.cs` reads compiled `Patchouli.UI` IL
  and metadata and lists every dependency from a top-level UI type to a type in `Patchouli.Host`,
  `Patchouli.Infrastructure`, `Patchouli.McpServer`, `Patchouli.Mcp`, `Patchouli.Ocr`,
  `Patchouli.Ocr.Engines`, `Patchouli.Llm` or `Patchouli.Extensions`. Because it reads compiled
  code, aliases, fully qualified names, inferred locals, member return types, lambdas, async state
  machines, generic arguments and attributes are all covered.
- `UiBusinessDependencyBaseline.txt` is the reviewed existing set at owner → target-type
  granularity. It may only shrink: new owner/target pairs fail, and removed pairs must be deleted
  from the file. No line numbers or counts are stored.
- Permanent exceptions are limited to `Patchouli.UI.Program`'s headless/diagnostic bootstrap
  types. `App`, `MainWindow` and `MainWindowViewModel` are baseline debt, not exceptions.
- The same test class fails if `Patchouli.UI` or `Patchouli.Host` references a `Patchouli.Remote*`
  assembly. The future remote-mode entry must add a narrow, reviewed exception rather than make the
  check unconditional.
- Contract tests for a use case are written once and run against both the in-process and the remote
  implementation. Passing remote runs do not replace local tests, and local performance is checked
  against the in-process path.

## Consequences

- The local desktop pays no transport cost for the remote capability; the remote client pays its own
  transport, authentication and upload costs.
- Migration proceeds slice by slice. Each slice deletes its baseline lines and the replaced UI
  orchestration; it does not keep a second execution path.
- #84 is not complete until the baseline contains no business UI entries and `Patchouli.UI` no longer
  needs project references to Infrastructure or McpServer outside the startup composition location.
- Not decided here: contract names and project layout, the wire protocol (HTTP JSON + SSE + binary
  responses is a starting hypothesis), and the order relative to the 0.6.0 Dock work (#79–#83), which
  keeps its own gate.
