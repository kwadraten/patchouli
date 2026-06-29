# 文献管理程序 PRD v1.1

## 1. Problem Statement

研究者需要一个个人文献管理器，用来管理题录、PDF/扫描本、OCR/HTR 文本、版面结构、全文检索结果和可复现证据引用。现有工具通常在以下方面不足：

- 文件同步、数据库同步和本地运行库容易混在一起，网盘同步可能损坏运行数据库。
- PDF 路径变化后，题录、OCR、索引和原文定位之间容易断裂。
- OCR/HTR 管线难以针对多语言、古籍、手稿、竖排、多栏、边注等材料做细粒度配置。
- OCR 结果很难持续修正，并且修正后的文本、bbox、layout、索引和外部引用缺少统一 revision 机制。
- 搜索结果往往只返回文本片段，缺少页级、bbox、revision 和来源证据。
- 外部 agent 需要通过 MCP 检索和读取证据，但不应该获得写权限、本机路径、密钥或执行 OCR 的能力。

目标是开发一个桌面优先、个人使用、可同步、可修正、可检索、可被外部工具读取证据的文献管理程序。

## 2. Solution

构建一个类似 Zotero / Calibre / DEVONthink 的个人文献管理器，并增加可编程增强层：

- 题录管理：以 item/work 为学术引用身份，支持基础元数据、可扩展 identifiers、标签、collection 和自定义字段。
- 文件管理：PDF/图像不入库，由用户自己的目录和云盘管理；程序通过 hash、known locations 和搜索目录重新定位。
- 同步：运行库与发布快照分离；发布快照由 SQLite shards + manifest 组成，可通过 Google Drive、OneDrive、Syncthing、NAS 等同步。
- OCR/HTR：用户手动选择 OCR Preset，可按 document/page/bbox 运行，支持云端或本地模型。
- Layout / bbox：OCR 输出进入可重组 layout tree；用户可以持续修正文字、bbox、reading order 和节点结构。
- 检索：全文检索以 SQLite FTS5 为第一版 provider；search units 持久化为可重建派生表，本地 FTS index 可重建。
- Evidence：搜索和 MCP 返回稳定 evidence_ref，支持 pinned/current/compare 解析和长期可复制的 evref 字符串。
- MCP：第一版只提供检索与证据读取，不提供写入、OCR 触发、本机路径、provider 密钥或原文打开。

## 3. Goals

- 建立可靠的个人书库数据模型：library、item、file_asset、document_instance、page、layout_node、search_unit、revision。
- 支持数据库快照通过常见同步服务同步，不依赖程序自带文件同步。
- 支持 PDF/图像文件路径变化后的快速定位与深度修复扫描。
- 支持多语言 OCR/HTR Preset、preset version、retry run、候选结果和局部采用。
- 支持用户持续修正 OCR 文本、bbox 和 layout tree。
- 支持页级全文检索、query rewriting、Search Profile 和 carrying stable evidence references 的 search results。
- 提供 text-only MCP，面向外部 agent 返回可验证文本证据。
- 第一版优先保证证据一致性、revision 可追踪、同步安全和大库可扩展。

## 4. Non-Goals and V1 Exclusions

以下能力不属于第一版范围。部分能力可以作为 v2/v3/backlog 继续讨论。

| Category | V1 Exclusion | Later Track |
|---|---|---|
| Collaboration | 团队功能、多人权限、审计日志、机构级后台管理 | v3 |
| File sync | 程序自带 PDF/图像文件同步、托管文件目录 | backlog |
| Search fallback | search index unavailable 时的 SQL LIKE / linear scan fallback | backlog |
| OCR automation | 自动推荐 OCR Preset、自动云成本确认策略 | backlog |
| Citation | CSL citation rendering、bibliography export、Copy Page Citation | v2 |
| Layout | 独立 reading-order view、多 parent layout graph、bbox-level candidate adoption | v2/v3 |
| Table model | 完整表格语义、公式、复杂样式、嵌套表格模型 | v2 |
| Query ranking | query rewrite 权重排序、语义混合排序 | v2 |
| MCP actions | MCP 写入、OCR 触发、bbox 编辑、metadata 更新、删除操作 | not planned for v1 |
| MCP media/path | MCP 返回图片、缓存路径、本机路径、file URL | not planned |
| Encryption | 库级加密、master password、per-device credential unwrap | v2+ |
| Model provenance | 完整模型 fingerprint / reproducibility bundle | backlog |

## 5. User Stories

