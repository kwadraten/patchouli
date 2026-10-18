# Literature Library

Patchouli is a personal literature manager that treats user-owned source files as evidence-bearing documents. The domain language centers on stable bibliographic identity, relocatable files, immutable page-local Document Box Trees, searchable units, and text-only evidence references.

The built-in agent keeps its original transcript append-only. Model requests preserve a stable
cached prefix until 80% of the configured context capacity, then compact the whole active prefix
and continue from a persisted summary. The session-local `history` tool retrieves original
entries on demand; see [ADR 0040](adr/0040-cache-preserving-whole-prefix-compaction.md).
Oversized tool results, including aggregate FSI SDK receipts, are folded only in model requests.
The Model and Translation settings expose a shared reasoning effort for built-in chat and workflow
model calls. Empty means provider default; explicit levels use the native API/subscription parameter
and depend on model support. OCR clients use their own request-level reasoning setting, independent of the chat/workflow default. The setting is saved
automatically and applies to subsequent calls, including explicitly selected workflow models.
The Model and Translation settings expose the maximum result length in characters; the warning
states that limit and directs the agent to request smaller batches. Original history stays intact.
The ordered context journal freezes model-visible results before requests, so replay and setting
changes preserve previously sent prefixes. History searches original nested payloads and can page
one FSI operation by ID. Request preflight reserves output and rejects estimated context overflow.

Provider cards use masked password inputs to load saved API keys from `ICredentialStore`. A saved baseline distinguishes loading from replacement edits, so reopening does not rewrite credentials and in-flight saves preserve newer input. Secret values stay out of the `Llm` configuration, session history and diagnostics. Provider authentication and subscription-model selectors use guarded selection properties; null selections during control creation, detachment or list refresh cannot overwrite stored configuration. Async headless UI tests must explicitly use `Dispatch<T>(Func<Task<T>>, ...)` (or unwrap the returned nested task) so assertions after awaited operations actually execute.

## Standing Product Boundaries

The delivered desktop baseline includes type-aware bibliographic editing and CSL rendering,
one-level collections, trash/restore/merge/purge, metadata and full-text search, PDF/Box editing,
OCR queues, explicit snapshot branch import and desktop single-instance activation.
CSL general is not silently mapped to document, and rendering failure is not empty success.
Page/document history uses working revisions and in-place commit; restoring history creates
a new commit. Bibliographic records and CSL styles do not share document-version history.
Product progress is maintained through [GitHub Issues](issue-tracker.md).

These boundaries are not backlog items. They are durable constraints that future issues should inherit unless an ADR explicitly replaces them.

**Storage and sync**:
The active runtime SQLite database stays outside sync roots. Sync publishes validated snapshot artifacts rather than syncing WAL/SHM files. Published snapshots use manifests plus SQLite shards; runtime caches, render images, and active working files are not snapshot payloads.

**User-owned source files**:
Original PDFs/images remain in user-managed folders. Patchouli stores FileAsset identity, fingerprints, known locations, pages, OCR/layout/search artifacts, and evidence metadata. A missing or moved source file does not delete Item metadata, OCR history, SearchUnits, or versioned evidence URIs.

**Cache boundary**:
Page renders, thumbnails, OCR intermediate images, and overlays are local rebuildable caches. `page_renders` is a local cache namespace only. MCP never returns cached images or image paths.

**Desktop storage and PDF rendering**:
Mutable application state uses platform application-data locations rather than an application bundle or sync root. FileSearchRoot paths and authorization payloads are device-local. PDFiumCore is the sole native page-rendering backend; its version is part of the renderer basis so page-render caches can be invalidated safely.

**OCR adoption**:
OCR output and manual edits produce a working revision. Only the committed current DocumentTreeRevision of each physical page feeds default search, evidence resolution, and MCP reads. Failed or cancelled OCR working revisions are deleted; only `OcrRun.status=failed` remains as audit. Cancelling page OCR inside a manual edit preserves that edit session and its draft. Saving or discarding ends the edit only after the database operation succeeds; closing a PDF tab and switching libraries await the corresponding draft cleanup using its original Host services. Whole-UI shutdown stops page OCR without bulk-deleting manual drafts, and hiding to tray keeps sessions alive. The next Desktop or Headless Host, after acquiring exclusive database ownership and applying migrations, transactionally removes only non-current `working` revisions with an `edit_session_id` and their boxes before serving requests. Recovery failures abort startup; committed history and ordinary OCR working revisions remain intact.

