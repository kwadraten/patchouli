# FSX extension and HTML table validation

Validation date: 2026-10-21. This records evidence for issues #8 and #26;
it does not replace the contracts in [extension-contracts](extension-contracts.md).

## Current response contract

Translation uses complete responses. The SDK LLM capability, shipped scripts, domain
run API and task queue have no translation streaming or provisional-content preview
path. Automatic step/batch/page progress and cooperative task controls remain.

## Evidence and limitations

The final main-project v10 regression passed: 3,450 cases, 3,443 passed, seven
skipped and zero failed in 11m18s (`.tmp/tests/fsx-worker-full-v10.log`). It used
the repository's existing four-thread conservative xUnit configuration. The long
100-generation replacement case passed separately in 10m7s, with a distinct real
worker and confirmed process exit each round while the host PID stayed unchanged
(`.tmp/tests/fsx-worker-hundred-v9.log`). Production worker source was unchanged
between these runs. Final Debug build and InspectCode results were zero warnings/
errors and zero blocking findings respectively; both published entrypoints also
passed the actual bundled-runtime probes described below.

Real external-service availability and real desktop interaction remain separate
from automated acceptance: Google returned a valid live result in the recorded
probe, DeepL returned HTTP 429, and no DeepLX service URL was provided. HTML editor
interaction was verified headlessly. Historical attempts below retain their
original results rather than being reclassified as successful full runs.

The complete-response focused suite passed 115/115 before the final full-suite
regressions were addressed. Its log is `.tmp/tests/fsx-complete-focused-final.log`.
The matching solution build had zero warnings and errors; the matching inspection
had zero blocking findings. These earlier results do not establish that the final
post-regression cohort has passed all tests.

The first broad run reached 2967 passed and four failed before a three-minute
hang timeout aborted the process. Its log is `.tmp/tests/fsx-full-complete-final.log`.
The timeout was localized to opening the reading workspace in a UI test that did
not start an Avalonia Headless session, before its close command was reached.
The run is not counted as a successful full-suite result.

The post-regression focused cohort passed 31/31 in 7.38 seconds, with a matching
zero-warning, zero-error build. It covers the corrected UI fixtures, legacy-table
validation and atomic writes, MinerU region normalization, progress reporting
architecture and update download tests. Evidence is
`.tmp/tests/fsx-final-regressions-v7.log` and `.tmp/build/tests-ui-headless-v7.log`.
UI fixtures explicitly dispatch and await `Task<bool>` rather than selecting an
`Action`/async-void overload; navigation assertions drain queued UI projections.
The close test validates tab removal, session release and awaited VM disposal.

The subsequent broad v7 run completed in 8m59s: 3,434 total, 3,425 passed, two
failed and seven skipped (`.tmp/tests/fsx-full-final-v7.log`). Failures were an
outdated settings-category expectation and a Windows sharing violation while a
database-switch test deleted an extension check directory after awaited disposal.
The category assertion is being updated; the directory ownership issue is being
isolated. This run is not counted as a green full-suite result.

The v8 production build passed with zero warnings/errors. Its 149-case runtime/UI
focused run passed 148 cases, including the previously deterministic database-switch
cleanup failure (56 seconds) and a gated source-check regression proving disposal
drains existing work and rejects new checks. The remaining failure is the old settings
test assuming every section uses the generic draft editor; extension configuration
has its own explicit persistence commands. Evidence:
`.tmp/tests/fsx-validation-build-final-v8.log` and
`.tmp/tests/fsx-runtime-focused-final-v8.log`.

The separate S3 OCR project passed 17/17 in 15.4 seconds
(`.tmp/tests/fsx-s3-ocr-final-v8.log`). The settings regression was corrected to
explicitly assert the extension section's independent editing contract and passed
on rerun.

## FSI generation evidence

