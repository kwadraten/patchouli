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
- Manifest v2 additionally requires `MinHostVersion`, the maximum of the backed-up Library's
  minimum and the snapshot compatibility-generation floor `0.4.2`. Import still accepts v1
  without this field, but rejects unsupported manifest versions, invalid/newer minimums,
  and incompatible metadata in every actual shard before creating staging. Shard inspection
  accepts supported older schema epochs for migration; source export remains strict.
  This does not implement the future PDF Bundle `min_reader_version` contract (#49).
- Large logical data is split into bounded SQLite shards rather than one mutable sync file.
- Older immutable data shards should be reused when possible; new revisions are written as new content.
- Sensitive mutable credential payloads remain logically separate from normal immutable data shards.
