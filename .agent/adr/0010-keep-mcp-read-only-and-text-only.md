# Keep MCP Read-Only And Text-Only

Status: accepted

The MCP surface is limited to read-only, text-only search and evidence retrieval. It never writes metadata, edits bbox, triggers OCR, rebuilds indexes, exposes local paths, returns images, reveals file URLs, or leaks provider secrets/configuration.

**Consequences**

External agents can cite evidence safely but cannot operate the local library or inspect private machine state.

**Standing Constraints**

- 第一版 MCP 是只读且纯文本的.
- MCP 从不触发 OCR 或索引重建.
- MCP exposes evidence availability, not local file paths, file URLs, cache paths, render images, or root names.
- MCP does not expose provider secrets, provider configuration details, model paths, or credential status beyond evidence capability.
- MCP never returns cached images or image paths.
- MCP may return structured text and bbox only when a read tool explicitly asks for blocks.