1. As a researcher, I want to manage my personal literature library, so that I can organize books, papers, scans, and metadata in one place.
2. As a researcher, I want the database to sync through Google Drive or similar services, so that I can use the same library across devices.
3. As a researcher, I want PDFs to remain in my own folder structure, so that the app does not take over my file organization.
4. As a researcher, I want the app to find moved PDFs by hash and search roots, so that metadata and OCR remain useful after file moves.
5. As a researcher, I want missing PDFs not to delete or hide metadata, OCR, and search results, so that my library remains usable offline.
6. As a researcher, I want item metadata with extensible identifiers, so that I can store DOI, ISBN, JPNO, NDLBibID, call numbers, and custom catalog IDs.
7. As a researcher, I want multiple document instances under one item, so that scans, OCR PDFs, partial files, and supplements can belong to the same cited work.
8. As a researcher, I want different editions, translations, volumes, and preprints to be separate items when citation identity differs, so that references stay accurate.
9. As a researcher, I want manually selected OCR/HTR presets, so that I can choose the right model for manuscripts, classical Japanese texts, or modern PDFs.
10. As a researcher, I want risky OCR results to remain candidates until I adopt them, so that low-confidence runs do not pollute search and evidence.
11. As a researcher, I want successful pages from partially failed OCR runs to be preserved, so that a few bad pages do not discard a large run.
12. As a researcher, I want to correct OCR text, layout, bbox, and reading order, so that search and citations point to better evidence over time.
13. As a researcher, I want page-level search results with matched text and evidence refs, so that I can verify claims in source context.
14. As a researcher, I want search to support historical spelling, variant characters, OCR confusions, and dictionaries, so that multilingual or historical material is findable.
15. As a researcher, I want stale or partial index status surfaced, so that I know when search results may be incomplete.
16. As an external agent, I want MCP search_library to return evidence refs and matched units, so that I can cite evidence.
17. As an external agent, I want get_search_result_context to return nearby units with their own evidence refs, so that I can cite context independently.
18. As an external agent, I want get_page_text to return plain text by default, so that I can request page context cheaply.
19. As an external agent, I want get_page_blocks to return structured text and bbox only when requested, so that I can validate layout when needed.
20. As an external agent, I want MCP to be text-only, so that it does not expose local images, file paths, or caches.
21. As an external agent, I want evidence refs to be pinned by default, so that citations do not drift after OCR corrections.
22. As a researcher, I want to copy Evidence Markdown from search results and blocks, so that I can paste reproducible citations into notes.
23. As a researcher, I want normal item-level citation generation later, so that bibliographies and CSL styles can be added after the core evidence system is stable.

## 6. Functional Requirements

### 6.1 Product Scope

- The app is a personal desktop literature manager with programmable enhancements.
- Team workflows, shared permissions, audit logs, and institutional administration are out of scope.
- Desktop app is primary; .NET ecosystem is preferred, with F# preferred where practical.
- UI stack: native Avalonia 12 for now.

### 6.2 Library Identity and Multiplicity

- A library is the top-level durable boundary for metadata, document instances, OCR/layout/search artifacts, evidence refs, snapshots, and MCP resolution.
- v1 supports one active library open at a time in the desktop app.
- v1 data model must still support multiple libraries on disk, each with a distinct `library_id`.
- `library_id` is generated once at library creation and must be stable for the lifetime of that library.
- `library_id` is not derived from path, device name, sync root, or user account.
- Library display name can be renamed without changing `library_id`.
- A library can be moved to another folder or sync service without changing `library_id`.
- v1 does not support automatic library merge or split.
- Cross-library evidence resolution is not allowed in v1. If an evidence_ref belongs to another library, resolution returns `library_mismatch`.
- Snapshot is per-library. A snapshot manifest cannot contain objects from multiple libraries.
- `device_id` identifies a writer device inside one library; it is not part of citation identity.

### 6.3 Library Database and Snapshot Sync

- The database is the only truth source for metadata, OCR/HTR, layout, bbox, revisions, dirty queue, OCR runs, search unit metadata, and optional vectors.
- PDF/image originals and large binary caches are not stored in the database.
- Runtime database lives outside sync folders and may use WAL.
- Published snapshot database is checkpointed into sync roots as SQLite shards plus manifests.
- Shard target size is 512-768 MB, with hard maximum below 1 GB.
- Shard sizing rationale:
  - keep individual sync files below common cloud-client stress thresholds;
  - reduce re-upload cost when active data changes;
  - keep validation/hash and repair operations bounded;
  - avoid large WAL/checkpoint stalls during snapshot publish.
- Old large data shards should be mostly immutable; changes are written as new revision/delta data in active shards.
- Snapshot identity must include library_id, device_id, snapshot_id, parent_snapshot_id, schema_version, shard list, shard hash, and logical generation.
- Snapshot publish is atomic from the app perspective: write candidate manifest, verify shard hashes, then update current pointer.
- Snapshot import never replaces the active runtime DB in place. It imports into a staging area and applies after validation.

### 6.4 Snapshot Conflict Resolution

- First version sync uses single-writer-by-convention, enforced by detection and warnings rather than hard distributed locking.
- Each device writes `device_id`, last local generation, parent_snapshot_id, and publish timestamp.
- On publish, if the sync root current snapshot is no longer the local parent, the app must not overwrite it.
- That condition creates a snapshot branch.
- v1 does not perform automatic object-level merge.
- v1 branch actions:
  - open as separate branch for inspection;
  - import selected item/document_instance into current branch through explicit user action;
  - discard local branch;
  - keep branch as separate library copy.
