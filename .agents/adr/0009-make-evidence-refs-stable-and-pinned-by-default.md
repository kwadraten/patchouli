# Make Evidence Refs Stable And Pinned By Default

Status: accepted

EvidenceRefs are long-term parseable text references and resolve in pinned mode by default. Later OCR/layout corrections produce explicit successor, current, compare, tombstone, purge, or mismatch outcomes instead of silently changing what an old reference means.

**Consequences**

Current-mode consumers may opt into drift, but copied evidence remains reproducible by default.

**Standing Constraints**

- EvidenceRef payloads do not include local paths, provider secrets, cache paths, or other unsynced local state.
- Pinned resolution preserves the referenced revision even after later OCR/layout/search updates.
- Current and compare resolution may follow successor chains, but they must surface supersession, source drift, and chain ambiguity.
- Copied evidence Markdown defaults to pinned text plus a minimal source and `evref:v1` identifier.
