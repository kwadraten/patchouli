# 0044. Native MCP Resource Formats and Box Writes

Status: accepted by user direction, 2026-10-15.

## Decision

Use YAML for native CSL/application item metadata and the Library resource, and XML wrapping
existing box payloads for source and translated pages. Keep the TOON/JSON tool envelope and
the separate texts/translations domains. The runtime Box Tree is authoritative; XML is a
projection, not the original MinerU interchange tree or a new persisted document model.

Allow atomic, same-page box payload replacements. Source writes create a new committed tree
revision and document commit; translation writes update selected derived rows. New writes
require source-version checks, plus translation-version checks for translations. Metadata,
structure and geometry are preserved. Active UI drafts prevent source writes. Existing user
tool/domain controls apply, including a texts.put capability.

Normalize root-relative and complete VFS addresses before permissions or resolution. Return
only complete canonical `.yml`/`.xml` links. Retain the old `.bib`, `.md`, and `library.toon`
interfaces and their body formats with an `[obsoleted]` response warning explicitly stating
that the old interface will be deprecated in **0.4.0**, and pointing to its replacement URI.
The marker is response metadata rather than resource content. Compatibility removal is a
separate 0.4.0 change; existing historical evidence remains readable during migration.

## Superseded constraints

This amends ADR 0023/0024 restrictions on whole-resource-only, no-precondition writes and
read-only source text; ADR 0028 evidence URI suffixes; and ADR 0034 Markdown-position-based
translation submission. Their single-host, immutable-history, derived-translation storage,
user-controlled permissions and no-path/secret/image/OCR/index-action boundaries continue.
Default search remains a committed-tree projection, with source updates marking it stale.

The normative schema, compatibility behavior, limits and error contract are maintained in
[mcp-protocol.md](../mcp-protocol.md). An unchanged payload roundtrip must preserve fields
outside the editable projection. Tests cover canonical output, alias permissions, legacy
warnings, suppressed footnotes, source history, optimistic concurrency and batch rollback.

## Parallel implementation plan

Execution uses three Luna fast agents in parallel: YAML and source writes; XML and translation
writes; and MCP/CLI/SDK/workflow interfaces. The primary agent owns shared contracts, VFS
integration, review, documentation coordination, and the consolidated cleanup/test/inspection
gate. Builds and cleanup run centrally to avoid concurrent changes to shared build outputs.

Implementation can proceed in four file-isolated workstreams after the shared request/response
DTO and tool schema are agreed. Shared contracts are a short integration boundary; no workstream
should independently redefine them.

1. **Resource identity and codecs:** canonical URI normalization and legacy alias metadata;
   YAML item/Library codecs; XML source and translation projections; safe XML range reads.
2. **Write services:** source box batch persistence through committed tree history, translation
   box batch persistence, version checks, active-draft refusal, permissions, and atomic rollback.
3. **Interfaces:** MCP schema and protocol validation, CLI batch-file/stdin adapters, Agent SDK
   URI and typed helpers, and built-in workflow prompt migration to canonical XML and box APIs.
4. **Integration and contracts:** canonical/legacy URI behavior, warnings, output formats,
   version conflicts, batch limits, rollback, SDK replay, CLI mapping, and host-level regression.

Each stream first lands focused tests with its implementation. Integrate at the shared-contract
boundary, then run the full host and interface suites together. The release gate checks that new
links are canonical, legacy requests return canonical links plus the 0.4.0 warning, aliases are
normalized before authorization, and source changes never write whole-page XML.
