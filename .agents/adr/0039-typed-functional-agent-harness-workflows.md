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

### Implementation boundary, 2026-10-09

The current implementation uses explicit wire codecs and generated F# DTO bindings. The host
reconstructs actual domain types; no CLR cast across dynamic FSI assemblies is required.
Foundation semantic DUs live in the fixed Agent assembly. Custom workflow functions execute
in the host, reached from named worker functions over typed RPC. Definitions and bindings
are rebuilt from frozen script and node input; identity includes script, runtime, codec and
binding hashes. Uncaptured external #load/#r dependencies are refused rather than treated as
deterministically packaged assemblies.

Managed SDK definitions and typed bindings are recoverable; arbitrary closures, temporary REPL
values, Tasks and handles are not durable state. Completed node outputs/routes and settled
operations are reused, while arbitrary interrupted FSI blocks are not replayed.
Unknown writes quarantine their target within the session. Domain-transaction idempotency receipts,
automatic unknown-write reconciliation and a dedicated reconciliation UI are not implemented.
Checkpoints currently save the full core context rather than content-addressed history references.
These limits must not be presented as exactly-once execution or rollback of arbitrary .NET IO.
The exact API and journal contracts remain in [workflow-api](../workflow-api.md).

### Native FC / FSI experiment evidence, 2026-10-08

The choice of mixed native FC and FSI was informed by 208 formal isolated runs: 144 SDK-presentation
trials, 16 paired transport-failure checks and 48 same-stage trials comparing a truly non-native
text code channel with the mixed mode. Pilots were excluded. Four model IDs were tested:
gpt-5.6-luna, gpt-5.6-sol, deepseek-flash and deepseek-v4-pro.
The fixture used the real AgentCore.chatStep and FSharp.Compiler.Service, but an in-memory
Library SDK rather than the production database. Both modes invoked the same fixture operations.

In the 48-run no-native comparison, text-only FSI passed 20/24 and mixed mode passed 24/24.
Of 17 paired successful tool tasks, FSI used 42 model requests versus 50 for mixed mode,
25 versus 33 outer actions, and 34 versus 33 underlying primitives. Nine pairs used fewer
model requests with FSI, one used fewer with mixed mode, and seven tied.
This supports retaining code composition without claiming that it reduces all business operations.
Each model had only 3–5 successful tool pairs; all bootstrap 95% latency-difference intervals
crossed zero. There is no supported stable speed multiplier or universally dominant mode.

The 144-run main experiment's FSI variant still submitted code through one native JSON tool:
removing individual SDK native tools is distinct from removing native code transport.
FSI passed 18/18, 18/18, 17/18 and 18/18 by the model order above. Mixed mode passed
18/18, 18/18, 14/18 and 14/18; its eight failures were local HTTP ResponseEnded transport faults,
not model/SDK failures. After framing/Connection-close repair, selective paired checks gave mixed
8/8 and FSI 6/8. This selected retest is not a fresh random success-rate estimate and does not
replace the original outcomes. The repaired stages had no infrastructure failures.

Observed model errors included a repaired F# type/overload check, duplicate page submission,
success claims without SDK execution, incorrect output format, and content errors caught by
try/with. Consequently compile success, outer FSI success and model prose cannot prove business
completion. Primitive attempts and receipts remain observable even if user code catches failures;
stage output must be validated against real execution evidence.
Native and FSI share one SDK and driver; code transport is a provider adapter decision.
Unconstrained text tags and native freeform were not established as superior default transports.

Count model requests (including final replies), outer actions and underlying SDK primitives separately.
Separate compile/argument errors, domain errors, injected failures and infrastructure failures;
do not count one domain failure again as an independent outer-tool failure. Compare performance
on paired successful tasks, separating cold initialization, model transport, SDK execution,
F# checking/evaluation and end-to-end latency. Both modes initialized FSI.
The small, low-cost in-memory fixture is not a production database/PDF or translation-quality benchmark;
temperature zero and two content seeds do not establish independent deterministic sampling.

Frozen reproduction materials are external historical evidence at
C:/Users/squaresum/.codex/experiments/native-fsi-20261008/:
AgentBench/Program.benchmark-v2.fs, TextBench/Program.fs, manifest.json,
all-trials.csv and final-summary.json. They are not repository/runtime dependencies.
Credentials were read in memory and not recorded. Conservative tracked spend including pilots
was CNY 13.5656 against a CNY 30 cap, not an invoice amount; Codex used the existing subscription.
The experiment did not itself modify production code; the subsequent mixed SDK implementation
is described above and in workflow-api.

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
