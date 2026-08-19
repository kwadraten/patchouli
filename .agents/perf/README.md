# Performance smoke harness and baseline (V3-T7 S0, AC1/AC11)

This slice owns the repeatable, privacy-safe performance harness, the committed baselines, and
the CI smoke gate for V3-T7. It covers the S0 deliverable (fixed fixture, statement count, query
plan, WAL size, cache behavior, MCP cold/warm baseline) plus AC1 (repeatable metric report), AC2
(interactive framework / first library rows budgets), AC3 (100 ms UI heartbeat during box
adoption), and AC11 (small CI smoke with obvious-regression detection; full-fixture three-run
budget enforcement on a designated runner).

## What the harness measures

`tests/Patchouli.Performance` (`patchouli-perf`) seeds a deterministic synthetic Library through the
same services the production host uses, then drives representative operations through the MCP read
path (`McpReadApi`) and the OCR working/commit path (`DocumentTreeService`). Every run reports,
per operation, the **median / p95 / min / max / mean latency**, **SQL statement count**, **rows
read**, and **allocated bytes**, plus:

- first library rows (`BrowseItemsAsync(0, 20)`)
- MCP item / document status / document outline / page text (cold and warm) / page blocks /
  evidence fetch
- OCR stage + adopt of a revision
- compiled-Markdown cache behavior (cold vs warm page text; direct hit/miss counters are owned by
  the S4 shared-cache work and covered by unit tests)
- `EXPLAIN QUERY PLAN` of the browse query, normalized (literals replaced) so it carries no content
- database/WAL bytes after seed and after the OCR write, journal mode, busy timeout

SQL counting uses a counting connection that wraps every command the measured services issue. It
records only integers; it never captures SQL text, parameters, paths, or content.

## Real-UI probes (AC1/AC2/AC3)

Passing `--ui` runs the UI probes on the **real UI boundary** (headless Avalonia via
`Avalonia.Headless`, the same infrastructure the UI test suite uses). All timings are measured on
the actual UI dispatcher against a real `MainWindow`, real view models, and the real host services —
nothing is synthesized with a Stopwatch around a console-side SQL query. The probes seed their own
isolated fixture database (same `--items` / `--pages-per-item` / `--boxes-per-page` scale flags) and
report:

- **Interactive framework cold/hot** — time from view-model/window construction to a shown,
  measured, and arranged window on the dispatcher (the framework boundary; native window/render
  setup is not included in headless, so absolute budgets are enforced only on the designated
  runner, see below).
- **First library rows cold/hot** — time from the interactive framework to the first library rows
  projected into the shell, through the real cold-open path (`AppServices.CreateAsync` migrations +
  OCR reconciliation + queue start, then the first-rows query).
- **100 ms heartbeat max-gap during box adoption** — a 100 ms heartbeat is posted to the UI
  dispatcher while the fixture's full box count is staged + adopted through `DocumentTreeService`
  on background workers; the max observed tick gap is the AC3 responsiveness signal. The counting
  connection also records `UiThreadDatabaseCommands`: any database command that executes on the UI
  dispatcher thread fails AC3's "no DB work on the UI dispatcher" requirement.

A **scalable profile** reaches the PRD magnitudes: `--items 1000 --pages-per-item 10
--boxes-per-page 50` stages + adopts 500 000 boxes during the heartbeat window on a designated
runner.

## Privacy

AC1 requires performance logs to not record document text, query content, local paths, EvidenceRef
values, or secrets. The harness only serializes counters, latencies, fixture scale, and environment
facts. `ReportPrivacy.AssertSafe` runs the report through the same sanitizer patterns the MCP
surface uses plus explicit forbidden markers (`evref:v2:`, `Bearer `, `sk-`, `api_key=`,
`provider_secret`, `file:///`) and fails the run (exit 3) on any leak. The UI report section carries
only durations, sample counts, and counters — never content or paths.

## Running

