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
- `.agents/PRD.md` when product intent, scope, or roadmap context matters.
- `.agents/palettes/` for the selectable UI color palettes (one `DESIGN.md` per palette).

If `.agents/CONTEXT.md` or `.agents/adr/` do not exist yet, proceed silently. Do not suggest creating them upfront unless the current task is explicitly about domain modeling, architecture documentation, or recording a decision.

## Expected structure

```text
/
├── AGENTS.md
├── .agents/
│   ├── CONTEXT.md
│   ├── PRD.md
│   ├── domain.md
│   ├── issue-tracker.md
│   ├── triage-labels.md
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
  `SqliteConnectionFactory`, runs migrations, and exposes the service fleet to consumers. Both
  hosts build it; nothing else should hand-wire domain services.
- **File watching** — `Watching/FileSearchRootWatcherService` owns the `FileSystemWatcher` fleet
  for configured FileSearchRoots plus the debounced rescan/import pipeline.
- **MCP lifecycle** — `Mcp/McpServerHost` owns MCP settings validation, listener start/stop/restart,
  and shutdown, surfacing status and failures through events (`McpHttpServer` and
  `McpProtocolHandler` live here).
- **Import orchestration** — `Import/LibraryImportOrchestrator` sequences multi-step import
  workflows without UI coupling.
- **Item cache and revision monitor** — `Caching/LibraryItemCache` is the in-memory first-screen
  read snapshot (tag filtering, untagged queries, sidebar tag counts);
  `Caching/LibraryRevisionMonitor` keeps it current.

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
