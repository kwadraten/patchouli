# 0045. MCP `add`/`view` Boundaries: Resource Creation, Path Input, Image Output, Crop Geometry

Status: accepted (issue #11), 2026-10-17.

## Decision

Extend the MCP surface from five verbs to seven with `add` (import a host-accessible
local file into the library) and `view` (return the source image of a library page or
box for model text-image collation). The two verbs have independent user tool switches
and domain permissions; existing `put` grants are never auto-expanded to `add`/`view`.

- **Resource creation.** `add` creates `patchouli://items/{item-id}.yml` and
  `patchouli://texts/{document-instance-id}/` through the host's shared import core
  (path validation, file handling, BLAKE3 fingerprinting, dedup, ingestion). PDF checks
  `items.add` + `texts.add`; CSL checks `csl-styles.add`. MCP, CLI, scan and window
  drag-drop reuse the same domain service; ViewModels hold no second import logic.
- **Path input.** `add` accepts a host-local path (`{"path", "kind"}`) and never a URL;
  the model downloads with its own ability first. This is the single explicit exception
  to the old "MCP does not accept local paths" constraint, which is updated accordingly.
  Authorization and configuration checks complete before any file is touched or moved.
  Callers cannot read arbitrary files through the path parameter: only supported
  resources are imported; directories and unsupported formats are rejected. Outputs never
  expose host source paths, cache paths, or unsanitized exceptions.
- **Image output.** `view` returns mixed content per MCP 2025-06-18 ImageContent:
  content[0] is the existing TOON/JSON text envelope with image metadata, content[1] is
  the native image block (`{"type":"image","data":"<raw base64>","mimeType":"image/png"}`);
  base64 never sits inside TOON/JSON text fields and carries no data-URL prefix.
  Failures use the unified error envelope plus `isError`; a fake successful image entry
  is never returned. `view` is the explicit exception to the text-only data boundary;
  all other tools keep it.
- **Crop geometry.** The crop range is reported in page-normalized coordinates
  (reproducible). This is `view`'s explicit geometry exception to the "no geometry
  exposed" constraint; no other tool exposes geometry.
- **File handling behavior.** Per-action device-local system settings
  ("MCP add import PDF", "PDF drag into window"), each defaulting to keep-in-place;
  never synced with Library snapshots; not overridable per request by MCP callers.
  Move mode targets the first file search root under its persisted stable order
  (never accidental DB ordering), moves before BLAKE3 computation, and fails
  explicitly when no root exists or the first root is unavailable/unwritable —
  never silently substituting another root or falling back to in-place. Move and DB
  commit are not one transaction: on import failure the original position is
  restored when possible; recovery records live in the runtime database, never in
  temp cache.
- **Agent multimodal chain.** Tool results carry metadata plus image bytes; images
  land in the session-managed persistent media area (`<session>/media/`, referenced
  by media id, never by path) and enter model history as `LlmImagePart` data-URI
  parts — real vision input, never degraded to a path or base64 text. Media ids
  survive context compression inside the recorded payload; retry/resume/context
  rebuild reload by media reference. Models without vision input fail explicitly
  (`UnsupportedInput`); collation is never faked. Recommended flow in the tool docs:
  `fetch` (text plus version) → `view` (same revision/box source image) →
  `put` with the original `source_revision`; a changed version yields the normal
  revision conflict.

## Superseded constraints

- "MCP does not accept local paths" now reads: only `add` accepts local paths, as the
  explicit input exception; every other tool neither accepts nor exposes them.
- The text-only data boundary (V3-AC28) gains the `view` image-output exception.
- The no-geometry-exposed constraint gains the `view` page-normalized crop-range
  exception.

ADR 0023/0024 (limited writes, structured protocol), ADR 0028 (versioned URI
evidence), ADR 0034 (translation profile) and ADR 0036 (agent sessions) otherwise
stand: single host, immutable history, user-controlled permissions, clean-success
envelope rules, and the `fetch`/`put` version-conflict contract are unchanged.

## Consequences

- CLI `add`/`view` are thin mappings onto the same MCP tools (relative paths resolved
  client-side; image blocks decoded and written via temp file plus final move).
- The built-in agent, FSI/SDK entries and native calls share one capability check,
  one execution-result shape and one operation-record semantic for images.
- Old-settings migration: the permission model is deny-list based (`McpPermissionPolicy.Allows`
  denies only on explicit tool or domain overrides), so old settings without `add`/`view` entries
  keep the same default-allow posture as the other verbs — including the independent `add`/`view`
  tool switches, which are on unless the user turns them off. There are no grant objects: an
  existing `put` allowance cannot auto-expand because `add` checks its own domain permissions
  (`items.add` + `texts.add` for PDF, `csl-styles.add` for CSL) and `view` checks the target
  domain (plus the source text domain for translation images); each is denied only by its own
  explicit override. Behavior settings default to keep-in-place.
