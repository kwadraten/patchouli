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
agents own their conversations, credentials and history, and keep their native configuration;
Patchouli supplies detection, a per-library workspace, a terminal and a revocable MCP
connection.

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

## UI Layer

`src/Patchouli.UI` manages the desktop application state using a three-layer reactivity model ([ADR 0033](adr/0033-ui-reactivity-three-layer-model.md)):
- **DerivedPropertyGenerator**: Generates notifications for static, synchronous, same-instance property dependencies.
- **System.Reactive**: Handles time, async, collection, cross-object, and event relations.
- **CommunityToolkit.Mvvm**: Handles mutable VM state and commands.

## Document ownership and remaining implementation gaps

Completed behavior belongs in its owning document: vocabulary and UI semantics in CONTEXT, protocol details in protocols/, runtime ownership in this document, and architectural choices in ADR. Product work is tracked through GitHub Issues, not a dedicated PRD. Link to the owning document instead of repeating completed acceptance tables.

The shared composition layer and ADR `0024` one-host-per-Library target are implemented. A device-local lifetime lock keyed before SQLite open by the canonical database path elects exactly one Desktop or Headless owner; an atomic discovery record publishes the live process identity, Library ID and MCP endpoint, and the authenticated local lifecycle channel performs Desktop takeover. Normal UI, CLI and MCP writes therefore share the in-process revision event path. `LibraryRevisionMonitor` remains available for explicit abnormal external-write recovery, but the Desktop no longer starts its polling loop during normal operation. Full incremental cache invalidation remains V3-T7 work.