- v1 must not silently last-writer-win across branches.

Object strategy in v1:

| Object Type | Same-Branch Update | Cross-Branch Conflict |
|---|---|---|
| library metadata | latest committed change in branch | manual pick |
| item metadata | revisioned update | manual import/pick |
| file_asset location | local known_locations can be appended | manual pick if identity conflict |
| document_instance | revisioned update | manual import/pick |
| page metadata | derived from document_instance/file | forbidden automatic merge |
| OCR run | append-only within branch | import only with owning document_instance |
| OCR/layout revision | append-only/current pointer within branch | manual pick; no automatic pointer merge |
| search_unit | derived/persisted within branch | rebuilt/imported with owning layout revision |
| evidence_ref | resolves only within owning library/branch context | returns branch candidates when ambiguous |
| provider credential | mutable credential store, latest in selected branch | manual pick/re-enter |
| cache/index | local rebuildable | never merged |

### 6.5 Credential Sync and Trust Boundary

- Provider credentials are user-owned cloud/local provider keys and tokens used by OCR/HTR adapters.
- By product decision, v1 may sync provider credentials across trusted user devices.
- Credentials are stored plaintext from the app's perspective; v1 does not implement library-level encryption, master password, or per-device unwrap.
- Credentials must not be written into immutable historical content-addressed data shards.
- Credentials live in a mutable credential store/shard referenced by the latest manifest and marked `sensitive_mutable`.
- Credential changes rewrite/rotate the mutable credential store instead of appending secrets to historical data shards.
- Snapshot publish must keep ordinary immutable data shards and `sensitive_mutable` credential shards logically separate.
- Emergency credential purge/revoke is a v1 requirement:
  - delete provider credential rows from active runtime DB;
  - rewrite the mutable credential store without the secret;
  - update manifest references;
  - mark affected OCR presets/providers as `credential_missing`.
- The app can remove credential shards and manifest references under its managed sync root, but cannot erase cloud provider historical versions, external backups, or files copied by the user.
- User is responsible for trusting devices, sync service, and sync folder access control.
- MCP cannot read provider secrets or provider config details.
- MCP only reports document evidence capability, not provider status.

### 6.6 File Assets and File Resolution

- The app does not own a managed files directory.
- Users configure file_search_roots and database_sync_roots separately.
- PDF file absence does not imply literature absence.
- File states: available, moved_candidate, missing, offline_root, conflict, changed.
- File identity uses quick_hash and optional full BLAKE3; SHA-256 is not a core field.
- Import stores path, name, size, mtime, quick hash, page count, and pdf trailer id if available.
- Full BLAKE3 is computed later during idle background work.
- Relocation first checks known_locations, then size + quick_hash, then full BLAKE3 if necessary.
- Scanning modes:
  - Light scan at startup.
  - Incremental watcher-driven scan.
  - User-triggered deep repair scan.
- A unified File Resolution API must be used for opening originals, rendering pages, running OCR, and verifying hash.
- `resolve_file(file_asset_id, purpose)` returns status, resolved_path, candidates, confidence, and required_action to trusted internal callers.
- MCP never receives resolved_path.
- conflict and changed do not auto-open; they require user confirmation.
- If file_asset status becomes `changed`, dependent page rendering/OCR/bbox evidence remains available only as previously committed evidence and must be marked `source_changed`.
- `source_changed` does not invalidate pinned evidence automatically, but current-mode consumers must receive a warning that bbox/page basis may no longer match the current source file.

### 6.7 Item Metadata and Bibliographic Model

- Core model has three layers: item/work, file_asset, document_instance.
- item/work is the citation identity.
- file_asset is file identity, location, and verification.
- document_instance is a concrete PDF/scan manifestation under an item and owns page/OCR/layout/search/vector artifacts.
- Different citation identities must be different items: editions, translations, volumes when citation differs, preprint vs official if metadata differs.
- Same citation identity can have multiple document instances: different scans, OCR PDFs, split PDFs, missing-page supplements.
- Default search targets primary document_instance only; advanced search can include alternates, partials, deprecated instances, or a specific document instance.
- First version item metadata fields:
  - item_id, item_type, title, subtitle, creators, date, publication_title, publisher, place, volume, issue, pages, language, abstract, tags, collections, custom_fields.
- identifiers are extensible with scheme/value/note and built-in common schemes DOI, ISBN, ISSN, URL, archive_id, call_number, jpno, ndlbibid.
- CSL rendering, bibliography export, citekey workflows, authority control, creator disambiguation, multilingual titles, and detailed edition/history fields are important TODOs.

### 6.8 OCR/HTR Presets, Models, and Provider Configuration

- "OCR Preset" is the user-facing name for a reusable OCR/HTR configuration.
- `ocr_preset` replaces the earlier "OCR Profile" term to avoid confusion with Search Profile.
- Users choose OCR/HTR Presets manually; the system does not auto-recommend presets.
- Preset scope priority:
  - bbox override
  - page override
  - document default
  - collection/tag batch assignment
