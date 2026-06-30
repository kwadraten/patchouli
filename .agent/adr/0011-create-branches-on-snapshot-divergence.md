# Create Branches On Snapshot Divergence

Status: accepted

When a device publishes from a parent snapshot that is no longer current in the sync root, the app creates a Snapshot Branch instead of overwriting with last-writer-wins. Branches preserve divergent work until the user explicitly imports or picks content.

**Consequences**

Automatic merge is deliberately conservative, especially for OCR/layout revisions, SearchUnits, and EvidenceRefs.
