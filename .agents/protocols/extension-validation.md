# FSX extension and HTML table validation

## Trusted compiled-FSX v3 migration (2026-10-23, in progress)

ADR [0053](../adr/0053-trusted-compiled-fsx-extensions.md) supersedes the worker/FSI and
extension-authorization evidence below. Earlier successful worker runs are historical evidence,
not acceptance of the compiled-DLL runtime.

- Reused the v2 task-level job driver, context/backfill semantics, host result validation,
  atomic commits, scheduler and provider parsing instead of restoring a separate v1 execution path.
- Historical metadata parser reference: commit `99e4eb784e3b718a67494547d62c4a4abd75183d`,
  `src/Patchouli.Infrastructure/Bibliography/MetadataLookup/HttpMetadataSources.cs`
  (`XmlMetadata`, `CalisMarc`, `NlcHtml`, and `JsonMetadata`).
- Production `Support.fsx` sources are compiled into the F# test assembly for direct pure-function
  checks. Protocol cases retain real package implementations and substitute only HTTP/LLM resources.
- The compiler backend and FCS live in `Patchouli.Extensions.Compiler`, not the runtime SDK.
  Bundled packages produce DLL archives and non-executing metadata schemas at build time.
- The build reached compilation/schema inspection of all 22 bundled packages. The initial
  implementation checkpoint preceded the focused results below; Full acceptance remains failed.
- `FSharp.Core` is centrally pinned to `10.1.401`, with an explicit central-version reference
  for every F# project. FCS remains pinned to `43.12.401`. Restore inspection found one Core
  version across 12 current consuming projects (including the separately restored S3 project).

Current integration logs and a migration-start workspace snapshot are under
`.tmp/fsx-v3-migration/20261023-181530/`. The `fsharp-core-resolution.json` file records the
resolved-version inspection; it excludes orphaned build outputs of deleted projects.

Independent exploratory measurements are under `.tmp/ext-spike/run-1/results/` and
`.tmp/ext-spike2/run-{1,2,3}/results/`. They support ordinary-DLL loading, private dependency
versions and metadata-only attribute inspection on the measured machine. NuGet was verified
with an installed SDK, not without one. The second probe found STJ retention mitigated by
metadata-update cache clearing; Newtonsoft mitigation and an unexplained hang remain unverified.
Those measurements do not substitute for production-path tests or cross-platform packaging checks.

### Performance-review follow-up (implementation pending acceptance)

- Translation receives `settings.Common` once per run; segment helpers no longer call the
  reflection settings reader. Runtime construction plans use weak type keys and do not cache
  per-run settings values.
- Artifact lookup is shared and key-addressed. Runtime cache reads no longer rehash DLL/dependency
  contents. Cleanup retains active, staged, draining and LKG artifacts and waits for in-flight
  operations; failed deletion is diagnostic rather than an activation failure.
- The compilation sidecar serializes requests, shares one FCS checker (including NuGet resolution),
  and exits after five idle minutes. This is not an execution worker or a business RPC path.
- Toolchain identity is generated from public API metadata, explicit compiler format and pinned
  package versions. Runtime readers consume a small stamp rather than hashing FCS or implementation
  assemblies. Identical schema/archive outputs are not rewritten.
- Independent no-SDK evidence established that FCS 43.12.401's default NuGet restore requires an
  available SDK; a self-contained runtime is insufficient. The production resolver preflights
  Patchouli's explicit host candidates (`DOTNET_HOST_PATH`, runtime host, PATH and platform
  fallbacks), then sets the selected host in the compiler sidecar's environment for FCS.
  This is not a claim to reproduce every FCS platform-specific search branch. Every declared
  package must appear in the actual resolved references; an unrelated global-cache directory
  does not establish successful resolution. Missing references are diagnosed before `FS0039`.

The follow-up SDK and compiler sidecar builds have passed individually. The updated 22-package
compile/schema command passed at `.tmp/fsx-v3-migration/20261023-181530/compile-bundled-p1-p2-fixed.log`.
These were intermediate checks, not Full or endurance acceptance.

### Acceptance review and follow-up (2026-10-23)