**Online multimodal OCR**:
The online recognizer has two modes in the multimodal OCR settings. Local layout detection is the default, using either the official PP-DocLayoutV3 ONNX export or the DocStructBench DocLayout-YOLO ONNX export. Weights are not bundled. The Local Files settings page provides the same confirmed download, progress, directory opening and clearing workflow as other local OCR models. Downloads use pinned upstream revisions with size and SHA256 verification, and install atomically into fixed directories below the application models root. The OCR settings page only selects mode and model. Missing weights produce an actionable configuration error rather than silently switching to direct recognition. The local detector supplies region geometry; the multimodal model orders the numbered regions and recognizes each crop. Reading order must be a complete permutation of detected region IDs; one invalid reply gets one correction attempt. Region recognition asks only for JSON text, never redundant coordinates or confidence; its text is anchored to the already known box. Region requests run with bounded concurrency (default 4, range 1–16) and results retain the LLM reading order even when calls finish out of order. Multimodal OCR has its own reasoning-effort setting (provider default, none, low, medium, high, max), defaulting to none; it applies to both ordering and recognition and leaves agent/translation requests unchanged. Reasoning effort is passed only to LlmTornado's generic ReasoningEffort setting; no provider-specific toggle, budget, or model-name workaround is injected by Patchouli. Any explicit region crop, including an already bounded logical-page target, skips local detection and reading-order calls and uses direct recognition. Region preset readiness checks only the online recognizer, so missing layout weights never block user-drawn regions. Direct mode bypasses local detection and uses the model's own boxes and text. Both modes use the shared document-tree import/commit pipeline and do not invoke MinerU for layout.

**OCR interchange schema**:

The document model is provider-neutral: only canonical box types and meaningful type-scoped subtypes are stored. There is no `base_type` or newly persisted OCR producer metadata. Unknown import types block adoption instead of silently becoming text. The shared Middle JSON 2.0 import adapter accepts `docvortex.middle`; legacy content-list adapters normalize aliases into the same leaf model. `code/algorithm` uses a Markdown algorithm payload (including inline math), while ordinary code retains literal code and its language. Visual annotations are typed payload data, not extra geometry inferred from parent regions.

Equations carry LaTeX and/or `HasImage`, which denotes the box's existing page region. Empty LaTeX is valid only with an image. Sidebar and whole-book reading crop the existing page bitmap. Agent XML includes an immutable `<image kind="page-region" page_id="…" x="…" y="…" width="…" height="…"/>` descriptor alongside the LaTeX payload; annotations are immutable siblings. XML carries no binary pixels, local paths or producer information. Migration 048 removes the old fallback column and normalizes historical type/payload representations while retaining revision IDs, box IDs, geometry and sibling links. Missing historical LaTeX cannot be reconstructed; reimport is needed. See ADR 0045.

OCR text and list Markdown may contain literal HTML-looking placeholders such as `<option>` in research prompts. The shared document-tree importer escapes forbidden raw HTML tags in these payloads before validation, preserving code spans, code blocks and typed code/LaTeX. Markdown validation distinguishes actual HTML nodes from escaped literals and code; raw complex-table HTML retains its validation boundary. Native preview and reading models decode Markdown entity nodes once into plain text, so escaped placeholders display as `<option>` without a WebView or a second HTML interpretation.
MinerU remains the preferred OCR provider, but its JSON is an import format rather than the database schema. Every provider produces a short-lived `OcrDocumentTreeCandidate`; the shared importer validates and creates working page-local `DocumentTreeRevision`/`DocumentBox` records. Provider JSON, table-cell records, reading-order integers, and Markdown ASTs are not canonical storage. An irregular table retains only its raw HTML source as diagnostic payload alongside the canonical `[Table]` placeholder. A physical page on which the engine succeeded but returned no text is likewise not a failure: the importer creates a `DocumentBoxType.LogicalPage` placeholder (payload `Blank page (OCR returned no content).`, diagnostic `blank_page_placeholder`), the page result counts as `Succeeded`, and the run can complete and commit normally.