- Supported tasks:
  - RunPresetOnDocument
  - RunPresetOnPages
  - RunPresetOnRegion
- Preset versions are immutable for OCR provenance.
- preset holds name, current_version_id, and archive state.
- preset_version holds engine_id, model_id, model_path, parameters, apply_on_success, and created_at.
- Changing engine/model/parameters/apply_on_success creates a new preset_version.
- Changing name/description/tags can update preset in place.
- ocr_run records preset_id, preset_version_id, engine_id, model_id, parameters_snapshot, source_revision_id, and output_revision_id.
- model identity first version is model_id + model_path only.
- model_path can be a local filesystem path or URL/endpoint/model page URL.
- Stronger model hashing/fingerprinting is a TODO, not a v1 requirement.
- Local model path missing/inaccessible blocks OCR and allows user rebind; rebinding creates a new preset_version.
- Cloud provider auth/model/endpoint failures block OCR and do not auto-fallback.

### 6.9 OCR/HTR Run Lifecycle

- OCR results are saved by page.
- ocr_run states: pending, running, completed, completed_with_errors, failed, cancelled.
- ocr_page_result states: pending, processing, succeeded, failed, skipped, cancelled.
- Running OCR writes staging result; staging is previewable but does not enter current layout, full-text index, or MCP.
- Cancelled OCR rolls back the whole run and deletes staging/temporary result for that run.
- apply_on_success=true promotes staging to current OCR/layout revisions, marks related search_units dirty, and schedules local index rebuild.
- apply_on_success=false promotes as candidate result; it is not searchable through default MCP until user adopts.
- Candidate adoption supports whole run or selected pages; first version does not support bbox-level candidate adoption.
- bbox coordinate conversion failure rejects the whole page: no text, layout, search_unit, index entry, or MCP exposure.
- Partial page failures produce completed_with_errors; successful pages are retained.
- Retry after source repair creates a new retry run with retry_of_run_id and retry_scope_pages; original run is not rewritten.
- Retry run adoption follows its own recorded apply_on_success.
- Automatic retry applies only to transient failures: network_timeout, temporary_provider_error, rate_limited, retryable quota_exceeded, worker_crashed.
- Manual repair required for auth_failed, model_not_found, bad endpoint config, model_path missing/inaccessible, source_file missing/changed/conflict, bbox_coordinate_transform_failed, unsupported_file, invalid_page_box.

OCR adoption transaction boundary:

- Adoption is serialized per document_instance.
- A document_instance can have multiple OCR runs in progress, but only one adoption transaction may update current OCR/layout pointers at a time.
- The transaction must commit current OCR/layout revision pointers, search_unit regeneration or dirty marking, and evidence successor links together.
- Local FTS rebuild happens after commit and is allowed to lag; search_index_status must become stale/partial until rebuild catches up.
- search_library must only return search_units associated with committed layout/text revisions.
- MCP read_mode=current must read from one committed revision set and must not mix old text with new bbox/layout in one response.

### 6.10 OCR/HTR Queue

- Queue supports global, local, cloud, per-provider, per-engine, and per-preset concurrency limits.
- Default recommendations:
  - global_max_concurrent = min(4, max(2, logical_cpu_count / 4)).
  - local_max_concurrent = 1 unless the local engine declares safe parallelism.
  - cloud_max_concurrent = 2.
  - per_provider_max_concurrent = 1 or provider-specific quota.
- Queue supports priority + aging.
- Priority order:
  - interactive_current_page
  - interactive_selected_pages
  - user_started_document
  - background_retry
  - batch_collection
  - maintenance
- Queue supports pause scopes: global, local, cloud, provider, task.
- First version does not support preset-level pause.
- pause affects not-started tasks; cancel interrupts running tasks and follows OCR rollback rules.
- Resume recalculates effective priority using priority + aging.
- Cloud OCR/HTR has no cost/page/call estimate confirmation and no extra privacy/cost warning. UI can show provider type/name only.

### 6.11 OCR Revision, Reset, Hide, Tombstone, Purge

- Original OCR/HTR outputs are immutable by default.
- User corrections are saved as revisions/deltas.
- current_revision pointer controls current view.
- Reset levels:
  - Unset Current OCR: remove current pointer; keep history.
  - Hide OCR Run: hidden from current view/index/MCP; keep data.
  - Tombstone OCR Data: hide from normal UI/index/MCP; keep tombstone for sync/reference handling.
  - Purge OCR Data: physically delete OCR text/layout/vector as advanced maintenance.

Cross-device semantics:

- Tombstone is a normal synced state and propagates through snapshots.
- A tombstone hides the target from current UI/search/MCP on importing devices, while preserving enough identity to resolve old evidence refs as `tombstoned`.
- Purge removes payload data where possible and leaves a minimal purge marker for evidence resolution.
- Purge must not require rewriting immutable historical shards in v1. If historical shards still contain payload, the app must treat them as unreachable from current manifests after purge.
- Full historical compaction is a TODO.
- If device B still has a reference to purged data from an older branch, resolution in the selected current branch returns `purged` or branch candidates, not silent resurrection.

