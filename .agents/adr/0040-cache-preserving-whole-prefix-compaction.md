# ADR 0040: Cache-preserving whole-prefix compaction

Status: accepted, 2026-10-08. Amends ADR 0036's provider-visible append-only invariant.

## Decision

Keep the existing single agent loop and append-only original history. Do not introduce
per-page conversation windows, separate agents, evaluators, budget frameworks or permission
machinery. The user explicitly rejected those expansions.

The shared host interpreter projects one stable model prefix from the original history.
Normal requests append to that same prefix to preserve provider inference caching. At 80%
of the configured provider/model context capacity, the same model summarizes the **entire
active conversation prefix**, including any previous summary. The fixed system instructions
and native tool definitions remain unchanged. No recent-message tail is retained automatically.

The summarization request retains the current wire prefix and appends a summary instruction,
so provider caching remains available. A successful, non-truncated text summary is atomically
saved in the session's `compaction.json` before the original task continues under a new local
prefix-invariant generation. Subsequent requests append to that summary until the next threshold.
Original core history, effect ledgers and workflow replay remain unchanged. A fingerprint binds
the checkpoint to the exact original prefix; mismatches fail rather than discard history.
Workflow model-turn bounds count agent decision turns; a turn may additionally make one
summarization request through the same provider before its normal decision request.

Provider-reported prompt usage plus estimated appended material determines pressure once
usage is available and the provider/model, endpoint, instructions, schemas and output cap match
the measured request envelope. Otherwise, a text estimate prices model-visible instructions,
schemas, assistant metadata and tool calls/results, rather than persisted transcript JSON.
Escaped Unicode in nested SDK JSON is priced separately from ordinary ASCII. This is
approximate, not a model-specific tokenizer. Cached input is counted once: the Anthropic
adapter adds its separately reported cache counts; aggregate prompt counters do not add them.
The provider settings expose context capacity (default 128000 tokens); users set it to the
capacity of their chosen model. The trigger is the smaller of 80% capacity and capacity minus
reserved output and headroom (5% capacity, capped at 8192 tokens). Both normal and summary
requests reserve output (10% capacity, capped at 8192 tokens); supported transports send that
cap, while subscription transports omit unsupported overrides. Preflight refuses estimated
input plus output above configured capacity. Summaries must shrink the model projection.

Amendment, 2026-10-10: model requests deterministically fold oversized native and text tool
results, including the aggregate FSI SDK receipts, into a warning and a short preview. The
settings page's Model and Translation section exposes `Llm.ToolResultMaxCharacters` (default
32768 UTF-16 characters, range 4096–1048576). The warning states the configured maximum and
actual length, explicitly forbids requesting so much content in one call, and directs the
model to narrow its scope and use small batches across separate turns. Original history and
full SDK receipts remain unchanged. FSI model results retain printed output and operation
identity/status while bounding nested receipt payloads and the aggregate result. Token estimates
use that projection. If the whole request cannot fit, newly projected results may be folded more
aggressively even below the configured character limit; previously sent results stay unchanged.
Before each provider attempt, `context/tool-payloads` log records freeze newly projected tool
results and bind them to the exact original history prefix. `context/compaction` records persist
summary generations and measurement anchors. Ordered log replay is authoritative; the JSON
snapshots are disposable inspection aids. Existing entries, including results left unfolded,
replay with byte-stable content after a settings change, failed attempt, restart or sync. Saving
a new limit applies only to not-yet-projected entries. No legacy-session migration is included.

`history` is a native, session-local tool. It searches original entries by text and supports
stable 1-based entry pagination and character offsets for long entries. It accepts no session
selector. Search returns content near a literal match or decoded nested JSON excerpts. An
optional operationId selects one original FSI receipt Payload for exact paging; maxChars reduces
the page size. History uses the original transcript, never model previews. Results re-enter
the same tool loop and append to the active cached prefix.
Already bounded history pages are exempt from folding so original content remains retrievable,
and are serialized without unnecessary Unicode escaping. Runtime page budgets scale down for
small configured contexts and shrink further near the measured pressure limit, reserving room
for subsequent summarization. Explicit offsets may not split Unicode surrogate pairs.
Workflow stages opt in with `AgentTool.History`; the shipped translation stages permit it.

## Recovery and limits

Failed, empty, tool-calling, truncated or cancelled summaries do not replace the checkpoint.
Reopening or syncing a session reuses its saved summary and generation. Purging removes the
checkpoint with the session; original Library content is unaffected.

Only fully settled histories are summarized at model-request boundaries, so native calls are
not split from their results. Context compression never re-executes tools or reconstructs FSI
bindings. Tool payload folding bounds individual model-visible results before both normal and
summary requests. It is not semantic summarization, and does not bound user messages, assistant
metadata or total conversation size; those can still exceed model capacity.
Provider-confirmed context overflow is distinguished from HTTP 400 endpoint errors. A live
request may make one forced summary and one new-generation retry, only if the summary shrinks
the input and fits the estimated budget; unchanged requests and completed tools are not replayed.

## Validation

Tests exercise stable prefix reuse below threshold, complete-prefix compression, repeated
compression, continuation, restart, provider-usage pressure, invalid/cancelled summaries,
checkpoint divergence, and session-local searchable/paged historical retrieval.
