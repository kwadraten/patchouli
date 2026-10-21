# Extension contracts

This is the implementation contract for issue #8 and ADR [0049](adr/0049-fsx-extension-pipelines.md).
It is not an acceptance report. Endpoint fixtures, live service availability, static checking,
and document commits are separate verification boundaries.
Recorded verification results are in [Extension validation](extension-validation.md).

| Boundary | Extension owns | Host owns | Required failure behavior |
| --- | --- | --- | --- |
| Package | Source files, implementation, stable extension ID | Immutable snapshots, fingerprint, checker toolchain, generation leases | Invalid candidates leave Active usable |
| Configuration | Stable field keys, types, defaults, ranges, descriptions | Static extraction, generated forms, stored values, run snapshot | Renaming a variable preserves values; dynamic declarations receive source diagnostics |
| Capability binding | Typed slots and default domain requests | Independent authorization, model selection, credentials, managed transport | Explicit unavailable selections fail; declarations never grant access |
| Pipeline | Segmentation, composition, batching, retry classification | Scheduling primitives, request limits, pause and cancellation | Errors short-circuit; cancellation reaches waits and requests |
| Progress | Step names and input identities | Execution identities, elapsed time, deduplicated counts and observer isolation | No percentage without a known total; no default body or credential logging |
| Translation | Context, filters, service protocol, alignment, candidate output | Source ownership and revision checks, atomic page commit | Wrong identities or invalid payloads never commit |
| Metadata | Identifier lookup and candidate content | Read-only proposal, expected-version check, atomic metadata/identifier application | Concurrent edits conflict; invalid identifiers cannot leave partial metadata |
| OCR | Recognition and normalization to MinerU content semantics | MinerU-based shared model, input evidence, geometry/ownership checks, import commit | Provider-specific shapes cannot bypass normalization; candidate generation does not write document trees |
| Retrieval | Query and ranking policy | Source revision/Box mapping, rebuildable derived index, evidence rules | Stale or foreign evidence cannot be promoted into authoritative source content |

## Authoring

Ordinary top-level immutable bindings declare fields through the SDK `bind` entry point.
Keys are persisted identity. FCS resolves SDK symbols and inspects supported constructors,
modifiers and aliases without evaluating the script. A model request is distinct from a
plain configuration field and from host authorization.

`|>>` composes cold typed steps. `Parallel.map` preserves input order while item execution
identities describe completion order. The shared host request gate bounds nested transport
concurrency. `Batch.packWithBudget` rejects oversized inputs so the engine must segment them
explicitly. `Batch.map` returns per-item results; `collect` requires all items to succeed,
while `collectPartial` explicitly retains partial failures. Retry classification excludes
cancellation and gives each attempt its own execution identity under one logical operation.

Progress completed counts describe the named collection or step, not relative execution
cost. Generated output and committed pages have separate events. Translation uses complete
responses without streaming or provisional content previews; generated candidates do not
establish that validation or commit succeeded.

## Host policy

OCR providers share the MinerU-based content model described in
[OCR normalization](ocr-normalization.md). Raw text and text-box results enter the
shared MinerU mapper; already typed candidates must obey the same payload, geometry,
and ownership rules. Box Tree supplies application identity and revisions. A typed
candidate is not permission to introduce a competing provider-specific data model.

HTTP and model permissions are stored independently of extension configuration. Application
packages have an explicit publisher policy; custom packages start without network grants.
The extension settings page can save grants. A run captures its own grants, configuration,
source generation and non-sensitive model settings before business execution. Changes apply
to subsequent runs. Managed handles carry no credential values into script configuration.

Working source, saved editor drafts, Active generations and LKG are independent states.
Saving a draft does not activate it. Explicit activation or an eligible external source
change stages a checked generation and switches Active atomically. Existing leases retain
their original generation. LKG promotion is an explicit operation and does not follow from
saving settings, compiling, activating, or successfully executing one method. Recovery does
not undo committed Library data.

The authorized execution target is a host-managed worker process per package generation;
implementation and acceptance status remain in the validation record. Static checking may
remain in the host. A dedicated duplex pipe separates control traffic from script output.
Reverse managed capability calls are bound to the invocation, physical worker instance,
logical source generation and declared capability slot. The host retains credentials,
authorization, request budgets and data commits.

Generation leases drain before worker disposal. Capacity is bounded: a further activation
cannot evict a leased generation merely to make room. Restarting a worker fails all its
unfinished invocations, with no automatic replay; a fully received candidate and committed
pages remain host-owned. Process termination must be confirmed, including host-lifetime
ownership of the Windows worker job. Trusted worker code is not a security sandbox.
Workflow replay and durable run recovery remain outside this contract. The removed built-in
Agent, chat and workflow scheduler remain removed.