**PDF import fault tolerance**:
A PDF import is atomic: FileAsset registration, Item creation, DocumentInstance attachment, every `pages` row, and the placeholder page revisions are built in memory and committed together under one write lease in a single transaction (pages inserted with batched multi-row `INSERT ... VALUES`), so a failed import leaves no partial rows. A physical page whose import-time page-info extraction (width/height/rotation) fails is likewise not a failure: the page still receives its `pages` row so page order stays complete, and a committed `DocumentBoxType.LogicalPage` placeholder (payload `Import failed for this page: <reason>`, diagnostic `import_page_placeholder`) records the failure on the page itself — the same product principle as `blank_page_placeholder`. Only when `failedPages / pageCount` exceeds `MaxFailedPageRatio` does the whole import fail and write nothing; at or below the threshold the book imports normally. The threshold defaults to 0.2 with range [0, 0.9], stored as `ImportAppSettings.MaxFailedPageRatio` in the settings JSON `"Import"` section and edited as a percentage in the settings 导入 section. Startup maintenance additionally runs `ImportResidueGcService` to remove pre-atomic half-imported residue (zero-page DocumentInstances, orphaned Item/FileAsset pairs) under a conservative all-rows-older-than-one-hour rule, delegating remaining unreferenced FileAssets to `FileAssetGcService`.

**Search and evidence**:
SearchUnits are persisted derived text units generated one per non-suppressed leaf DocumentBox in sibling-pointer order. SearchUnit metadata is synced; the local FTS index is a rebuildable local cache. Evidence identity is part of the versioned URI `patchouli://texts/{document-instance-id}/page-{page-index}.xml?rev={tree_revision_id}&box={box_id}`; a URI with `rev` reads that immutable revision, and a URI without `rev` reads HEAD. Legacy `.md` evidence addresses remain readable during migration and resolve to canonical `.xml` response URIs with an `[obsoleted]` warning.

**MCP surface**:
The virtual Library filesystem is resolved on demand through bounded runtime-host domain RPCs. One desktop or headless .NET host is the only authority for a Library database, cursors, revisions, projections and writes. The desktop host includes the UI and local MCP HTTP endpoint; `patchouli-cli` is a thin local client of that endpoint and auto-starts the same binary headlessly when no host exists. There is never a direct-SQL CLI path or second domain implementation. Directory paging, traversal and batch limits, command/output limits, and bounded rebuildable compiled-page caches constrain reads.
MCP is a first-class human-machine collaboration interface, not a read-only inspection surface (ADR `0036`, amending `0010`). The production surface is the structured `patchouli.find`, `patchouli.fetch`, `patchouli.add`, `patchouli.view`, `patchouli.put`, `patchouli.cite`, and `patchouli.send` contract from ADR `0024` (as extended by ADR `0034`, ADR `0036`, ADR `0044`, and ADR `0045`). **Capability boundaries are user-controlled**: the tool/write policy lets the user switch `put` and `send` off, returning MCP to read-only, and `add`/`view` have their own independent switches; **data boundaries are developer invariants** that no setting relaxes—MCP never edits bbox, triggers OCR, rebuilds indexes, reveals file URLs, or leaks provider secrets/configuration. Two explicit exceptions exist (ADR `0045`): `add` accepts a host-local file path as its input (never a URL), and `view` returns the source image as a native MCP image block; neither exposes host source paths, cache paths, or geometry beyond the view crop range. Native item and Library resources use YAML; source and translation pages use box XML. Canonical page `put` operations atomically replace payloads by box ID and use `source_revision`, plus `translation_version` for translations, as optimistic concurrency checks. Source edits create committed page-tree history and a document commit; active UI drafts block them. CSL style writes remain atomic whole-resource replacements. The tool/write policy still gates every write. The Bashkit virtual shell implementation has been removed from `main`; it exists only on the `feature/mcp-ab-benchmark` branch as historical benchmark evidence and is not the production MCP path. Unrelated metadata mutation remains out of scope.

MCP capability settings use a VFS domain × verb matrix (ADR [0038](adr/0038-chat-fsi-repl-and-vfs-permission-matrix.md)); every shared command checks its requested domain, including restored cursor scopes. Built-in chat and workflow sessions always have a session-local FSI REPL, started lazily on first execution; the chat page has no enable/disable control. FSI is a trusted .NET execution surface with full host privileges and is independent of the MCP matrix. Its interpreter bindings are process-local; its completed results remain in durable history.

MCP settings are excluded from the settings page's automatic-save pipeline. Leaving the MCP subpage, changing workspace tabs, closing settings, or exiting with pending MCP changes requires an explicit choice to discard or save and restart the listener. Dismissing the dialog, invalid settings, or a failed restart keeps the user on the current page. Discard restores the running listener's configuration when a previous save has not yet been applied. Reload detection compares configuration values rather than persistence revision numbers.


**Snapshot branches**:
Snapshot divergence creates a Snapshot Branch. Branches are inspected and imported explicitly; v1 does not perform automatic object-level merge or silent last-writer-wins conflict resolution.

**Provider credentials**:
ProviderCredential values are user-owned secrets for OCR/HTR providers. They may be present in trusted-user-device sync only through the mutable sensitive credential path, never in immutable historical content shards, never in MCP, and never in logs.