**Overall acceptance: failed.** The review at `.tmp/v3-review/acceptance/` recorded a successful
solution build and inspection, but the Full run at `.tmp/test-runs/v3-accept-full/` failed
29 cases and required-case registration; the separate SDK run passed 78/79. Subsequent
focused checks do not replace that Full result or establish packaging/cross-platform acceptance.

Follow-up implementation:

- Required-case registration now names the compiled OCR generation-pinning and standard
  HttpClient tests. All four registered names exist in the current source; actual Full batch
  execution is still a separate gate.
- The raw-run nested-parallelism test explicitly requests a job concurrency budget of two.
  The former timeout was a test barrier waiting for two requests while the job budget allowed
  only one, not a justification to increase HttpClient's timeout or remove overlap assertions.
- Settings reflection compares property definitions rather than wrapper reference identity.
  A focused group rerun exposed a legal `concurrency` override becoming unrecognizable while
  unload metadata handlers cleared reflection caches; this was not treated as a passing flake.
- Compilation uses `--crossoptimize-` after script-derived options, with compiler format revision
  4 and the flag included in toolchain identity. Previous cached/bundled keys are invalidated.
- SDK preflight includes explicit/runtime hosts and common Unix installation locations, rather
  than relying only on a shell's PATH. Cold NuGet restore still requires an installed SDK.
- Non-worker startup fixtures no longer carry obsolete `real-worker` traits; ADRs 0049,
  0051 and 0052 retain their history with partial-supersession notices linking ADR 0053.

Recorded intermediate follow-up results:

| Run under `.tmp/test-runs/` | Result | Scope |
| --- | --- | --- |
| `fsx-v3-sdk-current-20261023` | 79 passed, 0 failed/skipped | Direct F# SDK and production Support functions |
| `fsx-v3-boundaries-current-20261023` | 187 passed, 1 failed, 0 skipped | Remaining failure: new Unix-candidate test compared normalized expectations with unnormalized output on Windows; fixed afterward |
| `fsx-v3-settings-current-20261023` | 6 passed, 0 failed/skipped | MCP save/navigation routes and incomplete sync/search draft restoration; earlier Full-only failures not reproduced, not proven fixed |

The device-name failure's actual value was ASCII `ADMIN`; garbled console rendering of the
expected Chinese name does not establish an encoding defect. The settings tests and corresponding
production sources had no relevant migration diff. Passing in isolation does not rule out
cross-test state or timing interactions in Full.

The separate concurrent run `.tmp/test-runs/v3-accept2-full/` finished with 3,765 passed,
26 failed and 8 skipped (3,799 total); its compiled-extension batch passed 140/140 and UI
batch 13/13. It still exercised the pre-normalization NuGet candidate test, which failed,
and also contains two `TableEditorToolbarTests` cross-thread failures. Therefore its remaining
failures must not all be labelled an established baseline without a case-by-case comparison.
All four currently registered required cases were present and passed in its TRXs (three in
compiled-extension, the task-registration fixture in ordinary). The two settings regressions
did not recur there. This run is evidence for that intermediate
binary (`run.json` records SHA-256), not for the later successful rebuild.

A later rebuild was interrupted by another acceptance run locking the compiler DLL in the
shared test output. No tests ran in that attempt. Post-review validation must use a successful
rebuild after the shared run releases those outputs; no result from a mixed build is accepted.
Evidence and logs remain under `.tmp/fsx-v3-migration/20261023-181530/`.

#### Post-review focused gates (same rebuilt output)

The successful build at `build-review-isolated-window.log` reports zero warnings/errors;
`postreview-gates.json` records zero exit codes for all four gates below. Between that rebuild
and the Full rerun below, only documentation and the isolated temporary perf probe changed.
Subsequent failure-repair edits require fresh validation; these counts do not validate them.

| Recorded run | Result | Test duration |
| --- | --- | --- |
| `fsx-v3-sdk-postreview-20261023` | 79 passed, 0 failed/skipped | 7 s |
| `fsx-v3-boundaries-postreview-20261023` | 190 passed, 0 failed/skipped | 31 s |
| `fsx-v3-settings-postreview-20261023` | 6 passed, 0 failed/skipped | 2 s |
| `inspect-postreview.log` | JetBrains 2026.1.4 passed, no blocking findings | — |