The user authorized replacing in-host FSI execution with host-managed worker processes
on 2026-10-21 after two review rounds with the locally installed Kimi and agy tools.
ADR 0049 now records the process boundary, invocation/slot identity, bounded generation
admission and worker-wide forced-restart semantics. Independent process tests,
100 real generation replacements, bundled-runtime probes and the final main-project
regression now establish the implemented boundary; their evidence is recorded here.

The first independent process cohort passed 6/6 in 1m49s
(`.tmp/tests/fsx-worker-process-first.log`). It exercised typed payload and Box identity
round trips, two static model slots, terminal progress, script output separation,
single-invocation cancellation alongside another run, worker-wide forced restart,
new-instance recovery without replay, manager shutdown with a held lease, bounded
generation admission and a real SQLite translation commit retained after restart.
This is early evidence: subsequent lifecycle/transport and publication fixes require
a fresh final cohort. The metadata/retrieval, child-process and 100-replacement cases
were not included in this first run.

The first self-contained UI publication attempts exposed duplicate worker entrypoint
metadata and conflicting apphost settings. These were actual build failures, not
successful deployment evidence. Publication v5 succeeded after moving the worker
entrypoint into an independent `extension-worker/` bundle while retaining normal
duplicate-output checks (`.tmp/build/fsx-worker-publish-v5.log`). A Release FCS FS3511
warning remains in the existing static checker. A later executable probe exposed a real
SDK dependency: FCS and FSI initially selected installed framework reference packs.
The checker and worker now disable SDK reference resolution and derive framework
references from the running runtime's dependency manifests. Application assemblies
are not added indiscriminately from the trusted platform assembly list.

The final frozen production source published self-contained UI and standalone MCP
server bundles successfully. Both v9 executable probes passed the normal production
static checker, started the actual bundle apphost (verified through MainModule),
executed real FSX translation and confirmed actual process exit. The translation
returned the child's runtime directory, which matched its published `extension-worker/`
bundle. PATH contained only the corresponding publish root. Public FCS/FSI reference
diagnostics belonged to the probe host and contained no SDK/reference-pack paths or
Host/Infrastructure references; these are distinct from the child's observed runtime.
Evidence is `.tmp/build/fsx-worker-final-ui-publish-v9.log`,
`.tmp/tests/fsx-worker-final-ui-probe-v9.log`,
`.tmp/build/fsx-worker-final-headless-publish-v9.log` and
`.tmp/tests/fsx-worker-final-headless-probe-v9.log`.
The headless publication used a relative PublishDir. Both final publication logs
contained no warnings; an earlier clean Release compilation reported the existing
static-checker FS3511 performance warning. PATH isolation alone would not establish
absence of an SDK dependency.

The v8 worker/checker/domain cohort completed 35 cases: 32 passed and three failed
(`.tmp/tests/fsx-worker-focused-v8.log`). The failures occurred during worker
initialization in the OCR, LLM-cache and generation-admission cases. This run overlapped
publication and inspection; isolated reproduction is required before attributing them
to resource contention. Successful cases included real metadata/retrieval round trips,
derived-state clearing, process-tree termination and the corrected frozen translation
model selection. This is not a green final cohort or 100-replacement acceptance.

The three initialization failures passed an isolated v9 rerun with no concurrent
build, inspection or publication (3/3, 1m46s;
`.tmp/tests/fsx-worker-isolated-v9.log`). The original 30-second initialization
deadline was retained. This establishes the rerun result rather than proving that
every earlier failure was caused by contention. The v9 Worker and main test builds
reported zero warnings/errors (`.tmp/build/fsx-worker-test-window-v9.log`).

The strengthened v9 transport cases passed 2/2 in 21 seconds
(`.tmp/tests/fsx-worker-cancellation-v9.log`). A throwing extension cancellation
callback did not terminate another blocked invocation in the same worker. An
oversized reverse-capability reply failed promptly without replay, and a subsequent
normal call reused the same physical worker instance. A timeout is explicitly
rejected as success in these failure-path assertions.

