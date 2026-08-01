# Adopt the Structured MCP Resource Protocol

Status: accepted (2026-07-31); supersedes ADR `0022` for production MCP

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
- `patchouli.put` is reserved for the narrow, revision-gated writes defined by ADR `0023`.
- `patchouli.cite` renders citations from permitted item/document/page/evidence
  references. Document and page references resolve through the persistent
  `document_instances.item_id` relation; page ownership is validated before
  resolution. When no style is supplied, it uses the user's configured default
  CSL style. If that style is unavailable, it may use a deterministic enabled
  fallback and must return the effective style plus a warning. If no configured
  or fallback style exists, citation rendering fails.

The .NET application services remain the only domain authority. CLI and MCP
must converge on the same URIs, JSON responses, validation, permissions,
revisions, and error codes. The Bashkit sidecar and virtual shell are removed
entirely from the repository's `main` branch: implementation, tests, packaging,
and documentation. They survive only on the `feature/mcp-ab-benchmark` branch
as historical evidence; the benchmark A condition is available only through
that branch.

`patchouli.put` remains unavailable or disabled until its atomic item/style
replacement implementation satisfies ADR `0023`. No temporary write path is
acceptable.

## Consequences

The structured surface avoids a persistent stateful shell protocol. The
benchmark remains regression evidence, not a production dependency. The
current bridge still needs document, page, style, evidence, range, revision,
and atomic write support before V3-T1 is fully accepted.
