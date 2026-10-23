# 0051. Unified task-level translation extension contract

Date: 2026-10-22

## Status

Accepted by user direction, 2026-10-22.

Partially superseded by ADR 0053 on 2026-10-23: only the worker/RPC execution protocol is replaced
by trusted compiled in-process execution. The task-level `ITranslationJob` semantics, page ownership,
commit rules, and retry responsibilities in this ADR remain in force.

This ADR supersedes, in ADR [`0046`](0046-direct-batched-translation-pipeline.md) and ADR
[`0049`](0049-fsx-extension-pipelines.md), the host-driven per-page translation dispatch:
the engine's declared page execution mode (`ExtensionPageExecutionMode`) and the host's
per-page scheduling, page concurrency and page retry decisions. Everything else those ADRs
established remains in force — box-derived derived-data storage and lazy realignment
(ADRs `0034`/`0044`), target-language skip rules, one atomic page commit with bounded
conflict reloads, complete (non-streaming) responses, run-pinned generations and
configuration, generation leases and draining, LKG and rollback, volatile runs, and the
MCP and task-queue surfaces.

The ownership and failure matrix is in [Extension contracts](../protocols/extension-contracts.md).
Acceptance evidence and its current limits are in
[Extension validation](../protocols/extension-validation.md).

## Context

ADR `0049` made the selected FSX engine the owner of translation policy, but kept the host
as the per-page driver: `TranslationRunEngine` decided which page reached
`ITranslationExtension.TranslateAsync(context)` next, how many pages of one document ran at
once, and whether a failed page was retried. The split produced four problems:

1. **Batching across pages was impossible.** The host handed the engine one page at a time,
   so an engine could not pack several short pages into one model request or group a run of
   heading-only pages, which is exactly what the request budget is spent on.
2. **Sequencing was declared, not enforced.** `ExtensionPageExecutionMode.Sequential` was a
   descriptor flag the host interpreted. An engine could not express "these pages must run
   in order, and each needs the translation the previous page just committed" in a way the
   host could honour; the LLM engine's previous-page backfill had to be reconstructed from
   host-side run state.
3. **Retry classification lived with the host.** Whether a failure deserves another attempt
   depends on the provider protocol. A rate-limited model call and a rejected DeepL web
   request mean different things, and a host that does not know the engine can only apply a
   generic budget.
4. **Context was eager.** Neighbouring source pages and the previous stored translation were
   gathered for every dispatched page, even when the engine wanted none of it.

An optional task-level interface next to the per-page entry point was considered and
rejected. It keeps two translation paths, two host drivers and two failure semantics alive
at once, and every future engine feature would have to be implemented twice or silently
fall back to the weaker path.

## Decision

### One task-level entry point

`ITranslationExtension` has exactly one method:

```csharp
Task RunAsync(ITranslationJob job, IExtensionCapabilities capabilities,
    CancellationToken cancellationToken = default);
```

The extension receives the whole translation task and owns page order, cross-page batching,
concurrency and retry policy. The host does not dispatch pages and does not retry pages on
the extension's behalf.

There is no optional task-level interface and no compatibility layer. The per-page entry
point, `TranslationExtensionContext`, `TranslationExtensionResult`,
`ExtensionPageExecutionMode` and `ExtensionDescriptor.PageExecutionMode` are deleted from
`Patchouli.Extensions.Contracts` rather than deprecated.

### Split of responsibility

The extension owns:

- which page to work on next, and whether to work on pages concurrently;
- how many pages and boxes share one request, and under which grouping key;
- request concurrency, spacing, timeouts and request-level budgets;
- page-level retry: which failures deserve another attempt, the budget and the backoff;
- how reference context and already committed translations influence the next page.

The host owns:

- page identity: the document, page id, revision and page index of every page it hands out;
- which pages still need work, and the terminal outcome of every requested page, including
  the pages it settles itself (unreadable, no translatable leaf box, already current in the
  target language and not forced);
- reference context snapshots, frozen per run once read;
- validation of each page result against the frozen page and its translatable leaf boxes;
- one atomic page commit per page, with source revision and translation version checks;
- cancellation, progress plumbing, pause and the run's status projection.

### The job interface

`ITranslationJob` is the extension's whole view of the task:

- `Info` carries the run-pinned `TranslationJobInfo` (job id, document, source and target
  language, options, frozen configuration snapshot). The host never mutates it while the job
  lives.
- `NextPageAsync` returns the next page that still needs a translation, in request order, or
  null when none remains. Pages that need no work are settled by the host and are not
  returned. The host serializes the call, so concurrent callers still observe request order.
- `ReadContextAsync(page, neighborRadius)` reads read-only reference context on demand:
  neighbouring source pages within the radius plus the page's previously stored translation
  when it is current and revision-valid. What it reads is frozen for the rest of the job.
- `GetCommittedPageTranslationAsync(pageIndex)` returns the currently committed translation
  of one page, including pages committed earlier in the same job. This is the only way a
  sequential policy consumes what the previous page just produced;
  `TranslationContextSnapshot.PreviousRunTranslation` stays null.
