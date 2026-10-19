# ADR 0043: Explicit agent contexts and loop activations

Superseded by [ADR 0047](0047-external-agent-cli-terminal-integration.md): the built-in agent
session, conversation contexts and loop activations have been removed. No context handoff,
activation-scoped replay or worker isolation path remains.

Date: 2026-10-13

Status: accepted. Amends the Session-wide single-loop and provider-prefix decisions in
ADRs 0036, 0039 and 0040. The cold typed workflow and shared AgentDriver described below were
removed by ADR 0047.

## Decision

A Session owns the user inbox, ordered audit history, lifecycle and durable operation
evidence. It can contain multiple conversation contexts and multiple loop activations.
Conversation continuity does not require every activation to declare the same tools.

An agent definition is a cold typed description of instructions, tools, budgets and
output validation. An explicit context selector determines which history an invocation
uses. `Agent.run context agent` creates a workflow node; `step agent` and implicit-context
agent execution are removed.

`AgentContext.main` selects the Session's persistent primary conversation. User messages
continue through this context after automation yields, regardless of the last worker.
`AgentContext.empty()` describes an anonymous context allocated for each node invocation.
Ordinary F# `let` aliases do not change an anonymous descriptor into a shared context.

`AgentContext.bind seed (fun context -> workflow)` declares a reusable context within a
cold lexical workflow scope. The callback builds the inspectable child plan using a
symbolic reference; it does not create a live model conversation. The runtime resolves
the reference from the scope's recorded execution path. A binding outside a loop shares
history across iterations; a binding inside the loop creates one context per iteration.
Bound references may not escape their owning scope or become serialized workflow data.

All live context and activation identities are assigned by the SDK and recovered from
recorded execution identity. Source variable names, object hashes and display labels are
not persistence keys. Anonymous contexts are recoverable: resume continues the original
invocation rather than creating a replacement worker.

## Model history and execution contracts

Each agent invocation has an activation with frozen instructions, tool declarations,
input-bound tool identity, model selection, typed input and retry policy. Its pending
calls and retry state are independent of previous activations. A model-visible prefix
is append-only within a prefix generation of that activation. Compaction creates a new
generation; a new agent stage can establish a different request contract.

Context history remains continuous between activations using that context. Historical
stage policies are evidence, not permanently active instructions. Current tool guidance
is built from the actual activation declarations. Context handoff does not copy active
permissions, pending calls, budgets or temporary worker handles.

New independent contexts receive only the input selected by the workflow. Worker history
is not automatically merged into the main conversation; typed outputs and their input
formatters determine what is passed back. No automatic fork or arbitrary transcript
mutation API is introduced by this decision.

## Host boundaries and recovery

History retrieval and temporary FSI state belong to the calling context. Activated SDK
bindings still expire with their execution scope. Compaction, folded payloads and prompt
usage anchors cannot overwrite another context or activation's checkpoints.

Effect and SDK operation identities include activation identity so equal local effect
numbers in different loops cannot collide in receipts or UI activity pairing. Operation
evidence and unknown-write target quarantine remain Session-wide. Ending an anonymous
context releases live resources without deleting audit or recovery evidence.

Model-backed activation contracts are durably guarded by the Host activation envelope
before provider calls. Workflow checkpoints retain the context registry, activation
records and typed node outputs. Scope bindings are reconstructed from the frozen
control path when execution re-enters their scope.
Completed-node reuse restores its context changes as well as its business output. A
worker's lifecycle or final history must not replace the Session's main context.

The new workflow harness protocol rejects older automated snapshots rather than guessing
their context topology. Old history remains readable. Conversation continuation keeps
the original completed outcomes and starts a new request contract without replaying
completed tools.

## Validation

Required coverage includes explicit-context compilation, cold bounds, bound scope
lifetimes inside and outside loops, anonymous invocation/recovery identity, changed
stage tools through the real LlmChatClient with a fake transport, continuation after
automation, worker history/FSI isolation, compaction/retry identity, effect ID collisions,
and Session-wide unknown-write quarantine. Multi-context support alone does not establish
parallel execution, sandboxing or exactly-once external transactions.
