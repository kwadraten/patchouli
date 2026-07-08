# Literature Library

Patchouli is a personal literature manager that treats user-owned source files as evidence-bearing documents. The domain language centers on stable bibliographic identity, relocatable files, versioned OCR/layout text, searchable units, and text-only evidence references.

## Standing Product Boundaries

These boundaries are not backlog items. They are durable constraints that future PRDs should inherit unless an ADR explicitly replaces them.

**Storage and sync**:
The active runtime SQLite database stays outside sync roots. Sync publishes validated snapshot artifacts rather than syncing WAL/SHM files. Published snapshots use manifests plus SQLite shards; runtime caches, render images, and active working files are not snapshot payloads.

**User-owned source files**:
Original PDFs/images remain in user-managed folders. Patchouli stores FileAsset identity, fingerprints, known locations, pages, OCR/layout/search artifacts, and evidence metadata. A missing or moved source file does not delete Item metadata, OCR history, SearchUnits, or EvidenceRefs.

**Cache boundary**:
Page renders, thumbnails, OCR intermediate images, and overlays are local rebuildable caches. `page_renders` is a local cache namespace only. MCP never returns cached images or image paths.

**OCR adoption**:
OCR output is staging or candidate output until adopted. Only committed current LayoutRevisions feed default search, evidence resolution, and MCP reads. Failed bbox coordinate conversion blocks that page from OCR/layout/search/MCP exposure.

**OCR interchange schema**:
MinerU remains the preferred OCR/layout provider and its content-list-style output is the compatibility baseline for OCR text storage, editing, layout mapping, tables, bbox, SearchUnits, and evidence. Other OCR providers, including multimodal LLM OCR, must normalize their output into a MinerU-compatible intermediate shape before it enters LayoutRevisions, LayoutNodes, SearchUnits, or MCP-visible evidence.

**Search and evidence**:
SearchUnits are persisted derived text units generated from committed layout trees. SearchUnit metadata is synced; the local FTS index is a rebuildable local cache. EvidenceRefs resolve pinned by default, and current/compare modes must surface drift instead of silently changing copied evidence.

**MCP Read API**:
MCP is read-only and text-only. It can search and read evidence context, but it never writes metadata, edits bbox, triggers OCR, rebuilds indexes, exposes local paths, returns images, reveals file URLs, or leaks provider secrets/configuration. MCP 无法读取提供程序密钥.

**Snapshot branches**:
Snapshot divergence creates a Snapshot Branch. Branches are inspected and imported explicitly; v1 does not perform automatic object-level merge or silent last-writer-wins conflict resolution.

**Provider credentials**:
ProviderCredential values are user-owned secrets for OCR/HTR providers. They may be present in trusted-user-device sync only through the mutable sensitive credential path, never in immutable historical content shards, never in MCP, and never in logs.

## Language

**Library**:
The durable boundary for metadata, document instances, OCR/layout/search artifacts, evidence refs, snapshots, credentials, and MCP resolution. A library keeps the same identity when renamed or moved.

**Item**:
The bibliographic identity that a researcher cites, such as a book, article, edition, translation, preprint, or manuscript witness.
User-facing Chinese term: 题录. It has the same product meaning as citation: the citable bibliographic record, not the PDF file itself.
_Avoid_: Book, PDF, file, attachment

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
A concrete manifestation of an Item, such as a scan, OCR PDF, partial file, supplement, or alternate digitization. It owns pages, OCR/layout revisions, search units, and evidence refs.
_Avoid_: File, item, attachment

**Page**:
An ordered page within a DocumentInstance, with coordinate basis metadata used to interpret layout and evidence regions.

**LayoutRevision**:
A versioned layout/text state for a DocumentInstance. Only committed current revisions feed default search and MCP reads.
_Avoid_: OCR text blob

**LayoutNode**:
A node in the page layout tree, such as a paragraph, heading, table, line, or custom block, with text policy, reading order, and optional bbox.
_Avoid_: Search result, page block

**OCR Preset**:
A user-facing reusable OCR/HTR configuration. Presets are selected manually and are distinct from Search Profiles.
_Avoid_: OCR Profile

**OCR Preset Version**:
An immutable version of an OCR Preset used for OCR provenance. Rebinding paths, models, endpoints, or parameters creates a new version.

**OCR Run**:
An attempt to produce OCR/HTR output for a DocumentInstance, page set, or region using a specific OCR Preset Version.

**Staging Result**:
OCR output that is previewable but has not entered current layout, search, evidence, or MCP.
_Avoid_: Current OCR

**Candidate Result**:
OCR output preserved for later user adoption. It is not part of default search or MCP until adopted.

**SearchUnit**:
A persisted derived text unit generated from the layout tree for search, evidence, and page context. SearchUnit metadata is synced; the FTS index is rebuildable local cache.
_Avoid_: FTS row, snippet

**SearchProfile**:
A search-time bundle of rewrite rules, aliases, and recall behavior. It is unrelated to OCR Presets.
_Avoid_: OCR Profile, OCR Preset

**EvidenceRef**:
A long-term parseable text reference to evidence over a SearchUnit and its revision identities.
_Avoid_: Citation, file URL, local path

**Pinned Evidence**:
Evidence resolution that preserves the referenced revision even after later OCR or layout changes.

**Current Evidence**:
Evidence resolution that intentionally follows current document state and may warn when source files or bbox basis have drifted.

**Snapshot**:
A published sync artifact for a Library, represented by a manifest and content-addressed SQLite shards.
_Avoid_: Backup, runtime database

**Snapshot Branch**:
A divergent published snapshot lineage created when multiple writers publish from different parents.
_Avoid_: Last-writer-wins conflict

**ProviderCredential**:
A user-owned token, key, or credential used by OCR/HTR providers. It is never exposed through MCP.
_Avoid_: Provider config, secret in shard

**MCP Read API**:
The read-only, text-only external surface for search and evidence retrieval. It never exposes local paths, provider secrets, images, file URLs, or OCR/index actions.