```pwsh
# Small smoke fixture (runs in normal test time), writes a JSON + optional markdown report
dotnet run --project tests/Patchouli.Performance -- --profile smoke

# Smoke with regression check against the committed baseline + real-UI probes (what CI runs)
./scripts/run-perf.ps1 -Profile smoke -Check -Ui

# Full fixture on a designated runner, with three-consecutive-run budget enforcement (AC11):
# the gate fails only when all three runs exceed the budget
./scripts/run-perf.ps1 -Profile full -Check -FullBudgetCheck

# Same, plus real-UI probes and AC2/AC3 budget enforcement (--enforce-ui-budgets):
# interactive framework ≤ 2000 ms cold / 1000 ms hot, first rows ≤ 3000 ms, heartbeat ≤ 250 ms
./scripts/run-perf.ps1 -Profile full -Check -FullBudgetCheck -Ui -UiBudgetCheck

# Emit a new baseline after a hardware/OS change (designated runner)
dotnet run --project tests/Patchouli.Performance -- --profile full --ui --emit-baseline .agents/perf/baseline.full.json
```

`dotnet run` writes reports to `artifacts/perf/` (gitignored). Baselines live in
`.agents/perf/baseline.{smoke,full}.json` (committed, versioned with the code). See
`patchouli-perf --help` for all options.

## Scaling to the full fixture

The repository ships no large binary fixture. The `full` profile defaults to 100 items × 8 pages ×
25 boxes (20k boxes, ~20k search units) which matches the row shape of the PRD fixture at a
size that still runs locally. On the designated runner, scale up to the PRD magnitudes without a
20 GiB payload with the CLI flags `--items`, `--pages-per-item` and `--boxes-per-page`:

| Flag | PRD fixture | Set to |
|---|---|---|
| `--items` | 100 items | `1000` |
| `--pages-per-item` | (pages implied by 500k boxes / 150k units) | `10` |
| `--boxes-per-page` | 500k DocumentBox total | `50` (→ 500k boxes) |

For example:

```pwsh
# Via the runner script (forwards --items/--pages-per-item/--boxes-per-page)
./scripts/run-perf.ps1 -Profile full -Check -FullBudgetCheck -Ui -UiBudgetCheck -Items 1000 -PagesPerItem 10 -BoxesPerPage 50

# Or directly
dotnet run --project tests/Patchouli.Performance -- --profile full --ui --enforce-ui-budgets --items 1000 --pages-per-item 10 --boxes-per-page 50
```

Source-file scale (20 GiB aggregate, ≥500 MiB PDF) is intentionally out of scope for this slice:
those fixtures serve the AC2/AC6/AC15 startup/PDF budgets and belong to the PDF viewing-session
slice on the designated runner. Raw results, environment, and generator version are recorded in the
report so budgets are comparable across runs.

## Regression gate

`--check` compares the run against a baseline with two kinds of budgets:

- Deterministic metrics (SQL statements, rows read, allocations): strict multiplicative budgets
  (default `--det-tolerance 1.5`, `--alloc-tolerance 2.0`). These are machine-independent, so they
  are the primary CI regression signal.
- Latency: both a relative budget (`--latency-tolerance 3.0`) and an absolute ceiling
  (`--latency-ceiling-ms 2000`). Latency is only an "obvious regression" signal; it is never a
  fine-grained gate, so a slower or faster runner does not produce flaky failures.

The real-UI metrics are compared the same way (relative + ceiling) when the baseline includes them.
`--enforce-ui-budgets` additionally enforces the **absolute** AC2/AC3 budgets (2000 ms interactive
framework cold, 1000 ms hot, 3000 ms first library rows, 250 ms heartbeat max-gap) as hard
failures. Enforcement is opt-in and is meant for the designated runner's full (scalable) fixture,
because headless numbers exclude native window/render setup and a generic runner is not the stated
benchmark machine.

`perf-smoke.yml` runs the smoke profile with `--check --ui` on every push/PR, and `run-perf.ps1
-FullBudgetCheck` / `-UiBudgetCheck` implement AC11's "three consecutive over-budget runs fail the
build/release check".

## Feasibility notes (reported gaps)

- **UI dispatcher max pause** is now measured by the real-UI heartbeat probe (`--ui`). The
  heartbeat runs on the actual headless Avalonia dispatcher while a real box adoption executes on
  background workers; it is never synthesized in the console.
- **Per-statement lock wait** is not exposed by the SQLitePCLRaw version in use; the harness
  reports the configured busy timeout instead. Lock-wait measurement belongs with the S1 executor.
- **Direct cache hit-rate counters** (hits/misses/evictions) are instrumented in the shared read
  layer by the S4 work and asserted by unit tests; the harness reports cold/warm behavior so the
  report stays stable across slices.
