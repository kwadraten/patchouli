# Extension contracts

This is the implementation contract for the trusted compiled-FSX extension redesign in
[ADR 0053](../adr/0053-trusted-compiled-fsx-extensions.md). It describes the intended contract;
implementation and acceptance are in progress, not declared complete. Earlier operational
evidence remains in [Extension validation](extension-validation.md), and does not establish
acceptance of this redesign.
Author-facing guidance (SDK usage, nullability rules, examples) lives in the distributed skill's
[EXTENDING reference](../skills/patchouli/references/EXTENDING.md); keep it consistent with this contract.

| Boundary | Extension owns | Host owns | Required failure behavior |
| --- | --- | --- | --- |
| Package | Source files, implementation, stable extension ID | Immutable snapshots, fingerprint, compiler, generation leases and compiled-DLL cache | Invalid candidates leave Active usable |
| Configuration | Attributed records, stable keys, defaults, ranges and descriptions | Non-executing metadata inspection, generated forms, stored values, run snapshot | Invalid schemas reject candidates; explicit stable keys preserve saved values |
| Resources | Choice of libraries, network clients and business policy | Convenient HTTP/LLM resources, model resolution and plaintext credential lookup | Explicit unavailable selections fail; no extension network approval or sandbox is claimed |
| Pipeline | Segmentation, composition, batching, retry classification | Scheduling primitives, request limits, pause and cancellation | Errors short-circuit; cancellation reaches waits and requests |
| Progress | Step names and input identities | Execution identities, elapsed time, deduplicated counts and observer isolation | No percentage without a known total; no default body or credential logging |
| Translation | Whole-task page order, cross-page batching, concurrency, retry policy, service protocol, alignment, candidate output | Page identity and revision checks, filtering pages requiring translation, frozen context snapshots, per-page atomic commits, progress reporting, and cancellation | Wrong identities, invalid payloads, or revision drift reject commit; a page settles once, and re-commit is valid only after Conflict+RetryPage; uncommitted pages settle as Failed/Cancelled on job completion |
| Metadata | Identifier lookup and candidate content | Read-only proposal, expected-version check, atomic metadata/identifier application | Concurrent edits conflict; invalid identifiers cannot leave partial metadata |
| OCR | Recognition and normalization to MinerU content semantics | MinerU-based shared model, input evidence, geometry/ownership checks, import commit | Provider-specific shapes cannot bypass normalization; candidate generation does not write document trees |
| Retrieval | Query and ranking policy | Source revision/Box mapping, rebuildable derived index, evidence rules | Stale or foreign evidence cannot be promoted into authoritative source content |

## Authoring

Package identity is authored once in the `///` XML documentation attached to the
column-zero top-level `let extension` binding in `main.fsx` (ADR
[0052](../adr/0052-fsx-entrypoint-metadata-and-configuration-reads.md)). It declares id, kind,
package version, Host API, name and summary. The loader projects the runtime descriptor from
these values; authors do not capture an ambient metadata context. `extension.yml` is rejected with a migration
diagnostic; immutable Active/LKG snapshots are not rewritten. Helper FSX source files remain
allowed. Parsing identity is source-aware, bounded and non-executing; it does not replace compilation.

Configuration uses an attributed settings record. `MetadataLoadContext` and attribute metadata
provide fields without executing package code. The compiler enforces constant attribute arguments;
the schema reader validates record fields, defaults, ranges, keys and common-settings overrides.
Bindings and model selections are resources/configuration, not permission declarations.

The build tool `scripts/Patchouli.ExtensionSchemas` links the production package factories and
embeds the same FSX resources, then uses the production compiler to produce ordinary DLL bundles
and one derived schema index. Infrastructure embeds that index as
`Patchouli.Infrastructure.Extensions.BuiltInExtensionSchemas.json`; intermediate JSON and
checking work stay under managed `obj` outputs. Identity includes the complete package source
hash, Host API and checker/reference assembly identities. This index is not another authored
manifest and cannot replace code or artifact validation. Missing or incompatible
descriptions produce an explicit unchecked/source-only configuration view, not a runtime check
or compiler launch hidden behind opening a settings dialog.

`|>>` composes cold typed steps. `Parallel.map` preserves input order while item execution
identities describe completion order. The shared host request gate bounds nested transport
concurrency. `Batch.packWithBudget` rejects oversized inputs so the engine must segment them
explicitly. `Batch.map` returns per-item results; `collect` requires all items to succeed,
while `collectPartial` explicitly retains partial failures. Retry classification excludes
cancellation and gives each attempt its own execution identity under one logical operation.

