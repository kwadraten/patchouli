---
name: patchouli-dead-code-simplification
description: Manually audit or remove dead, test-only, inert, duplicated, or superseded code in Patchouli with production-consumer evidence and .NET/Avalonia/SQLite safety checks. Invoke explicitly with /skill:patchouli-dead-code-simplification.
compatibility: Patchouli repository; requires CodeGraph tools, ripgrep, .NET SDK, and JetBrains Command Line Tools 2026.1.4 for code cleanup or inspection.
disable-model-invocation: true
---

# Patchouli Dead-Code Simplification

Use this skill only when explicitly invoked by the user. Never infer invocation from InspectCode output, an unused warning, another skill, or a general review request. It adapts DeepSeek Harness's evidence-first simplification workflow to Patchouli's C#/.NET, Avalonia, SQLite, MCP, and ADR constraints. It is not a generic “delete every unused warning” pass.

## Invocation

```text
/skill:patchouli-dead-code-simplification audit [scope]
/skill:patchouli-dead-code-simplification clean [scope]
/skill:patchouli-dead-code-simplification verify [scope]
```

- `audit` is the default and is read-only. Survey the requested scope and report a few strong candidates without modifying files.
- `clean` proves and removes high-confidence candidates. The invocation authorizes ordinary repository edits inside the explicitly stated scope, but not product, compatibility, schema, or architectural decisions that remain ambiguous.
- `verify` is read-only. It checks whether a claimed removal is complete and whether surviving compatibility or dynamic roots remain valid; it never removes leftovers.
- `scope` may be a symbol, file, project, feature, current diff, or the whole repository. If omitted in `audit` or `verify`, inspect the current diff and directly adjacent production surface first. If omitted in `clean`, ask for an explicit scope before editing.

If the requested mode or scope is unclear, state the interpretation before acting. Never silently switch from `audit` to `clean`.

## Governing Principle

Prefer a few proven deletions over a long list of guesses. A passing test, an ADR, an interface, or a ReSharper finding is evidence, not automatic authority. Code is removable only after its production, dynamic, external, and persisted consumers have been classified.

A strong simplification reduces an actual maintenance obligation:

- a type, method, interface member, event, DTO field, error/status value, config key, service registration, package, resource, helper, or test artifact has no production consumer;
- tests or documentation are the only consumers and they pin behavior that is no longer load-bearing;
- every implementation supports an interface member no production consumer calls;
- a DTO field, vocabulary variant, warning, or status has no producer and no consumer;
- a setting or request knob is threaded through the system but has no working end-to-end path;
- two stores, events, projections, caches, flags, or state machines mirror the same fact without distinct ownership;
- an old implementation, fallback, adapter, DTO, query, notification, or compatibility path survived its replacement;
- a deployable production type or package exists only to host test fixtures;
- an empty compatibility file or source-text assertion exists only to keep an obsolete test green.

Complex code is not dead merely because it is defensive or hard to read.

## Adaptation Boundary

Borrow from DeepSeek Harness: production-versus-test consumer proof, strong-candidate selection, tests-are-not-golden-truth, complete-obligation removal, explicit trade-offs, and reintroduction conditions. Do not copy its pre-release “reject old data instead of migrating” stance, Cordis/knip assumptions, per-file 100% coverage pressure, bilingual Agent Note lifecycle, or rule that every non-trivial change needs an Agent Note. Patchouli uses supported SQLite migrations, `.agents/CONTEXT.md`, ADRs, its PRD, GitHub Issues, ReSharper, and real Avalonia/MCP/CLI entry paths.

## Non-Negotiable Repository Rules

