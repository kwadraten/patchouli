# Reset Library Schema To Page-local Document Box Trees

Status: accepted

Patchouli 0.2.0 starts a fresh SQLite library schema epoch. A library is composed of physical `Page` records and one current immutable `DocumentTreeRevision` per page. Revisions contain stable-ID `DocumentBox` records; sibling pointers are the only canonical order, and only `logical_page` Boxes may own children.

The 0.1.x `LayoutRevision`, `LayoutNode`, `reading_order`, `text_policy`, and table-cell schema is rejected at open time. Patchouli does not migrate, dual-write, decode, or adapt those records in the 0.2.0 runtime.

**Consequences**

- Page edits clone current state into a draft and use explicit `IDocumentTreeEditor` commands. Commit creates an immutable current revision; discard leaves current unchanged.
- OCR always creates staging revisions. Adoption is a separate page-local action.
- Typed leaf payloads compile deterministically to Markdown. A single Markdig pipeline owns validation, plain-text projection, and native preview parsing with raw HTML disabled.
- `SourceMap` exists only in the current compilation/UI session. It is not database, snapshot, evidence, or MCP state.
- Snapshot allow-lists contain Document Tree, SearchUnit, evidence, and OCR lifecycle metadata only; they exclude old layout tables, provider JSON, PDFs, caches, Markdown ASTs, and UI SourceMaps.
- Evidence codec v2 identifies `(tree_revision_id, box_id)` and intentionally does not decode v1 layout references.
