# 0046. Direct Batched Full-Text Translation Pipeline

Date: 2026-10-19

## Status

Accepted by user direction, 2026-10-19. Amends the premise of ADR `0034` that "Patchouli
embeds no translation engine" by adding an in-product producer. ADR `0034`'s box-derived
storage, lazy realignment, per-page write scope and derived-data status remain in force, as
amended by ADR `0044` (box-identity validation replaces positional Markdown validation). This
ADR replaces the built-in translation workflow that ADR `0036` described with a pipeline that
does not depend on an Agent; ADR `0047` removes that workflow and the rest of the built-in
Agent system.

ADR [0049](0049-fsx-extension-pipelines.md) supersedes this ADR's fixed provider registry
and C# batch orchestration with complete FSX translation engines. Its domain acceptance,
task controls, identity validation and atomic page commit boundaries remain applicable.

## Context

ADR `0034` treats a translation as derived data over the committed page-local Box Tree and
lets an external agent read source pages and write translated box payloads through MCP. That
contract is storage-correct, but it makes the common case expensive: a user who only wants a
full-text translation must install, configure and run an external agent, and the built-in
Agent workflow that previously wrapped this loop was removed with the rest of the built-in
Agent system (ADR `0047`).

The product still needs one-click translation, subject to four constraints:

1. **It must not depend on an Agent.** Translation is a library operation, not a
   conversational run. It must work without an external CLI, without a chat page and without
   a workflow engine.
2. **It must fit provider limits.** Long pages exceed any sensible single request, providers
   rate-limit bursts, and transient failures are normal. Segmentation, batching, bounded
   concurrency, request spacing and bounded retries are part of the feature, not an
   optimisation.
3. **It must preserve the derived-data model.** A page translation is still stored per box,
   still expires when the source payload changes, and still must never become the source of
   record for page text (ADR `0034`, ADR `0044`).
