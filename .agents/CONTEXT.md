# Literature Library

Patchouli is a personal literature manager that treats user-owned source files as evidence-bearing documents. The domain language centers on stable bibliographic identity, relocatable files, immutable page-local Document Box Trees, searchable units, and text-only evidence references.

## Standing Product Boundaries

These boundaries are not backlog items. They are durable constraints that future PRDs should inherit unless an ADR explicitly replaces them.

**Storage and sync**:
The active runtime SQLite database stays outside sync roots. Sync publishes validated snapshot artifacts rather than syncing WAL/SHM files. Published snapshots use manifests plus SQLite shards; runtime caches, render images, and active working files are not snapshot payloads.

**User-owned source files**:
Original PDFs/images remain in user-managed folders. Patchouli stores FileAsset identity, fingerprints, known locations, pages, OCR/layout/search artifacts, and evidence metadata. A missing or moved source file does not delete Item metadata, OCR history, SearchUnits, or versioned evidence URIs.

**Cache boundary**:
Page renders, thumbnails, OCR intermediate images, and overlays are local rebuildable caches. `page_renders` is a local cache namespace only. MCP never returns cached images or image paths.

**Desktop storage and PDF rendering**:
Mutable application state uses platform application-data locations rather than an application bundle or sync root. FileSearchRoot paths and authorization payloads are device-local. PDFiumCore is the sole native page-rendering backend; its version is part of the renderer basis so page-render caches can be invalidated safely.

**OCR adoption**:
OCR output and manual edits produce a working revision. Only the committed current DocumentTreeRevision of each physical page feeds default search, evidence resolution, and MCP reads. Failed or cancelled working revisions are deleted; only `OcrRun.status=failed` remains as audit.

**OCR interchange schema**:
MinerU remains the preferred OCR provider, but its JSON is an import format rather than the database schema. Every provider produces a short-lived `OcrDocumentTreeCandidate`; the shared importer validates and creates working page-local `DocumentTreeRevision`/`DocumentBox` records. Provider JSON, table-cell records, reading-order integers, and Markdown ASTs are not canonical storage. An irregular table retains only its raw HTML source as diagnostic payload alongside the canonical `[Table]` placeholder. A physical page on which the engine succeeded but returned no text is likewise not a failure: the importer creates a `DocumentBoxType.LogicalPage` placeholder (payload `Blank page (OCR returned no content).`, diagnostic `blank_page_placeholder`), the page result counts as `Succeeded`, and the run can complete and commit normally.

**Search and evidence**:
SearchUnits are persisted derived text units generated one per non-suppressed leaf DocumentBox in sibling-pointer order. SearchUnit metadata is synced; the local FTS index is a rebuildable local cache. Evidence identity is part of the versioned URI `patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree_revision_id}&box={box_id}`; a URI with `rev` reads that immutable revision, and a URI without `rev` reads HEAD.

**MCP surface**:
The virtual Library filesystem is resolved on demand through bounded runtime-host domain RPCs. One desktop or headless .NET host is the only authority for a Library database, cursors, revisions, projections and writes. The desktop host includes the UI and local MCP HTTP endpoint; `patchouli-cli` is a thin local client of that endpoint and auto-starts the same binary headlessly when no host exists. There is never a direct-SQL CLI path or second domain implementation. Directory paging, traversal and batch limits, command/output limits, and bounded rebuildable compiled-page caches constrain reads.
MCP is **text-only** and the selected production surface is the structured `patchouli.find`, `patchouli.fetch`, `patchouli.put`, and `patchouli.cite` contract from ADR `0024`. MCP never edits bbox, triggers OCR, rebuilds indexes, exposes local paths, returns images, reveals file URLs, or leaks provider secrets/configuration. MCP 无法读取提供程序密钥. **Limited writes** of whole item bibliography projections and CSL styles are deliberate v3 product decisions under ADR `0023`: `put` is an atomic complete-resource replacement with no base-revision precondition, and is implemented behind the configured tool/write policy. The Bashkit virtual shell implementation has been removed from `main`; it exists only on the `feature/mcp-ab-benchmark` branch as historical benchmark evidence and is not the production MCP path. Unrelated metadata mutation remains out of scope.


