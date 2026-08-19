# Create Branches On Snapshot Divergence

Status: accepted

When a device publishes from a parent snapshot that is no longer current in the sync root, the app creates a Snapshot Branch instead of overwriting with last-writer-wins. Branches preserve divergent work until the user explicitly imports or picks content.

**Consequences**

Automatic merge is deliberately conservative, especially for OCR/layout revisions, SearchUnits, and EvidenceRefs.

> Note: EvidenceRef identity and resolution semantics are superseded by ADR `0028`.

**Standing Constraints**

- A divergent snapshot is opened as an independent branch for inspection.
- v1 does not perform automatic object-level merge across branches.
- v1 must not silently apply last-writer-wins between branches.
- Branch import plans exclude provider credentials, cache paths, original-file copies, staging paths, and local machine paths.
- Imported documents mark their local search index state stale so the active branch can rebuild safely.