Progress completed counts describe the named collection or step, not relative execution
cost. Generated output and committed pages have separate events. Translation uses complete
responses without streaming or provisional content previews; generated candidates do not
establish that validation or commit succeeded.

## Task-level translation job

A translation run has exactly one extension entry point:
`ITranslationExtension.RunAsync(job, resources, cancellationToken)`. The extension
receives the whole run as an `ITranslationJob` and drives it; the host serves pages,
context and commits on request instead of dispatching pages. An extension must walk
`NextPageAsync` until it returns null and commit every page it took; pages the host settles
itself (unreadable, no translatable leaf box, already current in the target language and
not forced) are never returned to the extension.

Semantic summary of `ITranslationJob`:
- **`NextPageAsync(cancellationToken)`**: Returns the next physical page that still requires
  translation in request order. Page fetching is serialized by a single-consumer gate
  (`SemaphoreSlim`). The host settles pages internally and records their outcomes/progress
  without yielding them to the extension when:
  - Target `PageIndex <= 0` (settles as `Failed` / `InvalidArgument`);
  - Reading page revision, boxes, or translation status fails (settles as `Failed`);
  - The page has no translatable leaf boxes (settles as `Skipped`);
  - The translation is already current in the requested target language, has no stale boxes,
    and the run is not forced (settles as `Skipped`).
  When a translatable page is returned, the host emits a `page-translation` step event with
  `Started` and attempt 1. Returns `null` when no pages remain.
- **`ReadContextAsync(page, neighborRadius, cancellationToken)`**: Reads reference context on
  demand for a page. Adjacent source pages (|index difference| <= `neighborRadius`) are frozen
  in host memory on first read for the remainder of the job. The host also reads the stored
  translation of the previous page (`page.PageIndex - 1`) and verifies its current status,
  revision, and target language. `TranslationContextSnapshot.PreviousRunTranslation` is always
  `null` (sequential dependencies use `GetCommittedPageTranslationAsync` instead). Extensions
  that require no reference context never call this and incur no overhead.
- **`GetCommittedPageTranslationAsync(pageIndex, cancellationToken)`**: Queries the committed
  translation of a specific page index, checking the current run's committed cache first and then
  database storage. It validates `IsCurrent`, revision ID, and target language, returning
  `TranslationContextTranslationSnapshot` or `null`. Sequential translation policies use this to
  read the exact translation just committed by the preceding page.
- **`CommitPageAsync(result, cancellationToken)`**: Validates and writes page results in one
  atomic database operation (`PutPageBoxesAsync`):
  - Validates that `result.Page` matches an uncommitted page issued to this job with an unchanged
    revision; mismatched identity or duplicate commit attempts fail with `InvalidArgument`.
  - Validates outcomes: every translatable leaf box needs exactly one outcome; an unknown,
    non-translatable, duplicate or missing `BoxId`, or a `Translated` outcome without usable
    content, fails the page with `BatchMismatch` and writes nothing.
  - Builds payload replacements with structural validation (text, list, table, algorithm).
  - Returns `TranslationPageCommit`:
    - `Committed`: All translatable leaf boxes were successfully committed.
    - `Partial`: The page was committed with a mix of successful and failed/skipped boxes.
    - `Skipped`: No translatable leaf replacements needed writing.
    - `Failed`: All boxes failed or replacement validation failed.
    - `Conflict`: A concurrent write occurred (`AppErrorCodes.Conflict`). The host reloads the
      page; if the reloaded page still requires translation and conflict count < 2, it returns
      `TranslationPageCommitState.Conflict` with `RetryPage` carrying the reloaded input. The
      attempt's outcomes are not consumed, allowing the extension to re-translate and re-commit.
  - Every target page settles exactly once; a second commit of a settled page is invalid
    rather than a merge.
- **Progress reporting**: Step progress events set `ExtensionProgress.ItemId` to the string
  representation of the page index (`page.PageIndex.ToString()`). The host parses `ItemId` as an
  integer to populate `TranslationStepProgress.PageIndex` (defaulting to 0 on parse failure).

The bundled SDK provides the shared driver `Job.runPages` on top of these calls: bounded
page prefetch, cross-page grouping by `ContextKey`, bounded concurrency, per-page commit
with conflict reload, and a page-level retry budget with an explicit non-retryable
classification. A page marked `Sequential` uses a one-page window and is committed before
the next page is taken.

The compiled extension executes in a host-managed collectible `AssemblyLoadContext` (ALC);
there is no worker RPC boundary. The existing whole-task job contract remains host mediated.

## Host policy

