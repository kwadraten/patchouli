# 0057. Read page images through `patchouli.fetch` on `patchouli://images/`, retire `patchouli.view`

- Status: Accepted by user direction (design analysis 2026-11-08). Implementation in
  progress on `feature/mcp-fetch-images-replaces-view`; this records the target contract,
  not a completed acceptance run.
- Date: 2026-11-09
- Product scope: dev-0.4.0 MCP refactoring (no GitHub issue).

## Context

`patchouli.view` was two things at once: a source-page image reader and a plugin-UI
descriptor reader. One verb doing two jobs, and the image read is a read of a resource —
it belongs under `fetch` like every other read, with the content type decided by the
resource rather than by a separate verb. The agent path becomes: `find texts/` →
`fetch` page XML (OCR text + `source_revision` + `image_uri`) → `fetch image_uri` →
the model gets the image natively → `put` page XML.

Two concrete problems forced the change instead of a cleanup:

- A latent contract bug: `PageImageViewService` appended `?rev=...` to translation image
  URIs that `McpResourceUris` could not parse, so translation image addresses were not
  round-trippable through the URI layer.
- The verb table had grown to seven tools for six kinds of work, and every new caller
  (MCP client, CLI, agent skill docs) had to learn which verb reads images versus text.
  The five-way balance behind the MCP design — agent error-rate reduction, CLI
  human-friendliness, performance, internal-implementation exposure (agent control),
  simplicity/maintainability — favors one read verb with a discoverable resource tree.

## Decision

### New read-only `patchouli://images/` domain

`patchouli://images/{document-id}/page-{n}.png`, rendered on demand from the original PDF
and never stored on disk. The query expresses the read:

- `?rev={revision-id}` — a historical revision; unpinned when the page has no committed
  Box Tree.
- `?box={box-id}` — crop to that box. `box=` means crop by default; there is no separate
  `context=region` parameter.
- `?overlay=boxes` — draw the page's boxes with numbers, in page XML document order.
- `?max_edge={pixels}` — output resolution cap (64..8192, default 2048).

`rev`, `box`, `overlay` and `max_edge` all participate in URI normalization: different
parameters are different canonical URIs. There is no `translations/` image mirror —
images are only the original PDF page rendering, and a translation page's image is its
source document's page image. Discovery: `find patchouli://images/` lists document image
directories; `find patchouli://images/{document-id}/` lists that document's page images;
`fetch` of a text page (page/evidence/translation) and `find --long` projections expose a
directly usable `image_uri` in result metadata. It is never written into editable Page
XML.

### `fetch` serves images; `patchouli.view` is deleted

`ViewCoreAsync`, the `patchouli.view` tool definition and its CLI command are deleted.
`FetchAsync`/`FetchCoreAsync` handles `images/` URIs by calling the existing
`PageImageViewService.ViewAsync()` — the service and its interface are unchanged
(ResolvePage → ResolveRevision → ResolveRegion → PDFium render → Crop/Resize/Overlay →
PNG + Metadata; box numbering stays consistent with Page XML node order; pages without
an OCR/Box Tree still render whole-page images).

Restrictions, carried over from the old view contract:

- A single fetch call may batch text URIs as before, but if it includes an `images/` URI
  it must contain exactly that one image URI — no text batch, no `range`.
- Text `limit_bytes` rules (default 64 KiB, max 1 MiB) apply to text only. Images keep the
  rendering limits (max PNG 8 MiB, max pixels 16,777,216). An image is never truncated
  like text.
- The result is mixed text + native image: content[0] is the text envelope with image
  metadata (canonical URI pinning the actually used revision, `view_type`, MIME, actual
  width/height, actual crop, `image_index` 0, `boxes` mapping only with
  `overlay=boxes`), content[1] is MCP native ImageContent
  (`{"type":"image","data":"<raw base64>","mimeType":"image/png"}`). Base64 PNG never
  goes into the string Content. No new `resources/read` protocol is needed.

### Permissions: `images × fetch`, migration without expansion

There is no new tool switch. The permission matrix gains the `images` domain with
`find`/`fetch` support, and `view` leaves the MCP verb list (six verbs remain:
`find`/`fetch`/`add`/`put`/`cite`/`send`). Migration converts existing `view` grants
and `texts.view` limits into `images.fetch` grants with one hard rule: users with no
explicit view allowance must not gain image access through migration — they get an
`images × fetch` denial. `McpServerSettingsService` previously added a DISABLED entry
for `patchouli.view` when migrating old configs; that entry is removed by the migration.

Untouched by this decision: the plugin registry's `view` verb (plugin
`allowed_verbs`, `DeclaredVerbs`) and the plugin UI `view` authorization in
`PluginViewSession.cs` — allowing an agent to read a plugin's text resource does not
imply allowing the desktop app to load its HTML view. A future rename of that UI
permission is out of scope. `ViewPluginAsync()` goes away with the tool because plugin
`views/{view-id}` descriptors are already fetchable, host-synthesized, and need no
plugin process; `[[ui.views]]` registration and the UI View loading path stay. This
supersedes ADR `0056`'s "`fetch`/`view`" wording for view descriptors: `fetch` alone
returns them now (ADR `0056` itself is a historical record and is not rewritten).
ADR `0045`'s image-output boundaries (geometry exception for the crop range, PNG caps)
carry over under `fetch` where they described image output.

### CLI: image saving moves from `view` to `fetch --output`

`patchouli-cli fetch patchouli://images/<document-id>/page-<n>.png --output <image.png>
[--overwrite]` performs the same decode / PNG validation / temp-file write / atomic
move the old `view --output` did. `--output` is required for images/ URIs; fetching one
without it is a usage error. The `view` command now fails with a usage error pointing
at the `fetch` replacement (same pattern as the retired `ocr` command).

## Consequences

- Agent skill docs and the MCP protocol doc describe six verbs; `images/` is a seventh
  VFS root beside `texts/` (the root discovery list grows from six to seven entries).
- The `find --long` projection gains an optional `image_uri` on page/evidence/translation
  entries. It is an explicit exception to the V3-AC13 "never repeat what the URI
  expresses" rule: `image_uri` names a different resource (that page revision's image)
  and pins the revision the text actually came from.
- No generic binary-file / plugin-binary / universal image framework is built here.
  Only `images/` PNG page rendering now; other binary resources extend the same
  contract later if needed.

## Alternatives considered

- Keep `view` as a thin alias over the new domain: rejected — an alias keeps two verbs
  for one read, keeps the tool-switch/permission surface split, and keeps every new
  caller learning both.
- Put the image bytes behind `resources/read`: rejected — `patchouli.fetch` already
  returns native image blocks, so no new protocol is needed.
- Mirror images under `translations/`: rejected — a translation page's image is its
  source page's image, and the mirror is what produced the unparseable-`rev` bug.
