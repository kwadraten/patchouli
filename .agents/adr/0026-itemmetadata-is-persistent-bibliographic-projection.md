# ItemMetadata Is A Persistent Bibliographic Projection

Status: accepted

A source document (FileAsset behind a DocumentInstance) is the primary evidence-bearing object — a digital surrogate whose page images, front matter, layout, and pagination usually carry enough evidence to distinguish manifestations of the same work. A DocumentTreeRevision is a versioned structural/textual interpretation of that document. ItemMetadata is a persistent, editable bibliographic projection of the Item, produced through human, agent, document-derived, or identifier-derived description for the purposes of retrieval, sorting, citation (CSL), deduplication, and browsing.

The projection is **derived epistemically but not rebuildable computationally**. User normalizations (e.g. romanized creator names), CitationKeys, and Tags are user knowledge state; deleting an Item's metadata cannot be recovered from the attached PDF or from identifier lookup. Metadata lookup merges provider candidates into ItemMetadata as new persistent values rather than storing candidates and re-projecting them dynamically. ItemMetadata is therefore not a rebuildable cache, derived table, or materialized view, and must not be described as one.

We explicitly do **not** refactor ItemMetadata into a MetadataAssertion graph (per-field subject/predicate/value/source/confidence records) or a true database materialized view. For a personal literature manager — not a national-catalogue provenance system — the ontological gain is small and the implementation complexity is high. Rigorous versioning effort belongs to the evidence layers (PDF, OCR, Box Tree), not to per-field metadata provenance.

The epistemic direction "document → interpretation → metadata" does not require the database to point the same way. `DocumentInstance.ItemId` stays as defined in ADR `0005`. An Item with identifiers and metadata but no DocumentInstance is a legitimate state: a bibliographic resource whose identity is known but whose document instance Patchouli does not yet hold — not a "virtual PDF". Identifier-only import and later PDF attachment both follow from this.

**Consequences**

- `ItemMetadata` remains a plain, editable, durable record; PDF import continues to create Item + FileAsset + DocumentInstance exactly as `PdfImportWorkflow` does today.
- A missing or moved source file never deletes Item metadata (already stated in the CONTEXT.md user-owned-source-files boundary); this ADR explains why that is semantically correct rather than a fallback.
- Editing ItemMetadata edits Patchouli's current bibliographic description of the resource, not the underlying document facts; user edits do not break the projection model.
- Future metadata-versioning work (PRD V3-T6) versions the projection record, not a per-field assertion graph.
- This ADR does not itself change EvidenceRef or OCR staging; those are separate decisions, since resolved by ADR `0027` (unified working/commit model) and ADR `0028` (versioned URI evidence).

**Standing Constraints**

- Documentation and code comments must not call ItemMetadata "derived data", "cache", or "view" in the rebuildable sense; the approved term is "persistent bibliographic projection".
- No per-field provenance/assertion storage may be introduced for ItemMetadata without a superseding ADR.
- Identifier-based metadata creation must remain valid without any DocumentInstance or FileAsset present.