Translation retains the task-level contract above. The extension owns whole-task page ordering,
cross-page batching, concurrency, and retry policy. The host owns page identity, revision
verification, filtering, frozen context snapshots, per-page atomic commits, progress and
cancellation. SDK records and compiler warnings remain validated. No legacy v2 compatibility
claim or API-version blacklist is part of the target contract.

OCR providers share the MinerU-based content model described in
[OCR normalization](ocr-normalization.md). Raw text and text-box results enter the
shared MinerU mapper; already typed candidates must obey the same payload, geometry,
and ownership rules. Box Tree supplies application identity and revisions. A typed
candidate is not permission to introduce a competing provider-specific data model.

Extensions execute as trusted code with the host's operating-system privileges. There are no
extension HTTP/LLM grants or approval controls. Runs freeze configuration, source generation and
model settings; the credential resource can return plaintext secrets. Do not persist secrets in
configuration or emit them in ordinary diagnostics. The separate MCP permission model still
governs external agents and is not changed by this extension decision.

The recommended host `HttpClient` uses a Host-root scheduler. A configured endpoint groups requests for
different resource paths only when the request remains within that endpoint's origin and path
boundary; otherwise the request endpoint is used. Query strings and secret values are not quota
identities; a configured credential reference separates credential-specific partitions. Active
request policies combine conservatively (minimum concurrency, maximum start interval), and
finished policies do not permanently raise subsequent request concurrency. Cooldown and recovery
probe eligibility are rechecked after queue, pacing and pause waits. `Retry-After` supports seconds
and HTTP dates; repeated 429 responses allow a single recovery probe after cooldown, rather than
permanently disabling the service or transparently replaying a request. Network timeouts begin
after these waits. Metadata HTTP resources default to the metadata policy, not translation settings;
the shared metadata fetch helper now uses the Host's 120-second network timeout instead of a
15-second extension timer that also counted queue/cooldown time.
The bundled HTTP translation driver stops dispatching more work after a rate-limit result and
retains successful results; it does not turn that result into another page retry. Host HTTP
transport does not automatically retry requests. Automated evidence belongs in the validation
record, separate from these implementation contracts. Extensions may construct other clients or
use third-party HTTP libraries; those clients own their scheduling and cannot automatically use
the host's offline message-handler fixtures.

Every built-in bibliographic metadata source is an FSX package (`metadata.*`) and declares the
identifier schemes it serves in its `MetadataSourceDefinition`. The host seeds those declarations
so endpoint discovery and scheme routing work before any activation, then replaces definitions
from active leases. A lookup initializes only the packages that declare the requested scheme
(`EnsureExtensionAsync`); eager fleet initialization excludes metadata packages, so unrelated
implementations are not initialized. Providers for one identifier are queried concurrently while the resolved
source order still decides the winning candidate, preserving priority fallback. A failed bundled
package activation is recorded and skipped instead of failing the fleet, the editable source is
persisted before checking so it stays repairable, and the extension dialog activates only the
package it opens. Matching bundled and cached packages load precompiled DLLs without FCS or
dependency restore. There is no alternate hardcoded C# provider execution path. The metadata settings list shows a
per-source gear that opens the same configuration dialog as a translation engine; never-activated
bundled packages stay listed as 未激活 so their settings and source remain accessible.

Working source, saved editor drafts, Active generations and LKG are independent states.
Saving a draft does not activate it. Explicit activation or an eligible external source
change stages a checked generation and switches Active atomically. Existing leases retain
their original generation. LKG promotion is an explicit operation and does not follow from
saving settings, compiling, activating, or successfully executing one method. Recovery does
not undo committed Library data.

The intended execution target is a host-managed compiled DLL cache and per-generation
collectible ALC. Package-local DLLs, external BCL assemblies and NuGet dependencies are allowed;
the SDK/dependency-resolution requirements remain under validation. The host provides
credentials, request budgets and data commits. HTTP uses standard host-managed
resources, LLM calls use the configured default model when no explicit selection is made, and
credentials are resolved as plaintext at the point of reference. No extension network approval
is performed; existing external-agent MCP permissions remain in force.

Configuration is declared with attribute records. Editable `Support.fsx` remains supported,
with pure-function layers separated from host-effectful orchestration. Generation leases drain
before unload, and last-known-good promotion remains explicit. ALC unload is best-effort: warn
when collection cannot be confirmed and suggest restart, but do not block activation. Trusted
compiled code is not a security sandbox.
Workflow replay and durable run recovery remain outside this contract. The removed built-in
Agent, chat and workflow scheduler remain removed.