**Desktop settings**:
The settings sidebar has seven groups: 外观与阅读, 书库与本机文件, 导入与识别, AI集成, 元数据来源, 搜索, and 同步与快照. Library paths and managed local files share one page; import policy and OCR engines share another, including the multimodal OCR provider/model selection. AI集成 has separate 模型与翻译, 工作流, and MCP 服务与权限 tabs. Providers and credentials remain shared by translation and OCR. Stable section ids route menu shortcuts independently of icons. A flat section registry owns loading, dirty state, save, and discard so navigation grouping never hides a draft from auto-save. Workflow definitions and scripts are explicitly saved from a modal editor with F# highlighting, line numbers, undo, and unsaved-change protection; built-in and locked definitions remain read-only.

Incomplete provider connections persist with an explicit `IsAdded` flag; API keys remain in the credential store. Invalid library exclusion patterns, OCR engine choices, search rules, and incomplete sync bindings are saved as typed device-local drafts beside the user settings file (`.drafts.json`), with visible completion errors. They do not replace executable configuration until valid. Auto-save isolates failures per section and drains newer edits without falsely clearing their dirty state. Permission cells are disabled for unsupported resource verbs. Workflow menu launches open the chat tab and select the returned session id, including already finished sessions.

Settings writes preserve their ownership boundaries: ordinary preferences use `AppSettingsStore` field-level saves; device-root bindings and snapshot checkpoints update only their target entry under the shared settings-file write gate. Sync form edits use the explicit target-binding path, while unrelated saves preserve the latest persisted bindings and checkpoints. The full settings writer is internal fixture initialization only. Successful commits notify the Host of changed LLM, metadata-lookup, and file-scanning preferences; credentials and Library-synced metadata retain their own update paths. Other startup settings keep their existing application timing, and MCP listener changes retain their explicit restart flow.

## Language

**Library**:
The durable boundary for metadata, document instances, OCR/layout/search artifacts, versioned evidence URIs, snapshots, credentials, and MCP resolution. A library keeps the same identity when renamed or moved.

**Item**:
The bibliographic identity that a researcher cites, such as a book, article, edition, translation, preprint, or manuscript witness. An Item is the meeting point of its ItemMetadata (bibliographic projection) and zero or more DocumentInstances (evidence-bearing documents); an Item with identifiers and metadata but no DocumentInstance is a legitimate bibliographic resource whose document Patchouli does not yet hold.
User-facing Chinese term: 题录. It has the same product meaning as citation: the citable bibliographic record, not the PDF file itself.
_Avoid_: Book, PDF, file, attachment

**ItemMetadata**:
The persistent, editable bibliographic projection of an Item, produced through human, agent, document-derived, or identifier-derived description for retrieval, sorting, citation, deduplication, and browsing (ADR `0026`). It is derived epistemically from evidence but is not mechanically rebuildable: deleting it loses user knowledge such as normalized creators, CitationKeys, and Tags.
_Avoid_: Rebuildable cache, derived data, materialized view, metadata assertion
**Item language**:
The optional source-language metadata for an Item. Recognized language names and ISO 639-2 aliases normalize to ISO 639-1; valid region/script subtags are preserved in normalized BCP 47 form. Empty language is null and unknown values remain available as entered. CSL rendering may choose a different output locale on a render copy, but does not rewrite the source-language metadata.
_Avoid_: Output locale, translated language

**CSL language locale mapping**:
A device-local fallback preference from an Item's primary language to a CSL output locale. Matching bibliography locale selectors and their reachable macros take precedence. If no source-language branch matches, configured mappings are tried; default rules map `ja` and `ko` to `zh-CN`. Unmapped languages and unavailable mapping targets use the style's default layout, rather than forcing English or a special Chinese fallback. The saved/requested locale controls the root default for unqualified CSL terms and layouts. Rendering keeps the selected items in one processor invocation so whole-list ordering and citation behavior remain coherent.

Desktop CSL management lives in Settings → 引用与题录, with 题录本地化, 已安装样式, and 在线样式 subpages. The default-localization picker defaults to 使用样式默认 (a null locale override); explicit existing locales remain selectable. Localization and language mappings auto-save together without changing the default style. Installed-style actions and online catalog operations share one manager, and legacy style-management entry points and CSL resource links open the installed-styles settings subpage. Removing or disabling the default style clears that preference while retaining localization settings.
_Avoid_: Persisted item-language rewrite, per-item independent render

