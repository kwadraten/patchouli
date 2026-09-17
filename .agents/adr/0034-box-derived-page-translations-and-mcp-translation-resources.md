# 0034. Box-Derived Page Translations and the MCP Translation Resource Tree

Date: 2026-09-15

## Status

Accepted. Amends the resource tree of ADR `0024` (it adds one VFS root) and follows the
versioned-evidence model of ADR `0028` and the working/committed revision model of ADR `0027`.

## Context

Patchouli needs full-text translations of OCR pages, but it deliberately does not embed a
translation engine. The product path is that a human or an agent reads the page markdown
exposed by MCP, translates it outside Patchouli, and writes the result back. Any translation
storage therefore has to answer three questions:

1. **Derivation.** Page markdown is not stored; it is compiled on demand from the page-local
   `DocumentBox` tree of a committed `DocumentTreeRevision` (ADR `0015`, ADR `0027`). A
   translation that is stored as a frozen whole-page blob would silently drift from the tree
   whenever OCR is re-imported, a box is edited, or the page is committed again.
2. **Traceability.** The UI wants to highlight the translated counterpart of a clicked source
   box, so a translation must keep the box identity that produced each markdown range.
3. **Writability.** Agents write translations through the same narrow, atomic
   whole-resource `put` contract as item bibliographies and CSL styles (ADR `0023`): one URI,
   complete content, full validation before an atomic commit, no base-revision precondition.

## Decision

### Storage: a box-derived table, not a page blob

Translations are derived data over the box tree, stored per box in `translation_boxes`
(`page_id`, `box_id`, `ordinal`, `translated_md`, `source_hash`) with a per-page header in
`page_translations` (`source_tree_revision_id`, `version`, `updated_at`). The whole-page
translated markdown is compiled on demand by re-running the same traversal and rendering rules
as `DocumentMarkdownCompiler`, substituting each box's stored fragment and falling back to the
source fragment for boxes with no row. Because the compiler is the same, the compiled
translation keeps the source `MarkdownSourceMapEntry` box ids: a partially translated page is
still one structurally valid document, and the UI source-map linkage works unchanged.

`translated_md` stores only the box payload, not its markdown wrapper: titles store plain text
(normalized back to `#` × level on compile), equations store LaTeX, code boxes store raw code,
and other payloads store their markdown fragment. `source_hash` records the source payload at
write time.

### Alignment: lazy realignment against HEAD, row-level GC

Every content change produces a new committed `DocumentTreeRevision`, so staleness is detected
by comparing `page_translations.source_tree_revision_id` with the page's current HEAD revision;
no bundle-wide replay or payload diffing is needed. Reading a stale page realigns it first:
rows whose `box_id` still exists **and** whose `source_hash` matches are kept, everything else
is deleted, then the header revision and `version` are bumped. A full OCR re-import produces
new box ids and therefore expires the whole page naturally. Row deletion cascades on page and
document deletion, so there is no separate GC pass. All reads and writes are page-local; a
translation never spans physical pages.

### Write validation: positional structure equality

`put` on a translation page accepts a complete replacement of the compiled page markdown and
rejects it unless its Markdig block sequence is structurally identical to the current source
markdown: heading level, block kind, table row/column shape, code fences, math fences, media
placeholders, and logical-page `---` separators must line up one-to-one. Text content is free
to change. A failure returns **every** mismatch as a structured
`TranslationStructureError(block_index, expected, actual)` list rather than failing on the
first one, and writes nothing. On success the page's rows are replaced in a single transaction
and the compiler cache for that page is invalidated.

### MCP surface: a fourth VFS root

ADR `0024` requires URI-root changes to be synchronized across the contract, the ADR, and the
runtime. This ADR adds one root, `patchouli://translations/`, mirroring `patchouli://texts/`:

- `patchouli://translations/` — directory of documents with translation progress counters.
- `patchouli://translations/{document-id}/` — directory of pages, each with status
  `untranslated`, `partial`, `translated`, or `stale`.
- `patchouli://translations/{document-id}/page-{N}.md` — the compiled whole-page translation;
  readable and writable. `N` is the one-based physical PDF page index, identical to `texts/`.

Fetching a page with no translation returns `NOT_FOUND` and names the source page
`patchouli://texts/{document-id}/page-{N}.md` in the message, so an agent discovers the
read → translate → put loop from the error alone. A successful fetch carries the page's
`PageTranslationStatus` on the entry, including the stale box ids that still render source
text, so an agent can translate exactly the missing boxes next. A successful `put` returns the
resulting status and an `TRANSLATION_INCOMPLETE` warning when some content boxes still have no
row. Structure failures use `INVALID_CONTENT` with the structured mismatch list.

The translations root is read/write for translation pages and read-only in every other
respect: it exposes no box geometry, no OCR action, no index action, and no local paths, so it
stays inside the ADR `0023`/ADR `0010` boundary. Individual pages are addressed by physical
page index; logical pages remain a box-tree concept and are not separate translation
resources.

### Caching and projection

Compiled translations pass through a bounded cache keyed by
`(page_id, source_tree_revision_id, version)`, mirroring the compiled-markdown cache. The
directory projections are read-only SQL aggregates over the current committed revisions and
`translation_boxes`; they deliberately do not trigger realignment, so browsing a large library
stays cheap and side-effect free, while the first `fetch` of a stale page performs the
realignment.

## Consequences

- Translation text is never authoritative for source text; deleting `page_translations` /
  `translation_boxes` loses only translations and never page content.
- A translation row can survive an unrelated commit, so editing one paragraph does not throw
  away a whole page's translation; it expires only the changed boxes.
- The `source_hash` check makes realignment CPU-bound on payload hashing for the affected
  page, which is bounded by page size and happens at most once per revision change.
- No translation engine, language detection, or bilingual alignment is added: these remain
  external-agent responsibilities.
- The resource tree is now `items/`, `texts/`, `translations/`, `csl-styles/`, and the
  `library.toon` singleton. Root discovery, the `find` scope matrix, and the tool schemas must
  all list the new root; the runtime contract is `McpResourceUris`.
