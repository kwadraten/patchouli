# Use Page-local Document Box Trees As Source For Search Units

Status: accepted

The committed current `DocumentTreeRevision` of each physical page is the source for persisted SearchUnits. Every non-suppressed leaf `DocumentBox` creates one SearchUnit in sibling-pointer order. SearchUnit metadata is included in snapshots, while SQLite FTS is a rebuildable local cache generated from committed SearchUnits.

**Consequences**

Search and MCP depend on committed page-local Box Tree revisions. Evidence identity is `(tree_revision_id, box_id)`, so index rebuilds can be local without changing synced evidence identity.

**Standing Constraints**

- SearchUnit metadata is durable and synced with snapshots.
- `next_sibling_box_id` is the only canonical order; no `reading_order` compatibility column is retained.
- `suppressed=true` boxes do not enter default SearchUnits, FTS, compiled Markdown, or MCP output.
- The SQLite FTS index is local, rebuildable cache derived from SearchUnits.
- SearchProfiles（搜索配置文件）affect query rewrite and recall behavior; they do not mutate canonical text or rebuild the index text by themselves.
- Search must expose stale/partial/unavailable index state rather than hiding it behind an unsafe linear fallback.