**Snapshot branches**:
Snapshot divergence creates a Snapshot Branch. Branches are inspected and imported explicitly; v1 does not perform automatic object-level merge or silent last-writer-wins conflict resolution.

**Provider credentials**:
ProviderCredential values are user-owned secrets for OCR/HTR providers. They may be present in trusted-user-device sync only through the mutable sensitive credential path, never in immutable historical content shards, never in MCP, and never in logs.

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
The box-derived full-text translation of one physical Page. It is stored per content box (`translation_boxes`) under a per-page header (`page_translations`) and compiled on demand into a whole-page Markdown that shares the source page's box ids and SourceMap, so a partially translated page is still one structurally valid document and UI source-map linkage is unchanged. It is derived data over the current committed DocumentTreeRevision: a tree change expires only the boxes whose source payload changed or disappeared, and reading a stale page realigns it first. Patchouli ships no translation engine; external agents read the source page through MCP and `put` a structurally identical translated page to `patchouli://translations/{document-id}/page-{index}.md`. A translation never becomes the source of record for page text (ADR `0034`).
_Avoid_: Bilingual blob, translated revision, machine-translation provider, translation of a logical page

**Book Reading Mode**:
A full-tab reading surface inside the PDF workspace tab, entered from the toolbar's 阅读模式 button and left via 退出. A read-only AvaloniaRichEditor renders the DocumentInstance as HTML pages loaded on demand through `IBookReadingStream` (list indices + per-page compile): the workspace keeps a window around the current page (initial ±2/+8, batches of 8 when the reader scrolls near either edge), never the whole book. The session caches delivered pages so a view recreated by a tab switch replays them (`ReplayBookReading`); the cache dies on exit so fixes made in the workbench show up on re-entry. Page boundaries are not in-flow headings; a left rail pins one 第 N 页 badge per page at offsets tracked by `BookReadingPageMap` (the editor has no block-geometry API, so font changes rebuild the document from the cached per-page HTML and re-measure), and clicking a badge exits reading mode and navigates the workbench to that page. Its font family and size are device-local `UiPreferences` (`ReadingFontFamily`/`ReadingFontSize`), adjustable from the reading toolbar and the 外观与显示 settings section. It is text-only reading: no box-level traceability, no editing.
_Avoid_: replacing the library-shell IsReadingMode concept (a different, page-level mode); unloading far-away pages (the window only grows — offsets and badge geometry assume loaded pages stay put)

**OCR Preset**:
A user-facing reusable OCR/HTR configuration. Presets are selected manually and are distinct from Search Profiles.
_Avoid_: OCR Profile

**OCR Preset Version**:
An immutable version of an OCR Preset used for OCR provenance. Rebinding paths, models, endpoints, or parameters creates a new version.

**OCR Run**:
An attempt to produce OCR/HTR output for a DocumentInstance, page set, or region using a specific OCR Preset Version.

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
A long-term parseable text reference whose identity is the URI `patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree_revision_id}&box={box_id}`. A URI with `rev` resolves to the immutable revision; a URI without `rev` resolves to HEAD. SearchUnit is the discovery surface, not the evidence identity.
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

