# Use Layout Tree As Source For Search Units

Status: accepted

The layout tree is the source for persisted SearchUnits. SearchUnit metadata is included in snapshots, while SQLite FTS is a rebuildable local cache generated from committed SearchUnits.

**Consequences**

Search and MCP depend on committed layout/text revisions, and index rebuilds can be local without changing synced evidence identity.
