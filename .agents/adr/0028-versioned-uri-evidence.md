# Versioned URI Evidence

Status: accepted

Evidence identity moves into the resource URI itself: `patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree-revision-id}&box={box-id}`. A URI with `rev` reads the immutable revision it names; a URI without `rev` reads the current HEAD. There are no pinned/current/compare resolution modes, no successor chain, and no drift surface.

This supersedes ADR `0009` entirely, including its pinned-by-default and drift-surfacing semantics. Those semantics are abolished, not preserved.

**Consequences**

- `evref:v2` is completely removed: the codec, `evidence_ref_records`, `evidence_successors`, successor-chain logic, and resolution-mode/status enums are deleted with no legacy decode path.
- Pinned-text copies are no longer stored. "Copy evidence" outputs the versioned URI plus the referenced text; it does not write a new evidence record.
- Supersede/tombstone semantics are gone. A URI with `rev` always resolves to the original revision text. A later deletion of the same box does not change the old URI. After purge, the URI returns `NOT_FOUND`; there is no `purged` state.
- `library_id` binding and document/page ownership validation remain: a URI resolves only to the host's current Library, and the resolver rejects mismatched bindings with `NOT_FOUND`.
- This ADR depends on ADR `0027`: in-place commit guarantees that `tree_revision_id` and `box_id` remain stable from working revision through commit, so the versioned URI stays valid after promotion.

**Standing Constraints**

- Evidence URIs do not include local paths, provider secrets, cache paths, or other unsynced local state.
- Only committed revisions may appear in evidence URIs; working revisions are not externally referenceable.
