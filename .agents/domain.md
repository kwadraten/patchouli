# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Repo rule

`AGENTS.md` is the only root Markdown entrypoint for agent instructions.

All other agent-readable Markdown belongs under `.agents/`. Do not create or rely on `docs/`, and do not scatter agent-readable Markdown files at the repo root.

## Layout

This repo uses a single-context domain-doc layout under `.agents/`.

Before exploring, read these when they exist:

- `.agents/CONTEXT.md` for project domain language and glossary.
- `.agents/adr/` for architectural decision records relevant to the area being changed.
- `.agents/issue-tracker.md` for issue-driven product work; there is no dedicated PRD document.
- `.agents/protocols/` for normative protocol and contract specifications: `.agents/protocols/mcp-protocol.md` for MCP/CLI parameters, response schemas and regression obligations; `extension-contracts.md` and `extension-validation.md` for the FSX extension contract and its verification evidence; `ocr-normalization.md` for OCR content semantics.
- `.agents/palettes/` for the selectable UI color palettes (one `DESIGN.md` per palette).
- `.agents/skills/patchouli/` is the single Agent Skill Patchouli distributes to external agents (issue #23). It is the source of truth for user-facing usage guidance, including FSX extension authoring (`references/EXTENDING.md`). Repository contracts under `.agents/` stay normative for implementation; the skill states only delivered behaviour and must not describe planned Plugin VFS or Action projection as available.

If `.agents/CONTEXT.md` or `.agents/adr/` do not exist yet, proceed silently. Do not suggest creating them upfront unless the current task is explicitly about domain modeling, architecture documentation, or recording a decision.

## Expected structure

```text
/
├── AGENTS.md
├── .agents/
│   ├── CONTEXT.md
│   ├── domain.md
│   ├── issue-tracker.md
│   ├── protocols/
│   │   ├── mcp-protocol.md
│   │   └── ...
│   └── adr/
│       ├── 0001-example-decision.md
│       └── 0002-example-decision.md
└── src/
```

## Use the glossary's vocabulary

When output names a domain concept in an issue title, refactor proposal, hypothesis, test name, or implementation note, use the term as defined in `.agents/CONTEXT.md`.

If the concept is not in the glossary yet, treat that as a signal: either avoid inventing new language, or note the gap for a later domain-modeling pass.

## Flag ADR conflicts

If output contradicts an existing ADR under `.agents/adr/`, surface it explicitly rather than silently overriding it.

## Runtime host layer

`src/Patchouli.Host` is the shared runtime composition layer used by both the desktop UI
(`Patchouli.UI`) and the standalone MCP server (`Patchouli.McpServer`). Its responsibilities:

- **Composition root** — `Composition/HostServices` constructs and wires every domain service
  (identity, revisions, items, tags, files, OCR, snapshots, MCP APIs) over one
  `SqliteConnectionFactory`, runs migrations and startup maintenance (OCR run
  reconciliation, import-residue GC), and exposes the service fleet to consumers. Both
  hosts build it; nothing else should hand-wire domain services.
- **File watching** — `Watching/FileSearchRootWatcherService` owns the `FileSystemWatcher` fleet
  for configured FileSearchRoots plus the debounced rescan/import pipeline.
- **MCP lifecycle** — `Mcp/McpServerHost` owns MCP settings validation, listener start/stop/restart,
  and shutdown, surfacing status and failures through events (`McpHttpServer` and
  `McpProtocolHandler` live here).
- **Import orchestration** — `Import/LibraryImportOrchestrator` sequences multi-step import
  workflows without UI coupling. PDF import commits atomically per book with per-page
  failure placeholders (ADR `0035`).
- **Item cache and revision monitor** — `Caching/LibraryItemCache` is the in-memory first-screen
  read snapshot (tag filtering, untagged queries, sidebar tag counts);
  `Caching/LibraryRevisionMonitor` merges local change scopes and refreshes affected items.
  Unknown or skipped revisions and external writes force a full refresh; failures retry without
  treating an unrefreshed cache as current.

Rule: runtime logic belongs in `Patchouli.Host`, not in ViewModels. ViewModels subscribe to the
host's services and marshal events to the UI dispatcher; they never own watchers, timers, server
lifecycles, or cache invalidation.

Runtime composition seams:
- `HostServices` retains its public facade; `HostLibraryServices` composes the catalog over one
  connection factory and revision stream. `HostInfrastructureOptions` supplies optional clock,
  process, HTTP transport and PDF capabilities without changing persisted settings or default policies.
- MCP lifecycle consumes settings, progress records and a listener factory. Watcher orchestration
  consumes explicit dependencies and a `TimeProvider`; `WatchLoopSchedule` contains pure timing
  decisions, and `UnchangedFilePathLookup` owns the reconciliation SQL/filesystem lookup.
- Translation and metadata trackers own synchronization, cancellation and outcome slots. Their
  immutable phase values contain lifecycle decisions and status projections. Characterization tests
  preserve existing terminal overwrite semantics; extracting these values does not fix late-result
  cancellation races or establish a new shutdown protocol.
- OCR dispatch, completion, run binding, pause and progress policies are pure decisions over caller-owned
  snapshots. `OcrQueueScheduler` retains the state/progress locks, attempt resources, generation leases
  and notification ordering. String states at the public boundary are unchanged.

C# projects use C# 14 (individual projects may select an earlier version), with CS8509 treated as a
compiler error. This checks non-exhaustive switch expressions; it does not close record hierarchies or
detect newly added cases hidden by a default arm.

Cross-process consistency: the desktop UI and the standalone MCP server share one SQLite database
and remain coherent through the persistent `library_metadata.library_revision` counter.
In-process commits publish via `ILibraryRevisionService.ChangeCommitted`; the revision monitor
polls `GetCurrentRevisionAsync` (default every 2 s, interval is injectable) to catch writes from
the other process. Any detected change triggers a full cache reload; polling-detected changes
additionally raise `ExternalChangeDetected` so the UI can tell cross-process edits apart from its
own commits.


## External agent and translation runtime ownership

The built-in Agent, workflow and chat runtime has been removed (ADR `0047`).
ADR `0053` defines the in-progress target: trusted FSX packages compile to cached DLLs and execute
in a per-generation collectible host `AssemblyLoadContext`. This does not restore Agent sessions
or a durable workflow scheduler. Leases/draining and LKG remain; unload is best-effort and warns
without blocking activation. External BCL, package DLLs and NuGet are allowed. Cold NuGet restore
requires an installed SDK; BCL, package-local DLLs and verified compiled cache hits bypass that
restore requirement. See [ADR 0053](adr/0053-trusted-compiled-fsx-extensions.md) and the
[extension contract](protocols/extension-contracts.md); this is not an acceptance claim.
`Patchouli.Core/Agents` owns the compiled catalog (`AgentCatalog.g.cs`), detection, launch
and integration-settings contracts. `Patchouli.Terminal` owns the PTY session and embedded
terminal host. `Patchouli.Host/Agents` owns workspace location, launch preparation and
restricted MCP configuration generation; `Patchouli.Infrastructure/Agents` owns the OpenCode
archive installer. The UI's Agent workspace page is a terminal client, preset composer and
item-reference picker; it owns no agent runtime, session store or workflow engine. External
agents own their conversations, credentials and history, and keep their native configuration
except for the explicitly managed user-level `patchouli` Skill directory; Patchouli supplies
detection, a per-library workspace, a terminal and a revocable MCP connection.

`Patchouli.Host/Agents/AgentSkillService` embeds the single skill from
`.agents/skills/patchouli/` and manages device-level user installations plus coordinated
per-Library workspace copies. Launch preparation prefers an Agent-readable user directory,
cleans readable managed duplicates, and otherwise prepares the managed copy. Settings actions
only install/clear the external Agent's user-level directory. Desktop startup can replace a
registered, existing user installation marked with an older app version; it does not recreate
removed targets, repair same-version or unmarked content, or modify unregistered directories.
The on-launch precedence check does not inspect user-level content or its version. Upgrade file
replacement does not stop/restart a running Agent, though its on-disk files may change. See
[Agent Skill distribution](protocols/agent-skills.md) for adapters and safety boundaries.

Direct translation uses a pinned FSX engine from `Patchouli.Extensions` and its typed
contracts in `Patchouli.Extensions.Contracts`. The bundled LLM, Google, DeepL and DeepLX
source packages own translation policy; `TranslationRunEngine` retains source/version validation
and atomic page commits. The four earlier contracts remain reusable compatibility steps.
`Patchouli.Host/Extensions` owns capability authorization, frozen model/transport settings,
source watching and desktop/headless composition. The volatile `TranslationRunService` in
`Patchouli.Host/Translation` owns run acceptance and control. MCP exposes start/cancel messages at `runs/translation/control` and
`runs/translation/{id}/status`; the processing settings card and shared task queue page are thin
clients. Runs are in-memory only; committed translations follow ADR `0034`/`0044`.

Bibliographic metadata lookup follows the same extension boundary. Every built-in source is a
`metadata.*` FSX package; `BuiltInMetadataExtensions` declares the package ids and source
definitions, and `TranslationExtensionCatalog` seeds them before initialization and replaces
them from active leases. `MetadataRunService` in `Patchouli.Host/Metadata` owns volatile
enrich_batch tasks; MCP exposes lookup/apply/enrich/enrich_batch at `runs/metadata/control`
and `runs/metadata/{id}/status`. Core still owns candidate validation, optimistic item versions
and atomic metadata/identifier commits; the in-process HTTP sources were removed.

MCP/CLI parameters, runtime roots and regression obligations are in
[mcp-protocol](protocols/mcp-protocol.md). Agent integration rationale is ADR `0047`; extension
translation rationale is ADR `0049`, which supersedes ADR `0046`'s fixed orchestration.
Code/contract delivery does not establish real-device end-to-end acceptance or full-scale performance budgets.

## OCR extension execution foundation

OCR extension invocations use the OCR-specific task-level `IOcrJob`; see
[ADR 0054](adr/0054-task-level-ocr-extension-jobs.md) and the
[extension contract](protocols/extension-contracts.md). Host-mediated lazy inputs, typed
candidate settlement and generation leases do not give extensions authority over canonical
text or replace Working Revision/adoption. Explicit extension staging uses this contract;
the default queue's actual FSX engine migration remains #43 work, not delivered merely by the
new interface. PDF text-layer Provider and image OCR Engine remain independent choices.

The queue binds actual Runs to their task/attempt before model progress, carries the durable
preset version for legacy tasks once known, and rejects foreign/stale attempt callbacks.
Progress projection uses only explicit Run associations, including legitimate shared-Run
adoption/index retries, and does not infer ownership from DocumentInstance identity.
Ephemeral region recognition has a facade-owned background boundary; interactive callers keep
the existing modal policy, capture/apply UI state with context guards, and drain real work on
teardown rather than treating cancellation as completion. This does not establish that all
UI database/refresh paths have been remediated; those remain #46 scope.

## UI Layer

`src/Patchouli.UI` manages the desktop application state using a three-layer reactivity model ([ADR 0033](adr/0033-ui-reactivity-three-layer-model.md)):
- **DerivedPropertyGenerator**: Generates notifications for static, synchronous, same-instance property dependencies.
- **System.Reactive**: Handles time, async, collection, cross-object, and event relations.
- **CommunityToolkit.Mvvm**: Handles mutable VM state and commands.

Execution location is independent of reactivity: the UI thread applies state changes, but never
executes SQLite work or OCR inference. Neither an `Async` suffix nor `ConfigureAwait(false)` moves
synchronously completing work off the Dispatcher. Capture immutable inputs at the VM → Host
boundary, explicitly offload service work, then validate the Library/session/render generation
before applying a snapshot on the UI thread. OCR CPU work belongs behind the host execution
boundary. Event-driven reloads obey the same rule; visibility gates and one in-flight load plus
a dirty mark prevent refresh backlogs. `UiThreadExecutionGuard` records resource-boundary violations
without throwing or changing business results; the shared 100 ms UI heartbeat also detects pure
CPU stalls that do not open a database connection.

Foreground blocking is not UI-thread execution. Operations that irreversibly delete a
DocumentInstance's pages, Box Trees or revision history, and all existing blocking workflows,
use `ModalOperationRunner` with a separate modal dialog: main-window interaction stays disabled,
the running dialog cannot close, cancellation and progress remain responsive, and actual work
runs in the background even when no dialog can be shown. Soft Item changes (trash/restore/merge)
and tag/collection changes remain non-modal. This execution boundary complements, rather than
replaces, ADR `0033`'s notification/scheduling model.

Permanent Item purge freezes selected ids/titles before background dependency inspection. Only the
confirmed delete uses foreground modal blocking; switching/closing a Library cancels admission and
drains actual report/delete work before releasing its Host. A confirmation displays at most twenty
titles and dependency rows while retaining complete totals and the full deletion scope. Purge phase
diagnostics separately measure report/snapshot inspection, repository write-lease waiting, SQLite
write-lock/transaction acquisition, FTS readiness (including whether recovery rebuilt it), FTS and
document-history deletion, commit and UI projection. These records contain stages, durations and
SQLite error codes, not paths, ids or document contents. Batch deletion remains one transaction and
one LibraryRevision; original source files are never deleted by this operation.

## Document ownership and remaining implementation gaps

Completed behavior belongs in its owning document: vocabulary and UI semantics in CONTEXT, protocol details in protocols/, runtime ownership in this document, and architectural choices in ADR. Product work is tracked through GitHub Issues, not a dedicated PRD. Link to the owning document instead of repeating completed acceptance tables.

The shared composition layer and ADR `0024` one-host-per-Library target are implemented. A device-local lifetime lock keyed before SQLite open by the canonical database path elects exactly one Desktop or Headless owner; an atomic discovery record publishes the live process identity, Library ID and MCP endpoint, and the authenticated local lifecycle channel performs Desktop takeover. Normal UI, CLI and MCP writes therefore share the in-process revision event path. `LibraryRevisionMonitor` remains available for explicit abnormal external-write recovery, but the Desktop no longer starts its polling loop during normal operation. Full incremental cache invalidation remains V3-T7 work.