#### Frozen-output production-path performance probe

Evidence: `.tmp/fsx-v3-migration/20261023-181530/perf-proof/`, including `frozen-files.json`,
`cold.json`, `hmr.json`, `summary.json`, the probe source and build/run logs. The four measured
production DLL hashes match both the frozen inputs and the test output used for focused gates.
Toolchain format revision is 4; identity is
`a008ed5149d45e4f9df7702a690a28f7afba1ef8779f9e9cefabd3d11111ed51`.

One fresh process per mode on Windows 10.0.26100 x64, .NET 10.0.8, Debug, 16 logical processors:

| Operation | Measured duration |
| --- | --- |
| Install real embedded archives for all 22 built-in packages into a fresh runtime directory | 101.022 ms |
| Sequentially activate all 22 through production `ExtensionManager` / compiled loader | 586.645 ms |
| Sum of install and activation intervals | 687.668 ms |
| First activation (LLM, including first-use costs) | 319.203 ms |
| Each subsequent built-in activation | 9.775–18.271 ms |
| First LLM source change: compile and activate with a cold compiler sidecar | 8,515.617 ms |
| Second distinct source change: compile and activate with the same warm sidecar | 3,499.120 ms |

Cold deployment physically omitted the compiler directory, so an unintended source-compilation
fallback could not succeed. Both source-change observations verified the exact deployed compiler
executable and the same PID/start time. Neither host process loaded FCS; the child's loaded
assemblies were not measured. HTTP and LLM resources throw if invoked; no provider traffic or
credentials were involved. Timings exclude complete app/process startup, package enumeration,
instrumentation and disposal, and OS file caches were not flushed. This demonstrates these
production paths on this machine, not a before/after speedup, a Release benchmark, or packaging
acceptance. No high-count loop was run.

#### Full rerun against the post-review output (failed)

`.tmp/test-runs/fsx-v3-full-postreview-20261023/` completed all three batches. The binary
SHA-256 is recorded in its `run.json` and `postreview-validated-snapshot.json`; the latter also
records source/configuration hashes. A structured failure list is preserved as
`full-postreview-failures.json` in the migration evidence directory.

| Batch | Passed | Failed | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| ordinary | 3,614 | 26 | 8 | 3,648 |
| UI | 12 | 1 | 0 | 13 |
| compiled-extension | 140 | 0 | 0 | 140 |
| **Total** | **3,766** | **27** | **8** | **3,801** |

All four required cases are present and passed. The ordinary failures comprise 21 PDF
workspace/preview cases with source-file sharing violations (three also have an earlier
assertion/result error), four previously identified UI selection/thread/close cases, and one
sync draft-restoration failure. The sync failure now affects `emptySyncRoot: True`; it is not
resolved merely because the six isolated settings cases passed. The UI failure is
`ExtensionConfigurationDialogTests.Checking_source_updates_only_valid_forms_and_saving_config_preserves_unlisted_values`:
startup credential loading hits a sharing violation while opening that fixture's settings file.
These last two failures require investigation and must not be relabelled as accepted baseline.

Overall acceptance remains failed. Outstanding: repair of all 27 failures and a clean Full result;
cross-platform/self-contained packaging and production NuGet end-to-end coverage.
No high-count endurance result, commit, push or feature-branch merge is claimed.

#### Subsequent repair work (not yet validated)

At the user's request, six `opencode/deepseek-v4.1-flash-free` agents own separate groups covering
all 27 failures (PDF lifecycle, Markdown thread ownership, grid selection, desktop close
completion, sync draft restoration, credential read/write coordination). Devin CLI with free
`swe-2-high` separately investigates generated NuGet restore-project isolation; it is not
authorized to change the other helpers' files or run shared builds/tests. The original failed
smoke at `nuget-proof/proof.json` established NU1008 / NU1015 import failures, not successful
resolution or offline execution. Source changes from these helpers are pending centralized
cleanup, build, focused tests, Full and inspection.

