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
usage is available. Otherwise, a conservative text estimate includes instructions, schemas,
assistant metadata and tool calls/results. This is approximate, not a model-specific tokenizer.
The provider settings expose context capacity (default 128000 tokens); users set it to the
capacity of their chosen model. The threshold is fixed at 80% to keep configuration minimal.

`history` is a native, session-local tool. It searches original entries by text and supports
stable 1-based entry pagination and character offsets for long entries. It accepts no session
selector. Results re-enter the same existing tool loop and append to the active cached prefix.
Workflow stages opt in with `AgentTool.History`; the shipped translation stages permit it.

## Recovery and limits

Failed, empty, tool-calling, truncated or cancelled summaries do not replace the checkpoint.
Reopening or syncing a session reuses its saved summary and generation. Purging removes the
checkpoint with the session; original Library content is unaffected.

Only fully settled histories are summarized at model-request boundaries, so native calls are
not split from their results. Context compression never re-executes tools or reconstructs FSI
bindings. If a single new tool result already exceeds the model capacity, summary input may
also fail; this design does not silently truncate or split that input.

## Validation

Tests exercise stable prefix reuse below threshold, complete-prefix compression, repeated
compression, continuation, restart, provider-usage pressure, invalid/cancelled summaries,
checkpoint divergence, and session-local searchable/paged historical retrieval.