- `CommitPageAsync(result)` validates and atomically commits one page's outcomes and returns
  a `TranslationPageCommit` whose `State` is `Committed`, `Partial`, `Skipped`, `Failed`, or
  `Conflict`. `Conflict` means the page changed concurrently: the commit carries the
  reloaded page in `RetryPage`, the concurrent writer's page is preserved and this
  attempt's outcomes were not consumed. Every other page settles exactly once; a duplicate
  commit is invalid, not a merge.

Page-scoped progress sets `ExtensionProgress.ItemId` to the page index, which the host maps
to `TranslationStepProgress.PageIndex` (an unparseable item id maps to 0).

### Removing host-side page scheduling

`TranslationRunEngine` no longer schedules pages, enforces a page execution mode, runs pages
concurrently or classifies page failures. It builds one host job per run, calls `RunAsync`
once, and settles whatever the extension left unfinished: a page taken but never committed
settles as cancelled on cancellation and failed otherwise; a page never taken settles as
cancelled. Every requested page ends with exactly one outcome in request order.

The built-in engines share one SDK page driver, `Job.runPages` in
`src/Patchouli.Extensions/Job.fs`, instead of four ad-hoc loops. It prefetches a bounded
window of pages, groups prepared items by `ContextKey` so batching may span pages, runs
groups with the configured bounded concurrency, commits each page after its items settle,
re-prepares a page when a commit returns `Conflict`, and applies a page-level retry budget
that declines non-retryable failures. Page-level retry ownership moves from the host to the
SDK that drives it; the non-retryable error and exception classification moves with it.

The LLM engine marks a page `Sequential` when previous-page backfill is enabled and reads the
preceding page through `GetCommittedPageTranslationAsync`. Sequential pages use a one-page
window and are committed before the next page is taken; the other engines keep cross-page
grouping and concurrent commits.

### Reverse RPC between host and worker

An FSX engine runs in its own host-managed worker process (ADR `0049`), so the job crosses
the pipe. The forward `translate` method carries the `TranslationJobInfo` and returns
acknowledgement only; the worker reaches back through reverse invocations bound to the
invocation identity:

| Method | Payload | Reply |
| --- | --- | --- |
| `job/page` | `0` | `JobPageReply(bool HasPage, TranslationPageInput? Page)` |
| `job/context` | `JobContextRequest(TranslationPageInput Page, int NeighborRadius)` | `TranslationContextSnapshot` |
| `job/committed` | `int PageIndex` | `JobTranslationReply(bool HasTranslation, TranslationContextTranslationSnapshot? Translation)` |
| `job/commit` | `TranslationPageResult` | `TranslationPageCommit` |

Pages, context and commits are streamed rather than pre-materialized, so the worker pays
only for what it decides to use. Empty replies use wrapper records because the wire reader
rejects a bare null. Reverse calls link the invocation token and are cancelled when the
forward call ends; the worker serializes `NextPageAsync` so a misbehaving script cannot
invert page order.

### Host page commits stay atomic

The change removes host-side scheduling, not the storage boundary. Each page is still
written by exactly one `PutPageBoxesAsync` call that validates the source revision and the
translation version and writes that page's boxes in one transaction. A successful commit is
never rolled back, a concurrent writer's page is never overwritten, outcomes are validated
against the page's translatable leaf boxes and their typed payloads before the write,
reference context stays frozen for the whole run, and cancellation keeps committed pages
while settling the rest.

### Breaking host API version

`ExtensionHostApi.Current` becomes `patchouli.extensions/2`. Packages declaring the previous
host API are rejected rather than adapted: the entry point signature, the descriptor surface
and the worker protocol changed together, and a package that still exports the per-page
method has no correct translation under the new contract. Packages are rejected by the same
static check and activation path as any other host API mismatch. The four bundled translation
packages move in the same change, so no shipped engine is left behind. External FSX
translation packages must be rewritten against `RunAsync`; there is no compatibility layer
and no dual-dispatch fallback.

## Consequences

- One translation entry point, one host driver and one retry classification. A new engine
  implements `RunAsync` — or reuses `Job.runPages` — and gets page iteration, cross-page
  batching, bounded concurrency, conflict handling and progress without host changes.
- Cross-page batching becomes possible: built-in engines can pack short pages and related
  headings into fewer requests, and the LLM engine keeps page order only where backfill
  needs it.
- Reference context is paid for on demand; an engine that never reads it never waits for it.
- Page-level retry belongs to the engine, so the host no longer bounds it. A badly behaved
  engine can spend more requests than a host-imposed budget would allow; progress and
  cancellation still observe every attempt.
- External FSX translation packages break. They must walk `NextPageAsync` to null, commit
  every page they took, declare `patchouli.extensions/2`, and are rejected at activation
  otherwise.
- The worker protocol gains reverse job calls that the host must serve for the duration of
  the forward invocation. A forced worker restart still fails unfinished invocations without
  replay; committed pages remain committed.
- Evidence must be recorded per boundary — host job semantics, SDK driver, reverse RPC, real
  worker processes — before this contract is described as delivered.
