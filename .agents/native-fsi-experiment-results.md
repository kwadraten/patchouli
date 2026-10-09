# Native FC 与 FSI：真实 F# Agent 对照实验

日期：2026-10-08。本文记录本机实验；设计承接 [统一工作流与 SDK 计划](agent-workflow-sdk-plan.md)。正式实验已完成，统计、限制和实施调整见下文。

## 结果与选择

本轮默认选择 **FSI + native FC 的混合方案**，保留 F# SDK 作为唯一能力定义与验证/观察入口。它在真实无-FC代码通道对照中 24/24 通过；文本纯 FSI 为 20/24。纯 FSI 在成功的组合任务里经常更省模型往返，因此不能把混合解释成要求每个原语都走 native。没有一个版本在可靠性、步数、耗时三项上全面支配另一个。

最终计入 144 次 SDK 呈现主实验、16 次本机传输故障成对核对、48 次真正无 native FC 的代码通道对照，共 208 次正式运行。探测、早期 stdout/文本帧原型等记录保留为 pilot，未混入正式样本。修复后的两阶段没有基础设施失败。

### 真正无 FC 的纯 FSI 与混合版：48 次同阶段随机交错对照

种子 17；每个模型比较 lookup、aggregate、batch、resume、read_du、no_tool 六项。下面的性能只使用两版均通过的工具任务交集；不把失败后提前结束的短耗时算作性能收益。请求数包含最后完成回复。

| 模型 | 纯 FSI / 混合通过数 | 成功工具任务配对数 | 模型请求总数 FSI / 混合 | 外层动作 FSI / 混合 | 底层原语 FSI / 混合 | 耗时中位数秒 FSI / 混合 |
| --- | --- | --- | --- | --- | --- | --- |
| gpt-5.6-luna | 4/6 / 6/6 | 3 | 7 / 8 | 4 / 5 | 5 / 6 | 23.98 / 36.94 |
| gpt-5.6-sol | 6/6 / 6/6 | 5 | 11 / 15 | 6 / 10 | 10 / 10 | 22.59 / 30.88 |
| deepseek-flash | 6/6 / 6/6 | 5 | 16 / 16 | 11 / 11 | 12 / 10 | 6.20 / 5.48 |
| deepseek-v4-pro | 4/6 / 6/6 | 4 | 8 / 11 | 4 / 7 | 7 / 7 | 6.33 / 7.50 |

纯 FSI 出现 1 次 F# 类型推断/重载检查失败，随后修复成功；1 次重复提交已完成页面的 SDK 错误；Luna 两次只输出提交说明/成功声明，实际没有调用 SDK；Pro 的 no_tool 返回 `ACK.`，违反了精确 ACK 约束。四项任务失败中，两项没有实际写入，一项最终确实完成两页但违反无错误恢复验收，一项是输出格式不符合。混合版没有模型造成的编译、参数或领域调用错误。错误类别分别报告，外层失败与同一个领域错误不重复相加。

纯 FSI 的成功任务中共有 9 对减少模型请求、1 对混合更少、7 对相同；总共 17 对成功工具任务分别 42 / 50 次模型请求、25 / 33 个外层动作、34 / 33 次底层原语。它主要减少模型往返，并没有自动减少所有底层业务操作。不能从全部运行的总步数比较得出纯 FSI 更优，因为两次未执行任务也只请求一次模型。

四个模型各自成功配对的延迟差 bootstrap 95% 区间均跨 0，且每个只有 3–5 对工具任务。表中的延迟是本次观测，不支持宣称稳定的速度倍数或显著差异。相比之下，模型请求和实际原语计数可直接从轨迹核对。

### 单一 FSI JSON 代码入口：144 次主实验

这个版本没有逐个 SDK native 工具，但仍用 native JSON 提交代码。它保留的 provider 结构化动作信号与无-FC文本代码通道不同。

| 模型 | FSI 通过 | 混合通过（原始） | 混合本机传输失败 | FSI SDK 错误 / 混合 SDK 错误 | 模型请求总数 FSI / 混合 |
| --- | --- | --- | --- | --- | --- |
| gpt-5.6-luna | 18/18 | 18/18 | 0 | 0 / 0 | 40 / 48 |
| gpt-5.6-sol | 18/18 | 18/18 | 0 | 0 / 0 | 50 / 48 |
| deepseek-flash | 17/18 | 14/18 | 4 | 15 / 0 | 59 / 52 |
| deepseek-v4-pro | 18/18 | 14/18 | 4 | 0 / 0 | 52 / 51 |