1. Read root `AGENTS.md`, `.agents/CONTEXT.md`, relevant `.agents/adr/` records, and `.agents/PRD.md` when product scope matters. Surface ADR conflicts instead of overriding them.
2. Preserve unrelated user changes. Start with `git status --short`; never reset, clean, checkout, stage, or rewrite files outside the approved scope. Before the first edit, enumerate the complete planned file set—including declarations, callers, tests, docs, project files, and resources—and compare it with the initial status. If any planned file contains pre-existing changes, stop and ask unless the user explicitly authorizes editing that dirty file.
3. Use CodeGraph before text search for symbols, callers, flow, impact, and project structure. Use `rg` afterward for wire strings, SQL, XAML, JSON names, settings keys, comments, and generated or packaging references CodeGraph cannot see.
4. Do not add automatic or broad suppressions. A verified false positive may use only the narrowest ReSharper suppression at the affected location, with a short comment explaining the dynamic consumer and why it is safe.
5. Historical migrations and compatibility readers are not dead just because current C# has no ordinary caller. Patchouli opens user libraries and preserves snapshots, revisions, evidence identities, and schema epochs. This skill never deletes or rewrites an existing migration. Changes to current tables, columns, triggers, FTS objects, indexes, snapshot allow-lists, schema inspection, or compatibility readers are decision-required: audit and report them, but do not mutate them without separate explicit user approval and the relevant ADR/compatibility analysis.
6. MCP remains text-only and respects the standing path, image, secret, OCR, index, and limited-write boundaries. Simplification never weakens these constraints.
7. After changing non-document code or configuration, run `scripts/cleanup-code.ps1` with an explicit `-Include` list containing only the changed C#/AXAML files before tests. Never run the script with its repository-wide default against a dirty worktree. Before any non-document commit, run `scripts/inspect-code.ps1`. Do not use Full Cleanup or personal ReSharper settings.
8. Do not create an ADR or GitHub issue, edit issue state, commit, or publish merely because this skill was invoked. Do so only when the user explicitly asks. Local mechanical deletion does not need an ADR; changing a durable product boundary, public protocol, schema policy, or architectural decision does. A surface reserved or required by an accepted ADR is not dead merely because its implementation or first producer has not shipped yet.

## Liveness Model

Classify every candidate against all relevant roots. A test reference alone does not make production code live.

### 1. Direct production roots

- `Patchouli.UI`, `Patchouli.Cli`, and `Patchouli.McpServer` entry paths;
- runtime services reached from the manual composition root;
- MCP command dispatch and advertised tools;
- CLI parsing, help, and HTTP mappings;
- background OCR, rendering, snapshot, import, and workflow execution paths.

### 2. Dynamic production roots

Static caller tools may miss:

- Avalonia `*.axaml` `x:Class`, `x:DataType`, compiled bindings, event handlers, templates, converters, dialog registration, and `StyledProperty`/`AvaloniaProperty.Register` wrappers;
- `System.Text.Json` and MCP/settings records with `[JsonPropertyName]`, including deserializer-only constructors/setters; TOON and XML codecs; and Dapper `Query*<T>` construction of private `*Row` types or setters from SQL column names;
- reflection, `Activator`, delegates, callbacks, P/Invoke, native loading, and OS entry points;
- SQL table/column names, snapshot allow-lists, settings keys, resource URIs, package manifests, content files, and packaging scripts.

Read the dynamic registration or parser before accepting an unused warning. If the dynamic use is real, keep it and document the narrow analyzer exception when needed.

### 3. External contract roots

For public DTOs, enum values, constants, and protocol fields, classify direction:

- inbound and still accepted;
- outbound and actually produced;
- persisted and still readable;
- external API/wire compatibility;
- reserved only for a hypothetical future producer.

An inbound or persisted value may be live without a current producer. An outbound-only value that is never produced is a strong candidate. A value with neither producer, consumer, persistence, nor compatibility obligation is speculative surface.

### 4. Data and artifact roots

Treat these separately from ordinary C# reachability:

- current schema versus historical migrations;
- canonical, snapshot, cache, and runtime-local tables or columns;
- indexes justified by representative query plans; absence of a captured plan is not deletion evidence;
- FTS tables/triggers and other rebuildable caches, which remain live infrastructure even though their contents are derived;
- snapshot manifests and allow-lists;
- NuGet direct and transitive dependencies;
- native libraries, model files, icons, content resources, helper executables, and package output.

Use `dotnet nuget why <project> <package>` before calling a centrally pinned package unused. No direct `PackageReference` is not proof of deadness.

