# Unified Working Copy And Immutable Revision Model

Status: accepted

`DocumentTreeRevision` collapses to a single working copy per page and an immutable committed current revision. OCR import and manual edits both produce a working revision; validation is followed by an in-place commit that promotes the same revision to current without copying `DocumentBox` rows. Legacy `staging`/`draft`/`discarded` rows remain in user databases but are excluded from every read path.

This supersedes ADR `0007` and amends ADR `0015`: the staging-before-adoption concept is replaced by the working/commit boundary, and the rule that "OCR always creates staging revisions" no longer holds.

**Consequences**

- `DocumentTreeRevisionStatus` is `working` or `committed`. Only `status='committed' AND is_current=1` feeds search, evidence resolution, and MCP reads.
- OCR and manual edits share the same working-revision path. A failed or cancelled working revision is deleted; the only failure audit is `OcrRun.status=failed`.
- Commit is in place: `tree_revision_id` and its `DocumentBox` IDs stay stable across promotion. Revert is a new commit that copies the target revision's content, sets `source='revert'` and `reverted_from_tree_revision_id`, and points the page's current pointer forward. History is append-only and the current pointer never moves backward.
- A new `DocumentCommit` entity groups page revisions into document-wide commits: `document_commits(commit_id, document_instance_id, parent_commit_id, source, message, created_at)` and `document_commit_pages(commit_id, page_id, tree_revision_id)`. HEAD is the latest commit. `LibraryRevision` remains a whole-library change counter and is not a per-document history mechanism.
- Evidence URIs may only reference committed revisions. Because working revisions are committed in place, their IDs remain valid after promotion.

**Standing Constraints**

- Snapshot shards exclude `working` rows and legacy-status rows. Uncommitted content is not synced.
- Legacy `staging`/`draft`/`discarded` rows are never read, counted, or treated as GC dependencies.
