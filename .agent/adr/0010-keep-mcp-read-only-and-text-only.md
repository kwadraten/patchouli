# Keep MCP Text-Only (and Originally Read-Only)

Status: accepted; **amended by ADR `0023`** (limited writable MCP, 2026-07-31)

The first MCP surface was limited to **read-only, text-only** search and evidence retrieval so agents could cite safely without operating the library or inspecting private machine state.

**Amendment (`0023`)**

Absolute “never writes metadata” is relaxed for **narrow, revision-gated whole-resource replaces** of item `.bib` and style `.csl` projections, so agents can interact with the library to assist humans (fix styles, correct bibliography). All other write classes remain forbidden until a further ADR.

**Still in force from this ADR**

- Text-only external payloads: no images, image paths, local paths, `file:` URLs, or provider secret/config leakage.
- MCP 从不触发 OCR 或索引重建.
- MCP 无法读取提供程序密钥.
- MCP never returns cached images or image paths.
- MCP may return structured text and bbox only when a read tool explicitly asks for blocks.
- No bbox edit, index rebuild, or host-filesystem exposure via MCP.

**Historical phrase**

- “第一版 MCP 是只读且纯文本的” documents the v1/v2 first ship. v3+ intent is **text-only with limited writes** per ADR `0023`.

**Consequences**

External agents can cite evidence safely and, when writes are enabled under `0023`, maintain a small set of library resources without gaining a general automation bus or private machine inspection.
