# 0054. Task-level OCR extension jobs

Date: 2026-10-25

## Status

Accepted by user direction as the contract slice of issue #43, 2026-10-25. Implementation and
acceptance are in progress, not declared complete. This ADR mirrors ADR
[`0051`](0051-unified-task-level-translation-extension.md) for the OCR domain and keeps ADR
[`0053`](0053-trusted-compiled-fsx-extensions.md) execution rules (compiled FSX, collectible ALC,
generation leases, LKG) unchanged.

## Context

Issue #43 decided (2026-10-25 revision) that OCR engines move to the same task-level job model
translation adopted, with an OCR-specific `IOcrJob` rather than reused translation DTOs. The
pre-existing OCR surface — `IOcrExtension.RecognizeAsync(OcrExtensionContext)` — had three
structural problems:

1. **Host-driven, per-page-per-call.** The host rendered one page or region up front, embedded the
   image bytes eagerly in `ExtensionOcrInput`, and got one candidate back. An engine could not
   batch across inputs or pages, could not order its own requests, and could not express
   cross-page relations except through out-of-band state.
2. **Eager and unbounded.** Every input's image was materialized before the extension ran,
   whether or not the engine needed it, and the caller's buffer list had no lease discipline.
3. **No settlement semantics.** A returned candidate was success-or-nothing. There was no way to
   say "this input failed but this one succeeded", no explicit failure record, and no defined
   outcome for duplicates, foreign inputs or a moved baseline.

An optional task-level interface beside the per-page method was rejected for the same reason as
in ADR 0051: two execution semantics, two drivers, two failure paths.

## Decision

### One task-level entry point

`IOcrExtension` has exactly one method:

```csharp
Task RunAsync(IOcrJob job, IExtensionResources resources,
    CancellationToken cancellationToken = default);
```

The extension receives the whole OCR task and owns input order, cross-input and cross-page
batching, concurrency and request-level retry policy. The host does not dispatch inputs and does
not retry them on the extension's behalf. `OcrExtensionContext`, `ExtensionOcrInput` and the
per-page method are deleted rather than deprecated; `OcrInputId`,
`OcrExtensionBoxCandidate` and `OcrExtensionCandidate` (typed `DocumentBoxSeed` candidates)
remain as the submission vocabulary.

### Split of responsibility

The extension owns:

- which page or input to work on next, and whether work runs concurrently;
- how many inputs share one request, and under which grouping;
- request concurrency, spacing, timeouts and request-level retry budgets;
- normalization of engine output into typed `DocumentBoxSeed` candidates.

The host owns:

- page and input identity: the document instance, page id, frozen source revision and region of
  every input it issues;
- lazy bounded input materialization and deterministic release of image leases;
- validation of each submission against the issued page, its inputs and the job's box identities;
- per-page exactly-once settlement and terminal outcomes, including pages it settles itself;
- the Working Revision / adoption path after acceptance — candidates are never direct writes of
  canonical OCR text;
- cancellation, progress plumbing and the job's status projection.

### The job interface

`IOcrJob` is the extension's whole view of the task:

- `Info` carries `OcrJobInfo` (job id, `DocumentInstanceId`, `OcrJobOptions`, frozen
  `ExtensionConfigSnapshot`). Every requested page belongs to the job's document instance.
- `NextPageAsync` returns the next `OcrJobPage` — a page identity plus its issued `OcrJobInput`
  descriptors (id, normalized region, content type) — in request order, or null. The host
  serializes the call. The host copies the caller's page/input collections when the job starts
  and issues each page's inputs as a read-only view, so neither caller mutation nor descriptor
  replacement through collection casts can change the frozen selection.
- `ReadInputImageAsync(input)` materializes one issued input's bytes on demand as an
  `OcrInputImage` lease (`IDisposable`). Only the descriptor instance the job issued is
  accepted — foreign or equal-valued-but-different descriptors throw `ArgumentException` — and
  reads on an already-settled page throw `InvalidOperationException`. A per-job in-flight budget
  (`MaxInFlightImages`, default 4) bounds undisposed images: over-budget reads wait,
  cancellation-aware, until a lease frees a slot. Every call registers before its first await
  and links a job-owned read lifetime into its token: job completion cancels waiters and
  cooperative loaders, then waits for every registered read to actually exit — a loader that
  ignores the job's cancellation still holds the invocation (and the generation lease) open
  until it returns, and an image it returns after closure is disposed immediately. Leases still
  undisposed at completion are disposed by the host; `OcrInputImage.Data` throws after disposal
  and drops the buffer reference.
- `SubmitCandidatesAsync(candidate)` validates and records one page's candidate set and returns
  an `OcrSubmissionReceipt`: `Accepted`, `Rejected` (page stays unsettled; a corrected
  resubmission is allowed), `Conflict` (`RetryPage` reissues the page when the host can), or
  `Failed` (recorded via `FailPageAsync`). Duplicate submissions of a settled page are rejected,
  never merged.
- `FailPageAsync(page, errorCode, errorMessage?)` settles an issued page as failed so an
  all-failed page is never disguised as an empty recognition.

