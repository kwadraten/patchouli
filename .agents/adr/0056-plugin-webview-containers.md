# 0056. Plugin WebView containers for declared `[[ui.views]]`

- Status: Accepted by user direction as part of issue #8. Implementation and acceptance are in
  progress in the same change; this records the target contract, not a completed acceptance run.
- Date: 2026-11-07
- Product scope: [issue #8 — 插件化架构改造](https://github.com/kwadraten/patchouli/issues/8)
  (plugin view contract posted 2026-11-07).

## Context

Plugin manifests previously exposed only the static stdio MCP surface (`find`/`fetch`/`send` on
declared VFS nodes and endpoints). Issue #8 now lets a plugin declare UI views in
`patchouli-plugin.toml` so that third-party packages can contribute their own HTML surfaces —
e.g. a reader panel or a PDF-side tool — without the host learning each plugin's UI technology.
Two constraints frame the decision:

- Patchouli's own UI is deliberately native (the README's "no webview" positioning refers to the
  application's own interface). A plugin-authored surface is plugin content, not host UI, so a
  scoped WebView does not contradict that positioning as long as the boundary is explicit.
- A WebView must not become an implicit privilege or safety claim. Plugins are already trusted
  same-user installs (the store verifies signatures and hashes, neither of which is a sandbox),
  but the contract still should not hand a web page host objects, tokens or ambient authority.

The choice of embedded browser was between bundling a web engine (CEF/WebView2 Evergreen
redistributable), using a pure-Avalonia HTML renderer, or `Avalonia.Controls.WebView` 12.1.1
(MIT), which delegates to the platform-native engines: WebView2 on Windows, WKWebView on macOS
and WebKitGTK on Linux. The first adds a large bundled runtime; the second cannot run arbitrary
plugin HTML/JS faithfully. The platform-native delegation keeps the distribution small and gives
plugin pages a real browser engine.

## Decision

### Adopt `Avalonia.Controls.WebView` for plugin-authored surfaces only

Use `Avalonia.Controls.WebView` 12.1.1 (MIT) as the single plugin view renderer. It carries no
bundled Chromium: rendering uses the platform's own engine (WebView2 / WKWebView / WebKitGTK),
which the user already has or can obtain through normal OS channels. The host's own UI stays
fully native Avalonia; WebViews are scoped strictly to plugin-declared `[[ui.views]]` content.
This reconciles the README positioning rather than revoking it. The WebView is explicitly **not
an OS sandbox** — it isolates no privileges the plugin process does not already have.

### Manifest and resource contract

`patchouli-plugin.toml` may declare `[[ui.views]]` entries:

- `id` — unique within the plugin, matching `^[a-zA-Z][a-zA-Z0-9_-]{0,63}$`.
- `title` — display title.
- `placement` — one of `tab`, `window`, `pdf-sidebar`.
- `source` — the path of a declared `kind="file"` VFS node whose `verbs` include `fetch`; the
  node supplies the view's HTML.

`views` becomes a host-reserved mount path alongside `plugin.yml`, `README.md` and `files`;
plugins cannot declare it themselves. `patchouli://plugin/{id}/views/{view-id}` is a
host-synthesized resource: `find` lists `views/` and `views/{view-id}` with entry type `view`,
`plugin.yml` carries the declared views array, and `fetch`/`view` on the URI return the JSON
descriptor `{uri, plugin_id, view_id, title, placement, source_uri}` without starting the
plugin process. `view` joins the device `allowed_verbs`, the community `requested_verbs` and
the manifest `DeclaredVerbs` when views exist; `plugin`/`plugin/{id}` permission domains accept
`view`, while `put`/`add`/`cite` stay rejected on plugin addresses. MCP `patchouli.view` on a
view URI returns the descriptor as a text-only envelope with no image block — the image-read
semantics of `view` for library page/box URIs are unchanged — and `view` on any other plugin
path is rejected. See [mcp-protocol](../protocols/mcp-protocol.md), revision
`plugin-views-v1`.

### Three desktop containers

- `tab` opens a retained workspace tab (kind PluginView), deduplicated per plugin/view:
  reactivating the same view URI selects the existing tab instead of duplicating it.
- `window` opens a non-modal popup window; activating the URI again reactivates the existing
  window rather than opening a second one.
- `pdf-sidebar` appears as a selectable tab inside the PDF workspace right sidebar. It reuses
  the existing sidebar show/hide behavior and the 200–800 px splitter, and is hidden in
  box-edit mode like the other sidebar tabs. With no active PDF workspace, URI activation
  falls back to the `tab` container.

Opening entry points are in-app/URI activation of `patchouli://plugin/{id}/views/{view-id}`
(virtual links follow ADR `0029`) and the PDF sidebar tab flyout, which lists authorized
pdf-sidebar views.

### View content delivery

View HTML is fetched by the host through `patchouli.plugin.fetch` on the declared `source`
node. The host never reads package files directly for view content and never caches view
HTML: each container materialization goes through the plugin's declared fetch surface, so a
plugin controls what it serves. When the plugin is offline, disabled or unauthorized, the
container shows an explicit unavailable state instead of a stale or faked page.

### Host→view context injection

On webview creation and on every change, the host injects:

- `window.patchouli.theme = { dark, colors { all palette variables } }`, mirrored as
  `--patchouli-color-*` CSS custom properties and `color-scheme` on `:root`, plus a
  `patchouli:theme` DOM event.
- For `pdf-sidebar` views, `window.patchouli.page = { uri }` holding the canonical
  `patchouli://texts/{doc}/page-N.xml?rev=…` link of the current page, pushed on page,
  revision and document changes, plus a `patchouli:page` DOM event.

View JavaScript may call `invokeCSharpAction('{"type":"patchouli:request-context"}')` to ask
the host for a re-push. No host tokens, objects or bridges are exposed beyond this injected
context. External `http(s)` links open the system browser; only `about:` and `data:`
navigations are allowed in place.

## Consequences

- The `view` verb now has two delivered meanings: source-image reads on library page/box URIs
  (unchanged, MCP native ImageContent) and descriptor reads on plugin view URIs (text-only).
  Plugin `put`/`add`/`cite`, managed `files/`, URI resolve, plugin library grants and
  background plugin actions remain undelivered.
- Plugin pages run plugin HTML/JS in a real browser engine with the host-context injection
  above. This is a capability surface, not a containment boundary; store signature/hash
  verification likewise is not a security guarantee for plugin code.
- A plugin view is only as available as its plugin process and `fetch` declaration; descriptors
  remain readable without the process, so discovery never fails merely because a plugin is not
  running.
- Packaging follow-ups are deferred: the Windows installer should check/prompt for the WebView2
  runtime, and Linux packages should declare WebKitGTK dependencies. Until those land, missing
  engine dependencies surface as the container's unavailable state, not an install blocker.