The test-record summarizer also misclassified completed runs as truncated when xUnit omitted
skipped rows from its `executed` counter, or when adapter messages contained test names with
`cancel`/`timeout`. The pending fix uses actual skipped/result rows and explicit run-abort
messages, with new `TestRunSummarizerTests`. Re-parsing the existing ordinary TRX preserves
every `(case key, outcome)` multiset entry and all counts (3,614 passed, 26 failed, 8 skipped);
only the false incomplete signals disappear. The original failed Full result is not rewritten,
and required-case / unexpected-skip enforcement remains unchanged.

#### Production NuGet compiler and collectible-load follow-up

Devin's restore-isolation fix disables only ancestor MSBuild/central-package imports inside the
compiler process (`ImportDirectoryBuildProps`, `ImportDirectoryBuildTargets`,
`ImportDirectoryPackagesProps`, `ManagePackageVersionsCentrally`). Normal NuGet source configuration
and actual resolved-reference validation remain. A second smoke exposed a further defect:
FCS's forward-slash paths failed prefix comparisons against Windows `DirectoryInfo.FullName`,
so compilation succeeded while `lib/` remained empty. Both sides are now normalized; a pure
mixed-separator regression test was added.

Compiler format revision **5** invalidates prior incomplete artifacts. The defective smoke
used revision 4 / identity `a008ed…`; revision 5 was introduced only after that smoke, so another
bump is not needed for its invalidation. New identity:
`f0a2389e053e0433a94b06877ef70d311a8f8093ea2ec072778be887a215c47b`.

Recorded proof: `.tmp/deepseek-full-fixes/devin-nuget/verified-proof.json`, `smoke-v3/`,
`activation-result.json` and the isolated activation probe source. The compiler rebuilt with
zero warnings/errors; an installed SDK and a fresh package cache resolved fixed Newtonsoft.Json
13.0.3 from a local nupkg feed, compiled the extension with zero diagnostics, and copied its
runtime DLL into the artifact. Cache-only production `ExtensionManager` activation then succeeded
with no compiler deployed and no FCS loaded in the host. The module initializer round-tripped a
fixed public string through Newtonsoft.Json, whose actual loaded path was inside that artifact
and whose ALC was collectible. DLL SHA-256:
`22c649f75fce5be7c7ccda8880473b634ef69ecf33f5d1ab8ad892caf47d5a07`.

For this loader-only proof, the small host stamp was derived from the compiler-generated manifest
using the identical frozen SDK/Core/Contracts DLLs. It is not evidence of packaging's stamp-copy
pipeline. No network capture was performed; no claim of SDK-free-machine, provider execution,
general NuGet compatibility, full unload or cross-platform packaging acceptance follows.
The new regression tests and the 27-failure repairs still require fresh centralized gates.

### First recorded v3 focused run (failed; not acceptance)

The solution reached a successful build before the first recorded runs:

- `fsx-v3-sdk-20261023`: 67 passed, 12 failed, 0 skipped (79 cases).
- `fsx-v3-boundaries-20261023`: 76 passed, 112 failed, 0 skipped (188 cases).

Failures exposed shared implementation/fixture issues: non-public F# record construction,
double-sending one request through two HttpClients, compiled ZIP resources added after the
MSBuild resource pipeline had already captured its inputs, missing token annotations in a script
fixture, invalid fixture ISBN/version values, and old toolchain-description expectations.
Follow-up fixes and reruns are in progress. These counts must not be described as successful
validation or used as evidence that the migrated runtime is ready to commit.

### `#r nuget` without an SDK (external probe, 2026-10-23)

A self-contained FCS 43.12.401 / `FSharp.DependencyManager.Nuget` 10.1.401 probe ran on Debian 13
x64 with no `dotnet` executable, SDK, shared runtime or MSBuild installed. Ordinary FSI scripts
executed. A cold-cache `#r "nuget: Newtonsoft.Json, 12.0.3"` did not resolve: nuget.org and the
nupkg URL answered HTTP 200, but `GetProjectOptionsFromScript` returned zero diagnostics and only
framework references, no package files were written, and execution failed with FS0039. Both the
FCS and resolver host lookups (`getDotnetHostPath`, `getDotnetGlobalHostPath`) returned none.