Fresh InspectCode 2026.1.4 passed on the frozen v9 source with zero blocking
findings (`.tmp/build/inspect-fsx-workers-v9.log`, `.tmp/inspectcode.sarif`).
The Worker project explicitly references the Core and Contracts types it directly
uses; no inspection suppression or duplicate-output-check bypass was added.

The independent 100-generation worker replacement case passed in 10m7s
(`.tmp/tests/fsx-worker-hundred-v9.log`). Each round compiled and executed real
FSX through a distinct physical worker, checked its typed result, disposed the
generation and confirmed actual OS process exit. The host PID remained unchanged
throughout. Host static checking ran once for declarations unchanged except for a
trailing comment; this is process-replacement evidence, not a claim of 100 separate
static-analysis runs or arbitrary callback termination.

The first v9 main-project full run completed 3,450 cases in 10m56s: 3,442 passed,
one failed and seven skipped (`.tmp/tests/fsx-worker-full-v9.log`). Its only failure
was an existing UI fixture deleting its temporary SQLite file before shutting down
its MainWindowViewModel. The production shutdown path already drains the library
revision monitor's asynchronous refresh. The fixture now awaits that path before
clearing pools and deleting its own database; no production delay, deletion retry
or warning suppression was introduced. This first full run is not counted as green.

The corrected UI fixture passed independently (1/1, 3.9s;
`.tmp/tests/fsx-worker-ui-fixture-v10.log`). The final v10 Debug build completed
with zero warnings/errors (`.tmp/build/fsx-worker-final-debug-build-v10.log`), and
InspectCode passed again with zero blocking findings after this test-only change
(`.tmp/build/inspect-fsx-workers-v10.log`, `.tmp/inspectcode.sarif`). Production
worker source did not change after the successful 100-replacement and publication
probes.

An executable probe using the actual application dependencies loaded the DeepL-free
package, exercised its real JSON request construction against a fake HTTP capability
and obtained `Bonjour`. With the initial FSI arguments, its three script assemblies
were noncollectible and remained alive after disposal and 30 GC passes
(`.tmp/tests/generation-probe-smoke-v3.log`). This baseline does not establish unloadability.

The pinned FCS 43.12.401 package identifies source commit
`dotnet/dotnet e34a38d2ae1fc26406a317517196e55c68ff83ab`. Its
[FSI compiler implementation](https://github.com/dotnet/dotnet/blob/e34a38d2ae1fc26406a317517196e55c68ff83ab/src/fsharp/src/Compiler/Interactive/fsi.fs#L1644)
passes the collectible flag to the single-emit assembly builder, while the default
multi-emit path uses a different assembly loader. The next probe enabled explicit
`--multiemit-`; the flag alone is not collection evidence.

With single-emit enabled, the next real-package smoke reported a collectible script
assembly, but weak references remained alive after disposal and 30 GC passes.
The process-worker decision supersedes in-host collection acceptance; neither the
collectible flag nor successful translation is counted as unload proof.

An actual headless RichEditor interaction chain loaded HTML, edited neighboring
text, performed undo and redo, normalized the result and validated a savable payload
for plain, merged/header and MinerU formula tables. Evidence is
`.tmp/tests/fsx-rich-editor-edit-chain-run.log`. This is headless interaction evidence,
not a real desktop acceptance run.

Protocol fixtures are distinct from live service checks. The recorded unauthenticated
Google GTX probe returned HTTP 200 and parsed `Bonjour`; the DeepL web probe returned
HTTP 429 and was not retried. No DeepLX endpoint was provided, so its live availability
has not been verified. These observations do not establish ongoing service availability.

MinerU schema and producer evidence, including the difference between native PDF
styles and scanned-table OCR output, is recorded in
[OCR normalization](ocr-normalization.md). Tests must not infer arbitrary styled OCR
output from a generic HTML whitelist.