4. **It must be a stable extension point.** The likely follow-up is provider pluggability
   (issue #8); the engine should depend on contracts, not on the current LLM implementation.

The direct design follows the practical model popularised by
[KISS Translator](https://github.com/fishjar/kiss-translator) ("简约翻译"): split page text
into translatable units, aggregate units into batches, send batches concurrently under a
user-visible throttle, keep terminology and neighbouring-page context, skip text that is
already translated, and write each result back in place. KISS Translator's batch aggregation,
AI context memory, custom terminology dictionary and smart segmentation are the specific
motivations; Patchouli keeps that simplicity while replacing "web page DOM" with the
page-local Box Tree and MCP-visible translation storage.

## Decision

### A direct pipeline, independent of the Agent system

Translation is a host capability invoked from the UI, the menu bar, the Library context menu
and the reading toolbar. It does not create an Agent session, a workflow, a chat tab or a
persistent scheduler. The pipeline is:

```text
committed page revision + boxes
  -> ITranslationSegmenter        split translatable box payloads into segments
  -> ITranslationBatcher          group segments into model requests
  -> ITranslationModelInvoker     call the model, validate structured results
  -> ITranslationResultProcessor  merge results back to box payloads
  -> TranslationRunEngine         bounded concurrency, retry, conflict handling
  -> IPageTranslationService      one atomic PutPageBoxesAsync per page
```

`TranslationRunEngine` lives in `Patchouli.Infrastructure/Translation`; the default
segmenter/batcher/invoker/result-processor live beside it. `TranslationRunService` in
`Patchouli.Host` owns the volatile run registry: it resolves the requested one-based pages,
captures the persisted translation settings for the run, starts one background engine
execution and exposes status and cancellation. It deliberately has no durable task ledger,
actor, queue or scheduler.

### Four extension contracts, one provider set per run

The engine depends only on four Core contracts (`Patchouli.Core/Translation`):

- **`ITranslationSegmenter`** splits one page into segments. It skips non-translatable and
  empty boxes, never merges boxes, preserves source order, and is the only stage allowed to
  split an oversized text box; split parts concatenate back to the exact source text. Tables are atomic segments, even above the batch character budget. HTML-only tables send their original HTML to the model, which returns a translated GFM pipe table. The derived translation stores that Markdown without attaching source diagnostic HTML; source table payloads remain unchanged. A valid pipe table may replace an HTML-only `[Table]` placeholder, while ordinary Markdown tables retain structural validation.
- **`ITranslationBatcher`** places every segment in exactly one ordered batch and respects
  batch size, character and optional token limits. A single oversized segment stays in its
  own batch rather than being split or dropped.
- **`ITranslationModelInvoker`** sends one batch and validates the structured response. It
  never infers segment mapping from response order; missing, duplicate, unknown or
  mismatched segment ids fail with a typed batch-mismatch error.
- **`ITranslationResultProcessor`** merges results strictly by segment id, rejects
  inconsistent box/part declarations, and concatenates one box's parts in part order.

`TranslationProviderRegistry` publishes one consistent set for a run, and the engine takes a
snapshot of that set when the run starts. Replacing a contract therefore cannot mix two
implementations mid-run. The registry is the reserved extension point for issue #8's provider
plug-in work: a future provider registers these four interfaces without changing the engine,
the Host run service or the MCP actions.

### Concurrency, throttling and retries

- Model work is bounded by one `SemaphoreSlim` per run. Concurrency is configurable from
  **1 to 16**, default **4**. Batches, not pages, are the unit of parallel model work.
- One shared request pacer spaces **request starts** by the configured interval across all
  batches of the run. The pacer, not the concurrency limit, is the throughput ceiling when an
  interval is set; raising concurrency past the interval's saturation point only queues work.
- Every batch gets **1 + `MaxRetries`** attempts, default two retries. Backoff starts at
  **250 ms**, doubles per attempt and is capped at **5 s**; it is cancellable and happens
  before reacquiring a concurrency slot.
- A page commits only after every batch for that page has settled. Successful batch results
  are merged; failed batches are excluded, and the page is committed as **partial** with the
  boxes that did succeed. A page whose batches all fail is failed and writes nothing.
- A page with no translatable leaf boxes (all code, equations, images, charts, media,
  suppressed or logical-page containers) is **skipped**; there is nothing to send or commit.
- A page whose translation is already current **in the requested target language** and has no
  stale translatable box is **skipped**. `force` disables the skip and refreshes the page.

### Atomic commit, conflict semantics and cancellation

Each page is written by exactly one `PutPageBoxesAsync` call carrying the page revision, the
translation version and the requested target language. The call validates both versions and
updates the stored rows and version in one transaction, so a reader never observes a
half-applied page. Translation remains derived data: source text is never replaced.

A commit conflict means another writer changed the page revision or translation version
concurrently. The engine never overwrites it: it reloads the whole page (revision, boxes and
translation status) and retries the page at most **two** times after the initial attempt.
Repeated conflicts fail that page while leaving the concurrent writer's result intact.

Cancellation stops new batch dispatch, cancels in-flight requests and leaves the run's
already committed pages committed. Remaining pages settle as cancelled. The UI progress
window reports the same rule: cancellation is not a rollback of completed work.

### Persistence and write boundary

- Migration **053** adds `page_translations.target_language`. The skip decision compares the
  requested language with this column, so switching languages retranslates instead of reusing
  rows written for another language. Legacy rows have a null language and count as stale as
  soon as a specific language is requested. Lazy realignment re-anchors rows to a newer source
  revision but preserves the language.
- The engine writes through the existing page-translation service, not through MCP. The
  host-only `PutPageBoxesAsync` batch limit is **4096** boxes so a large page can commit in
  one atomic write; the MCP `put` limit stays at **100** boxes, preserving the narrow
  external-write contract of ADR `0044`.
- Runs are **volatile**. The Host registry keeps them in memory only; they are not persisted,
  not part of a snapshot and disappear when the host process exits. Committed page
  translations are durable and unaffected.

### MCP and UI surface

The `runs/translation/control` endpoint accepts `start` against a Document and `cancel`
against a translation task instance or status URI. Flat send fields are endpoint/action/target/args,
with optional outer request_id. Start returns a started receipt and status URI; process-local
request-id replay is atomic and reports reused. Both messages require the send tool switch and
explicit runs/translation send permission, not separate action grants. The runs parent checkbox
aggregates child choices in three states; new children default off. See the MCP protocol for
CLI options, envelopes, discovery and migration rejection rules. This 2026-10-20 revision
supersedes the previous combination-action authorization design (issues #8/#16/#19/#20).

The settings card lives in the existing **文献加工 → 文献翻译** section and exposes target language,
batch size, character/token caps, concurrency, request interval, timeout, retries, window
radius, previous-window backfill, and an optional provider/model override. The main menu, the
Library context menu and the reading toolbar start a full-document translation. The shared task queue page polls translation runs once per second, shows per-page errors and offers pause/resume, cancellation and failed-page retry. OCR and translation appear in the same view, defaulting to all tasks. Global task controls fan out to both services; display filtering does not limit them. Translation uses cooperative per-run gates before model dispatch; in-flight requests may finish, local pauses survive global resume, and cancellation also interrupts paused waits. Retries create an idempotent new run for failed/partial pages using the original target language. Both services retain independent scheduling and can run concurrently. Shared model connections remain under AI integration.

### Performance expectations

A local benchmark with a simulated model (fixed 300 ms per batch, 16 text boxes per page,
batch size 8) measured near-linear scaling with no request interval: concurrency 4 averaged
**3.98x** and concurrency 8 averaged **8.01x** over concurrency 1. With a **200 ms** request
interval, throughput capped at about **40 box/s** and concurrency 2 already saturated the
pacer, so larger concurrency added queueing rather than throughput. The report is kept at
`.tmp/perf/REPORT.md` (a local, non-versioned benchmark artifact); these numbers describe
orchestration overhead, not real provider latency, rate limits or tokenisation costs.

### Relationship to ADR 0034

ADR `0034` remains authoritative for what a translation is: a per-box derived row set under a
per-page header, compiled on demand into a document that shares the source page's box ids,
lazily realigned against the committed HEAD revision, and validated on write. This ADR adds a
producer and the target-language dimension; it does not add a page blob, a second translation
store, a bilingual revision or a machine-translation pipeline inside the document model.

ADR `0034`'s "an agent translates outside Patchouli and writes back" premise is therefore
superseded for the product's own translation path. The external-agent path and the direct
pipeline write through the same contract and remain peers: an external agent can still
translate pages through `put`, and the direct pipeline's output is indistinguishable in
storage from an external agent's output.

## Consequences

- Users get full-text translation without installing an Agent, configuring it twice or
  keeping a chat session open.
- Segmentation, batching and merging are deterministic and testable independently of any
  model. The four contracts keep provider-specific code out of the engine and reserve the
  issue #8 plug-in seam.
- A request interval is a hard-ish throughput ceiling. Documentation and UI should advise
  raising concurrency only when the interval is zero or well below model latency; otherwise
  concurrency 2 is usually enough.
- Atomicity is per page, not per book. A cancelled or partially failed run leaves already
  committed pages translated and later pages untouched; rerunning with `force` fills the gaps.
- Conflict retries are bounded at two. A page that keeps losing the race fails explicitly
  instead of overwriting a concurrent edit.
- Runs are not durable: after a host restart a status URI returns `NOT_FOUND`, while all
  committed translations remain. Clients must not treat `runs/translation/` as a task ledger
  or as a substitute for the translations root.
- Migration 053 is additive. Old rows with no language are interpreted conservatively and
  retranslated when a specific language is requested.
- The benchmark numbers are a simulated orchestration bound, not a service-level guarantee
  for any provider.