**MCP surface**:
The text-only external surface for library exploration, evidence retrieval, citation rendering, and—when enabled—limited whole-resource writes of item bibliography and CSL styles (ADR `0023`). Production uses `patchouli.find`, `patchouli.fetch`, `patchouli.put`, and `patchouli.cite` under ADR `0024`, served by the one desktop or headless runtime host for the Library. CLI is a local MCP client of that host; remote/local agent clients use the same service. All agents can fetch the fixed `patchouli://library.toon` projection (`library_id`, `display_name`, and—when the device-local `ExposeLibraryTags`/`ExposeLibraryCollections` policy allows—sorted tags and collections with item counts; collections include empty ones). Item filtering supports exact `collection_id` and exact case-sensitive `tag` clauses intersecting with AND; hiding a category returns `PERMISSION_DENIED` for that filter and removes it from relationship output. There is no `/collections` VFS directory and no collection URI, and Collections are MCP write-protected. A fourth root, `patchouli://translations/`, exposes the derived page translations: a document directory lists translation progress, a document directory lists per-page status (`untranslated`/`partial`/`translated`/`stale`), and `patchouli://translations/{document-id}/page-{index}.md` is a writable whole-page translation that must be structurally identical to the source page Markdown. MCP never exposes local paths, provider secrets, images, file URLs, or OCR/index actions. `.NET` remains the sole domain authority for Library data.


## Library Lifecycle And Search UI

**Item Lifecycle**: Active, Trash (回收站), Merged (合并重定向墓碑), and Purged (永久删除，仅保留 purge record). Trash and Merged retain Item rows; Purged does not. These are lifecycle states, separate from the user-editable bibliographic `item_status`. See [ADR 0030](adr/0030-item-lifecycle-merge-and-purge.md) for merge, purge, snapshot remapping, tags and GC.

**Bibliographic Search / 元数据筛选**: title/creator/identifier matching plus structured Item filters through `LibraryItemQueryService.SearchRowsAsync`. Structured keys include exact `collection_id` and exact case-sensitive `tag` filters that intersect with the other filters by AND. Results exclude trash/merged Items and use the library's columns and persisted grid preferences.

**Full-text Search / 全文搜索**: SearchUnit/FTS search grouped by Item, with child snippets and navigation through versioned evidence URIs. Snippets normalize line breaks, emphasize matches and truncate around matches with CJK display width considered. Index readiness remains a derived capability, not an Item/Document/FileAsset status.

**Advanced Search / 高级搜索**: one fixed keyword row plus removable filter rows combined with AND. Structured keys align with MCP Item filters (`item_type`, `item_status`, `primary_document_ocr_index_status`, `citable`), with title/creator/identifier contains filters. Both search modes apply these rows; full-text search pushes them down as `SearchRequest.ItemFilters`. The MCP texts scope also accepts `item_id`; this does not change evidence identity or add semantic search. Sidebar tag filtering is a separate library interaction.

Switching modes keeps input and filter rows. Enter and the search button use the same command; both modes recognize `patchouli://` navigation. Empty full-text input prompts for terms; bibliographic search accepts filters alone, otherwise prompts for terms or filters. Opening the advanced-filter form does not itself run a search. Copy/export evidence and Markdown, explicit UI index rebuild, and stale/partial/unavailable indicators remain available.

## Desktop View And Dialog Vocabulary

- **Library / 书库**: the ProDataGrid Item list, tag and collection sidebars and selected-Item inspector. The collection section lists one-level playlists sorted by name, filters the grid to a selected collection, and accepts Item drag/drop, rename, and dissolve actions. Source fields are type-aware; column visibility, width, order and sorting use persisted UI preferences. Search grids follow the library column settings.
- **Trash / 回收站**: a library section with restore/permanent-purge actions; tag navigation and content-edit/OCR entry points are hidden.
- **PDF Workspace / PDF 工作台**: page navigation, raster, Box Tree, text preview and page revision history. Document commit history is also available from the Item editor's file-management area. History restoration creates a new commit, never rolls HEAD backward.
- **ConfirmDialog**: shared confirmation window; danger mode is used for destructive local-file/tag operations.
- **ItemMergePreviewDialog**: explicit target and field-conflict selection before merging Items.
- **PurgeConfirmDialog**: blocking permanent-delete confirmation with expandable dependency details.

These names identify existing UI surfaces, not new persisted domain entities. More elaborate annotation storage and Markdown preview component selection remain PRD V3-T2 work.