Consequences:

- The default resolver needs a discoverable `dotnet` host to run its MSBuild restore. A
  self-contained application runtime does not replace the SDK for cold-cache NuGet resolution.
- "No diagnostics" is not evidence of success. Each `#r nuget` directive must be checked
  against the resolved references and fail explicitly instead of surfacing later as FS0039.
- On Windows, clearing `PATH`/`DOTNET_*` does not simulate SDK absence: the host lookup also
  falls back to `ProgramW6432`/`ProgramFiles\dotnet\dotnet.exe`. This explains the earlier
  successful scrubbed-environment run; it was not re-executed on an SDK-free Windows machine.
- Not measured: warm global-cache or resolver-cache hits without an SDK, and the SDK-free
  alternatives (own NuGet client resolution, or pre-resolved package-local DLLs).

## Automatic Transmart client identity probe (2026-10-23)

One serial .NET request with public `Hello world` text, source `en`, target `zh`, the
Transmart web protocol and a newly generated browser-prefix/UUID/timestamp identifier
returned HTTP 200 with one non-empty `auto_translation` item at approximately
2026-10-23 06:48 UTC. The provider reported the success code `succ`; the adapter accepts
that code rather than treating it as a business error. The generated identity and response
text were not logged. Evidence: `.tmp/tencent-client-id/live-probe.json`.

This probe shows acceptance of that generated identifier shape on this machine at that
time, not a durable service/quotas guarantee. The extension generates one anonymous identity
per worker generation and reuses it across runs and retries, without requesting manual input,
persisting machine identifiers or rotating an identity for each request. Explicit non-empty
legacy `client-key` values remain supported; missing/blank values use automatic identity.

## Limited .NET User-Agent comparison (2026-10-23)

This is a small live transport probe from the current machine, not an application run,
not a quota/load test and not acceptance of the new provider extensions. Each request
used a fresh .NET `HttpClient`, public `Hello world` text and no credentials or cookies.
The initial pairs used identical method/query/body/Accept headers except for User-Agent,
were serial, and were separated by three seconds. The compatibility UA was the public
Chrome/123 value used by the pinned KISS Translator reference.
The probe ran in PowerShell 7.6.6 on .NET 10.0.12 with System.Net.Http assembly version
10.0.0.0; it did not execute inside the desktop application's Host or its frozen capability.

| Endpoint | Default .NET headers, no UA | Added compatibility UA |
| --- | --- | --- |
| Google GTX | HTTP 200, expected sentences JSON | HTTP 200, expected sentences JSON |
| Microsoft Edge | IOException while reading the response; no usable HTTP result | HTTP 200, expected translations JSON |

A subsequent Microsoft request without UA and with the same Accept/query/body returned
HTTP 400 with a JSON error, not a translation. An intervening confirmation omitted Accept
and failed JSON parsing; that request is not a controlled UA comparison and is not evidence
of UA necessity. In total this investigation sent six serial requests: two Google and four
Microsoft. No Tencent or DeepL live requests were sent during that UA comparison.

Evidence is stored in `.tmp/reliability-fixes/user-agent-probe.json`,
`.tmp/reliability-fixes/microsoft-no-ua-confirmation.json` and
`.tmp/reliability-fixes/microsoft-no-ua-same-headers.json`; runtime details are in
`.tmp/reliability-fixes/user-agent-probe-runtime.json`. Probe times were approximately
2026-10-23 05:10–05:12 UTC. Only status, response shape/type, timings and error types were
recorded; no user document text, secrets or cookies were involved.

Conclusion: missing UA is not demonstrated to cause Google 429; both variants succeeded
here, which says nothing about a user's different exit IP or active package/configuration.
Microsoft shows a compatibility signal supporting a per-provider configurable UA default,
but this small order-dependent sample does not establish causality or durable availability.
Tencent's UA/Referer requirement is based on the pinned upstream adapter, not a new live
verification. Compatibility headers do not replace shared pacing/cooldown or bypass service
authorization, quotas or interactive challenges.