### 5. Non-production roots

Tests, performance fixtures, documentation, comments, snapshots, and generated expected output may explain intent but do not prove a shipped consumer. Inspect whether the artifact protects current behavior, compatibility, or merely its own obsolete API.

## Workflow

### Phase 0: Protect the Worktree

- Run `git status --short` and identify pre-existing modifications.
- Record the requested scope and files that may be edited.
- In `audit` and `verify` modes, make no changes.
- In `clean` mode, derive the complete deletion file set before editing and compare it with the initial status. If the full obligation crosses the approved scope or any planned file has unrelated uncommitted edits, stop and ask; never perform a partial deletion merely to avoid a dirty file.

### Phase 1: Load Context and Architecture

- Read the domain vocabulary and relevant ADRs before judging a domain type, MCP field, OCR lifecycle element, schema object, or snapshot surface.
- Identify the candidate's owner and boundary: Core model, infrastructure implementation, OCR contract, MCP wire type, UI state, migration, package, or test support.
- Do not treat an intentional seam or alternate implementation as dead merely because one implementation is currently selected. An unused member inside that seam may still be removable.

### Phase 2: Collect Signals

Use multiple signals; none is sufficient alone:

1. CodeGraph `search`, `node`, `callers`, `impact`, or `explore` for symbols and production flow.
2. Exact `rg` searches for the symbol and serialized/string forms.
3. ReSharper SARIF from `artifacts/inspectcode.sarif`; run `scripts/inspect-code.ps1` only when a fresh full-solution result is appropriate for the worktree.
4. Project references, `AppServices`, MCP/CLI dispatch, XAML, JSON/Dapper mappings, SQL, settings, snapshot lists, package manifests, and packaging scripts.
5. Tests and docs, explicitly separated from production consumers.
6. Git history only when it explains whether a path is an unfinished migration, a superseded implementation, or an intentional compatibility commitment. History is supporting evidence, not the only source of rationale.

Relevant JetBrains findings include `NotAccessedField.Local`, `UnusedVariable`, `UnusedMember.Local`, `UnusedType.Global`, `ClassNeverInstantiated.Global`, `UnusedMember.Global`, and `UnusedMemberInSuper.Global`. Do not copy Roslyn `IDE00xx` identifiers into the InspectCode gate; InspectCode reports JetBrains rule IDs and `CSharpWarnings::CSxxxx`.

### Phase 3: Build a Consumer Table

For each candidate, record:

| Evidence | Finding |
|---|---|
| Definition and owner | Where the obligation lives |
| Direct production callers | Exact callers, or none |
| Dynamic callers | XAML/serialization/reflection/SQL/config/package references |
| Producers and consumers | For events, states, DTO fields, warnings, settings, and vocabulary |
| Test/doc-only callers | Whether they protect current behavior or only the candidate itself |
| Persistence/compatibility | Schema, migration, snapshot, wire, URI, or old-data obligations |
| ADR/PRD rationale | Current decision, superseded decision, or none |
| Removal surface | Declaration, implementations, tests, docs, DI, settings, packages, resources |
| Risk and reintroduction | Capability lost and what real consumer would justify bringing it back |

Search for both the symbol and its external spelling. For methods, inspect interface declarations, every implementation, fakes, and all call sites. For mirrored state, draw ownership and lifecycle transitions before proposing collapse.

### Phase 4: Classify the Candidate

Use one of these outcomes:

- **Remove now** — no direct, dynamic, external, persisted, or intentional consumer; deletion is local or mechanically complete, touches no schema/migration/compatibility surface, and fits the approved clean scope.
- **Remove as a bounded simplification** — evidence is strong, but removal crosses multiple projects or deletes a public surface. In `clean` mode proceed only when the invocation scope clearly authorizes it; otherwise present it for confirmation.
- **Product/architecture decision required** — a production caller exists, compatibility would change, an ADR owns the behavior, or the proposal removes a capability rather than dead implementation. Do not disguise it as cleanup.
- **Keep: dynamic/compatibility root** — static warning is a false positive or the code reads supported historical/external data.
- **Keep: intentional seam or lifecycle safety** — the machinery protects a distinct transition, rollback, cancellation, transaction, callback containment, resource ownership, dispose-to-quiescence, or security rule.
- **Reject as thin** — naming/style cleanup without meaningful surface reduction. Do not inflate the audit with it.

