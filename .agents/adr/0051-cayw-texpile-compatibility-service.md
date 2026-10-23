# 0051. CAYW / TexPile compatibility service as an in-host adapter

Status: accepted and implemented, 2026-10-22. Issue #14.

Patchouli exposes a Better BibTeX "Cite As You Write" (CAYW) compatibility
subset and the two TexPile JSON-RPC methods (`item.search`, `item.export`) so
editor integrations written against Zotero + Better BibTeX can cite from a
Patchouli library. The service is an adapter inside the process that already
owns the Library host — it is never a second host. ADR 0024's rule stands: a
Library has exactly one host at a time, enforced by the runtime-host lease;
the desktop takes over from the headless host exactly as it does for MCP, and
the interop listener moves with that ownership. Starting a second Host to
answer a CAYW request is forbidden.

## Shape

- Transport: one loopback-only Kestrel listener bound to `127.0.0.1`
  (default port 23119, BBT's port), opt-in and off by default, configured by
  the device-local `CitationInterop` settings section (never part of a
  Library snapshot). It validates the `Host` header and any `Origin` header
  against loopback, sends no CORS headers, caps request body (64 KiB), query
  string (2048 chars) and batch sizes, and answers unknown routes/methods
  with stable errors. A bind failure (typically Zotero already on 23119) is
  surfaced verbatim in the settings page; the port is never silently changed.
- Layering (mirrors the MCP split): Core owns the protocol-neutral pick
  contract (`CitationPick`), the compatibility constants, the pure-text
  formatters and the classic BibTeX mapper; the Host owns the
  `CitationInterop` module (search/resolve/format/export composition, the
  request gate and lifecycle, the HTTP transport) and defines the desktop
  bridge interface; the UI implements the bridge (picker window, selection
  snapshot, clipboard, window minimize/activate) and the settings page.
- CSL: formatted output goes through the existing `Fsharp.Citeproc`
  processor. The renderer contract gained a cluster operation that passes
  per-item locator/label/prefix/suffix/suppress-author and reads the inline
  `citations` output; citation-only styles render clusters with an empty
  bibliography instead of failing. The bibliography operation keeps its
  existing validity rules and callers. Issue #9's language localization
  pipeline applies to cluster rendering unchanged.
- BibTeX: `Better BibTeX` export is a real classic mapping (article →
  journal, date → year/month, publisher → school/institution for theses and
  reports, unknown types → misc), with per-field information-loss warnings.
  A BibLaTeX document is never relabelled as classic BibTeX. Classic output
  is serialized by a small .NET serializer, NOT by the biblatex-helper
  writer (ADR 0021): probing the helper binary showed its write path
  canonicalizes `journal`/`address`/`school` to
  `journaltitle`/`location`/`institution` (BibLaTeX names classic BibTeX
  silently ignores, with school/institution colliding when both are
  present). The helper remains the independent parser used to validate the
  classic output in tests. No Rust, DTO or packaging change was needed.

## Compatibility contract

Verified against the public BBT CAYW documentation (retorque.re, read
2026-10-22): format names and aliases (natbib/latex/cite/citep/citet,
biblatex, pandoc, typst, json, formatted-citation, formatted-bibliography),
the probe body `ready`, flag truthiness (a flag is set by ANY non-empty
value — the string "false" included; generic boolean parsing is a bug
here), natbib's combined-vs-split command behavior and year variants,
biblatex's starred single-item suppress-author and `s`-suffixed multicites,
the default locator-label abbreviation table with per-request overrides,
and the JSON-RPC method shapes and translator names (`Better BibLaTeX`,
`Better BibTeX`). TexPile omits the JSON-RPC `id`; the endpoint answers
requests without an id with a result envelope (`"id": null`) rather than
treating them as notifications, because that is what TexPile parses.

Assumed where public sources do not pin the behavior down (recorded here so
a future upstream check can tighten them):

- A CAYW request without `format` defaults to `latex`.
- The Typst mapping (`@key` shorthand; `#cite(<key>, form: "year",
  supplement: [...])` with prefix/locator/suffix folded into the
  supplement) follows Typst's own citation model; the BBT Typst formatter's
  exact output was not publicly verifiable.
- The `json` pick output is the item's CSL-JSON projection plus the pick
  fields; the projection's `id` is the Patchouli item id and `citekey`
  mirrors `citation-key`. Patchouli does not mint Zotero numeric ids or
  zotero.org URIs, and says so in the settings page.
- `formatted-citation` returns plain text unless the client passes the
  `html` flag; error responses are HTTP 500 with a stable plain-text
  message, matching BBT's user-visible failure mode.
- `item.search` is the common free-text search over title, citation key,
  creators, identifiers, publication title, the item date text and issued
  dates, capped at 100 results server-side; it is not equivalent to BBT's
  full-library search, and the docs do not claim otherwise.
- Probe reports ready only when an interactive picker exists. A headless
  host answers 503 with an explanation; its JSON-RPC subset still works.

Deliberately unsupported, each with an explicit error instead of a silent
fallback: the stateful POST picker, Eta, scannable-cite, TeXstudio push
(`texstudio` parameter), mmd, jupyter, asciidoctor-bibtex and the full
translate set. The surface is documented as a "common CAYW compatibility
subset", never as full Better BibTeX compatibility.

## Request lifecycle

One interactive pick at a time: additional interactive requests fail
immediately with a diagnosable `busy` error (the HTTP request stays the
synchronization point; no queue). A pick has its own interactive deadline
(default 10 minutes, independent of API timeouts). User cancellation or
closing the picker is a normal empty result. Client disconnect, deadline,
host shutdown and library switch cancel the pick and close the window,
release the gate, and complete the request at most once; a late
confirmation after disconnect produces no output and no clipboard/selection
side effects. `selected` snapshots are taken on the UI dispatcher and are
immutable. Picks are re-validated against the current Library at confirm
time (items must still exist, be active and be citable); citation keys are
re-resolved from the Library, never trusted from the picker. One endpoint
serves exactly the Library its host owns.
