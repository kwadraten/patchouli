# 扩展 Patchouli

## Core、Extension 与 Plugin

| 层 | 职责 | 状态 |
|---|---|---|
| Core | 身份、校验、事务、历史、并发、证据与快照规则 | 由宿主维护，不能替换 |
| Extension | 可替换的核心业务动作实现，例如翻译引擎、元数据来源 | 正在实现：可信编译 FSX 包；尚未完成验收 |
| Plugin | 独立应用，可以拥有自己的数据，通过 MCP 使用书库 | 规划中，尚不可用 |

扩展的业务契约是返回候选结果，由宿主校验并提交；但扩展是宿主内运行的可信代码，不是安全边界或沙箱。不得把“不能访问数据库/文件/网络”当作安全保证。

## 扩展包与生命周期

- 每个扩展包有 `main.fsx` 入口，可 `#load` 包内 `.fsx`；包源码位于应用数据目录下的 `extensions/{extension-id}/`。入口 XML 文档仍是必需契约：在 `let extension` 前使用 `/// <patchouli-extension id="…" kind="…" version="…" host-api="patchouli.extensions/3">`，并包含 `<name>`。它不是 attribute 元数据。可参考随包 `main.fsx`。
- FSX 源码编译为 DLL，按 generation 缓存并在宿主内 collectible `AssemblyLoadContext` 执行。允许外部 BCL、包内 DLL 和 NuGet 依赖。NuGet 冷还原需要已安装的 .NET SDK；只有 runtime 的安装不能替代 SDK。包内 DLL、BCL 引用和已验证编译产物缓存命中不需要 NuGet SDK 还原。依赖版本由仓库中央管理：`FSharp.Core` 10.1.401、`FSharp.Compiler.Service` 43.12.401。可信代码不是沙箱。
- 同目录下的 `.generations/`（运行中的不可变快照）、`.lkg/`（恢复快照）、`config.json` 和 `extension-state.json` 由 Patchouli 管理，不要编辑；`.lkg/AGENTS.md` 也说明了这一点。只修改包根目录下的源码。
- 桌面入口：“文献加工 → 文献翻译”的引擎列表，以及“元数据来源”的每一行，都有齿轮按钮，打开扩展配置窗口。
  1. **检查并保存**：用 FCS 静态检查后保存源码草稿。检查失败时，运行中的版本和表单保持不变。
  2. **应用源码**：再次检查后，原子切换到新的运行版本（generation）。已经开始的任务继续使用原来的版本。
  3. **将运行版本保存为恢复副本**：显式更新 LKG（last-known-good）。加载成功不会自动更新 LKG。
  4. **回滚**：从 LKG 恢复运行版本。它不撤销书库中已提交的数据，也不覆盖源码草稿。
- 高级操作可以把现有扩展复制为新 ID。样例试跑不切换运行版本，也不写入书库。静态检查与样例试跑都不能证明线上服务可用。
- 普通打开配置窗口不等于检查或激活源码。已验证的 Active 描述与随包构建索引可直接显示字段；未知或不匹配的源码可能显示“未检查 / schema 不可用”，此时显式检查后再配置，不通过反复打开窗口触发编译。

## 编译规则

- FSX 以 `--checknulls+` 编译，nullness 警告 FS3261 视为错误，并且编译警告不能被关闭。宿主契约的 C# 可空标注就是脚本看到的类型：
  - 标注为 `T?` 的值，用 `Option.ofObj` 在使用处转换一次（例如 `box.Text`、`job.Progress`、`NextPageAsync` 的结果），之后在内部传递 `option`，用 `Some` / `None` 做模式匹配。
  - 必填值（例如 `job.Info.Options`、`job.Info.Config`）直接使用，不做判空。没有内容的集合用空集合表示。
  - 向契约写回可选值时用 `Option.toObj`。
- 配置使用带 `[<Settings>]` 的 F# record；普通字段用 `[<Field(...)>]`，可选默认值用 `[<Default(...)>]`，数值约束用 `[<Range(min, max)>]`，模型用 `[<Model(...)]`。嵌套公共设置 record 可放在 `Common` 字段上，并通过父字段的 `[<Override("key", value)>]` 覆盖该包的默认值。配置由 `ConfigSettings.Read<Settings>` 读取；元数据 schema 由宿主读取，不执行属性之外的脚本逻辑。保留编译器警告和 schema 校验。

### NuGet 引用与 SDK 要求