### 6.12 Layout Tree, Text, Tables, and BBox

- First version uses a mutable tree hierarchy, not a separate reading-order view and not a multi-parent graph.
- layout_node supports node_id, document_instance_id, page_id, parent_node_id, node_type, bbox, own_text, text_policy, reading_order, source, revision_id, confidence, ignored.
- Supported operations: merge, split, move under new parent, change type, change reading_order, adjust bbox, create parent node from selection, detach, mark ignored/non-text.
- Node types are semi-open: built-in standard types plus user-defined types mapped to base types.
- Unknown custom node types imported from another device must be preserved, displayed as their base type, and not discarded.
- text_policy:
  - own
  - aggregate_children
  - none
- index_policy is type default + node override:
  - container
  - self
  - ignore
  - ignore_subtree
- Ordinary bbox overlap is forbidden in current layout tree; ruby, warichu, annotations, marginalia, seals/stamps, and configured custom types may overlap.
- OCR import/staging can temporarily contain overlap conflicts, but non-allowed overlaps must be resolved or skipped before adoption.
- Local OCR on a selected bbox inserts/replaces nodes inside the single page current tree.
- replace mode only replaces explicitly selected nodes in first version.
- Canonical bbox uses normalized_page coordinates x/y/width/height in 0..1.
- normalized_page is viewport-first: relative to the actual visible/rendered page box used by the app's page renderer for that committed page revision.
- Fallback basis order is crop_box, media_box, then image_bounds.
- Page coordinate basis, basis dimensions, page rotation, and renderer basis version must be recorded per page revision.
- Canonical bbox is normalized to upright_view; source_bbox can retain raw engine coordinates.
- If source file changes, existing bbox remains valid only relative to the recorded page basis, not automatically relative to the new source file.
- Evidence and MCP responses must surface `source_changed` or `bbox_basis_stale` when current file verification no longer matches the recorded page basis.
- MCP returns bbox only and does not generate natural-language location descriptions.
- Tables are represented in layout tree using table/table_row/table_cell; no independent table model in first version.
- table_cell may store row_index, col_index, row_span, col_span, is_header.
- plain text output defaults to Markdown table when safe.
- irregular tables degrade to a `[Table]` block or structured blocks on request; the app must not invent fake regular Markdown tables.

### 6.13 Page Text and Structured Blocks

- get_page_text defaults to layout-derived plain text.
- Structure, bbox, OCR boundaries, and evidence refs are requested explicitly via structured format or get_page_blocks.
- get_page_text/get_page_blocks support read_mode current, pinned, compare.
- Page text plain rules:
  - append page search_units by reading_order.
  - single newline for continuous text.
  - blank line between paragraphs/blocks/columns.
  - exclude headers, footers, page numbers, ignored nodes by default.
  - footnotes go after main text with `[Footnotes]`.
  - marginalia/annotations are excluded unless include_annotations=true.
  - table output is Markdown table when safe.

### 6.14 Search Units, Index, and Querying

- search_unit is a persisted derived table and included in snapshot; local FTS index is a rebuildable local cache and not synced.
- search_unit fields include unit_id, document_instance_id, page_id, root_node_id, text_revision_id, bbox_revision_id, layout_revision_id, resolved_text, bbox_union, node_type, reading_order, status.
- unit_id remains stable for text edits, bbox edits, node_type edits, reading_order edits, and small moves.
- split/merge/replace/delete-recreate generates new unit_id and links with supersedes/superseded_by.
- Full-text index is generated from layout tree/search_units.
- SQLite FTS5 is first provider. SearchProvider abstraction allows later Lucene.NET/Tantivy.
- CJK first version uses character n-gram; Latin text uses word tokens; mixed text uses mixed analyzer.
- Canonical text remains original. Index text only applies minimal technical normalization: Unicode normalization, case folding, necessary whitespace handling, and Latin alphanumeric full/half width handling.
- No default simplification/traditional conversion, old/new character conversion, variant replacement, historical kana normalization, or semantic synonym replacement in index text.
- Query rewriting handles recall: variants, old/new forms, simplified/traditional, historical kana, OCR/HTR confusions, synonyms, regex rewrites, user dictionaries.
- Search Profiles combine rewrite rules and command aliases.
- Search Profile priority: explicit alias, current search box selection, global last-used, system default.
- Rewrite plan is executed by default and viewable in results; advanced setting can preview before execution.
- First version rewrite hits have equal weight.
- Search results are grouped by page and matched units are deduplicated within page.
- search_library returns cursor pagination with default page_size 20 and max 100.
- It does not guarantee exact total_result_count; may return estimated_total.
- estimated_total is an approximate FTS/provider estimate and must be labeled as estimated.
- Each SearchPageResult returns default 5 matched_units and max 20; matched_units_has_more indicates truncation.
- get_search_result_context returns default 2 preceding and 2 following sibling search_units; max 10 each side; no cross-page context.
- get_search_result_context does not include whole page text; use get_page_text.
- All context units include unit_id, evidence_ref, text, bbox, is_match, reading_order.
- Search index rebuild is automatic and local/partial by default; manual rebuild selected document/collection/whole library is available for maintenance.
- First sync/import schedules eager background index rebuild, but it must not block library open or metadata browsing.
- Dirty scopes are rebuilt by document_instance priority:
  - current/open documents;
  - recently modified documents;
  - user-pinned collections;
  - remaining library.
