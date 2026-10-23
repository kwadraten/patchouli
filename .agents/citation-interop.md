# Citation interop (CAYW / TexPile) protocol

Issue #14, ADR 0051. The normative behavior of Patchouli's Better BibTeX
CAYW compatibility subset and TexPile JSON-RPC subset. The service is off by
default; enable it under 设置 → 引用与题录 → Cite As You Write. It binds
`127.0.0.1` only (default port 23119), validates `Host`/`Origin` against
loopback, and serves exactly the Library owned by the hosting process
(desktop or headless — ADR 0024 single-host rule).

## GET /better-bibtex/cayw

All flag parameters follow the BBT rule: a flag is set when present with ANY
non-empty value (the string `false` counts as set).

| Parameter | Meaning |
| --- | --- |
| `probe` | Flag. Returns the plain-text body `ready` when an interactive picker is available; 503 with an explanatory body on headless hosts. Never opens the picker. |
| `format` | One of the formats below. Default `latex`. |
| `command` | natbib/biblatex command name (letters only). Defaults: natbib `cite`, biblatex `autocite`; aliases imply `cite`/`citep`/`citet`. |
| `brackets` | Flag (pandoc): wrap the pick in one bracketed citation. |
| `selected` | Flag: format the desktop's current library selection (dispatcher snapshot) instead of opening the picker. Headless: `ui_unavailable`. |
| `clipboard` | Flag: after a confirmed pick, also write the result to the clipboard. |
| `select` | Flag: after a confirmed pick, also select the picked items in the library view. |
| `minimize` | Flag: after a confirmed pick, minimize the main window. |
| `style`, `locale` | CSL style id / locale for the formatted formats. |
| `html` | Flag: `formatted-citation`/`formatted-bibliography` return HTML instead of plain text. |
| `<label>` | Any locator label name (e.g. `page`, `chapter`) overrides its abbreviation; an empty value removes the label. |
| `texstudio` | Flag: explicitly unsupported — stable error (TeXstudio push is not implemented). |

Formats: `natbib` (aliases `latex`, `cite`, `citep`, `citet`), `biblatex`,
`pandoc`, `typst`, `json`, `formatted-citation` (inline CSL cluster),
`formatted-bibliography` (CSL bibliography). Unsupported formats
(`mmd`, `jupyter`, `asciidoctor-bibtex`, `scannable-cite`, `eta`,
`translate`) and unknown formats fail with HTTP 500 and a stable plain-text
message; keys that cannot be expressed in a format are explicit errors —
keys are never silently rewritten. POST to this path is 405 (the stateful
POST picker is not supported).

Lifecycle: one interactive pick at a time (extra requests fail immediately
with a `busy` message); a 10-minute interactive deadline; user cancel /
window close is an empty 200 response; disconnect, deadline, shutdown and
library switch cancel the pick, and late confirmations have no side
effects. Picks are re-validated at confirm time (active + citable, keys
re-resolved from the Library).

## POST /better-bibtex/json-rpc

JSON-RPC 2.0 envelope; a request without `id` still receives a result
envelope with `"id": null` (TexPile behavior). Only two methods:

- `item.search` — params `[query]` (a single text string; condition objects
  are rejected with -32602). Returns CSL-JSON projections (verbatim CSL
  variables) with `citekey` mirrored from `citation-key` and `id` set to the
  Patchouli item id. Empty query returns the most recent items. Server-side
  cap: 100 results; general items excluded.
- `item.export` — params `[citekeys, translator]`, translator exactly
  `Better BibLaTeX` or `Better BibTeX`. All-or-nothing: a missing,
  trashed, merged or general key, or an unsupported translator, fails the
  whole batch (-32000 with the stable Patchouli error code in the message).
  Duplicates are de-duplicated at first occurrence; output order follows
  the request. `Better BibTeX` is the classic mapping of ADR 0051 (lossy
  field drops are recorded as mapper warnings, not silently hidden).

Error codes: -32700 parse, -32600 invalid request, -32601 method not
found, -32602 invalid params, -32000 domain failure, -32603 internal.
Request body cap 64 KiB. Error messages never contain host paths,
credentials or stack traces.
