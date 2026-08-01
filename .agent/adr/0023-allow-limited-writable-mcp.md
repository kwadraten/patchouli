# Allow Limited Writable MCP

Status: accepted (2026-07-31); amends ADR `0010`; production transport selected by ADR `0024`

## Context

ADR `0010` made the first MCP surface **read-only and text-only** so external agents could search and cite evidence without operating the library or inspecting private machine state. That boundary was correct for alpha/v2 safety, but it also caps product value: a read-only agent can only *consume* the library. It cannot help the human *maintain* it.

Real assistant workflows need interaction, for example:

- The installed CSL catalog has no adequate style. An agent that can read full style XML and library context should draft a conforming style and write it back.
- Bibliographic fields on an Item are wrong or incomplete. An agent that can read the whole item projection (not a truncated UI form) should correct the record and commit a validated replacement.

Without a deliberate write path, users either leave the agent loop (manual UI only) or invent unsafe ad-hoc channels. v3 therefore **opts into limited, auditable writes** while keeping the rest of the MCP threat model.

## Decision

1. **MCP remains text-only** for payloads that leave the process: no images, image paths, local filesystem paths, `file:` URLs, provider secrets, or provider configuration dumps. ADR `0010` standing constraints on secrets, paths, images, OCR triggers, and index rebuilds **remain in force**.

2. **MCP is no longer absolutely read-only.** A narrow write capability is allowed so agents can interact with the library to assist humans, not merely browse it.

3. **Allowed writes** replace exactly one existing writable resource, after full-content validation, with optimistic concurrency:
   - `patchouli://items/{item-id}.bib` — whole-item BibLaTeX (or agreed bibliographic) projection replace
   - `patchouli://styles/{style-id}.csl` — whole CSL style document replace

4. **Write mechanics** for the structured production transport selected by ADR `0024` (and any future shared CLI service):
   - Client supplies the complete replacement body and an expected **base revision**
   - Server validates the entire body before any mutation
   - Commit is atomic; validation failure leaves the library unchanged
   - Base mismatch returns a stable revision-conflict error; no last-writer-wins silent merge
   - Writes never create, delete, or rename resources in this ADR’s scope
     - Items of type `general` must not be silently treated as a typed CSL item. On the MCP agent surface, an `@misc` round trip preserves `general`; when the minimum renderable fields are present, an explicit MCP `@misc` citation fallback may render it with a `general_as_misc` warning. A supported non-`misc` BibLaTeX entry is an explicit type refinement and may persist the mapped Patchouli type. Unknown or insufficiently populated entries return `NOT_CITABLE`, and the agent projection path must not weaken the UI general-type restrictions (align with PRD v3)

5. **Forbidden writes** (non-exhaustive; still require a future ADR if ever needed):
   - Document Box Tree / bbox / page Markdown as MCP mutations
   - EvidenceRef mutation or fabrication of pinned evidence identity
   - OCR run, preset rebind, staging adoption, or search index rebuild
   - Snapshot publish/import, FileSearchRoot changes, credential changes
   - Arbitrary SQL, settings secrets, or host filesystem writes

6. **Transport-neutral product rule:** whether the surface is `patchouli_shell`, discrete tools, or `patchouli-cli` / `patchouli.put`, the domain authority is .NET. Sidecars and MCP hosts must not hold a second writable library API.

7. **User control:** library or MCP settings must allow disabling write tools/commands without disabling read/search/cite. Default for new installs may enable writes only when authentication and bind policy already satisfy existing MCP security rules (no unauthenticated `0.0.0.0` writes).

## Relationship to ADR `0010` and `0022`

| Prior decision | After this ADR |
|---|---|
| `0010`: MCP never writes metadata | **Amended:** metadata write is allowed only as whole-resource replace of listed item/style URIs under revision gating |
| `0010`: text-only, no paths/secrets/images/OCR/index | **Unchanged** |
| `0010` phrase “第一版 MCP 是只读且纯文本的” | Historical for v1/v2 first ship; **v3+ production intent is text-only with limited writes** per this ADR |
| `0022`: Bashkit read-only VFS | Superseded by ADR `0024`; the shell implementation was removed from `main` (2026-08-01). Any write enablement on the structured surface must implement this ADR’s resource and safety rules, not open-ended VFS writes |

## Consequences

- Agents can assist humans by fixing bibliography records and authoring/updating CSL styles inside Patchouli’s validation boundary.
- Product and security docs must stop promising “MCP never writes” without the limited-write exception.
- Contract tests must cover: successful `put`, `REVISION_CONFLICT`, `INVALID_CONTENT`, `PERMISSION_DENIED` on documents/evidence/`general`, and disabled-write configuration.
- UI should surface that agent-originated item/style updates occurred (audit-friendly status or history is desirable; exact UX is not fixed here).
- Expanding writable URI kinds requires a new ADR; do not grow `put` by convenience.

## Standing constraints (additive)

- MCP 从不触发 OCR 或索引重建.
- MCP 无法读取提供程序密钥.
- MCP never returns cached images or image paths.
- Writable MCP is **assistive library maintenance**, not a general automation bus.
