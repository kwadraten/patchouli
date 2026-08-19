# Use Path-Independent Library Identity

Status: accepted

A Library gets a generated `library_id` at creation time, and that identity is not derived from path, device, sync root, or account. Renaming or moving a library preserves identity, while cross-library evidence resolution returns an explicit mismatch.

**Consequences**

Library display name and storage location are mutable presentation details, not citation or sync identity.