- search_library returns index_status current/stale/partial/unavailable.
- stale/partial returns available results with affected_scopes_summary.
- partial status must include at least pending_document_count and pending_unit_count; progress_percent should be returned when total scope is known.
- unavailable returns empty results and reason; no SQL LIKE or linear scan fallback.

### 6.15 Evidence References

- search results and MCP return stable evidence_ref plus optional short-lived result_id for UI sessions.
- EvidenceReference includes library_id, document_instance_id, page_id, unit_id, text_revision_id, bbox_revision_id, layout_revision_id, optional snapshot_id.
- Default evidence resolution mode is pinned.
- current follows unit_id to current/latest revision.
- compare returns pinned and current with change flags.
- evidence_ref_id is a long-term public parseable string: `evref:v1:<payload>`.
- Payload should use compact binary or URL-safe base64 encoding. Exact encoding is implementation-defined but versioned.
- v1 accepts long evref strings for durability; short local aliases are a TODO.
- evidence_ref_id must not contain local path, provider secret, or unsynced local state.
- Old evidence resolution returns explicit status:
  - found_pinned
  - superseded
  - tombstoned
  - purged
  - not_found
  - library_mismatch
- superseded returns successor_evidence_refs but does not auto-adopt.
- current/compare follows successor chain to final current, with max depth and chain summary.
- successor branch returns multiple_current_candidates and does not auto-select newest.

Example Evidence Markdown:

```markdown
> 漢字文化圏における書誌記述は...

Source: 『近代東亞書誌研究』, p. 42
Evidence: evref:v1:full:Ab3Z4Q9r7K2mX8pV5nE1sT0uY6cD4fG2hJ9kL3mN8pQ
```

The example payload is illustrative; real payload length may be longer depending on ID encoding.

### 6.16 MCP API

- First version MCP is read-only and text-only.
- MCP tools:
  - search_library
  - get_item_metadata
  - get_document_status
  - get_page_text
  - get_page_blocks
  - get_search_result_context
- MCP does not provide:
  - run_ocr, edit_ocr, edit_bbox, reset_ocr, purge_ocr, update_metadata, delete_anything.
  - resolved_path, local filesystem path, open_original, file:// URL.
  - provider secrets or provider configuration details.
  - cache images or image paths.
- get_document_status returns has_ocr_text, has_current_layout, is_search_indexed, source_file_status.
- source_file_status values exposed through MCP are limited to available, missing, offline_root, changed, conflict, unknown.
- source_file_status intentionally exposes evidence usability, not local paths or root names.
- has_ocr_text means current document has readable OCR/HTR text; it does not mean OCR can be run.
- MCP never triggers OCR or index rebuild.
- MCP search uses a two-step pattern: search_library for results, then get_search_result_context for evidence context.

### 6.17 UI Evidence Copy

- UI supports Copy Evidence Reference and Copy Evidence Markdown.
- First version does not support Copy Page Citation or Copy Page Evidence Citation.
- Evidence Markdown includes quoted pinned text, minimal Source, and Evidence evref.
- Source is title + page_label/page_index.
- Evidence Markdown default text must match the pinned evidence_ref.
- Copy Current Evidence Markdown is an explicit operation if current revision is desired.

### 6.18 Caches

- page_renders, thumbnails, OCR intermediate images, and overlays are local rebuildable caches.
- They do not enter database shards, published snapshots, or sync.
- DB can store cache metadata only.
- If source file is missing/offline, UI may show old page render cache as stale_possible preview.
- MCP never returns cached images or image paths.

### 6.19 Vectors

- Embeddings are optional enhancement.
- Full-text search is core.
- Vectors are not generated for the whole library by default.
- Optional generation scopes include collection, tag, selected documents, language, or OCR preset output.
- text_revision changes mark embeddings stale.

## 7. Implementation Decisions

### 7.1 Module Shape

- LiteratureApp.UI: native Avalonia 12 desktop UI.
- LiteratureApp.Core: domain models, library identity, item metadata, document instances, OCR revisions, layout tree, evidence refs.
- LiteratureApp.Infrastructure: SQLite shards, Dapper/manual SQL, file watcher, snapshot publish/import, file resolution, provider config, credential store.
- LiteratureApp.Search: search unit generation, SQLite FTS5 provider, query rewriting, Search Profiles, dirty index rebuild.
- LiteratureApp.Ocr: OCR/HTR presets, preset versions, adapters, model/path validation, queue, retry, staging/candidate adoption.
- LiteratureApp.Mcp: read-only text-only MCP tools and evidence resolution.

