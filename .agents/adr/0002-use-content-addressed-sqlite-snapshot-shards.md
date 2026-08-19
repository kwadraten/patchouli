# Use Content-Addressed SQLite Snapshot Shards

Status: accepted

Published libraries are represented as manifests plus content-addressed SQLite shards. This keeps individual sync files bounded, reduces re-upload cost, supports validation/repair, and avoids treating one large mutable database file as the sync unit.

**Considered Options**

- A single published SQLite file.
- Program-managed file sync.
- SQLite shards with a manifest.

**Consequences**

Snapshot tooling must manage shard identity, manifests, current pointers, and validation.

**Standing Constraints**

- Snapshot manifests include library identity, device lineage, schema version, shard list, shard hashes, and logical generation.
- Large logical data is split into bounded SQLite shards rather than one mutable sync file.
- Older immutable data shards should be reused when possible; new revisions are written as new content.
- Sensitive mutable credential payloads remain logically separate from normal immutable data shards.
