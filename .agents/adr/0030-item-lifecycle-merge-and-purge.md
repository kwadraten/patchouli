# Item Lifecycle, Merge Redirects And Permanent Purge

Status: accepted; recorded 2026-09-10 from implemented V3-T5 requirements.

## Context

ItemMetadata is persistent user knowledge (ADR `0026`). Removing an Item from the active library, combining duplicate bibliographies, and permanently deleting library payload have different identity and snapshot consequences. The completed PRD must not remain the only record of these distinctions.

## Decision

- **Active / Trash / Merged** retain an `items` row. Trash is a restorable soft-delete tombstone; Merged is a redirect tombstone with `merged_into_item_id`. Active discovery, default search, tag counts/filtering and duplicate detection exclude both kinds of tombstone.
- **Purged** has no `items` row: `item_purge_records` remembers the removed identity. Permanent purge deletes the Item and its library-owned document/page/tree/search payload. It never deletes the user's original PDF or other source file. An old versioned evidence URI then resolves as `NOT_FOUND`, not as an evidence `purged` response (ADR `0028`). There are no legacy evidence-record/successor tables to maintain.
- Soft deletion preserves DocumentInstance, revision history and evidence identity. Restore and purge are explicit lifecycle operations; document history/revert cannot change an Item's deletion state. Item/CSL version control and cross-Item history stitching remain outside v3.
- Purge is available from trash after a blocking confirmation and dependency report. Active OCR and working revisions block destructive execution. Snapshot references are disclosed; purge cannot erase already-published immutable shards. Copied evidence URIs are not individually tracked, so the UI must not invent an issued-evidence count.

## Merge And Duplicate Resolution

- Merging two Items requires a preview, explicit target selection and confirmation of conflicting fields. Nonempty target fields win by default; source fields fill gaps; tags are unioned. Unsaved edits, active OCR or concurrent changes must not silently pass the merge guard.
- A successful merge is atomic. The source remains a redirect tombstone; its DocumentInstances move to the target without changing FileAsset, DocumentInstance, Page, tree, SearchUnit or versioned URI identities. Merge neither reruns OCR nor rewrites the document text. Document history remains within each DocumentInstance.
- Duplicate detection is manually triggered and presents pairs for explicit resolution or skipping. Candidates use exact identifiers, bibliographic similarity and identical FileAsset content hashes. Skipping performs no write; resolving uses the same merge preview. The default target follows library sort order.

## Snapshot And GC Boundaries

- CF-08 applies when both branches retain the same `item_id` row but disagree on active/trash/merged lifecycle. The conflict UI offers explicit local/incoming Item-level selection alongside field/link resolution.
- If a local purge record exists, importing an older snapshot remaps the incoming Item to a new `item_id`. It does not resurrect the purged identity, attach the new Item to the purge record, or offer CF-08 keep/use for that identity. This is distinct from a retained trash/merge tombstone.
- Trash state, tags, merge redirects and library-level pinned-tag preferences participate in library persistence/sync; branch conflicts remain explicit, without automatic object-level merge.
- FileAsset GC only cleans unreferenced library records, stale known locations and rebuildable caches. DocumentInstances (including those of trash Items), working revisions, retained committed evidence content and snapshot dependencies must be considered. Preview, deferred execution, retry and logs belong to the GC workflow; it never removes user-managed originals or mistakes in-flight imports for orphans.

## Tags And Protocol

- Tags are trimmed, case-sensitive text. Renaming to an existing tag merges names. Multiple selected tags filter active Items by AND; “无标签” selects active Items with no tags.
- Tag addition/removal/rename/merge affects active Items, not trash. Restoring an Item preserves its stored tags even if that name was removed from active Items meanwhile. Pinned tags and their stable order are separate library UI preferences, not Item tag content.
- Dragging Items onto a tag adds it idempotently; dragging onto “无标签” clears all tags after blocking confirmation. Global removal, tag merge and destructive confirmation flows stay explicit.
- Successful lifecycle/tag/merge writes publish the committed Library revision/change notification. MCP/CLI gain no delete/restore/merge commands. Fetch/put on trash or merged Items fail with `ITEM_IN_TRASH` / `ITEM_MERGED`; diagnostics carry state/redirect details. Evidence resolution still follows ADR `0028`.

## Implementation Evidence

Services: `ItemService`, `ItemMergeService`, `ItemPurgeService`, `ItemTagService` and FileAsset GC. Regression anchors: `ItemLifecycleTests`, `ItemMergeServiceTests`, `ItemPurgeServiceTests`, `ItemTagServiceTests`, `SnapshotPurgeImportTests`, `McpItemLifecycleErrorTests`. Original product acceptance IDs: V3-T5-AC1–AC19; this record preserves durable semantics rather than the completed UI task checklist.
