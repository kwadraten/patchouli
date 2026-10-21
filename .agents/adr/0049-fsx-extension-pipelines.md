# 0049. FSX extension pipelines and static configuration bindings

Status: accepted and implemented, 2026-10-21. Issue #8.

The ownership and failure matrix is in [Extension contracts](../extension-contracts.md).

## Decision

Reintroduce FSI only as a trusted Extension package loader. This amends ADR 0047's removal
of FSI without reinstating its removed Agent, conversation, workflow replay, checkpoint,
session persistence or general scheduling machinery. External agents remain the authors.

Each package exports a typed implementation, declares stable configuration keys and managed
capability requests, and runs through the same checker in desktop and headless hosts.
FCS extracts declarations without evaluating source. Execution and contract tests are separate
from static checking. Configuration fields generate forms; source remains editable.

The SDK pipe operator reports step outcomes. Bounded parallel, batching and retry combinators
share cancellation, throttling and progress scopes. Pipelines express domain execution, not
a persistent task graph. Core retains data ownership, version checks and atomic commits.

Translation selects a whole FSX engine. LLM, Google free, DeepL free and DeepLX remain separate
packages and protocols; the reusable SDK does not force them through an LLM response format.
Runs pin a generation and configuration, resolve model defaults once, and retain committed
pages on cancellation. Browser-only KISS Translator features are outside this decision.

Translation uses complete-response requests and has no streaming API or provisional content
preview. This follows the revised user direction on 2026-10-21. Pipeline progress, pause and
cancellation remain independent of response delivery; candidates still require complete
identity/structure validation and host-side atomic page commits.

This supersedes ADR 0046's fixed four-provider composition and C# batch orchestration.
The four step interfaces remain reusable building blocks. The domain run service retains
acceptance, idempotency, task controls and atomic page commits; the selected extension owns
translation policy. The host supplies immutable source and translation context with physical
page identities. The LLM package chooses its context window and previous-page backfill from
its own bindings, and declares sequential page execution when ordering matters.

The revised execution decision, authorized on 2026-10-21, puts each package generation's
FSI session in a host-managed worker process. Static checking initially stays in the host.
An FSI session's collectible assemblies do not establish successful reclamation: the pinned
toolchain's direct-session and extension probes retained their references after disposal.
Process exit is the reclamation boundary; this decision does not depend on identifying every
runtime root or changing the FCS version.

Candidates initialize in a separate worker and publish only after validation. Leases keep
old generations alive until consumers exit. Logical source generation and physical worker
instance are distinct identities. A replacement instance never inherits unfinished calls.
Active and draining workers have a bounded admission policy: activation waits or fails
without changing Active when retained generations exhaust capacity.

A dedicated named pipe carries asynchronous duplex requests, results and progress. Standard
output and error remain logs. Reverse capability requests identify their invocation, worker,
generation and static binding slot. The host owns authorization, credentials, frozen model
resolution, shared request limits and pause/cancel controls; workers receive managed proxies.
All FSX domains use this boundary. Native OCR implementations need not move into workers.

Cooperative cancellation targets an invocation. Forced worker restart fails every unfinished
invocation in that instance; it never silently replays an uncertain provider call. A complete
candidate already received by the host can still undergo host validation and atomic commit.
Committed pages remain committed. Windows job ownership ties worker process trees to host
lifetime; shutdown requires confirmed process exit after graceful or forced termination.

Working sources, immutable Active snapshots and independent LKG copies retain separate
ownership. Compilation or activation alone does not promote LKG. Reload does not roll back
Library data. Workers execute trusted code and do not provide an arbitrary-code sandbox,
workflow replay or durable run recovery. Implementation evidence must establish these process
contracts before they are described as delivered.

## Delivery evidence

Implementation and tests must establish each capability before domain documentation describes
it as shipped. Fixture-based protocol tests do not establish real endpoint availability, and
type checking does not prove arbitrary callbacks terminate or produce correct translations.
Current verification evidence and remaining limits are recorded in
[Extension validation](../extension-validation.md).