### 7.2 Deep Modules

- LibraryIdentityService: library_id creation, rename-safe identity, branch/library mismatch detection.
- SnapshotPublisher: active DB checkpoint, shard selection, content-addressed manifest generation, current pointer update.
- SnapshotImporter: manifest validation, branch detection, staging import, branch action orchestration.
- CredentialStore: plaintext synced credential store, sensitive_mutable shard handling, emergency purge/revoke.
- FileResolutionService: known locations, root availability, quick/full hash matching, conflict/changed/missing status.
- OcrRunCoordinator: run lifecycle, staging, cancellation rollback, completed_with_errors, retry runs, serialized adoption.
- OcrQueueScheduler: concurrency limits, priority + aging, pause/resume, transient retry.
- LayoutTreeService: tree mutation, bbox overlap validation, text_policy resolution, table modeling.
- PageCoordinateService: per-page coordinate basis, upright normalization, source_changed/bbox_basis_stale warnings.
- SearchUnitBuilder: index_policy traversal, unit identity preservation, supersedes links, plain text generation.
- EvidenceReferenceService: evidence_ref_id encode/decode, pinned/current/compare resolution, successor chain handling.
- SearchService: query rewriting, FTS execution, page aggregation, pagination, index status handling.
- McpReadApi: text-only tool surface, field filtering, no path/secrets/actions.

### 7.3 Storage Technology

- SQLite is primary database.
- Multiple SQLite shards form one logical library.
- Dapper + handwritten SQL are preferred over EF Core.
- Local FTS index is rebuildable and not synced.
- search_unit metadata is persisted and synced.

### 7.4 Technology Rationale

- .NET is preferred because it offers mature desktop packaging, strong SQLite access, stable Windows integration, and practical cross-platform support.
- Native Avalonia 12 is preferred for now because the app is desktop-first and needs native-feeling long-running workflows, file dialogs, keyboard-heavy UI, and local background workers.
- F# is preferred where it improves domain modeling, immutable state transitions, parsers, discriminated unions, and evidence/status handling.
- C# may be used where library ergonomics, UI bindings, interop, or ecosystem support make it lower risk.
- Dapper/manual SQL is preferred because shard layout, migrations, FTS, and append/revision semantics need explicit control.
- Tauri/Rust and Electron/TypeScript remain plausible alternatives, but v1 optimizes for desktop app velocity and .NET ecosystem coherence.

### 7.5 Security and Trust Boundaries

- Provider secrets are stored in synced plaintext credential storage by first-version decision, but not in immutable historical data shards.
- User is responsible for trusting devices, sync service, and sync folder access control.
- MCP cannot expose secrets, provider config, local paths, file URLs, cache paths, or images.
- There is no first-version library-level encryption or master password.

## 8. Sizing Assumptions and Performance Targets

These are v1 design targets, not hard product limits.

| Area | V1 Target |
|---|---|
| Items | 50k items |
| Document instances | 100k document instances |
| Pages | 5M pages |
| Runtime DB logical data | 20GB excluding original files and caches |
| Snapshot shard size | target 512-768MB, hard maximum below 1GB |
| Library open | metadata usable within 5s for warm local DB |
| Search current index | p95 under 1s for common queries returning first page |
| Search partial index | return available indexed results under same pagination rules |
| get_page_text | p95 under 300ms for cached committed layout text |
| get_page_blocks | p95 under 800ms for cached committed structured blocks |
| Snapshot publish small delta | under 30s for ordinary metadata/OCR delta |
| Snapshot import validation | streaming hash validation, progress visible for large libraries |
| OCR queue UI responsiveness | queue operations reflected in UI within 500ms |

## 9. Testing Decisions

- Tests should verify external behavior and durable invariants, not implementation details.
- Snapshot tests should cover shard reuse, manifest correctness, current pointer updates, branch conflict preservation, and sensitive_mutable credential shard separation.
- Library identity tests should cover creation, rename, move, cross-library evidence mismatch, and per-library snapshot boundaries.
- File resolution tests should cover known path available, moved candidate, missing file, offline root, changed file, conflict, quick hash match, full BLAKE3 confirmation, and source_changed propagation.
- Metadata tests should cover item/document_instance/file_asset relationships and extensible identifiers.
- Credential tests should cover sync inclusion, no immutable shard inclusion, emergency purge/revoke, and MCP non-exposure.
- OCR lifecycle tests should cover staging preview isolation, cancellation rollback, apply_on_success true/false, candidate adoption by page, completed_with_errors, retry run provenance, hard page rejection on bbox transform failure, and serialized adoption.
- Queue tests should cover concurrency limits, priority ordering, aging, pause/resume scopes, cancel behavior, transient retry, and manual-fix-required failures.
- Layout tests should cover text_policy resolution, index_policy traversal, bbox overlap constraints, tree mutation operations, custom type preservation, and table Markdown degradation.
- Page coordinate tests should cover crop/media/image basis fallback, upright_view normalization, source_changed, and bbox_basis_stale warnings.
- Search unit tests should cover stable unit identity through edits and new unit creation through split/merge/replace.
- Search tests should cover query rewriting, Search Profile selection, page aggregation, matched unit truncation, pagination, stale/partial/unavailable index status, progress fields, and no linear fallback.
- Evidence tests should cover evref encode/decode, pinned/current/compare, superseded successors, tombstone, purge, not_found, library_mismatch, branch candidates, and max chain depth.
- MCP contract tests should verify no write tools, no OCR triggers, no provider secrets, no provider config, no local paths, no file URLs, no images, and correct text-only responses.
- UI-focused tests should cover Evidence Markdown generation, pinned/current copy behavior, index rebuild status, branch warnings, credential_missing state, and no Copy Page Citation in first version.