- `#r "nuget: Package.Id, 1.2.3"` 的冷依赖解析会调用 FCS 43.12.401 的 NuGet dependency manager，由它发起 SDK/MSBuild 还原。NuGet 全局包缓存本身不保证能在没有 SDK 时解析；即使 FCS 没有报告选项错误，也必须确认每条声明确实产生了对应的解析引用。
- 只有 .NET runtime 的机器不满足冷 NuGet 还原要求。无 SDK 时可用的路径是 BCL 引用、包内真实 DLL（例如 `#r "lib/Dependency.dll"`），或已验证编译产物缓存命中；这不等于支持离线 NuGet 还原。
- Compiler sidecar 的 SDK 定位顺序是：`DOTNET_HOST_PATH`、当前进程/运行时对应的 dotnet host、按顺序搜索 PATH 目录、Windows `ProgramW6432` 与 `ProgramFiles` 目录，随后尝试 `/usr/share/dotnet`、`/usr/local/share/dotnet` 和 `~/.dotnet`。候选必须通过 `dotnet --list-sdks` 确认有 SDK；runtime-only host 不算 SDK。选中的 host 只在编译 sidecar 进程内设为 `DOTNET_HOST_PATH`，令 FCS 使用同一 host。
- 这是 Patchouli 的明确查找顺序，不宣称完整复刻 FCS 内部所有平台查找分支。只读检查依据 pinned `FSharp.Compiler.Service` 43.12.401 nuspec 指向的 `dotnet/dotnet` source commit `e34a38d2ae1fc26406a317517196e55c68ff83ab`；反射 `Internal.Utilities.FSharpEnvironment.getDotnetHostPath`、其 continuation、`probePathForDotnetHost` 和 `getDotnetGlobalHostPath` 确认包含 `DOTNET_HOST_PATH`、PATH probe 以及 Windows `ProgramW6432` / `ProgramFiles` fallback。Patchouli 显式优先相同的 `DOTNET_HOST_PATH`，并额外提供当前 dotnet runtime host 与常见 Unix 安装位置候选。

## 配置与托管能力

- 每个 root record 标记 `[<Settings>]`。`Field` 的 `Key` 是持久化身份，应保持稳定；`Default` 提供字段默认值，`Range` 声明数值范围，`Model` 声明模型选择及可选默认域。
- 可嵌套公共设置 record（通常命名 `Common`）。父属性上的 `[<Override("key", value)>]` 覆盖该扩展的嵌套字段默认值；用户已保存的值优先于覆盖默认值。
- Factory 用 `ConfigSettings.Read<Settings>(snapshot)` 将冻结配置读入 record。
- 工厂为 `Extension.createTranslation<Settings>` 和 `Extension.createMetadata<Settings>`；入口身份与 descriptor 由加载器提供。
- `IExtensionResources.Http` 提供标准 `HttpClient`；`ResolveLlm(defaultDomain, selection)` 获取托管 LLM，未指定 selection 时可跟随默认模型。凭据按引用调用 `GetCredentialAsync(reference, token)`，只在使用点取得明文，并避免写入日志、提示词或异常。
- FSX 扩展访问联网不经过单独的联网授权流程；MCP 权限控制属于独立边界，不授权或限制可信 FSX 代码。不要把 MCP 权限误当成扩展网络沙箱。
- 模型调用使用完整响应 `CompleteAsync`，没有流式输出。

## SDK

- `Step.sync` / `Step.async` 构造 pipeline，`|>>` 串接各步骤。`Pipeline.run progress phase pipeline input token` 才真正执行；`progress` 是 `IProgress<ExtensionProgress> option`，传 `None` 时继承外层 `ExecutionScope` 的观察者。
- `Parallel.map maxConcurrency work items token` 保持输入顺序，并为集合和每个 item 自动发布进度。外层 `ExecutionScope` 会进一步限制并发。
- `Batch.packWithBudget budget measure items` 按条目数、字符数和可选 token 估算分批。单个 item 超出预算时会抛出错误，必须先安全切分，不能截断。`Batch.map` 的回调必须按原顺序为每个输入返回一个 `Result`；`Batch.collect` 遇到任何错误即失败，`Batch.collectPartial` 分别返回成功项与失败项。
- `Retry.withPolicy` / `Retry.run` 提供请求级重试。取消令牌要一直传递到资源、pipeline、retry 和 batch 调用。
- 扩展持有的资源要在 `IDisposable` / `IAsyncDisposable` 中释放。generation lease 排空后 ALC 尽力卸载；卸载失败会警告，但不阻止激活或要求重启。

## 翻译扩展（任务级契约）

