# Downgrade CF-06 To Workspace Overlap Warnings

Status: accepted

OCR providers routinely emit overlapping boxes, and the CF-06 sibling-overlap check in `DocumentTreeValidator` rejected whole adoption batches, blocking large numbers of OCR documents from being imported. Ordinary sibling bbox overlap is therefore no longer a structured conflict anywhere: staging, adoption, draft commits, and manual edit commands all accept overlapping boxes.

Two mechanisms replace the blocking check:

- **Containment becomes hierarchy at the source.** During staging (`DocumentTreeService.NormalizeContainedBoxes`), a box almost fully contained in a larger ordinary sibling is re-parented under the immediate (smallest-area) container, using a ratio-based containment test (intersection / contained area >= 0.98) instead of strict geometric containment. Multi-level nesting resolves into proper parent-child chains, which are legitimate and never reported as overlaps.
- **Remaining sibling overlaps are workspace warnings.** `DocumentBoxOverlapDetector` (Core) reports significant ordinary sibling overlaps (intersection / smaller area >= 0.1, same exemptions as before) with their intersection rectangles. The PDF workspace draws dashed warning markers directly over each intersection and highlights the involved boxes; clicking a marker selects both boxes so the user can resolve the overlap explicitly (adjust bbox, change type, suppress, delete, split/merge).

**Consequences**

- `CF-06` is removed from `ConflictCode`; the conflict vocabulary is CF-01 to CF-05 with domains `snapshot_sync` and `file_resolution`. `DocumentBoxConflictActionExecutor` and the CF-06 descriptor mapper are deleted.
- Adoption and commit no longer roll back on overlaps; overlap remediation is an explicit, incremental user action in the workspace rather than an import-time gate.
- Equal-area duplicate boxes are never auto-nested (strict area comparison); they surface as overlap warnings for explicit handling.
- Manual edits do not auto-reparent; the containment normalization runs once at staging.
