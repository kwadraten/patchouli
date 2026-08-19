# Adopt the Structured MCP Resource Protocol

Status: accepted (2026-07-31); amended 2026-08-01; supersedes ADR `0022` for production MCP

## Context

Patchouli compared the Bashkit virtual shell (A) with structured protocol B
using the pinned `gkamradt/needle-in-a-haystack` UUID-chain recipe. Evidence is
preserved on `feature/mcp-ab-benchmark` at commit `083b0b5`.

The 50 completed complex-task records used one persistent library session per
condition. B completed 25/25 tasks (100%), averaging 6.68 tool calls, 1.92
tool errors, and 14.2 seconds. A completed 11/25 tasks (44%), averaging 41.16
tool calls, 34.16 tool errors, and 49.4 seconds. A's persistent shell session
entered repeated error loops, and restarting it would not represent the
production UI/library lifetime.

## Decision

Adopt B as the single production MCP surface:

- `patchouli.find` discovers and searches text-only Library resources.
- `patchouli.fetch` retrieves a known resource without implicit search or link following.
- `patchouli.put` is reserved for the narrow, atomic whole-resource writes defined by ADR `0023`; it has no base-revision precondition.
- `patchouli.cite` renders citations from permitted item/document/page/evidence
  references. Document and page references resolve through the persistent
  `document_instances.item_id` relation; page ownership is validated before
  resolution. When no style is supplied, it uses the user's configured default
  CSL style. If that style is unavailable, it may use a deterministic enabled
  fallback and must return the effective style plus a warning. If no configured
  or fallback style exists, citation rendering fails.
  The public projection has no `citation_target`: callers pass any
  `citable=true` URI directly, and the host resolves it internally to its
  citation Item.

This is a deliberate resolution of a painful interface triangle; no prior
approach can simultaneously preserve all three properties:

1. leave upstream shell/coreutils behavior untouched so it remains natively
   familiar to agents;
2. enforce read/write authority at the Library boundary; and
3. optimize virtual-file operations for a database backend so built-in commands
   remain fast.

The historical Bashkit approach chose (1) + (2) and sacrificed (3): every
logical file operation crossed IPC, while its claimed native fidelity was only
simulation (for example, `grep` flag and stdin behavior already diverged).
Materializing Library content into a real directory would choose (1) + (3) but
sacrifice (2): data escapes the authority boundary, database state is flattened,
and a read-only restriction cannot be reliably enforced. The structured
discrete-domain-tool surface chooses (2) + (3): authorization and database
optimization occur in the same call, at the explicit cost of abandoning Bash as
the interface shape. This—not a preference against shell syntax—is the reason
the Bashkit production path is retired.

The desktop UI, `patchouli-cli`, and MCP are three operation interfaces over
one Library **runtime host**, which is the sole authority for the runtime
database, cursors, revisions, projections, validation, and writes:

- The desktop host contains the full human UI and a local MCP HTTP endpoint.
- MCP is the agent-only collaboration interface, usable locally or remotely
  through that endpoint.
- `patchouli-cli` is a thin client for the local MCP HTTP endpoint, for humans
  and agents. It does not open SQLite directly or implement a second Library
  domain surface. Its four resource verbs map directly to the four MCP tools.

CLI first discovers a host for the selected Library. If the desktop host is not
running, CLI launches the same binary as a background, UI-less headless host,
waits for its local MCP HTTP endpoint, and then sends the request. The headless
host remains a daemon. A Library has exactly one host at a time, enforced by
host discovery state and a Library lock; launching the desktop for that Library
terminates the headless host and takes over database ownership. Desktop and
headless hosts share MCP settings: in particular, a headless `0.0.0.0` bind
requires a token just as a desktop-host bind does. An optional CLI host
lifecycle command (for example `serve-mcp`) is separate from the four resource
verbs and is not an MCP tool.

The production discovery tree is limited to `patchouli://items/`,
`patchouli://texts/`, and `patchouli://csl-styles/`. It has no discoverable
evidence root. A text page URI is
`patchouli://texts/{document-instance-id}/page-{page-index}.md`, where
`page-index` is the stable, one-based physical PDF page number within that
DocumentInstance. Evidence is consumed through the page URI's `?evref=` query;
fetching or citing it validates that the EvidenceRef belongs to the declared
document and page, and returns `NOT_FOUND` when it does not.

A `find` query in `patchouli://texts/` returns one result per matching
SearchUnit, not a document-level aggregate that hides the evidence identity.
The default result `uri` is the matching page's canonical `?evref=` URI, so an
agent can fetch that evidence or supply the URI directly to `cite` without
requesting a detailed projection or reconstructing an evidence URI.

> Note: the `?evref=` token shape and EvidenceRef resolution semantics are
> superseded by ADR `0028`; production evidence URIs use `?rev=&box=`.

TOON v3.0 is the default compact textual projection. The production encoder and
decoder are the MIT `Corvus.Toon.SystemTextJson` NuGet package; Patchouli does
not maintain a custom TOON implementation. Encoding fixes UTF-8/LF, literal TAB
as the tabular delimiter, and `KeyFolding=Off`. Corvus applies the TOON v3
lexical quoting and escaping rules while numbers, booleans, and null retain their
strict JSON types. The exact package release is pinned and must pass
byte-for-byte fixtures for that profile; an unsupported release is not an excuse
for a custom parser, encoder, or semantic string post-processing.

