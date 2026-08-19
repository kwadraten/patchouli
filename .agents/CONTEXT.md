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
MinerU remains the preferred OCR provider, but its JSON is an import format rather than the database schema. Every provider produces a short-lived `OcrDocumentTreeCandidate`; the shared importer validates and stages page-local `DocumentTreeRevision`/`DocumentBox` records. Provider JSON, table-cell records, reading-order integers, and Markdown ASTs are not canonical storage. An irregular table retains only its raw HTML source as diagnostic payload alongside the canonical `[Table]` placeholder.

**Search and evidence**:
SearchUnits are persisted derived text units generated one per non-suppressed leaf DocumentBox in sibling-pointer order. SearchUnit metadata is synced; the local FTS index is a rebuildable local cache. Evidence identity is part of the versioned URI `patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree_revision_id}&box={box_id}`; a URI with `rev` reads that immutable revision, and a URI without `rev` reads HEAD.

**MCP surface**:
The virtual Library filesystem is resolved on demand through bounded runtime-host domain RPCs. One desktop or headless .NET host is the only authority for a Library database, cursors, revisions, projections and writes. The desktop host includes the UI and local MCP HTTP endpoint; `patchouli-cli` is a thin local client of that endpoint and auto-starts the same binary headlessly when no host exists. There is never a direct-SQL CLI path or second domain implementation. Directory paging, traversal and batch limits, command/output limits, and bounded rebuildable compiled-page caches constrain reads.
MCP is **text-only** and the selected production surface is the structured `patchouli.find`, `patchouli.fetch`, `patchouli.put`, and `patchouli.cite` contract from ADR `0024`. MCP never edits bbox, triggers OCR, rebuilds indexes, exposes local paths, returns images, reveals file URLs, or leaks provider secrets/configuration. MCP 无法读取提供程序密钥. **Limited writes** of whole item bibliography projections and CSL styles are deliberate v3 product decisions under ADR `0023`: `put` is an atomic complete-resource replacement with no base-revision precondition, and remains unavailable until that contract is implemented. The Bashkit virtual shell implementation has been removed from `main`; it exists only on the `feature/mcp-ab-benchmark` branch as historical benchmark evidence and is not the production MCP path. Unrelated metadata mutation remains out of scope.


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
A search-time bundle of rewrite rules, aliases, and recall behavior. It is unrelated to OCR Presets.
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
The text-only external surface for library exploration, evidence retrieval, citation rendering, and—when enabled—limited whole-resource writes of item bibliography and CSL styles (ADR `0023`). Production uses `patchouli.find`, `patchouli.fetch`, `patchouli.put`, and `patchouli.cite` under ADR `0024`, served by the one desktop or headless runtime host for the Library. CLI is a local MCP client of that host; remote/local agent clients use the same service. It never exposes local paths, provider secrets, images, file URLs, or OCR/index actions. The Bashkit shell has been removed from `main` (historical evidence remains on the `feature/mcp-ab-benchmark` branch) and is not a production tool. .NET remains the sole domain authority for Library data.