翻译 factory 接受 `Settings -> ITranslationJob -> IExtensionResources -> CancellationToken -> Task` 的 run 回调；一次调用接收整个任务。由扩展决定取页顺序、合批、并发与重试；宿主负责给页、提供参考上下文，并逐页原子提交。实际入口和签名请复制随包 `main.fsx`。

`ITranslationJob` 提供：

- `Info`：任务 ID、文献、源语言与目标语言、`Options`（批大小、字符与 token 上限、并发数、重试次数、上下文半径、术语表等），以及冻结的 `Config`。
- `Progress`：可选的进度接收器。
- `NextPageAsync`：按请求顺序返回下一个需要翻译的页面，取完时返回 null。不需要翻译的页面由宿主自行结算，不会交给扩展。
- `ReadContextAsync(page, radius)`：按需读取相邻页原文和上一页已存的译文；首次读取后在本次运行内冻结。
- `GetCommittedPageTranslationAsync(pageIndex)`：读取某页当前已提交的译文，包括本次运行刚提交的页面。
- `CommitPageAsync(result)`：校验并原子提交一页。

规则：

1. **正常执行时一直取页直到返回 null**。不要自己按页码递增跳读。取消或明确的限流终止可以提前停止，但必须让宿主结算未完成页，不能把提前停止报告成整本文献翻译成功。
2. **取出的每一页都要提交**。扩展返回或被取消时，已取出但未提交的页会被结算为失败或取消。只有当结果为 `Conflict` 时才能重新提交同一页：此时用携带的 `RetryPage` 重新翻译后提交。其他情况下每页只结算一次，重复提交无效。
3. **结果必须与可翻译叶子 box 一一对应**。`page.Boxes` 中还包含只作上下文的 box。可翻译叶子 box 是指：没有子节点、未隐藏、类型为可翻译文本类，且文本非空。
   - 每个可翻译叶子 box 必须恰好有一个 outcome：`Translated`、`Skipped` 或 `Failed`。
   - 下列情况会使整页以 `BatchMismatch` 失败：漏掉某个 box、多出不可翻译的 box、出现重复 ID，或 `Translated` 结果的内容不可用。
   - 可翻译文本类型：text、title、ref_text、list、table、algorithm、index、header、footer、aside_text、page_footnote，以及 image/table/chart/code 的 caption 与 footnote。
    - 随包引擎的 `Support.fsx` 实现了分段、结构保护和 outcome 合并等共享逻辑；优先复制整个随包引擎及其 Support 源码作为起点，不要复制改写其算法。
4. **结构化内容使用强类型 payload**：`ExtensionBoxOutcome.FromPayload(box.Id, payload)`，表格直接返回 `TableBoxPayload`。同一结果不能同时提供强类型 payload 和 JSON payload。宿主会校验 payload 与源 box 的类型是否匹配，以及表格结构和受保护的公式。
5. **页序依赖**：页面依赖上一页刚提交的译文时，用 `GetCommittedPageTranslationAsync(previousIndex)` 读取，并把该页标为 `Sequential`。
6. **进度**：页级进度事件把 `ExtensionProgress.ItemId` 设为页索引字符串，宿主据此关联任务队列中的页面。

### SDK 驱动 `Job.runPages`

`Job.runPages job options budget translator token` 实现上述全部规则：有界预取、按 `ContextKey` 跨页合批、有界并发、逐页提交、冲突时重新准备，以及页级重试。

- `JobTranslator` 有四个回调：
  - `Prepare`：把一页转换为 `{ Page; State; Items; ContextKey; Sequential }`。
  - `Measure`：返回 `字符数 * token 估算 option`。
  - `Translate`：对一批 item 按原顺序返回同样数量的结果。
  - `Finish`：把一页的结果映射回 box outcome。结果以 `Result<'TResult, exn> array` 传入：整批请求失败时，该批每个 item 收到 `Error ex`，由引擎把它转换为带错误码的 `Failed` outcome。
- 只有 `ContextKey` 相同的页面中的 item 才会合进同一批。只有能在一次请求中翻译多段的引擎才能从中获益；逐段翻译的服务每段仍是一次请求。
- 遇到 `Sequential` 页面时，预取窗口缩为 1，并在该页提交后才取下一页。
- 页级重试只在整页 outcome 全部失败、且错误码都可重试时发生，次数取 `Options.MaxRetries`。退避从 250 ms 开始翻倍，等待期间可以取消。
   - 不重试的错误码：`translation_request_rejected`（服务方明确拒绝，例如 408/429 以外的 4xx）、`rate_limited`、`quota_unavailable`、`translation_invoker_unavailable`、`invalid_argument`、`validation_failed`、`unsupported_operation`、`not_found`、`library_mismatch`、`mapping_required`、`stale_settings_revision`、`database_error`。
  - 没有错误码的失败按可重试处理，但仍受次数上限约束。
  - 已提交、部分成功和已跳过的页面不会重跑。冲突重试与失败重试分别计数。
