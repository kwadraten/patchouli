# ADR 0039: Typed functional workflows control the existing agent

2026-10-09 amendment: FSI is always available in chat and workflow sessions. The session grant and chat-page toggle are removed; workers remain lazy and isolated. This supersedes the earlier grant policy.

Status: accepted, 2026-10-07. Supersedes the imperative workflow API and built-in translation script in ADR 0036. The user explicitly requested removing compatibility.

Amendment, 2026-10-08: the mixed SDK implementation described in
[workflow-api](../workflow-api.md) supersedes the historical protocol/replay details below.
Snapshot API is now `patchouli.workflow.harness/5`. Chat and workflows use the shared `AgentDriver`
and `AgentCore.chatStep`; workflows supply stage control policies, not another provider/tool loop.
Native FC remains the default transport. Native calls and session-local FSI functions reach the same
SDK invoker, validation, capability checks and operation receipts. Typed pipeline nodes and routes
are automatically checkpointed; completed callbacks are skipped on restart. Interrupted arbitrary
FSI code is never replayed automatically. Bound translation tools hide target parameters, and page
outcomes are derived from real commit receipts. No distributed exactly-once claim is made.

## Decision

Use [Agent.NET](https://github.com/JordanMarr/Agent.NET)'s cold typed workflow construction and typed agent contracts as the API reference. Patchouli retains its existing pure `AgentCore.chatStep` and host interpreter rather than adding another agent runtime.

A script exports a **value** `let run : AgentWorkflow = ...`, constructed with `workflow { step ... }` and `Workflow.define`. There is no bound host object, `WorkflowScriptApi`, async entry point, fetch/put wrapper, arbitrary async executor or legacy execution path. New snapshots use `patchouli.workflow.harness/2`; old versions fail with `WORKFLOW_API_REMOVED` before evaluation. Users rewrite custom scripts and start new sessions. Existing session files remain intact.

`Workflow<'Input,'Output>` preserves types throughout sequence, branch, event boundaries and bounded refinement loops. `ChatAgent<'Input,'Output>` describes a role, pure input formatter and output parser. The closed control graph is inspectable before execution. Commands and typed continuations form a free program; there is no object-valued step accumulator or mutable workflow state.

The runner carries immutable state and interprets agent stages. Each stage supplies a goal to the agent, then executes **the effects returned by `AgentCore.chatStep`**. Model JSON tool requests use the same parser and gateway as chat. Stage allowlists narrow host capabilities. FSI is always available to chat and workflow sessions, and a stage permits its use via `AgentTool.Fsi`. FSI remains a separate process with the session's OS temporary workspace.

Budgets are enforced before model or tool execution. `requireToolUse` checks completed tool attempts before final prose can end a stage, so translation cannot silently succeed without reading and writing. It does not prove that all requested resources were translated. Model transport/provider failures are explicit run failures.

Stage goals are append-only user messages. The session system prefix is fixed; tool results use the text protocol's user-role representation. Replay rebuilds agent state from recorded results and compares structural path, stage contract, prompt, effect ID, tool name and arguments before reuse. Mismatch fails before new effects. Completed tools are logged even when cancellation arrives during their commit. Complete launch selection is persisted for multi-document replay.

The session service owns the harness driver lock and stop/cancel/lifetime token. Each effect publishes its current agent context to the existing observable session. Resume reads its ledger under that same lock, so concurrent resume requests cannot execute a stale unfinished suffix twice.

Full-text translation now declares a translator role, launch goal, tool scope and finite budget. The existing agent chooses discovery, fetching, translation, validation, repair and persistence. The script contains no mutable page cursor, JSON business parser, duplicated RPC implementation or procedural translation loop.

## Verification boundary

F# checks stage, branch, loop-state and event-boundary type compatibility. `ControlShape` and `Workflow.bounds` support checking structural upper bounds on model calls, tool calls and event waits: sequence sums, branches take maximum, bounded loops multiply. Checked arithmetic rejects overflow. The runner enforces budgets and tool scope independently of model text.

These properties assume author-supplied formatting, parsing, mapping and predicates are pure and terminate. Scripts are trusted .NET code; the type system does not sandbox arbitrary top-level IO. Event waits may last indefinitely until completion or cancellation. No proof of model correctness, translation quality, arbitrary script termination or exactly-once effects across an unlogged crash is claimed.

## Validation

Compile-negative tests cover incompatible stage/branch/event/loop types and removed/async entry contracts. Runtime tests cover cold construction, typed routing, bounded immutable refinement, agent-selected tools, scope and budgets, completion requirements, model failure, cancellation, replay, divergence and parser failures. Host tests use the real agent interpreter with stub model/MCP transports; translation tests execute the shipped embedded script.