**Tag**:
A user-editable label attached to an Item for organization and filtering. Tags originate from the `keyword` field in metadata sources (e.g. CSL JSON, BibTeX), but are an extension that allows arbitrary user editing — they are not tied to the original metadata keyword after import. The Item model stores tags as a JSON string array in `tags_json`; `keyword` is NOT stored independently. When importing literature metadata, the `keyword` field should be ingested into tags rather than kept as a separate field.
User-facing Chinese term: 标签.
_Avoid_: Keyword, subject, category

**Collection**:
A one-level user playlist inside a Library: a named many-to-many relation over Items (集合). Collections sort by name, may be empty, and cannot nest. Membership lives in `item_collections` keyed by stable `collection_id`; the legacy `items.collections_json` mirror is cleared and never authoritative. Dissolving a Collection removes only its membership rows and never deletes Items. Memberships survive Item trash/restore, follow the target on merge, and are removed on purge.
_Avoid_: Folder, tag, nested collection, `collections_json` as authority

**FileAsset**:
The identity and verification record for an original user-owned file, independent of where that file currently lives.
_Avoid_: PDF, attachment, path

**KnownFileLocation**:
A remembered local path where a FileAsset has been seen.
_Avoid_: File identity, canonical path

**FileSearchRoot**:
A user-approved directory tree that can be scanned to relocate missing or moved FileAssets.
_Avoid_: Sync root, library root

**DocumentInstance**:
A concrete manifestation of an Item, such as a scan, OCR PDF, partial file, supplement, or alternate digitization. It owns physical pages, page-local Document Tree revisions, search units, and versioned evidence URIs. It is an evidence-bearing digital surrogate: its page images, front matter, layout, and pagination usually carry enough evidence to distinguish manifestations of the same work, but a cropped or incomplete file loses physical-form evidence and is not the original physical object itself.
_Avoid_: File, item, attachment

**Page**:
An ordered page within a DocumentInstance, with coordinate basis metadata used to interpret layout and evidence regions.

**DocumentTreeRevision**:
An immutable, page-local revision of a physical Page's Box Tree. Revisions are either `working` or `committed`; only one committed revision per page is current. Legacy `staging`/`draft`/`discarded` rows remain in user databases but are never read.
_Avoid_: LayoutRevision, document-wide OCR text blob

**DocumentCommit**:
A document-wide commit that groups one `DocumentTreeRevision` per page of a `DocumentInstance`. HEAD is the latest commit; history is append-only and revert is recorded as a new commit. Distinct from `LibraryRevision`, which remains a whole-library change counter.
_Avoid_: Library-wide version, global revision

**DocumentBox**:
A stable-ID node inside one DocumentTreeRevision. Sibling pointers are the only canonical order. A Box has a normalized bbox, type, optional typed leaf payload, and `suppressed`; only `logical_page` may have children.
_Avoid_: LayoutNode, reading-order row, table cell

**Logical Page**:
An optional `logical_page` root used only when one scanned physical Page contains multiple page regions. Logical pages are ordered siblings inside the physical page and are not rows in `pages`.

**Compiled Markdown**:
The deterministic, ephemeral Markdown projection of a DocumentTreeRevision. The central Markdig pipeline produces validation, plain text, and native-preview nodes; AST and UI SourceMap are never persisted or synced.

**Page Translation**:
The box-derived full-text translation of one physical Page. It is stored per content box (`translation_boxes`) under a per-page header (`page_translations`) and compiled on demand into a whole-page Markdown that shares the source page's box ids and SourceMap, so a partially translated page is still one structurally valid document and UI source-map linkage is unchanged. It is derived data over the current committed DocumentTreeRevision: a tree change expires only the boxes whose source payload changed or disappeared, and reading a stale page realigns it first. The canonical `patchouli://translations/{document-id}/page-{index}.xml` view carries source and translation versions and exposes each box's translated flag. An agent submits selected `{box_id, content}` replacements where `content` is the complete fetched `<box>` XML element; its id and structural attributes stay fixed while translated state and payload text change. The host validates both versions and commits the batch atomically. A page with no translation returns an editable version-zero view that falls back to source payloads. Legacy `.md` requests retain whole-page Markdown behavior during migration and return a canonical XML URI with an `[obsoleted]` warning naming the 0.4.0 deprecation target. Translation never becomes the source of record for page text (ADR `0034`); see ADR `0044`.
_Avoid_: Bilingual blob, translated revision, machine-translation provider, translation of a logical page