- `Job.runPagesSequentialWithFailurePolicy` 是传统 HTTP 的显式稳定策略，页内 batch 也串行；普通 `runPages` 的并发语义不变。LLM 的 `Sequential` 页序依赖不意味着其页内请求必须串行。

### 作者入口

这里不提供独立的可编译模板：工厂和任务 API 仍在演进，手写片段容易遗漏配置、叶子 box 处理、结构保护或提交规则。请复制最接近的随包 `main.fsx` 与 `Support.fsx`，保留入口 XML，按需调整 `[<Settings>]` record，并通过 `Extension.createTranslation<Settings>` / `Extension.createMetadata<Settings>` 创建实现。纯算法应留在可共享的 Support 层；HTTP、凭据和 LLM 调用只在 run/lookup 边界使用 `IExtensionResources`。

### 随包翻译引擎

| 包 ID | 实现 | 注意 |
|---|---|---|
| `translation.llm` | 托管 LLM；跨页合批多段请求，按需读取上下文窗口，支持术语表，保护代码、公式与标记，并逐段校验。开启 `backfill` 时按页序执行。 | 可选择模型；未指定时按默认模型解析。prompt 不是安全边界。 |
| `translation.google-free` | 无凭据的 Google GTX 网页协议，每段一次请求。 | 非官方协议，稳定性不受承诺。 |
| `translation.deepl-free` | 无密钥的 DeepL 网页 JSON-RPC。 | 非官方协议，可能被限流或改变。 |
| `translation.deeplx` | 用户配置的 DeepLX 端点，可附带凭据引用。 | 没有端点时无法验证线上可用性。 |
| `translation.microsoft-free` | Microsoft Edge 非官方网页数组接口，页内多段合批并顺序发送。 | 不是 Azure 免费套餐；兼容 UA 可覆盖或清空，线上稳定性不受承诺。 |
| `translation.tencent-free` | Tencent Transmart 非官方网页数组接口，自动生成匿名客户端标识，无需用户填写。 | generation 内复用标识；不承诺线上可用性或繁体目标支持，不绕过验证码或登录。 |

四个免费网页来源的随包默认值为并发 1、请求起始间隔 1 秒；LLM 和自建 DeepLX 不因此改成串行。
未保存或空白的翻译引擎选择默认使用 Microsoft Edge；已有明确选择不被覆盖。
保存过的用户配置优先，必要时在配置窗口使用稳定参数按钮，保存后对后续任务生效。
429 会停止未发送的工作并保留成功结果；不要为“恢复”叠加片段级与页级重试。
共享 HTTP 冷却支持 Retry-After，排队/冷却不消耗真正的网络超时。UA 是来源兼容参数，不是配额保证。

## 元数据来源扩展

实现 `IMetadataExtension`：

- `SourceDefinition`：来源 ID、名称、支持的标识符 scheme、优先级。
- 用 `Extension.createMetadata<Settings> sourceDefinition (fun settings identifier resources token -> ...)` 创建实现；回调返回 `Task<Result<MetadataCandidate>>`。来源声明应固定其 ID、名称、scheme 和优先级。

不支持的 scheme 返回 `UnsupportedIdentifier`；响应中没有可用元数据时返回 `InvalidResponse`；标准网络访问使用 `resources.Http`。候选只是提议，由 Core 校验并原子应用（见 [CITATIONS](CITATIONS.md)）。随包来源（`metadata.crossref`、`metadata.datacite`、`metadata.openalex` 等）可以作为模板复制。

## OCR 扩展

`IOcrExtension` 只能通过宿主的显式入口调用，默认的 OCR 队列仍使用内置后端。OCR 结果要复用 Core 的 `DocumentBoxSeed`；只识别出文字时只生成文字块，不要编造表格或公式结构。

## 证据边界

静态检查只证明脚本能通过编译器；fixture 测试只证明在模拟响应下请求与解析逻辑正确；一次线上探测只证明那一次的结果。报告扩展可用性时，注明是否为线上验证、验证时间、状态码和解析结果，并且不要输出凭据。
