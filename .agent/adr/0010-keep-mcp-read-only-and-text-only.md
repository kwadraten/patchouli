# Keep MCP Read-Only And Text-Only

Status: accepted

The MCP surface is limited to read-only, text-only search and evidence retrieval. It never writes metadata, edits bbox, triggers OCR, rebuilds indexes, exposes local paths, returns images, reveals file URLs, or leaks provider secrets/configuration.

**Consequences**

External agents can cite evidence safely but cannot operate the local library or inspect private machine state.
