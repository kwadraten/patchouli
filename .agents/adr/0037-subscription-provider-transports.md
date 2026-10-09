# 0037. Subscription Provider Transports

Date: 2026-10-07

## Status

Accepted. Extends ADR `0036` with provider subscription authentication and explicitly scopes
the provider-prefix invariant for protocols that require their own model instructions.

## Context

LLMTornado 3.8.69 exposes ChatGPT/Codex subscription access separately from its API-key chat
transport. Its public consumer OAuth session API is `CodexOAuthSession`; no Claude Pro/Max
or Gemini consumer-subscription protocol is implemented by this pinned package. ChatGPT plan
names and model entitlements are account data, not a static allow-list maintained by Patchouli.

## Decision

- `LlmSubscriptionCatalog` lists every supported subscription protocol with a concrete adapter.
  A regression check detects new public upstream OAuth session types when the dependency changes.
  Adding a protocol requires implementing authentication, model discovery and inference;
  merely adding a label never advertises support.
- A subscription connection is a distinct catalog provider (`openai-subscription`). It can
  coexist with `openai` API access, including API-backed visual OCR. `ByVendor(OpenAi)` still
  resolves the ordinary OpenAI API entry.
- Authentication mode is non-secret provider configuration. Complete access/refresh/identity
  credential sets are stored through `ICredentialStore` with a separate subscription key.
  No credential enters the `Llm` settings section, session history, MCP or diagnostic output.
  Token rotation replaces the complete stored set. A shared gate prevents concurrent settings
  and inference operations from refreshing the same credential independently.
- Direct browser OAuth uses LLMTornado's callback, refresh and account-specific model discovery.
  Login can be cancelled; logout revokes when possible and clears the local subscription
  credential. API credentials remain independently configured.
- Codex subscription threads currently accept text only. They are available to translation
  and chat. OCR selection and runtime validation reject this backend before vision inference.

## Transcript and agent ownership

Patchouli's existing F# core remains the only agent step/effect loop. The public Codex thread
API requires model-supplied base instructions and has a more constrained role/history API
than Patchouli's recorded history (including interleaved system instructions and textual
host tool results). The subscription transport therefore supplies a stable protocol instruction
and a lossless JSON transcript of all host messages and tool descriptors, in recorded order,
and requests the next assistant message. Leading host system instructions are also passed
as native developer instructions so the immutable host prefix retains protocol-level authority.
Native Codex function tools are disabled; it never
starts another tool loop or grants filesystem execution.

The host history and its append-only verification remain unchanged. Resume sends the entire
recorded transcript, never a summary or reconstructed business context. This is an explicit
exception to ADR `0036`'s claim that the concrete wire payload has no transport-added prompt:
subscription wire input includes the stable protocol framing and upstream mandatory model
instructions. Those upstream instructions may change with the model catalog; exact wire-prefix
cache identity across upstream changes is not guaranteed. Cache behavior does not decide
whether an existing result is reused or a workflow step is re-executed.

## Validation and limits

Tests use fake credentials, a fake login service and a fake HTTP handler around the real
LLMTornado protocol. They cover independent API/subscription configuration, secret replacement,
signed-out and corrupt credentials, runtime selection, ordered transcripts, token accounting,
dynamic model updates and vision rejection. Real account login and paid inference are manual
integration checks; no real account is accessed by these tests.

## Sources

- [LLMTornado Codex guide](https://github.com/lofcz/LLMTornado/blob/master/src/LlmTornado/Codex/README.md)
- [Official OpenAI authentication documentation](https://learn.chatgpt.com/docs/auth)
