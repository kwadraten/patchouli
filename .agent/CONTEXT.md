# Literature Library

Patchouli is a personal literature manager that treats user-owned source files as evidence-bearing documents. The domain language centers on stable bibliographic identity, relocatable files, versioned OCR/layout text, searchable units, and text-only evidence references.

## Language

**Library**:
The durable boundary for metadata, document instances, OCR/layout/search artifacts, evidence refs, snapshots, credentials, and MCP resolution. A library keeps the same identity when renamed or moved.

**Item**:
The bibliographic identity that a researcher cites, such as a book, article, edition, translation, preprint, or manuscript witness.
User-facing Chinese term: 题录. It has the same product meaning as citation: the citable bibliographic record, not the PDF file itself.
_Avoid_: Book, PDF, file, attachment

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
