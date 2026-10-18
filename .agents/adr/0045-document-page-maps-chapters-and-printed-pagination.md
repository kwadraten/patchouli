# 0045. Document Page Maps: Chapter Structure and Printed Pagination

Status: accepted by user direction, 2026-10-17.

## Decision

Each document instance may carry one authoritative structured page map: pagination rules
that bind physical PDF pages (1-based) and logical pages (Box IDs, never serial numbers)
to printed page numbers, explicit page-label overrides, and a chapter tree. The database
(`document_page_maps`, migration 052) holds the structured model; the MCP
`patchouli://texts/{document-id}/map.yml` resource (fixed filename, profile
`patchouli-page-map-1`) is a virtual-file projection of it. Full per-page maps are never
persisted, and PageLabel authority is not duplicated: the PDF-embedded `Page.PageLabel`
remains the outline's label source, while map `page_labels` only override the map's own
computed display values.

Navigation positions are `pdf_page` (1-based) plus an optional `logical_page` Box ID.
Pagination rules:

- `physical_page`: `printed = printed_start + pdf_page - start`, both ends inclusive.
- `logical_page`: logical pages enumerate in physical-then-reading order within the range,
  `+1` each; the caller supplies the reading order, the resolver never sorts by Box ID or
  geometry.
- `page_labels`: explicit string labels override computed values for the same target; no
  conflicting duplicates on the same target.

The resolver is pure and total: forward (position → printed number/label), reverse
(printed number → ALL matching positions; no global uniqueness), and chapter display
numbers resolved at read time (unknown → physical page only, the chapter stays visible
with a diagnostic). Nothing is extrapolated outside ranges; step-1 integers only.
Inserts, missing pages, and renumbering are expressed via split ranges. Mixing
`physical_page` and `logical_page` rules on the same physical page is rejected, as is a
logical range whose physical page has no logical pages (re-split or complete the logical
division instead).

Write-time validation additionally checks document/page ownership, range bounds against
the real page count, logical-page Box type, and current committed revision ownership.
Chapter targets store positions only.

Logical-page invalidation saves the structural signature (identity + order, i.e. the
stored reading order) of each logical range at write time — validation metadata, not
per-page maps. Text-only changes never invalidate. Deletion, addition, page moves
(reorder), and disappearing endpoints mark the affected rule as needing repair: its
computation pauses, its original definition is kept, and the rest of the map keeps
resolving — one bad reference never makes the whole map unreadable. A chapter whose
logical target died keeps the chapter and degrades to its physical page with a
diagnostic.

Map writes replace the whole map atomically in one transaction (an empty definition
clears the map); on success the Library revision advances and the change is published
after the commit commits. The map lifecycle follows the document instance (FK cascade);
snapshots shard the table with the document group, and branch import surfaces a
blocking `CF-09` conflict when both sides define different maps instead of silently
last-writer-winning.

The map is a texts-domain resource: `find` discovers `map.yml` in the document scope,
`fetch` returns YAML in the unified envelope (documents without a map get the
discoverable, first-put-able empty-map projection; plain fetch triggers no OCR/AI),
`put` takes whole-resource YAML under the texts `put` permission and the put switch,
and the map itself is not citable. Truncation is explicit and never looks like a
complete submittable resource.

The PDF workspace gains a chapters sidebar tab: a collapsible chapter tree showing
title, resolved printed number, and physical position (multiple headings per page
supported). Clicking jumps via `GoToPageAsync` first and only highlights the logical
region after confirming the landing page; draft-blocking aborts before any highlight.
Navigation commits publish changesets carrying the DocumentInstanceId so open
workspaces reload navigation preserving page, tab, and expansion.

Out of scope: auto-generating map candidates from heading boxes or the TOC (revisit
later), and any independent map history mechanism.

## Superseded constraints

This extends ADR 0023/0024's structured resource protocol with a new writable texts
resource and ADR 0028's evidence addressing with logical-page positions. Their
single-host, immutable-history, user-controlled permissions, text-only MCP, and
no-path/secret/image boundaries continue. The runtime Box Tree stays authoritative for
box identity; the map references it but never rewrites it.

The normative schema, empty-resource behavior, logical-page rules, invalidation
handling, limits and error contract are maintained in
[mcp-protocol.md](../mcp-protocol.md). Tests cover the pure resolver, validator,
invalidation, and the YAML round-trip.

## Consequences

- Agents can read and maintain printed page numbers and chapter structure through
  `map.yml` without touching OCR state; the UI chapter tab and MCP share one resolver.
- A map whose logical pages were edited degrades locally (paused rules, diagnostics)
  instead of failing reads or writes; repair is a fresh `put` of the corrected YAML.
- Branch imports now block on divergent maps (`CF-09`, keep_local / use_incoming)
  rather than silently overwriting them.