## 10. Acceptance Criteria

| AC | Acceptance Criterion | Verification |
|---|---|---|
| AC1 | A user can create/import a personal library with stable library_id and rename/move it without changing identity. | Library identity tests |
| AC2 | A user can add item metadata, attach document instances, and locate files through search roots. | Metadata + file resolution tests |
| AC3 | Database sync can publish content-addressed SQLite data shard snapshots without syncing WAL/SHM runtime files. | Snapshot publish tests |
| AC4 | Provider credentials can sync through the mutable credential store and are not written into immutable historical data shards. | Credential shard tests |
| AC5 | Multi-writer snapshot divergence creates branches and never silently last-writer-wins. | Branch conflict tests |
| AC6 | Missing or changed source files do not remove metadata, OCR, layout, search units, or evidence refs. | File state + evidence tests |
| AC7 | Changed source files surface source_changed/bbox_basis_stale warnings where evidence depends on old page basis. | Page coordinate tests |
| AC8 | OCR/HTR runs can be staged, cancelled, completed, failed by page, retried, and adopted according to preset version settings. | OCR lifecycle tests |
| AC9 | OCR adoption commits current revisions, dirty search_units, and successor links atomically per document_instance. | Serialized adoption tests |
| AC10 | Bad bbox coordinate conversion prevents that page from entering OCR/layout/search/MCP. | OCR hard-failure tests |
| AC11 | Layout nodes can be corrected and produce search units without parent/child duplicate indexing. | Layout + search_unit tests |
| AC12 | Search returns page-level results with evidence refs, matched unit truncation, cursor pagination, progress-aware index status, and no linear fallback when unavailable. | Search contract tests |
| AC13 | MCP can search, read metadata/status/page text/page blocks/context, and cannot mutate library state or expose local paths/secrets/images. | MCP contract tests |
| AC14 | Evidence refs are long-term parseable, pinned by default, and resolve old/superseded/tombstoned/purged references explicitly. | Evidence tests |
| AC15 | UI can copy Evidence Reference and Evidence Markdown with pinned text and evref, but does not expose Copy Page Citation in v1. | UI tests |

## 11. Important TODOs

### v2 Candidates

- Item-level citation generation.
- CSL style support.
- Bibliography export.
- Citekey / Better BibTeX-like workflow.
- Full CSL field mapping.
- Multilingual titles and transliterated titles.
- Detailed edition/history fields.
- Short local evidence aliases.
- Region-level OCR merge/adopt.
- Derived reading-order view.
- Query rewrite weighting.
- Lucene.NET / Tantivy SearchProvider evaluation.

### v3 Candidates

- Team/shared library workflows.
- Authority control and creator disambiguation.
- More sophisticated multi-writer sync merge.
- Multi-parent layout graph.
- Full table semantic model.
- Vector / hybrid / semantic search as a first-class workflow.
- Library-level encryption or per-device credential unwrap.

### Backlog / Research

- MeCab / Sudachi / Jieba analyzers.
- BBox overlap candidate replacement.
- OCR data purge compaction.
- Stronger model fingerprinting if reproducibility requirements increase.
- Program-managed file sync.
- Controlled small-scope substring fallback before FTS rebuild, if first-sync UX proves too rigid.

## 12. Versioning Philosophy

- v1 should be conservative: preserve evidence, expose ambiguity, and reject unsafe automation.
- v1.1 PRD clarifies the development contract; it should not silently expand product scope.
- v2 should focus on citation workflows, better search/ranking, and controlled evidence UX improvements.
- v3 may revisit collaboration, automatic merging, stronger encryption, and institutional workflows.
- Features that weaken evidence reproducibility must be opt-in and explicitly labeled.

## 13. Further Notes

- The first version optimizes for personal use, evidence correctness, local-first storage, and explicit provenance.
- The central product bet is that OCR/layout/search/evidence can be treated as a versioned, inspectable knowledge layer over user-owned files.
- The app should prefer conservative failure behavior where evidence would otherwise become ambiguous.
- The MCP surface should remain small, predictable, text-only, and safe for external agent use.