Tests are not golden truth. If a test exists only to exercise a removed API, delete or rewrite that test. Conversely, do not delete a negative contract test that prevents a retired path from returning.

### Phase 5: Report or Remove

#### Audit mode

Return a ranked table with no more than a few strong candidates unless breadth was requested:

| Candidate | Classification | Production evidence | Dynamic/compat evidence | Proposed deletion | Confidence |
|---|---|---|---|---|---|

Also list representative rejected candidates and why they are live. Prefer “no strong candidate found” over weak guesses.

For a durable architectural simplification, offer a GitHub issue or ADR proposal with:

- Problem;
- exact production-consumer evidence;
- proposed deletion;
- strongest reason to keep it;
- capability given up;
- compatibility and migration implications;
- acceptance criteria;
- reintroduction condition.

Do not create it without explicit approval.

#### Clean mode

Delete the complete obligation, not only its most visible declaration:

- contract/interface/DTO/vocabulary member;
- every implementation and adapter hook;
- DI/composition registration and feature flag;
- tests that only pin the removed behavior;
- stale docs, comments, XML docs, generated contract outputs, and expected fixtures;
- obsolete settings, package references, resources, helper files, build entries, and packaging rules;
- duplicate cache/query/event/notification/fallback path replaced by the surviving owner.

If complete removal requires a file outside the approved scope, a dirty file, or a decision-required data/compatibility edit, stop and report the candidate instead of leaving a half-removed obligation.

Do not leave a no-op method, obsolete alias, commented code, empty anchor file, or “maybe later” TODO merely to preserve an unused API. Preserve compatibility readers and shims required by supported libraries, snapshots, wire contracts, or accepted ADRs; their lack of ordinary callers is not removal authority. Re-add speculative future surface with its first real consumer rather than reserving it now.

When replacing hand-rolled code with a dependency, treat it as a separate high-impact simplification: verify maintenance, license, transitive/native footprint, exact semantic coverage, and net deletion. A wrapper that relocates the same complexity is not a simplification.

### Phase 6: Verify Absence and Behavior

After deletion:

1. Repeat exact symbol and external-string searches. Hits in ADR/history/negative tests may be legitimate; classify them rather than demanding zero repository-wide hits.
2. Re-run CodeGraph callers/impact or equivalent searches to detect orphaned callers, interfaces, registrations, and fakes.
3. Confirm UI, MCP, CLI, serialization, schema, snapshot, and packaging closed sets remain coherent.
4. Run cleanup on changed C#/AXAML before tests:

```powershell
./scripts/cleanup-code.ps1 -Include @('<changed-file-1>', '<changed-file-2>')
```

5. Run focused tests that exercise the surviving real entry path. Add broader build/test commands only when the impact requires them. Coverage can identify unexecuted paths but does not prove deadness or correctness.
6. Run `./scripts/inspect-code.ps1` before a non-document commit. It inspects the full solution and may fail because of unrelated pre-existing worktree findings; if so, do not fix outside scope or claim a clean delta. Preserve the SARIF path, report that the global signal is contaminated, and separately report the changed-file findings you can attribute.
7. Run `git diff --check` and inspect the final diff for unrelated churn.

A simplification is complete only when the removed behavior is absent from production code, configuration, current schema, wire output, packaging, and supported tests, or when each surviving compatibility artifact has an explicit reason.

## Output Receipt

Always report:

- scope and mode;
- removed candidates and total surface deleted;
- candidates retained and why;
- files changed;
- tests/inspection/cleanup commands run and their results;
- dynamic, persistence, protocol, or migration checks performed;
- pre-existing worktree changes kept untouched;
- residual risks and any decision that still needs a human.

Do not equate passing tests with proof of deadness. The proof is the consumer analysis; tests verify that the surviving product still works.
