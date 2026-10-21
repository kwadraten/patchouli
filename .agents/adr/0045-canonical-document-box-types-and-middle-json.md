# 0045. Canonical Document Box Types and Middle JSON

Status: accepted by user direction, 2026-10-17.

Table representation is amended by [ADR 0050](0050-html-table-payloads.md), implementing
issue #26: validated HTML can be the explicitly versioned, editable table authority.
Legacy Markdown and diagnostic-placeholder payloads remain read-compatible; this does
not change the payload decisions for other box types or permit rewriting history on read.

The content vocabulary is based on MinerU v4's shared Middle JSON 2.0 model. By user
direction on 2026-10-21, other OCR engines normalize to that same model rather than
defining competing content semantics. Application-owned Box IDs, geometry and revisions
remain independent of the producing engine. Import adapters accept the shared model or
legacy content lists, validate their shape, and normalize type aliases before creating
page-local leaf candidates. The evidence boundary is recorded in
[OCR normalization](../ocr-normalization.md). No producer,
provider, source-format or provenance fields are added to canonical document storage.
Unmapped input types block adoption rather than silently degrading to ordinary text.

Retain `sub_type` only where it has explicit semantics: code versus algorithm, ordinary
versus reference lists, and visual image/chart categories. Algorithms retain Markdown
and inline formula semantics; ordinary code retains its literal string and language.
Remove `base_type` from models, reads, writes and XML. This supersedes the original
fallback-type design accompanying ADR 0015, while retaining its page-local tree model.

An equation can have LaTeX, a page-region image, or both. The page-region marker refers
to the box's own normalized bbox rather than a provider asset path. Native readers
crop their existing page bitmap. XML exposes immutable image-region descriptors and
typed visual annotations beside the editable payload. This amends ADR 0044's absence
of geometric descriptors specifically for image formulas; it does not add binary image
transport, file paths or a provider-specific document representation.

Migration 005 no longer creates the fallback column. Migration 048 checks old schemas,
normalizes extension types according to their existing usable payload interpretation,
rebuilds the table transactionally without the column, and restores dependent indexes
and triggers. All historical IDs, geometry and ordering are retained. It also normalizes
known aliases, converts algorithm payloads, and marks affected derived projections for
realignment. These are representation changes to historical rows; missing text is never
invented. Existing empty formula rows become image formulas and require reimport for
LaTeX recovery. Test both fresh schemas and old schemas with the original constraint.