Validation date: 2026-10-21. This records evidence for issues #8 and #26;
it does not replace the contracts in [extension-contracts](extension-contracts.md).

## 统一任务级翻译接口（2026-10-22）

根据 ADR [0051](../adr/0051-unified-task-level-translation-extension.md)，翻译扩展改为统一任务级接口 `RunAsync(ITranslationJob job, IExtensionCapabilities capabilities, CancellationToken cancellationToken)`，移除了宿主按页调度和 `ExtensionPageExecutionMode`。扩展拥有全任务页顺序、跨页合批、并发调度与页级重试；宿主拥有页身份校验、revision 检查、参考上下文快照、逐页原子提交、取消与进度。

### 验收项

1. **统一任务级接口与契约清理**：
   - `ITranslationExtension` 仅保留单一入口 `RunAsync`；彻底拒绝可选任务级接口与逐页入口并存的双实现。
   - `Contracts.cs` 中删除 `ExtensionPageExecutionMode`、`ExtensionDescriptor.PageExecutionMode`、`TranslationExtensionContext` 与 `TranslationExtensionResult`。
   - `ExtensionHostApi.Current = "patchouli.extensions/2"`；破坏性 API 升级，无向后兼容层。声明旧版 API 的包在校验时被拒绝；非翻译扩展（元数据、OCR、检索）仅需将 manifest 中的 `host_api` 字符串更新为新版本。
2. **宿主执行与原子提交语义（`HostTranslationJob`）**：
   - `NextPageAsync` 单消费者串行取页，按请求顺序返回待翻译物理页；无效页（PageIndex <= 0）、读取失败页、无可翻译叶子 box 页（`Skipped`）及已最新且未强制重译的页（`Skipped`）由宿主在内部结算并上报进度，不返回给扩展。
   - `ReadContextAsync` 按需读取相邻页（|index 差| <= radius）原文（首次读取后冻结缓存）及上一页已存译文（校验 revision 与目标语言）；`PreviousRunTranslation` 恒为 null。
   - `GetCommittedPageTranslationAsync` 优先查询本运行已提交缓存再读库，供顺序依赖读取最新译文。
   - `CommitPageAsync` 逐页原子提交（`PutPageBoxesAsync`），校验页身份与 revision 一致性、translatable 叶子 box 归属与 payload 结构；并发冲突（`AppErrorCodes.Conflict`）自动重载，冲突次数 < 2 时返回 `Conflict` 状态并携带 `RetryPage` 供扩展重跑；终态为 `Committed`、`Partial`、`Skipped`、`Failed`，每个目标页只结算一次。
   - 任务结束时，已取出但未提交的页结算为 `Failed`（“扩展未提交该页”）或 `Cancelled`；未取出的页结算为 `Cancelled`。
   - 进度映射：`ExtensionProgress.ItemId` 必须为页索引字符串，由宿主解析为 `PageIndex` 关联任务进度。
3. **Worker 反向 RPC**：
   - 宿主正向通过 `translate` 传递 `TranslationJobInfo` 启动任务。
   - Worker 通过反向 RPC 方法流式取页与提交：`job/page`（取下一页）、`job/context`（按需上下文）、`job/committed`（查询已提交译文）、`job/commit`（逐页提交结果）。
   - 空值与可选值使用专用包装 record 传递，避免 JSON 反序列化异常；反向调用严格绑定 invocation identity 与 generation lease，支持级联取消。
4. **SDK 驱动与随包引擎**：
   - SDK `Job.fs` 提供 `Job.runPages` 通用驱动：预取窗口管理、按 `ContextKey` 进行跨页合批（`Batch.packWithBudget`）、`options.Concurrency` 并发控制、`Conflict` 自动使用 `RetryPage` 重排。
   - 页级重试由 SDK 驱动接管：整页 outcomes 全部失败且错误码可重试时，依据 `options.MaxRetries` 执行指数退避重排；不可重试分类（参数/权限错误、结构不符、4xx 确定错误等）立即失败。
   - 4 个内置翻译包（`translation.llm`、`translation.google-free`、`translation.deepl-free`、`translation.deeplx`）改写为统一任务级入口；LLM 开启 `backfill` 时通过 `Sequential = true` 限制预取窗口为 1，确保严格页序与上一页译文回填。