Candidate rules: geometry is normalized page coordinates contained in the referenced input's
region; job-allocated seed `BoxId`s must be unique across the job; `ParentBoxId` resolves inside
the same submission; `ContinuesFromBoxId` resolves inside the same submission or to a box id
accepted earlier in the job. That second case is how cross-page continuations (the MinerU v4
continuation semantics of issue #29) survive — and it is settlement-order sensitive by design:
a submission whose continuation targets a not-yet-accepted box is rejected, so engines that
settle pages concurrently cannot emit forward references. Sequential settlement order is the
contract; a baseline-aware pending-resolution model remains possible future work, not part of
this slice. A `Rejected` submission leaves the page unsettled rather than burning it.

Input-level outcomes: every submission carries `OcrInputSettlement` entries covering each issued
input exactly once — `Recognized` (blank output included), `Failed` with an error code, or
`Skipped`. This makes a partially recognized page explicit: it submits its recognized boxes with
the failed inputs still recorded as failed, and `FailedInputCount` on the receipt and the
recorded `OcrPageSettlement` expose the partiality to host callers. An all-failed page still
settles via `FailPageAsync`.

Terminal settlement: every requested page settles exactly once. Issued pages still unsettled when
the invocation returns settle as `Failed`, or `Cancelled` when cancelled; pages never taken settle
as `Cancelled`. Post-completion job use throws.

Invocation lifetime: the extension call runs on the thread pool (`IOcrJob` is a mediation
surface, not a thread boundary). Job completion is asynchronous: it marks the job closed,
cancels the read lifetime, disposes leaked leases, then drains every read call registered
before closure — no `WaitAsync` timeout pretends an ignoring loader has stopped.
`ExtensionDomainRun.DisposeAsync` shares one drain-plus-lease-release operation across all
disposers, so a concurrent caller awaits the same lease disposal rather than returning early;
new invocations are rejected once disposal starts.

### Scope is not persistence

A job's scope can be a document run, selected pages, a single page or region, a logical page or a
working-draft/ephemeral prefill. Ephemeral jobs share the same contract and need no durable
`OcrRun`: the host collects the accepted candidates for its caller (region prefill, staging)
instead of importing them.

### Default SDK driver

`OcrJob.runPages` / `runPagesWithOptions` in `Patchouli.Extensions.Sdk` is the bounded default:
strictly sequential page pulls (no descriptor or image prefetch), per-input materialization and
deterministic disposal, a per-input attempt budget spent only on errors an explicit
`IsRetryableInputError` classifier accepts — the default retries only transient infrastructure
errors (timeout, HTTP request, IO), while cancellation of any kind, argument/validation errors
and programming errors are terminal on first raise — typed per-input outcomes, partial-input
submission, an all-failed page going through `FailPageAsync`, bounded `Conflict` reprocessing,
and cancellation propagation. Default error text is a stable code plus the exception type name;
raw engine messages (which may carry secrets or paths) are not used as public error text.
`Extension.createOcr` builds a task-level engine; `Extension.createOcrPages` wraps a per-input
recognizer in the default driver. Engines needing batching or concurrency implement `RunAsync`
directly. There is deliberately no general processing-job framework.

### Host invocation

`ExtensionDomainRun.RunOcrAsync(OcrDomainJobRequest)` builds one host job per invocation, calls
`RunAsync` once, completes the job (settling unfinished work and releasing leaked image leases),
and returns the accepted candidates plus every page's terminal settlement in request order.
`ExtensionDomainService.RecognizeAndStageAsync` migrated to this invocation with a lazy
render-on-read loader; the queue/executor path migrates in a later slice.

## Consequences

- One OCR entry point, one host invocation path, one settlement vocabulary. A new engine
  implements `RunAsync` — or wraps a per-input recognizer via `createOcrPages` — and gets lazy
  bounded inputs, classified retries and exactly-once settlement without host changes.
- Cross-page continuations work for sequentially settled pages: a continuation head accepted on
  one page can be referenced by a later page's submission in the same job. Forward references
  are rejected deterministically rather than deferred — concurrent settlement order is not
  supported for continuation-producing output, and nothing in this slice claims it is.
- Image memory is bounded by `MaxInFlightImages` outstanding leases, not by job size; reading is
  paid for on demand. Read calls are tracked work: an extension may drop a read's task, but the
  invocation — and the lease — cannot exit until the loader behind it has finished, so image
  resources never outlive the generation they belong to.
- `Accepted` records a validated candidate, not full recognition success: a partial page
  settles `Accepted` with `FailedInputCount > 0`, its `page-ocr` progress event reports
  `Failed` (`partially recognized`) rather than `Completed`, and any consumer adopting or
  reporting page outcomes must inspect `candidate.Inputs`/`FailedInputCount`.
- `ExtensionRuntime.fs`'s OCR wrapper forwarding was migrated from the deleted per-page method
  to `RunAsync` under integration ownership; that edit is outside this slice's ownership.
- `Conflict` is a receipt shape plus SDK handling only: no host code path currently produces a
  `Conflict` receipt. Producing one requires a baseline-aware settlement sink (importer-side
  revision compare at accept time), which remains queued for the migration slice — this ADR does
  not claim conflict handling is live.
- Whether `ExtensionHostApi.Current` moves past `patchouli.extensions/3` for this break is left
  to the integration wave: no shipped OCR packages exist, but a bump would invalidate every
  bundled package's declared host api.

## Verification state

The contract foundation was validated on Windows on 2026-10-25: normal C#/F# builds and the
post-cleanup rebuild had zero warnings/errors; the recorded focused run passed 209 cases; the
separate F# SDK project passed all 96 cases; Full passed 4,200 cases with eight existing
allowlisted terminal skips and no failures; JetBrains 2026.1.4 inspection had zero errors and
zero blocking findings. This includes the production compiled OCR generation-pinning path and
the new lazy-input, partial-outcome, read-drain, lease-disposal and interactive thread-boundary
regressions. See [extension validation](../protocols/extension-validation.md) for recorded run IDs.

These are fixture correctness gates for the current foundation, not acceptance of all of #43.
The default queue still needs task-level FSX engine integration and the actual engine migrations;
baseline-aware submission conflicts, complete preset/extension-generation wiring, independent
text-layer providers and release/packaging decisions remain unfinished. No real-model,
performance/endurance, cross-platform or installed-application acceptance follows from these runs.
