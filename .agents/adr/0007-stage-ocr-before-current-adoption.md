# Stage OCR Before Current Adoption

Status: accepted

OCR output enters staging first and only affects current layout, search units, evidence successors, and MCP reads after an adoption transaction. This prevents partial, cancelled, low-confidence, or bbox-invalid output from silently polluting searchable evidence.

**Consequences**

Adoption is serialized per DocumentInstance and must update current revision pointers, search dirty state, and evidence successor links together.