### 验证命令

- 项目编译验证：
  - `dotnet build src/Patchouli.Extensions.Contracts`
  - `dotnet build src/Patchouli.Extensions`
  - `dotnet build src/Patchouli.Infrastructure`
  - `dotnet build src/Patchouli.Extensions.Worker`
- 代码风格与静态分析：
  - `pwsh scripts/cleanup-code.ps1`
  - `pwsh scripts/inspect-code.ps1 -UseExistingBuild`
- 核心功能与回归测试：
  - `dotnet test tests/Patchouli.Tests --filter "FullyQualifiedName~TranslationJob"`
  - `dotnet test tests/Patchouli.Tests --filter "FullyQualifiedName~TranslationRunEngine"`
  - `dotnet test tests/Patchouli.Tests --filter "FullyQualifiedName~BuiltInTranslationExtensionProtocol"`
  - `dotnet test tests/Patchouli.Tests --filter "FullyQualifiedName~ExtensionWorkerProcess"`

### 实施与证据状态

> [!IMPORTANT]
> 当前处于接口契约与任务级架构改造阶段，各项功能实现、Worker 管道及测试套件适配正在进行中。
> 实际测试结果、测试通过数量、耗时、日志文件路径与 InspectCode 报告等客观证据待各实施角色全部完成后如实记录；当前未记录测试通过证据，严禁编造虚假测试记录。

以下遗留按实施规格原文记录，完成前不得描述为已交付：

- 页级重试预算口径变化：改造前 `max-retries` 缺失时沿用应用运行设置；改造后由 SDK 驱动
  直接从冻结的 `TranslationExtensionOptions.MaxRetries`（默认 2，`0` 表示不重试）读取，
  不再回退到应用设置。此项需在验收时确认是保留还是补回应用设置回退。
- 旧宿主"已提交页、部分成功页不因页重试而重跑"的独立提交冲突上限（两次）现在由
  `Conflict` + `RetryPage` 表达，与 SDK 页级失败重试分别累计；需用用例固定两者的边界。

## Metadata source extension migration (2026-10-22)

Every built-in bibliographic metadata source is now an FSX package (`metadata.*`), and the
in-process HTTP implementations were deleted. Evidence:

- All 16 bundled packages activate in an `ExtensionManager` and expose their declared source
  id, schemes and default priority: 16/16 cases in `BuiltInMetadataExtensionTests`.
- 16 behavioural parity cases in `BuiltInMetadataSourceTests` replay representative provider
  responses through a fake HTTP capability and assert the parsed candidate for Crossref,
  DataCite, arXiv, PubMed, NDL (two lookups), Open Library, Google Books quota handling,
  Library of Congress, DnB MARCXML, CALIS (search/detail, non-matching record, invalid JSON,
  rate limit) and NLC (Aleph HTML mapping, non-matching ISBN, rate limit).
- The `runs/metadata/control` endpoint, its `runs/metadata/{task-id}/status` projection and
  request_id deduplication pass the MCP command tests; the host `MetadataRunService` batch,
  cancellation, retry and deduplication cases pass their own suite.
- `MetadataLookupSettingsTests` passed 12/12 in 5m01s. The settings page now shows a
  per-source gear that opens the shared extension configuration dialog; the row never queries
  host services, so database switching keeps the existing draft/reload semantics.
- The full solution build completed with zero warnings and errors, and JetBrains 2026.1.4
  inspection with `-UseExistingBuild` passed with zero blocking findings.

### Lazy per-scheme activation and startup hardening (2026-10-22 follow-up)

A live CiNii lookup failed with `EXT0099 … exceeded the 30-second startup deadline`, and the
extension configuration dialog could not open because fleet initialization aborted on the first
failing worker. The fixes were verified as follows:

- `Catalog_keeps_metadata_packages_lazy_and_activates_only_the_requested_source` proves
  `InitializeAsync` leaves every `metadata.*` package inactive, all 16 declarative definitions
  stay discoverable, only CiNii declares NCID, and `EnsureExtensionAsync("metadata.cinii")`
  activates CiNii without starting DataCite.
- `Built_in_metadata_definitions_declare_every_supported_identifier_scheme` pins the 24 declared
  schemes (doi, isbn, pmid, pmcid, mid, arxiv, openalex, mag, semantic_scholar, oclc, lccn, olid,
  google_books, jpno, ndlbibid, ndljp, ncid, naid, crid, dnb, gnd, urn_nbn_de, ark, frbnf).
- `Lookup_queries_matching_sources_in_parallel_and_keeps_priority_order` verifies every matching
  provider is called concurrently, priority decides the candidate, and a source that does not
  declare the scheme is never invoked. The fast metadata suites passed 31/31.
- Extension domain tests passed 2/2 (including real OCR generation pinning) and the
  configuration-dialog suite passed 3/3 after the dialog stopped initializing the whole fleet.
- The worker startup deadline is now 120 s for both pipe connection and initialize; activation
  failures are recorded and skipped instead of aborting initialization, and a failed bundled
  package keeps its editable source for repair.
- The solution build completed with zero warnings and errors. The repository-wide inspection
  gate still reports only pre-existing findings in the concurrently edited `Pdfium*Issue31Tests`
  files; this change's files contain no blocking findings.

Static checking runs in the test host through package activation rather than a bare
`ExtensionCompiler.CheckAsync` loop: the bare 16-package loop intermittently terminated the
test host under memory pressure, while activation-based checking is the same path the
translation suites already exercise and is stable.

## Page retry follow-up

The page retry regression passed 40/40 cases in 52s
(`.tmp/tests/page-retry-20261021.log`), including 15 new cases for response/identity
failures, transient and permanent errors, retry limits, the frozen extension budget,
cancellation during backoff, independent commit-conflict limits, and lost commit
acknowledgements. The existing partial-success test also verifies that an enabled
page retry budget does not repeat an already committed partial page. The isolated
build had zero warnings and errors (`.tmp/build/page-retry-20261021/build.log`).
JetBrains 2026.1.4 inspection passed with zero blocking findings
(`.tmp/build/page-retry-20261021/inspect.log`).
No extension input/output contract or page scheduling mode changed.

## Translation settings and sidebar UI follow-up

The focused configuration run passed both cases (`.tmp/tests/fsx-settings-dialog-focused.log`).
It checks FCS failures preserving the previous form and Active generation, valid-value
retention after schema changes, configuration validation, preservation of unlisted keys,
and copying configuration without copying network authorization.

The final UI regression passed 96/96 cases in 7m41s
(`.tmp/tests/fsx-settings-dialog-final.log`). It covers the settings navigation and fixed
headers, model settings and autosave, the engine gear opening a separate configuration
dialog, source-file switching and draft retention, highlighted source with line numbers,
and discarding without saving configuration or authorization. The reading sidebar test
clicks the relocated cancel/apply actions: cancel leaves the chapter map unchanged,
while apply writes the selected chapter. The translation action retains its command
and appears only in the translation sidebar header; the export controls are absent.

Headless screenshots were reviewed under `.tmp/ui/extension-dialog/` and
`.tmp/tests/chapter-editor-previews/`. The settings gear and translation symbol use
bundled `settings.svg` and `languages.svg` resources. The settings page has neither
the standalone extension navigation item nor an “other extensions” entry. These are
headless interaction/rendering results; the running desktop application was kept open.

The final isolated test-project build passed with zero warnings and errors
(`.tmp/build/fsx-settings-dialog-20261021-impl/build-final-fixtures-retry.log`).
JetBrains 2026.1.4 inspection then passed with zero blocking findings
(`.tmp/build/fsx-settings-dialog-20261021-impl/inspect-recheck.log`).
After fixing the test predicate captures and switching the screenshot through the
application's palette service, both affected interaction cases passed again
(`.tmp/tests/fsx-settings-dialog-appearance.log`). The expanded source editor was
visually checked in the application's dark palette as well as light mode.

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