**Book Reading Mode**:
A full-tab reading surface inside the PDF workspace tab, entered from the toolbar's 阅读模式 button and left via 退出. A `ReadingView` control renders the DocumentInstance's compiled Markdown pages as `ReadingScene`s loaded on demand through `IBookReadingStream` (list indices + per-page compile): the workspace keeps a window around the current page (initial ±2/+8, batches of 8 when the reader scrolls near either edge), never the whole book. The session caches delivered pages so a view recreated by a tab switch replays them (`ReplayBookReading`); the cache dies on exit so fixes made in the workbench show up on re-entry. Page boundaries are not in-flow headings: the page rail is part of the reading scroller's content and pins one 第 N 页 badge per page at the measured top of that page's first block (`GetMeasuredBlocks`), so a badge stays block-aligned at every scroll offset; clicking a badge exits reading mode and navigates the workbench to that page. Its font family and size are device-local `UiPreferences` (`ReadingFontFamily`/`ReadingFontSize`), adjustable from the reading toolbar and the 外观与阅读 settings section. It is text-only reading: no box-level traceability, no editing.
_Avoid_: replacing the library-shell IsReadingMode concept (a different, page-level mode); unloading far-away pages (the window only grows — offsets and badge geometry assume loaded pages stay put)

**OCR Preset**:
A user-facing reusable OCR/HTR configuration. Presets are selected manually and are distinct from Search Profiles.
_Avoid_: OCR Profile

**OCR Preset Version**:
An immutable version of an OCR Preset used for OCR provenance. Rebinding paths, models, endpoints, or parameters creates a new version.

**OCR Run**:
An attempt to produce OCR/HTR output for a DocumentInstance, page set, or region using a specific OCR Preset Version.

**Workflow**:

Workflow scripts now export a cold, typed `AgentWorkflow` value that controls the existing
agent through `AgentCore.chatStep`: typed agent stages, tool scopes, budgets, routing and
bounded immutable refinement. The former imperative script API was removed by ADR `0039`.
See [the workflow API](workflow-api.md) for authoring and verification boundaries.
A `.fsx` script that declares display metadata and independent typed parameters through the SDK, with stable platform identity and lock state saved with the Library. Static analysis provides the parameter and selection projection; user values are configured in the workflow editor (ADR 0042). A Workflow runs in-process with host permissions and is designed for user-trusted local scripts, not as a sandbox. A session stores a snapshot of its Workflow, so editing a Workflow affects only later sessions; the built-in full-text translation Workflow is locked by default. Workflow definitions are user configuration managed separately from Session data.
_Avoid_: Task, script file, job definition

**Session**:
An agent conversation run driven by a Workflow. Session data is auxiliary Library data kept in one directory per session (message/event history, launch parameters, script snapshot, current step and completed results, pending messages and calls, run status) and can be physically purged without touching bibliographic records, source files, translations, or OCR output. A Session's lifecycle is owned by the host session service; a missing session directory does not prevent the Library from opening.
_Avoid_: Job, task, thread

**Agent Context**:

One continuous original transcript owned by a Session.
The main context receives user turns; explicitly bound contexts share history within their
workflow scope; anonymous contexts belong to one agent-node invocation. The SDK assigns
and restores runtime identities. A normal F# `let` alias does not establish a binding scope.

**Loop activation**:

One invocation with frozen instructions, tools, bindings and
execution policy. Multiple activations can use the same Context. Prefix generations belong
to an activation; effects and receipts include activation identity. Context handoff preserves
history and typed results, while worker evidence and unknown-write isolation remain Session-wide.
See [ADR 0043](adr/0043-explicit-agent-contexts-and-loop-activations.md).

**Run**:
The run projection of an OCR task or an agent Session, observed through `patchouli://runs/`. OCR runs and agent Sessions report their own status and incremental events and do not share a queue, scheduler, or state machine; `runs/` does not unify their states. A Run is a volatile runtime resource: it is not part of snapshots and cannot be `put`.
_Avoid_: Job, task queue, unified run state

**Chat Tab**:
The chat surface for Sessions: session list, session view (message stream, tool/progress events), and message input, with stop/resume controls. Closing a chat tab only removes the frontend; it never stops the Session, which is an independent operation. Opening a chat tab alone does not start any Session.
_Avoid_: Task queue view, window, dialog

**Working Revision**:
A previewable page-local revision produced by OCR import or manual editing. It becomes visible to search, evidence, and MCP only after in-place commit. A failed or cancelled working revision is deleted.
_Avoid_: Staging result, candidate result

**SearchUnit**:
A persisted derived text unit generated from one non-suppressed leaf DocumentBox for search, evidence, and page context. SearchUnit metadata is synced; the FTS index is rebuildable local cache.
_Avoid_: FTS row, snippet

