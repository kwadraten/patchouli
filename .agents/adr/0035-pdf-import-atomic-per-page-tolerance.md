# 0035. Atomic PDF Import with Per-Page Failure Tolerance

Date: 2026-09-29

## Status

Accepted. Amends the PDF-import consequence of ADR `0026` in implementation detail only (the
created object graph is unchanged) and extends the per-page placeholder principle of ADR
`0025` and the OCR interchange schema boundary to import time.

## Context

`PdfImportWorkflow` today composes one domain-service call per entity:
`RegisterFileAsync`, `CreateItemAsync`, `AttachDocumentInstanceAsync`, and then one
`CreatePageAsync` per page — each call commits its own small transaction. A failure on page
N of M returns an error while the Item, FileAsset, DocumentInstance, and pages 0..N-1 stay
committed: a half-imported book that pollutes the library and never retries cleanly. The
production database `D:\test_db` already holds such residue — one orphaned Item plus one
orphaned FileAsset from the same book, created 4 seconds apart.

Two more gaps follow from the same shape. First, import-time page-info extraction
(width/height/rotation) is effectively all-or-nothing: one unreadable page either fails the
whole import or would have to be silently dropped, and dropping is not acceptable because
`pages` rows are positional evidence anchors — versioned evidence URIs address
`page-{index}`, and the page-local revision model assumes exactly one committed revision per
physical page. Second, the per-page loop costs one transaction (and its fsync) per page,
which dominates import latency for books of any size.

## Decision

### Atomic import: one write lease, one transaction, batched page insert

The workflow builds the entire import in memory — FileAsset row, Item row,
DocumentInstance row, all `pages` rows, and the per-page placeholder
`DocumentTreeRevision`/`DocumentBox` rows — and commits them under one write lease in a
single transaction, inserting pages with batched multi-row `INSERT ... VALUES` statements.
A commit failure rolls the whole import back, so "a failed import writes nothing" holds by
construction.

We rejected compensating deletion (on failure, walk back and delete what was created):

- every delete is its own transaction that can itself fail, creating new residue while
  cleaning old residue;
- the compensation set must mirror every FK cascade and side table exactly, so it rots
  whenever the schema changes;
- partially committed rows are visible to concurrent readers between the original commit
  and the compensation, so the library briefly shows books that do not exist;
- it saves nothing on the success path, which is the common case.

A single transaction buys both properties we actually need — correctness (rollback) and
performance (one commit for the whole book instead of one per page) — with no success-path
overhead.

### Per-page failure tolerance keeps page order complete

Import-time extraction of a page's info (width/height/rotation) — or any other single-page
exception — no longer fails or drops the page. The page still receives its `pages` row so
page order and count match the source file, and a committed
`DocumentBoxType.LogicalPage` placeholder records the failure on the page itself: payload
`Import failed for this page: <reason>`, full-page `NormalizedBBox(0, 0, 1, 1)`,
diagnostic `import_page_placeholder`. This is the same product principle as
`blank_page_placeholder` (ADR `0025`): a page carrying a placeholder is not a failed page,
and one bad page must not fail an otherwise good book.

The placeholder is committed (not a working revision) because at import time the page has
no better content to stage: there is nothing to preview or adopt later, and leaving the
page without a current revision would break the one-committed-revision-per-page invariant
that search, evidence, and MCP reads rely on.

When the page-info reader is unavailable as a whole, the workflow degrades to the previous
behavior — width/height `null`, no failure counted — rather than failing imports.

### Threshold: the book fails only past a configurable ratio

The import fails as a whole — and, being atomic, writes nothing — only when
`failedPages / pageCount > MaxFailedPageRatio`. At or below the threshold the book imports
normally with placeholders on the failed pages. The threshold defaults to 0.2, takes values
in [0, 0.9], is stored as `ImportAppSettings.MaxFailedPageRatio` in the settings JSON
`"Import"` section, and is edited as a percentage in a new 导入 section of the settings
page. `PdfImportResult` carries the outcome: `PageCount`, `FailedPageCount`, and
`PageFailures` (record `PdfImportPageFailure(int PageIndex, string ErrorMessage)`), with
`Status` becoming three-state — `"imported"`, `"imported_with_page_failures"`, or `null`
on failure.

### Startup GC for pre-atomic residue

The atomic workflow prevents new residue but does not clean what earlier imports left.
`ImportResidueGcService` runs once at startup inside `HostServices.CreateAsync`'s startup
maintenance block, alongside `OcrRunEngine.ReconcileInterruptedRunsAsync`. Its rules are
deliberately conservative; every candidate row must be older than one hour:

1. A DocumentInstance with zero pages is deleted (FK cascade cleans its rows); its parent
   Item is deleted only if it then has no remaining instances.
2. Orphan pairing: a live Item with no instances, no identifiers, and no collections is
   deleted together with an unreferenced FileAsset whose file-name stem equals the Item
   title — the fingerprint a half import leaves behind.
3. Any remaining unreferenced FileAssets are left to the existing `FileAssetGcService`.

"No DocumentInstance" alone is never a deletion condition. ADR `0026` makes an Item with
identifiers and metadata but no DocumentInstance a legitimate bibliographic resource, so
BibLaTeX/identifier-only 题录 that never had an instance must survive GC; the pairing rule
therefore requires the stem-equals-title corroboration plus empty identifiers and
collections before it deletes anything.

## Consequences

- `PdfImportWorkflow` stops composing per-entity domain services for writes; the
  Item/FileAsset/DocumentInstance object graph it creates is unchanged, so ADR `0026`'s
  semantic point stands — only the write shape changes.
- Failed imports leave nothing behind, so import-retry semantics become trivial: re-import
  the same file.
- Placeholder pages render `Import failed for this page: <reason>` in place of content
  until an OCR run or edit replaces them; `import_page_placeholder` is diagnosable
  alongside `blank_page_placeholder`.
- The settings page gains a 导入 section; the threshold is device-local application
  settings, not synced state.
- Residue GC is a one-time cleanup for legacy databases; it can be retired once no
  supported upgrade path crosses the pre-atomic era.