There is an explicit trade-off: research reports reduced structural correctness
for TOON on models without native support, and its own agentic evaluation finds
that multi-turn parsing failures can cascade. Patchouli limits TOON to the
agent's **read** path—MCP tool calls and `put` input remain ordinary structured
JSON—and keeps `format=json`/`--json` as an equivalent no-TOON fallback. This
removes the model-generated TOON-call failure mode but does not claim to remove
result-comprehension risk; the fallback remains part of the contract. See
[Kutschka and Geiger, 2026](https://arxiv.org/html/2605.29676v2).

CLI `--json` and MCP `format=json` are semantically equivalent JSON projections
for batch agents and other programming-language clients; choosing JSON never
requires parsing TOON and never changes fields, pagination, warnings, errors,
or the requested default/detailed projection. TOON and both JSON projections
share the PRD's closed response shape: `meta`, `continuation`, optional
`message`, and `entries`; there is no separate `data` envelope. `message`
contains stable warning codes and/or the request-level error, and is omitted
for a clean success. CLI help and the MCP initialize response state this Unix
convention explicitly: no `message` is the success signal, rather than an
empty object or a human-readable `OK` text.

The implementation removes the legacy serialized `McpEnvelope<T>.Revision`
field and migrates all clients and contract tests to `meta.library_revision`.
This is an observation of the current Library state, not resource history.
Until a later version-control ADR defines the complete model, the public
protocol has no `fetch --revision`, `resource_revision`, or other resource-level
historical-version selector.

`find` pagination uses opaque, stateless, real-time cursors. A cursor binds the
scope, query, filters, ordering, and continuation position, but does not retain
a materialized result set, server-side handle, TTL, or agent-specific namespace.
Every page is evaluated against the Library as it exists at the time of that
call. A response that emits or consumes a continuation therefore includes
`RESULT_SET_MAY_HAVE_CHANGED` in `message.warnings`: entries and totals can
drift between pages, including repeats or omissions caused by concurrent UI,
CLI, or agent writes. Snapshot-consistent multi-page traversal is deliberately
out of scope.

`meta.library_revision` is the persistent Library revision, formatted
`lib:<positive decimal integer>`. It strictly increases after every successful
Library write that changes protocol-visible resources or relations, and does not
reset when desktop and headless hosts hand off. A
fetched resource is only a client-side snapshot at that revision: MCP does not
push invalidations or retract past response entries. A real-time cursor continues
after a change with `RESULT_SET_MAY_HAVE_CHANGED`; when an MCP session's last
observed revision is stale, its next response additionally places
`LIBRARY_CHANGED_SINCE_LAST_RESPONSE` in `message.warnings`. This warning model
deliberately does not restore a `put` base-revision condition.

`patchouli://` resolves only to the current Library fixed for the lifetime of
the handling host. Although the current desktop UI cannot switch Libraries,
the resolver must compare a cursor, EvidenceRef (`?evref=`), or future explicit
Library context's embedded Library binding with the host `library_id`. On a
mismatch it discards any prepared entries and returns `NOT_FOUND`; it must
never return entries, partial entries, or a citation from a different Library.

Regular-expression search is deliberately absent from the CLI/MCP protocol. The
structured `find` scope × query/`--literal`/filter matrix in the PRD is the
single authority for supported combinations; an unsupported combination,
including `--regex`, returns `INVALID_ARGUMENT`. Agents that need regex
matching perform it locally on text returned by `find` or `fetch` rather than
turning the MCP service into a virtual shell or grep endpoint.

`find` normalizes recoverable boundary inputs rather than creating server state:
whitespace-only queries browse; a known file URI is a singleton discovery scope;
root discovery can be paged; and a continuation cursor restores its embedded
context when conflicting request values are supplied. `where` splits on its
first `=` and uses the last value for a repeated key. Each normalization emits
the corresponding stable PRD warning in `message.warnings`; an invalid cursor
or unsupported matrix combination still returns `INVALID_ARGUMENT`.

All writes, including desktop UI writes, flow through the host write service;
each successful commit emits its resource-changed notification for a connected
desktop UI. CLI/MCP parity is therefore structural rather than a comparison of
two domain implementations: contract tests verify CLI parsing to MCP request
mapping, plus the shared server response schema and error codes. The host owns
bind, CORS, tokens, enabled tools, validation, and write policy for every
frontend. The Bashkit sidecar and virtual shell are removed entirely from the
repository's `main` branch: implementation, tests, packaging, and
documentation. They survive only on the `feature/mcp-ab-benchmark` branch as
historical evidence; the benchmark A condition is available only through that
branch.

The host applies deliberately generous deadlines: 60 seconds by default for
`find`, `fetch`, and `cite`, and 120 seconds for `put`, including validation.
Timeout returns `DEADLINE_EXCEEDED`, not a timeout-shaped partial success. MCP
cancellation, HTTP disconnect, and CLI interruption propagate to the host. A
cancelled `put` before its atomic commit point returns `CANCELLED` and writes
nothing; after that point the host completes the atomic commit or rollback, so
the client must re-fetch after a disconnect rather than infer a partial state.
Unexpected host, database, or internal-helper failures map to the PRD's stable
`INTERNAL` error code; responses may carry a correlation id but never raw
exception details, stacks, local paths, or secrets.

`patchouli.put` remains unavailable or disabled until its atomic item/style
replacement implementation satisfies ADR `0023`. No temporary write path is
acceptable.

## Consequences

The structured surface avoids a persistent stateful shell protocol. The
benchmark remains regression evidence, not a production dependency. The
current bridge still needs document, page, style, evidence, range, revision,
and atomic write support before V3-T1 is fully accepted.
