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
- `.agents/mcp-protocol.md` for MCP/CLI parameters, response schemas and regression obligations.
- `.agents/palettes/` for the selectable UI color palettes (one `DESIGN.md` per palette).

If `.agents/CONTEXT.md` or `.agents/adr/` do not exist yet, proceed silently. Do not suggest creating them upfront unless the current task is explicitly about domain modeling, architecture documentation, or recording a decision.

## Expected structure

```text
/
├── AGENTS.md
├── .agents/
│   ├── CONTEXT.md
│   ├── mcp-protocol.md
│   ├── domain.md
│   ├── issue-tracker.md
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
ADR `0049` restores FSI solely for trusted, replaceable Extension implementations;
it does not restore Agent sessions or a durable workflow scheduler.
The revised execution boundary is a host-managed worker process per package generation,
with source checking, credentials, managed capability dispatch and data commits owned by
the host. See [Extension validation](extension-validation.md) for migration evidence.
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

MCP/CLI parameters, runtime roots and regression obligations are in
[mcp-protocol](mcp-protocol.md). Agent integration rationale is ADR `0047`; extension
translation rationale is ADR `0049`, which supersedes ADR `0046`'s fixed orchestration.
Code/contract delivery does not establish real-device end-to-end acceptance or full-scale performance budgets.

## UI Layer

`src/Patchouli.UI` manages the desktop application state using a three-layer reactivity model ([ADR 0033](adr/0033-ui-reactivity-three-layer-model.md)):
- **DerivedPropertyGenerator**: Generates notifications for static, synchronous, same-instance property dependencies.
- **System.Reactive**: Handles time, async, collection, cross-object, and event relations.
- **CommunityToolkit.Mvvm**: Handles mutable VM state and commands.

## Document ownership and remaining implementation gaps

Completed behavior belongs in its owning document: vocabulary and UI semantics in CONTEXT, protocol details in mcp-protocol, runtime ownership in this document, and architectural choices in ADR. Product work is tracked through GitHub Issues, not a dedicated PRD. Link to the owning document instead of repeating completed acceptance tables.

The shared composition layer and ADR `0024` one-host-per-Library target are implemented. A device-local lifetime lock keyed before SQLite open by the canonical database path elects exactly one Desktop or Headless owner; an atomic discovery record publishes the live process identity, Library ID and MCP endpoint, and the authenticated local lifecycle channel performs Desktop takeover. Normal UI, CLI and MCP writes therefore share the in-process revision event path. `LibraryRevisionMonitor` remains available for explicit abnormal external-write recovery, but the Desktop no longer starts its polling loop during normal operation. Full incremental cache invalidation remains V3-T7 work.
