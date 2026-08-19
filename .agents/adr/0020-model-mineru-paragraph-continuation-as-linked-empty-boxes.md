# Model MinerU Paragraph Continuation Regions As Linked Empty Boxes

Status: accepted

MinerU splits a paragraph that spans pages (or columns) into multiple regions: the full merged text is kept in the first region's block, and every remaining region is emitted as a `paragraph` block with empty content (`"paragraph_content": []` in content_list_v2, `"text": ""` in v1) and a valid bbox. MinerU's own layout visualization draws a dashed connection between these regions. Verified against 224 real result zips: 948 such blocks, 786 at page start (cross-page), 162 within a page (column jumps), 8 chains of three or more regions; the preceding body block is always a paragraph.

`document_boxes` gains a nullable `continues_from_box_id` column (migration 026). A continuation box keeps an empty `TextBoxPayload` and points at the box holding the text. The pointer is a bare `box_id` without a composite foreign key: box ids are document-wide unique GUIDs that survive staging→adoption copies, while every existing tree foreign key is `(tree_revision_id, box_id)`-scoped and each physical page has its own revision, so a cross-page pointer cannot be FK-enforced. Referential integrity is maintained by the service layer instead:

- `MergeLeavesAsync` re-points links targeting merged boxes to the merge result (which itself clears its inbound link, since it now holds real text).
- `SplitLeafAsync` re-points links targeting the split box to the tail fragment and clears the tail's own inbound link.
- `DeleteBoxAsync` (and the evidence-ref purge copy) clears links targeting the deleted box.
- Reorders, indent/outdent, and suppression changes keep ids and need no maintenance.

`MinerUDocumentTreeCandidateMapper` links each empty text region to the nearest preceding non-suppressed, non-empty text box in document reading order (tracked across pages; chained empty regions share one head) and pre-assigns the head's box id so the cross-page pointer is known before per-page staging. Continuation seeds are excluded from staging-time containment normalization so the empty shell regions never absorb children or get re-parented.

The PDF workspace renders same-page links as dashed connectors between the two boxes, shows a clickable badge above regions whose source box lives on an earlier page (jumping there and selecting the source), renders continuation rows in the box tree with a ↳ prefix and the source text, and offers a "跳转到续接源框" context menu action.

**Consequences**

- Empty continuation boxes no longer appear as mysterious empty text boxes; their provenance is visible and navigable.
- Markdown compilation and search indexing skip empty payloads already, so continuation regions contribute no text and no search units; the merged paragraph text is indexed once via the head box.
- Dangling pointers are possible in principle (no FK), but every mutation path that removes or replaces a box repairs or clears inbound links; unresolved links simply render as plain empty boxes.
- Manual creation/removal of continuation links is not yet exposed; links originate from MinerU import only.
