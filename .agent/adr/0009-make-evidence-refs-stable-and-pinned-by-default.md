# Make Evidence Refs Stable And Pinned By Default

Status: accepted

EvidenceRefs are long-term parseable text references and resolve in pinned mode by default. Later OCR/layout corrections produce explicit successor, current, compare, tombstone, purge, or mismatch outcomes instead of silently changing what an old reference means.

**Consequences**

Current-mode consumers may opt into drift, but copied evidence remains reproducible by default.