混合版 8 次原始失败全部是初版本机 HTTP `ResponseEnded`，失败前写入已保存。保留这些原始记录；在修正 HTTP/Connection close 后同时重测这些案例的两版，混合 8/8 通过、无错误，FSI 6/8 通过，额外产生 3 次 SDK 内容错误。核对集是基础设施失败案例的选择性重测，不能作为另一个随机总体成功率，也不能直接替换主实验中不利的 FSI 结果。

主实验 Flash 的一次 FSI 恢复产生 15 次内容试探错误，其中大部分被代码中的 try/with 捕获；外层只记录一次失败。核对时同一个案例也可能无错误通过，但另一个种子出现 3 次内容错误。说明远程输出仍有波动，且“FSI 调用成功”无法替代底层操作验证。Luna 的单一 FSI JSON 入口全部通过，并比混合少 8 次模型请求；这支持按模型/阶段选择代码为主的呈现，也说明不能把删除逐个 SDK native 工具和删除全部 native 代码承载协议混为一谈。

### 时间拆分与预算

| 模式 | 冷初始化中位数秒 | 每任务工具耗时中位数秒 | F# 检查总秒 | F# 执行总秒 | 输入 / 输出 token 总数 |
| --- | --- | --- | --- | --- | --- |
| textfsi | 1.919 | 0.463 | 7.405 | 2.661 | 116670 / 6188 |
| hybrid | 1.906 | 0.020 | 1.754 | 0.602 | 186393 / 4529 |

这两行是全部 24 次运行的分解，包括失败任务；仅说明工作落在哪一层，不是成功任务的速度排名。混合模型确实也会选择 FSI，因而混合行的检查/执行不一定为零。native 多发的模型请求可能抵消省下的 F# 检查时间；FSI 长代码的生成和 verbose 输出也可能抵消少一次请求的收益。