**SearchProfile**:
A search-time bundle of rewrite rules, aliases, and recall behavior. Rewriting is gated per library by the `search_settings.rewrite_enabled` flag (default enabled): disabling it skips plan building in full-text search, while an explicitly requested preview still works. `simplified_traditional` rewrite rules use an OpenCC configuration name (for example `s2t`) as their pattern and expand Simplified↔Traditional, Taiwan, Hong Kong, and Japanese variants; `bidirectional` also adds the reverse configuration's conversion. It is unrelated to OCR Presets.
_Avoid_: OCR Profile, OCR Preset

**Versioned Evidence URI**:
A long-term parseable text reference whose canonical identity is the URI `patchouli://texts/{document-instance-id}/page-{page-index}.xml?rev={tree_revision_id}&box={box_id}`. A URI with `rev` resolves to the immutable revision; a URI without `rev` resolves to HEAD. Legacy `.md` addresses remain readable during migration and return the canonical URI with a deprecation warning. SearchUnit is the discovery surface, not the evidence identity.
_Avoid_: Citation, file URL, local path, evref

**Snapshot**:
A published sync artifact for a Library, represented by a manifest and content-addressed SQLite shards.
_Avoid_: Backup, runtime database

**Snapshot Branch**:
A divergent published snapshot lineage created when multiple writers publish from different parents.
_Avoid_: Last-writer-wins conflict

**ProviderCredential**:
A user-owned token, key, or credential used by OCR/HTR providers. It is never exposed through MCP.
_Avoid_: Provider config, secret in shard

API LLM providers use one active key per provider through ICredentialStore. OCR, chat and workflows share provider connections and credential storage. OCR and ordinary chat
have separate model selections. Workflow parameters and their defaults are independently declared
through the SDK in fsx and configured only in the workflow editor (ADR 0042). Translation declares
its own language, window radius and previous-translation context parameters.
Subscription authentication follows ADR 0037.

MultimodalLlmOcrAdapter sends page/region images directly to the selected vision model and normalizes
the response to OcrEnginePageResult. OcrRunEngine converts it to OcrDocumentTreeCandidate for the
shared import/commit path; providers never directly write document_boxes. Environment/preset
checks validate provider, credential metadata and model configuration. Failures retain the existing
OcrRetryPolicy classification. Text-only subscription backends cannot serve vision OCR.
OCR remains independent of agent sessions and keeps its own queue, scheduler and activity tracking.

**MCP surface**:
A first-class human-machine collaboration interface for library exploration, evidence retrieval, citation rendering, local file import, source-image collation, and controlled interaction with agent runs and workflows (ADR `0036`, amending `0010` and `0023`). Production uses `patchouli.find`, `patchouli.fetch`, `patchouli.add`, `patchouli.view`, `patchouli.put`, `patchouli.cite`, and `patchouli.send` under ADR `0024` (as extended by ADR `0034`, ADR `0036`, ADR `0044`, and ADR `0045`), served by the one desktop or headless runtime host for the Library. CLI is a local MCP client of that host; remote/local agent clients use the same service. All agents can fetch the fixed `patchouli://library.yml` projection (`library_id`, `display_name`, and—when the device-local `ExposeLibraryTags`/`ExposeLibraryCollections` policy allows—sorted tags and collections with item counts; collections include empty ones). Item filtering supports exact `collection_id` and exact case-sensitive `tag` clauses intersecting with AND; hiding a category returns `PERMISSION_DENIED` for that filter and removes it from relationship output. There is no `/collections` VFS directory and no collection URI, and Collections are MCP write-protected. A fourth root, `patchouli://translations/`, exposes the derived page translations: document directories list translation progress and per-page status (`untranslated`/`partial`/`translated`/`stale`); canonical page XML uses box IDs, payloads, and version checks, with atomic selected-box writes. Legacy `.md` translation requests retain their Markdown format and receive an `[obsoleted]` warning naming the 0.4.0 deprecation target and canonical XML URI. Item metadata and the Library projection use YAML; legacy `.bib` and `library.toon` requests retain their encodings with the same migration warning. Two further roots, `patchouli://runs/` and `patchouli://workflows/`, expose run observation and workflow discovery/launch (see **Run** and **Workflow**). Capability boundaries are user-controlled: `put` and `send` are switchable tool/write-policy entries that return MCP to read-only when disabled (ADR `0036`); `add` and `view` have their own independent switches. Data boundaries are developer invariants regardless of that policy: MCP never exposes local paths, provider secrets, file URLs, or OCR/index actions, and it cannot read provider keys. The two explicit exceptions are `add` accepting a host-local file path as input and `view` returning the source image as a native MCP image block (ADR `0045`); neither exposes host source paths, cache paths, or unsanitized errors. `.NET` remains the sole domain authority for Library data.


