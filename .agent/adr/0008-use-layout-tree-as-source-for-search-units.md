# Use Layout Tree As Source For Search Units

Status: accepted

The layout tree is the source for persisted SearchUnits. SearchUnit metadata is included in snapshots, while SQLite FTS is a rebuildable local cache generated from committed SearchUnits.

**Consequences**

Search and MCP depend on committed layout/text revisions, and index rebuilds can be local without changing synced evidence identity.

**Standing Constraints**

- SearchUnit metadata is durable and synced with snapshots.
- The SQLite FTS index is local, rebuildable cache derived from SearchUnits.
- SearchProfiles（搜索配置文件）affect query rewrite and recall behavior; they do not mutate canonical text or rebuild the index text by themselves.
- Search must expose stale/partial/unavailable index state rather than hiding it behind an unsafe linear fallback.