含所有已记录探测与原型请求的保守计费累计为 **¥13.5656**，约 ¥13.6；另有最初 9 token 的连接探测，落在预留余量内。低于用户 ¥30 上限。这是运行时保守预留，不声称已经取得最终发票或账户扣费差额。按本轮抓取的[官方峰值单价](https://api-docs.deepseek.com/quick_start/pricing/)、全部输入算缓存未命中、10 CNY/USD 预算留白估算，已记录成功请求约 ¥5.22，另有微量初始探测；该换算用于预算留白，不是市场汇率报价。Codex 使用现有订阅。硬件 AMD Ryzen 7 PRO 7730U；.NET SDK 10.0.204；同时最多 2 个任务。

## 重新制定的落地计划

1. 保留现有不可变 Workflow pipeline 和一个 AgentCore driver。Workflow 只控制阶段、分支和退出条件；作者不另写 Agent loop，不手工 checkpoint。
2. 先统一 SDK：F# 类型/语义 DU、静态 bind、输入输出 codec、receipt、验证和原语观察属于公共能力定义。native JSON 只是可选投影；组合函数在 FSI 中是真实函数。
3. 默认保留混合能力，同时支持模型/阶段选择仅代码入口。简单操作允许 native；重复批处理、筛选、条件逻辑优先导出经验证的组合函数，并让 native 与 FSI 共用它，避免混合模型总把流程拆成逐个原语。
4. 不立即采用无约束文本标签作为唯一代码协议。优先维持可验证的代码动作承载；native freeform 是下一轮候选，不是本轮已经验证的赢家。
5. 自动检查点保存 typed state/cursor，加上阶段内的底层效果日志。部分成功恢复使用真实 receipt，不能重跑整个 FSI 块，也不能让模型靠记忆决定哪些页面已提交。
6. 类型化区分 retryable、validation、conflict 诊断，返回必要的真实输出。阶段完成必须经实际执行证据验证；代码捕获异常或模型声称完成不能绕过观察与验证。

因此，F# 类型系统可以成为统一语义语言，但它不能单独约束控制流、解释自然语言完成声明或保证效果恢复。native FC 不需要另一个工具系统或另一个 Agent loop；保留薄投影与上述统一并不冲突。详细阶段与验收已写回 [统一计划](agent-workflow-sdk-plan.md)。本轮交付的是隔离实验与计划，生产统一、SDK 自动生成和 durable checkpoint 尚未实施。

## 实验问题与实现

比较同一个模型、同一个任务、同一个输入种子下的错误操作、任务验收、模型往返、外层动作、底层 SDK 调用和执行时间。不把一个执行多次写入的 FSI 调用当成一次业务操作，也不把人工注入的拒绝当成模型错误。

实验引用当前工作树的 `src/Patchouli.Agent/Patchouli.Agent.fsproj`，实际运行 `AgentCore.chatStep` 的 Event/Effect 状态机。F# 代码在真实 `FSharp.Compiler.Service` 的持久 `FsiEvaluationSession` 中先检查再执行。模型连接通过只监听 loopback 的反代：DeepSeek 使用本机已有密钥；Codex 使用仓库已采用的 LLMTornado 3.8.69 OAuth 传输。两者都只提供模型输出，工具执行和后续调度由 F# AgentCore 完成。

SDK 是隔离的内存 Library 夹具，不连接生产数据库。两种呈现调用同一个 `Fixture` 实现，不用 CLI 自己的 Agent loop 替代 Patchouli 核心，也不根据模型口述推断已经执行过工具。

主实验有两个版本：

- `fsi`：只开放代码执行入口，全部业务操作必须编译、执行 F# SDK 函数。代码在网络上仍由单一 JSON `fsi` 调用承载，因此这个版本验证的是“SDK 全 FSI”，不代表完全删除 provider 的 native 调用协议。
- `hybrid`：同样的 FSI 入口，加上 SDK 的 native `find/fetch/put/read` 投影。由模型选择，允许混用。

另做 `textfsi` 对照，真正不提供 native 工具定义：模型通过显式 `<fsi>...</fsi>` 内容块提交 F#，普通文字仍是普通回复。宿主只执行显式代码块，把代码动作映射到同一个核心，执行结果通过内容通道回送。这是文本代码提交适配器，不是假造 provider 返回过 native 调用。代码块以外的文字不会执行。

四个模型 ID：`deepseek-flash`、`deepseek-v4-pro`、`gpt-5.6-luna`、`gpt-5.6-sol`。前两者关闭 thinking、temperature=0、max_tokens=1800；Codex 使用 low reasoning，其订阅协议不提供 temperature/max-token 覆盖。只在同模型内部比较两版性能，不把不同传输和推理设置的耗时直接归因于参数规模。

DeepSeek 当前 `deepseek-flash` 对应 V4.1-Flash，[官方发布说明](https://www.deepseek.com/en/news/deepseek-v4-1-flash/)标注 552B 总参数、输入激活 8B、输出激活 16B。[本轮实际抓取的官方定价页](https://api-docs.deepseek.com/quick_start/pricing/)把 Pro 映射为 DeepSeek-V4-Pro-0813；V4-Pro 的[官方模型卡](https://fe-static.deepseek.com/chat/transparency/deepseek-V4-model-card-EN.pdf)标注 1.6T/49B。早期发布说明曾预告 Pro 路由变化，本轮采用现行定价表的版本映射；实验仍不能独立验证服务端权重。Codex 两个档位的参数数目没有本轮可验证的官方依据。因此不把四个 ID 当作四种已证明的参数规模。

## 任务与统一契约

夹具提供六个域的 `Domain` DU、类型化 `Entry/PageRef/PageData`、带载荷的 `ReadTarget = Source of PageRef | Translation of PageRef` 和 `ReadResult = Found of PageData | Absent of string`。完整签名进入提示词；native 对应 enum/discriminator。写入工具预绑定文档 d2，模型不能覆盖 document/domain；page 限定 1 或 2。内容仍是待校验的 string，类型正确并不等于内容正确。本轮没有自由 string 与 DU 的独立消融对照，不能把低域选择错误率单独归因于类型提示，也没有验证 DTO 到 SDK/schema 的自动生成器。

| 任务 | 验证内容 |
| --- | --- |
| lookup | 正确选择 Texts 域、实际查询并计数 |
| aggregate | 实际读取所有页面、过滤 marker、汇总整数 |
| commit | 一次有效提交精确内容 |
| repair | 根据一次人工拒绝的实际诊断恢复 |
| batch | 按序提交两页，避免重复提交 |
| no_tool | 不需要工具时直接 ACK |
| read_du | 根据实际查询取得资源，处理带载荷 DU 读取结果 |
| group | 分组、过滤、整数汇总和精确输出格式 |
| resume | 保留第一笔提交，第二笔被拒后只恢复未完成部分 |

主实验每个任务使用种子 17/29，4 模型 × 2 模式 × 9 任务 × 2 种子 = 144 次运行，顺序固定随机化，同时最多 2 次任务运行。种子改变内容和答案，并非独立随机采样重复：temperature=0 也不保证远程服务完全确定。文本代码通道按种子 17 对六个代表任务做纯 FSI/混合同阶段随机交错的 48 次补充对照；不把不同阶段的耗时直接相互替换。

人工拒绝会在 repair 或 resume 的指定首次写入返回 `STRUCTURE_MISMATCH`，附精确期望内容，之后正常校验。它是一次性故障注入，不能等同于真实翻译质量评测。resume 的原型评分另要求没有额外领域错误；因此需区分“最后两页确已提交”与“无额外错误地恢复”，避免混淆业务最终状态和过程质量。

## 计数与时间口径

- `modelCalls`：模型请求次数，包含完成回复，不包含 SDK 调用。失败尝试也计数。
- `actionCalls`：Agent 执行的外层动作数，包括 FSI 与直接 native 动作。
- `primitiveCalls`：底层 SDK 的实际操作数；一段 F# 中做多次操作逐次记录。
- `compileErrors/argumentErrors`：F# 检查失败与外层参数解码/契约失败。
- `domainErrors`：SDK 记录的错误操作，包含被模型生成的 `try/with` 捕获的错误。人工注入单列。
- `toolErrorCalls`：向 Agent 返回失败的外层动作数；与 domainErrors 可能指向同一根因，不能相加当作独立错误。
- `seconds`：从夹具/FSI 初始化到 Agent 结束的任务耗时；`processSeconds` 另含 dotnet 进程启动。
- `startupSeconds`：共同的 FSI 初始化开销；`modelSeconds` 是模型传输整段耗时，含反代与 Codex relay 初始化；`toolSeconds` 是本机外层动作耗时；`checkSeconds/evalSeconds` 拆分 F# 检查和执行。

所有运行都初始化 FSI，避免两版运行时环境不同。直接 native 动作绕过当次代码检查与动态编译。扣掉 startup 得到的是本次进程中初始化之后的耗时，不是经过独立测量的长期热会话性能。内存 SDK 的执行成本很低，这个实验也不能代表数据库、网络、PDF 等真实工具耗时。

本机初版 HTTP 反代发生 `ResponseEnded`，主要落在快速连续 native 调用后的完成请求，不能算成模型或 native FC 错误。原始失败记录保留；修复 HTTP framing/Connection close 后的核对单独标记，统计同时呈现基础设施失败与可用的成对样本，不把故障前后的耗时无说明地混在一起。

## 复现材料与范围

源程序、逐次模型回复/工具结果、SDK 原语轨迹、计时、汇总 CSV/JSON 和构建哈希保存在 `C:/Users/squaresum/.codex/experiments/native-fsi-20261008/`。凭据只在内存读取，没有写进实验记录。生产 Agent/Host/SDK 源文件未修改，未创建提交。

OpenCode 免费 CLI 单独探测成功，但直接请求和真实 CLI 经本地反代替换载荷都返回 free-tier 403，不能进入同条件对照；未伪造客户端凭据/标识绕过。AGY 模型目录可见，但没有在本轮建立只提供单次原始模型输出的等价传输，未计入胜负。DSH 的配置被检查，DeepSeek 实际实验使用另一份已可用的本机凭据；Kimi 按用户要求未调用。这些客户端的可见模型列表不算模型实验结果。

付费预算上限 ¥30。反代按输入 ¥20/百万 token、输出 ¥50/百万 token 做保守累计/预留，失败或结果不明的远程请求按预留额计入，内部阈值 ¥27 留出探测余量；这不是供应商发票金额。Codex 使用已有订阅，未调用另外计费的 OpenAI API。

方法参考：[OpenAI function calling 与 strict schema](https://developers.openai.com/api/docs/guides/function-calling)、[OpenCode 运行时配置](https://docs.opencode.ai/docs/config/)、[DeepSeek 当前 API 模型名](https://api-docs.deepseek.com/quick_start/pricing-details-cny/)。strict schema 对 native 的约束不能自动证明业务语义；F# 编译成功同样不能证明一次写入内容正确或恢复逻辑正确。

复现入口：`AgentBench/Program.benchmark-v2.fs` 为主实验冻结源码；`TextBench/Program.fs` 为无-FC适配器；`manifest.json` 保存版本、任务和哈希；`all-trials.csv` 包含全部 208 次正式运行；`final-summary.json` 是最终分阶段统计。初版和修正后的代理脚本分别保留，原始记录没有覆盖。代理均已停止。