## Library Lifecycle And Search UI

**Item Lifecycle**: Active, Trash (回收站), Merged (合并重定向墓碑), and Purged (永久删除，仅保留 purge record). Trash and Merged retain Item rows; Purged does not. These are lifecycle states, separate from the user-editable bibliographic `item_status`. See [ADR 0030](adr/0030-item-lifecycle-merge-and-purge.md) for merge, purge, snapshot remapping, tags and GC.

**Bibliographic Search / 元数据筛选**: title/creator/identifier matching plus structured Item filters through `LibraryItemQueryService.SearchRowsAsync`. Structured keys include exact `collection_id` and exact case-sensitive `tag` filters that intersect with the other filters by AND. Results exclude trash/merged Items and use the library's columns and persisted grid preferences.

**Full-text Search / 全文搜索**: SearchUnit/FTS search grouped by Item, with child snippets and navigation through versioned evidence URIs. Snippets normalize line breaks, emphasize matches and truncate around matches with CJK display width considered. Index readiness remains a derived capability, not an Item/Document/FileAsset status.

**Advanced Search / 高级搜索**: one fixed keyword row plus removable filter rows combined with AND. Structured keys align with MCP Item filters (`item_type`, `item_status`, `primary_document_ocr_index_status`, `citable`), with title/creator/identifier contains filters. Both search modes apply these rows; full-text search pushes them down as `SearchRequest.ItemFilters`. The MCP texts scope also accepts `item_id`; this does not change evidence identity or add semantic search. Sidebar tag filtering is a separate library interaction.

Switching modes keeps input and filter rows. Enter and the search button use the same command; both modes recognize `patchouli://` navigation. Empty full-text input prompts for terms; bibliographic search accepts filters alone, otherwise prompts for terms or filters. Opening the advanced-filter form does not itself run a search. Copy/export evidence and Markdown, explicit UI index rebuild, and stale/partial/unavailable indicators remain available.

## Desktop View And Dialog Vocabulary

The mixed agent SDK is implemented (workflow snapshot API `/8`): native FC and session-local FSI
functions share validation and durable operation receipts; chat and workflows share AgentDriver.
Cold immutable pipelines checkpoint node outputs and branch/loop routes. Translation binds page
targets in `commitTranslation` and derives completion from receipts. Interrupted FSI blocks and
unknown writes are not blindly replayed. See [workflow-api](workflow-api.md) for APIs and recovery limits.

- **Library / 书库**: the ProDataGrid Item list, tag and collection sidebars and selected-Item inspector. The collection section lists one-level playlists sorted by name, filters the grid to a selected collection, and accepts Item drag/drop, rename, and dissolve actions. Source fields are type-aware; column visibility, width, order and sorting use persisted UI preferences. Search grids follow the library column settings.
- **Trash / 回收站**: a library section with restore/permanent-purge actions; tag navigation and content-edit/OCR entry points are hidden.
- **PDF Workspace / PDF 工作台**: page navigation, raster, Box Tree, text preview and page revision history. Document commit history is also available from the Item editor's file-management area. History restoration creates a new commit, never rolls HEAD backward.
- **ConfirmDialog**: shared confirmation window; danger mode is used for destructive local-file/tag operations.
- **ItemMergePreviewDialog**: explicit target and field-conflict selection before merging Items.
- **PurgeConfirmDialog**: blocking permanent-delete confirmation with expandable dependency details.

These names identify existing UI surfaces, not new persisted domain entities. More elaborate annotation storage and Markdown preview component selection require future issue-driven design.


## Native MCP resources and migration

MCP resources now use CSL/application YAML for items and `library.yml`, and page-local box XML
for source text and translations. The tool envelope stays TOON/JSON. New resource links use
`.yml`/`.xml`; legacy BibLaTeX, Markdown and `library.toon` addressing retains its encoding and
compatibility writes, with an `[obsoleted]` warning naming the 0.4.0 deprecation target and new
URI. `put` accepts either complete `content` or 1–100 complete `<box>` XML element replacements, never both.
Canonical XML writes use source revision checks and translation version checks; source writes
create committed page history and a document commit. Root-relative VFS paths normalize before
permission checks.
See [MCP protocol](mcp-protocol.md) and ADR 0044 for the normative current contract.
