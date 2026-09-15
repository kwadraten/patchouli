# Patchouli PRD v3

状态：持续维护

版本线：0.3.x（当前基线 0.3.2，迈向 1.0）

更新日期：2026-09-10

仓库只维护这一份 PRD。已实现内容汇总在顶部；长期领域规则、协议契约与设计决策分别维护在 CONTEXT、mcp-protocol 与 ADR 中。

## Walkthrough（已完成基线）

以下是截至 2026-09-10 已落地的产品基线（0.3.2）。实现状态依据当前代码、已有测试与提交历史核对；不代表本次重新执行全部验收。已完成需求不再在下方重复展开。

- 桌面栈：.NET + Avalonia；首轮初始化、库页、题录编辑、搜索、OCR 队列、PDF 工作台、设置、About
- 库与文件：路径无关 `library_id`；Item / FileAsset / DocumentInstance；FileSearchRoot 与文件解析冲突
- Document Box Tree：0.2.0 fresh schema；页级 immutable revision；typed leaf；sibling 顺序；Markdig 中央编译
- 搜索与证据：SearchUnit + 可重建本地 FTS；搜索配置文件；versioned URI `?rev=&box=`；带 `rev` 读固定版本、无 `rev` 读 HEAD
- OCR：MinerU 为首选生产路径；`OcrDocumentTreeCandidate` 统一进入 working revision；队列看板；局部 OCR 与工作台编辑
- CSL：type-aware `CslItemTypeProfile`；`general` 不可静默当 CSL `document`；样式管理与复制/导出；渲染失败不空成功
- MCP / CLI（V3-T1 已实现部分）：结构化 `find` / `fetch` / `put` / `cite`、CLI HTTP 客户端、TOON/JSON、实时分页、有限题录/样式写入；Bashkit 已移出 main。参数与响应见 [协议契约](mcp-protocol.md)，宿主自动接管仍待完成。
- UI 信息架构：设置五分组；`UiCommandDescriptor`；书库 ProDataGrid（列宽/顺序/排序/显隐/持久化）；阻塞与冲突模态
- 同步：快照分片；分支检查与显式导入；无自动对象级合并
- 书库与题录（V3-T5）：按类型显示来源、分组详情、回收站与恢复、永久删除、标签置顶/筛选/拖拽、一层集合（collection）侧栏/拖放/重命名/解散、查重与合并、FileAsset GC；原 PRD 记录 AC1–AC19 已验收。集合以 `item_collections` 为唯一权威，可空、不可嵌套，成员关系随 trash/restore 保留、随 merge 跟随目标、随 purge 删除。生命周期与快照规则见 [ADR 0030](adr/0030-item-lifecycle-merge-and-purge.md)。
- 搜索（V3-T8）：元数据/全文双模式、AND 高级筛选、平铺题录与分组片段结果、证据跳转；全文筛选下推 `SearchRequest.ItemFilters`，MCP texts scope 支持 `item_id`。见 [领域文档](CONTEXT.md)。
- 本地 OCR（V3-T9）：NDL koten、RapidOCR 与 NDLOCR-Lite 的 C# ONNX 管线、按需模型下载、文档/页面/区域引擎选择、本地模型与 OCR 临时文件管理；见 [ADR 0025](adr/0025-ndlkotenocr-lite-onnx-port.md) / [0031](adr/0031-native-rapidocr-onnx-port.md) / [0032](adr/0032-ndlocr-lite-onnx-port.md)。更多 provider 仍属 V3-T3。
- 文档版本（V3-T6 / V3-T10）：统一 working/committed、原地 commit、文档级 DocumentCommit、页面/文档历史与恢复、版本谱系 UI、versioned URI；见 [ADR 0027](adr/0027-unified-working-copy-and-immutable-revision-model.md) / [0028](adr/0028-versioned-uri-evidence.md)。不包含题录/样式版本或 diff。
- 性能（V3-T7 已实现部分）：共享 Host 组合层、首屏/查询与 OCR 批量路径优化、PDF viewing session/缓存、性能烟测及 UI 探针；完整规模预算和订阅式增量刷新仍须收口。基准入口见 [perf/README](perf/README.md)。
- 桌面补充：单实例与本地激活（[ADR 0029](adr/0029-ui-single-instance-and-local-activation.md)）、可选配色、BibLaTeX 自定义字段往返与验证警告。

实现追溯：MCP `0076558` / `2415ec4`，书库 `74337b4` / `4b62176`，搜索 `a3f3108`，OCR `1e22357` / `edd7c11`，版本 `28a71ec` / `c9de557`，性能与宿主 `4d0649c` / `da18c9b` / `6ba2ea5`。这些提交供历史追溯，长期规则以链接文档为准。

## 测试锚点与长期边界

下列短语与约束必须继续可被文档/契约测试命中；权威解释见 CONTEXT/ADR，本处仅作产品声明：

- MCP 从不触发 OCR 或索引重建
- 搜索配置文件
- 本地 FTS 索引是可重建的本地缓存
- 提供程序凭据；MCP 无法读取提供程序密钥
- 缓存图像；MCP never returns cached images or image paths；`page_renders`
- v1/v2 首发 MCP 是只读且纯文本的；v3+ MCP 仍保持 text-only，并允许 ADR `0023` 定义的有限写入
- 作为独立分支打开以供检查；v1 不执行自动对象级合并；不得在分支间静默执行最后写入者胜出

削弱证据可复现性的能力必须显式 opt-in，并在 UI 与 MCP/CLI 响应中标记。

## 1. 产品定位与 v3 目标

Patchouli 是桌面优先的个人文献管理器：题录、用户自有源文件、页级 Document Box Tree、搜索单元与稳定证据引用。

**v3 对应 0.3.x，目标是向 1.0 正式版迈进**：不再以“功能堆叠”为主，而是探索、评测并固化**真正值得长期坚持的能力与能力组合方式**。进入 1.0 的能力必须：

1. 边界清晰（读写、秘密、路径、图像、证据身份）
2. 可测、可文档化、可被 agent 与人类稳定复用
3. 组合成本低（同一概念在 UI / CLI / MCP 上同构，而不是三套语义）
4. 失败可解释（revision、conflict、permission、validation）

v3 明确不做完整 1.0 范围膨胀：向量化/语义搜索、程序托管原文件同步、账号计费、库级加密、自动对象级合并等仍默认延后，除非后续 PRD 修订显式纳入。

## 2. 剩余任务总览

完成项 V3-T5、V3-T8、V3-T9、V3-T10 及已并入 V3-T10 的 V3-T6 仅保留在顶部基线；任务编号保持稳定。

| 任务 | 当前状态与剩余范围 |
|---|---|
| V3-T7 | P0；已有优化与基准，继续收口完整规模预算、增量刷新及实现边界 |
| V3-T1 | 结构化生产协议已落地；宿主发现、自启与独占接管待实现，持续保留协议回归 |
| V3-T2 | 现有工作台可用；校注编辑设计与 Markdown 预览组件选型待补全 |
| V3-T3 | MinerU 与 NDL koten 已落地；其他 OCR provider 待设计 |
| V3-T4 | Linux PATH 与 `.deb` / `.rpm` / AppImage 正式发行待完成 |

### 2.1 V3-T7：性能与响应性治理

**状态**：P0；已有优化实现与性能基准，尚未据完整验收矩阵确认完成。该任务优先于其他尚未开始的 v3 体验增强任务，但不得以削弱 Document Tree、working/commit 生命周期、versioned evidence URI、快照或 MCP 契约为代价换取表面速度。持久化模型重设计已由 ADR `0027`/`0028` 决策并转入 V3-T10，不再是 V3-T7 的阻塞后续项。

当前实现入口见 [perf/README](perf/README.md) 与 [domain.md](domain.md)。当前跨进程 revision monitor 仍轮询且全量重载缓存，与下文 AC5 的目标有差距；共享 Host 组合层不等于独占宿主接管已完成。以下保留未关闭任务的目标约束与验收预算，不将所有条款宣称为未实现。

#### 2.1.1 问题与目标

本任务最初针对以下四条用户可感知路径；已有优化见顶部基线，剩余差距以本节验收和最新测量判断：

1. 桌面 UI 首屏与书库首批数据出现过慢；启动路径可能执行与首屏无关的聚合、相关子查询或大量数据库读取。
2. OCR 在导入数据库及 commit 阶段会阻塞 UI；后台任务、数据库连接与 UI dispatcher 的边界不清晰，working/commit 路径还可能造成大规模 Box Tree 写入和数据库膨胀。
3. MCP 读取 OCR 页面、document outline 或单条题录的响应偏慢；重复元数据查询、页面 Markdown 编译、源文件指纹计算和 helper 启动不得在未变化资源的每次读取中重复发生。
4. PDF 工作台按页串行执行文件解析、source hash、PDFium document open、raster、Box/preview 加载；PDFium 足够快时掩盖了切页等待，但该模型无法支持大文件、云端文件、快速翻页和后续更丰富的页面 UI。

本任务建立可回归的性能预算，并从数据库访问、宿主服务、UI 数据流、缓存和代码净化五个方面共同治理。优化不能只用 loading UI、延迟显示或更长 timeout 掩盖实际工作量，也不能在保留旧路径的同时继续叠加一套“新版”实现。

#### 2.1.2 数据库与首屏

- 建立带固定规模与数据分布的性能 fixture，至少覆盖 100 个 Item、50 万个 DocumentBox、15 万个 SearchUnit、20 GiB 聚合源文件及一个不小于 500 MiB 的 PDF；冷启动与热启动分开记录。
- 首屏只读取形成可交互框架和首批书库行所需的数据。昂贵聚合、详情、状态统计与非首屏关系采用分页、按需加载或提交后的增量投影。
- 对启动和书库列表查询执行 query-plan、SQL 调用次数与索引审计；消除逐行相关子查询和 N+1 读取，补充由代表性 fixture 验证的复合索引。不得以把整库常驻内存作为默认修复。
- 数据库读写不得在 Avalonia UI dispatcher 上执行。所有长查询支持 cancellation；关闭窗口、切换视图或新查询取代旧查询时，过期结果不得覆盖新状态。
- 评估并以并发、崩溃恢复和快照测试验证 SQLite journal、busy timeout、连接复用、批量事务及单写者调度策略；不得依赖高频重试或 UI polling 掩盖锁争用。

#### 2.1.3 OCR 导入与 working/commit 性能

- OCR provider 继续输出 `OcrDocumentTreeCandidate`，由 ADR `0027` 定义为统一的 working revision；commit 原地提升该 revision 为 current，bbox 校验与 committed-current 可见性由 `0027` 保证。
- 在 working/commit 语义内，候选解析、验证、规范化、Box/SearchUnit 写入和 commit 使用有界后台流水线、批量参数与少量明确事务；UI 线程只接收进度、结果和提交后通知。
- commit 必须原子更新 current revision pointer、search dirty state 和所有协议可见关系；取消、失败或连接中断不得产生部分 current tree。
- 专门测量 working 与 commit 对 Box 数量、写放大、WAL/数据库峰值、提交时间和最终数据库体积的贡献。优先消除不必要的重复序列化、逐 Box 往返和永久残留的已丢弃中间数据。
- V3-T7 的 P0 交付优化 ADR `0027` 语义内的 SQL、事务、调度和清理，不以再次消除 staging 或整树双份持久化为完成条件（该问题已由 `0027` 的 in-place commit 解决）。
- 子项 `V3-T7-D1` 的方向已由 ADR `0027` 决定并转入 V3-T10；V3-T7 不再单独保留该决策子项，也不阻塞 V3-T7 的开发、验收或发布。

#### 2.1.4 响应式 UI 与单一宿主数据源

- 采用 [ADR 0033](adr/0033-ui-reactivity-three-layer-model.md) 的三层响应式模型：DerivedPropertyGenerator 处理同实例同步派生属性；System.Reactive (Rx) 处理跨对象、集合、异步与时序状态；CommunityToolkit.Mvvm 处理可变 VM 状态与命令。
- 保证严格的一致性更新：Rx 的 `Throttle` 或 `Debounce` 后接 `Switch`，非丢弃提交流使用 `Buffer` 后接合并 ID 与 `Concat`。
- UI、MCP 与 CLI 的一致性以 ADR `0024` 的单一 Library runtime host 为边界。UI 与 MCP 复用宿主内部同一套查询、投影、revision 和写入服务；CLI 仍是本地 MCP HTTP 瘦客户端，不得直连 SQLite 或新增第二套领域数据源。
- 所有改变 protocol-visible canonical 状态的成功写入经宿主写服务提交后发布类型化 `resource-changed` 通知。书库列表、打开的题录、CSL 样式、OCR 队列与 PDF 工作台订阅相关变更并增量刷新，正常 Desktop 不轮询（仅在异常恢复时显式轮询）；FTS rebuild、预取和运行时缓存维护只发布内部状态，不伪造 Library commit。
- 变更通知只在事务成功后发出，并携带足以定位受影响资源的信息；订阅者不得在通知处理期间同步执行长数据库查询。OCR 运行进度事件与 protocol-visible Library commit 通知是不同事件，不能提前暴露未 commit 的 working 内容。
- MCP 不增加服务端推送式撤回或远程 cache invalidation。外部 MCP/CLI 调用者仍通过 `meta.library_revision`、`RESULT_SET_MAY_HAVE_CHANGED` 和 `LIBRARY_CHANGED_SINCE_LAST_RESPONSE` 观察变化。

#### 2.1.5 共享读取缓存与 MCP 延迟

- 在 runtime host 的共享读取层实现有界、可观测、可重建的缓存，使 UI 与 MCP 复用；不得只在 MCP transport/controller 内建立另一套领域缓存。CLI 通过 MCP 间接受益。
- 已有 compiled page Markdown 缓存继续以 immutable `tree_revision_id` 与完整 compilation options 为键。对题录、outline、关系投影和文件指纹扩展缓存时，键必须包含 `library_id`、资源身份及有效 revision/basis，或在对应 commit notification 后精确失效。
- 共享领域读取 LRU 必须有按实际 UTF-8/对象估算的内存上限、并发请求合并、命中/未命中/驱逐指标；不缓存失败、取消、超限结果、secret、本地路径、图像或不可重建状态。UI raster 只进入 2.1.6 定义的独立、本机、有界页面缓存，不能混入 MCP/read-store 或被其返回。
- 响应的 `meta.library_revision`、会话 warning、权限和 truncation envelope 每次按当前请求重新计算，不得作为旧响应整体缓存。`find` cursor 继续是无状态、实时 continuation；缓存不得物化结果集、创建客户端可寻址的服务端 handle、TTL snapshot 或承诺跨页一致性。宿主内部、不可由协议寻址且可随时重建的 viewing session 不属于结果集 handle。
- 未变化源文件的 page/evidence fetch 不得每次重新散列整份 PDF。文件指纹缓存必须以受验证的文件身份、长度、修改时间和 fingerprint basis 失效，同时继续遵守不向 MCP 暴露路径的边界。

#### 2.1.6 PDF 查看会话与响应式预取

- PDF 查看以下沉到 runtime host 的 `FileAsset` 级 viewing session 为目标。一次 session 统一拥有 resolved source、可选且共享的 source-basis 验证状态/结果、PDFium document handle、page count/metadata、当前 renderer basis、页面 raster working set 和页面 read-model 预取；ViewModel 不逐页重新拼装这些步骤。
- 首次进入需要 source basis 的操作时只允许一个共享的 full hash validation；单纯打开题录、outline 或纯文本页面不以预先散列源文件作为前置条件。相同 resolved binding、size、mtime、quick hash、stored full hash 与 fingerprint basis 未变化时，后续页面复用该结果；并发 UI/MCP/evidence 访问共享同一个 in-flight validation。文件 watcher、重新绑定、metadata/quick hash 变化、Library 切换或 fingerprint basis 升级使 session 失效。
- source-basis warning 与 overlap marker 是不同投影。source validation 以 FileAsset 为粒度；页面 overlap marker 由当前 `tree_revision_id` 的 Box 集生成，不触发文件 hash。纯 Item/outline/default search 与带 `rev` 的 versioned evidence URI 不触发 source validation；PDF+bbox、包含 bbox 的页面读取以及不带 `rev` 的 evidence URI 按 HEAD 解析时才需要验证。

**惰性 source validation 与警告投影**：

- viewing session 维护可重建、非 canonical 的运行时验证状态：`unverified`、`validating`、`current`、`changed`、`unavailable`。该状态和在途任务不进入快照，也不得成为 versioned evidence URI 身份的一部分。
- 每次真正访问文件时先做低成本 binding/size/mtime/quick-hash 检查。元数据变化、重新绑定或 fingerprint basis 升级只把旧验证标为失效，不自行启动整文件扫描；下一次坐标敏感访问或用户显式执行“重新验证源文件”时才计算 full hash。多个调用必须合并到同一在途验证，不得按页或按 endpoint 重算。
- 纯 Item、document outline、default search、纯文本 page fetch 和带 `rev` 的 versioned evidence URI 文本解析不等待 full hash，使用已持久化的 last-known source status。它们不得为了附带 warning 把整个 PDF 散列变成热路径；带 `rev` 的 URI 按固定 revision 提供可复现文本。
- UI 打开 PDF 页时，当前页 raster、Box/Markdown 读取和 source validation 并行启动。raster 可先显示，但验证完成前只作为 session 内临时画面，不写入以 full hash 为 basis 的持久 render cache；依赖 source basis 的 bbox overlay、source-drift warning 和坐标交互显示明确“验证中”状态或暂不启用。overlap marker 可以先由 Box 投影计算，但在 source basis 未确认前不得把它呈现为已验证的源文件坐标结论。
- MCP 的纯文本 page/document/evidence 读取不触发 full hash。任何已由既有契约声明的坐标敏感读取，以及不带 `rev` 的 evidence URI（按 HEAD 解析），必须等待共享验证并继续服从 ADR `0024` 的 deadline/cancellation；超时返回既有 `DEADLINE_EXCEEDED`。V3-T7 不为此新增 bbox 参数、服务端推送、隐式成功的“稍后验证”结果或另一套协议状态机。
- full hash 验证结果属于整个 FileAsset，而非单页。确认未变化时不产生数据库写入或 `library_revision` 递增；确认内容变化、稳定缺失或重新绑定时，由 host write service 一次提交协议可见的 FileAsset/source status，递增 revision、发布 change set，并失效相关 viewing session、raster 与坐标投影。取消、deadline 或瞬时 I/O 失败只更新运行时状态并允许重试，不缓存失败、不写库、不递增 revision。
- overlap 计算以 `(tree_revision_id, page_id, overlap-policy-basis)` 为键，在用户进入该页或低优先级预取命中该页时惰性执行。immutable revision 的结果可复用；工作台草稿或 Box 编辑只失效受影响页，不读取文件、不计算 hash，也不触发整份文档重算。

- PDFium document 在 session 内打开一次并复用，页面 handle 仍按页短期持有并及时释放。当前页面渲染拥有最高优先级；预取任务不得占用全局 native gate 而延迟用户刚请求的页面。切换文档、source 失效、session 驱逐和 host 关闭必须确定性释放所有 native handle。
- 打开文档后并行加载当前页 raster、Page/current revision/Box projection 和 Markdown preview；只在 UI dispatcher 上提交最终 DTO/bitmap。raster 就绪即可先显示页面，Box、overlap、continuation 和 Markdown 可随后以同一 generation 增量出现；旧 generation 完成时不得覆盖新页。
- 当前页可交互后按导航方向预取相邻页面。默认 working set 为当前页、前一页和后两页；连续向前/向后翻页时动态调整窗口。预取至少包含目标 DPI raster、Page metadata、current revision identity 和 Box projection；Markdown/overlap 等派生投影只在预算允许时低优先级预计算。
- UI preview 直接消费一次 PDFium BGRA raster，不得为了预览先编码 PNG 到磁盘、再重新打开同一 PDF 渲染第二份 BGRA。OCR/导出所需的持久 PNG 与交互 preview 是不同 projection，可以共享 document session 和 source hash，但按各自 DPI/格式生成。
- 页面 raster cache、compiled projection cache 与 document session cache 均为有界 LRU。预算按实际像素字节和 native/managed 占用计算；当前页 pin，预取页可驱逐，不缓存失败、取消或失效结果。不得使用无上限 dictionary 保留所有已查看页面。
- “整份文件进入内存”只能是小型、本地、已验证 PDF 的可选策略，必须受单文件阈值和全局内存预算约束；大文件、云端文件和内存压力场景使用 file-backed PDFium handle/操作系统页缓存。不得默认把 500 MiB 级 PDF 复制为一个 managed byte array。
- 用户快速翻页时取消尚未开始的远端预取并降低已开始任务优先级；请求合并保证同一 session/page/DPI/renderer basis 只有一个在途 render。预取失败不影响当前页，且不得伪装为当前页失败。
- viewing session、验证状态、native handle 与 raster cache 均为本机运行时资源；MCP 仍不返回缓存图像或路径。V3-T7 只负责提供可预取的 canonical Markdown/read-model projection，V3-T2 继续负责 Markdown 预览组件和具体交互表现，二者不得各建一套内容数据源。

#### 2.1.7 架构净化与冗余剔除

- 每项性能改造必须同时盘点并处理它所替代的旧实现。新路径达到契约与测试要求后，应在同一任务范围删除不再使用的 service、repository、query、事件、轮询器、adapter、DTO、DI 注册、feature flag、设置项、helper、依赖和对应测试 fixture，不保留无调用者的“备用实现”。
- UI、runtime host、MCP 和 CLI 的同一领域操作只允许一条权威数据流。不得以兼容、渐进迁移或便于回滚为由长期保留 direct-SQL frontend、重复 projection、双写、双缓存、双通知或新旧查询并行分支；确有受支持兼容义务的例外必须写明契约来源、调用者和删除条件。
- 优先删除死代码、不可达分支、已失效 abstraction、仅转发而无边界价值的包装层、重复 mapping/validation，以及已被 ADR 或 PRD 淘汰的实现残余。新增抽象必须有明确所有者和边界，不得只为隐藏一次调用或为未来假设预建层级。
- 性能关键路径不得同时存在多个可被生产选择的实现。迁移期间允许短期双路径时，必须由同一个 issue 跟踪，有明确默认路径、对照测试和删除截止条件；V3-T7 完成时不得遗留永久 compatibility toggle 或“旧版 fallback”。
- 删除数据库表、列、迁移、序列化字段或快照内容仍受 schema epoch、ADR 和兼容范围约束。历史迁移只要仍用于打开受支持 Library 就不是死代码；任何数据删除先证明不存在 current revision、已发出的 versioned evidence URI、working revision、快照或回滚依赖。
- 删除后同步清理测试、文档、打包清单、配置 schema、遥测维度和依赖锁定；构建产物不得继续携带已经没有生产入口的 helper、native payload 或资源文件。
- 净化以降低认知复杂度和错误表面积为目标，不以代码删除行数为指标。不得为了“少代码”合并具有不同事务、权限、安全或 revision 语义的边界。

#### 2.1.8 SQL 与数据访问实施设计

本节固定 V3-T7 的实施顺序与 SQL 边界；具体类型名可以在实现中调整，但不得退化为 UI/MCP 各自持有连接或用无界 `Task.Run` 包装同步 SQLite 调用。

**连接与执行模型**：

- Library host 取得独占所有权后、运行普通 migration 之前，以不池化的管理连接执行并验证 `PRAGMA journal_mode=WAL`。该 PRAGMA 不得放入现有事务化 migration；无法进入 WAL 时必须阻止正常打开并给出可诊断错误。
- 普通连接保持 `Cache=Private` 并启用 pooling。查询使用只读连接和 `query_only=ON`；所有写入使用 host write service 拥有的单写者队列。read/write 使用可区分的连接池或在归还前可靠复位每连接 PRAGMA，禁止把带 `query_only=ON` 的 pooled connection 借给 writer。migration、snapshot、恢复和 desktop/headless 交接使用第三类独占管理连接。
- 每个连接明确设置 foreign keys、busy timeout 和 synchronous。目标配置为 WAL + `synchronous=NORMAL`，但 NORMAL 必须先通过进程强杀、恢复和快照完整性测试；不满足耐久性要求时保留 `FULL`，不得降低原子性换取性能。
- read executor 有固定并发上限，每次查询及时释放连接并传播 cancellation；write executor 每个 Library 只有一个 worker。SQL 锁等待不再作为进程内写入调度机制，30 秒 busy timeout 不得成为正常 UI 行为。
- 启用 pooling 后，Library 切换、host 交接、migration、snapshot 和运行库文件操作必须先停止接单、排空 writer、取消 read、关闭连接并清理对应连接池。日常 checkpoint 不阻塞活跃请求；`FULL`/`TRUNCATE` checkpoint 仅在独占维护窗口执行。

**持久 revision 与响应式提交**：

- Library 持久保存正整数 `library_revision`。每个改变协议可见资源或关系的成功事务在同一事务末尾递增并返回 revision；desktop/headless 交接不得重置。working revision 和本地 FTS cache rebuild 不单独递增，成功 commit 作为一次协议可见提交递增一次。
- host write service 在 commit 成功后发布类型化 `LibraryChangeSet`，至少能够携带受影响的 Item、DocumentInstance、Page、OCR Run、CSL style 与新的 Library revision。回滚、取消和 working 中间状态不得发布 protocol-visible change。
- UI 根据 change set 请求 `GetRowsByIds` 一类小批量 read model 并按稳定主键更新集合；不得在通知处理器中全量刷新 Library。OCR progress 走独立运行时事件，不能用数据库轮询代替。

**首屏与书库查询**：

- 首屏采用稳定 keyset pagination：先选择首批 Item 核心行，再仅针对这批 ID 聚合作者、primary document、页数、latest OCR、latest error、SearchUnit count 和 source status。不得对全部 Item 执行多层 correlated scalar subquery，也不得为得到 source path 额外读取全部 DocumentInstance。
- latest OCR 使用受当前 document ID 集限制的 window/ordered CTE；作者、页数与 SearchUnit count 使用 batch aggregate。详情字段在选择行后按需读取。文件搜索根先显示定义与可用性，文件计数延迟加载；不得用不符合跨平台路径语义的裸 `LIKE root || '%'` 代替路径归属判断。
- 首轮复合索引候选至少覆盖 active Item 排序、primary DocumentInstance、`ocr_runs(document_instance_id, hidden, created_at)`、`ocr_page_results(ocr_run_id, created_at)` 的有效错误行，以及 `search_units(document_instance_id, status)`。每个索引必须由代表性 fixture 的 query plan、读取行数和总写放大证明；删除旧单列索引前先证明没有其他查询依赖。

**OCR、SearchUnit 与 FTS 写入**：

- Candidate 解析、bbox 转换、包含关系规范化、Markdown/plain-text projection 和 payload 序列化尽量在写事务外完成；进入 writer 后复核 immutable current basis，避免长时间持有写事务执行 CPU 工作。
- working revision 以一个 document 的连接/写 lease 执行，通过 prepared command 或 `json_each` 按有界 chunk 批量写 Box 和 page result，不按页重新开连接、不按 Box 单独往返。失败或取消不得留下不完整 working revision。
- commit 继续保持 DocumentInstance 级原子性。commit 原地提升 working revision 为 committed current，`tree_revision_id` 与 Box ID 保持不变；不再有 staging→committed 的 Box 复制。该行为由 ADR `0027` 保证。
- SearchUnit predecessor matching 一次加载旧集合并在内存中建立索引，不允许每个 unit 再 SELECT 或对 previous units 做 O(n²) 扫描。新旧 unit 状态和 search status 通过 temp table/JSON batch 在 commit 事务内提交。
- FTS 是本地可重建缓存，在 canonical commit 后由 writer job 重建。每个 DocumentInstance 使用集合 delete 与 batch insert；如果 index text 必须由 .NET 生成，则在事务外生成后批量传入，不逐 unit 执行 INSERT。FTS 失败不得回滚已经成功的 canonical commit，但必须保持 stale/unavailable 状态并可重试。

**MCP 读取 SQL**：

- 纯文本 page/document/evidence 热读取不得同步散列整个源 PDF，只读取已持久化的 FileAsset status、size、mtime、fingerprint 与 page source basis。坐标敏感读取通过共享 fingerprint service 等待一次合并后的惰性验证，不得在 endpoint 内直接重算；结果变化后经 write service 提交并使相关投影失效。
- 页面 Box/SearchUnit 一次批量读取；需要生成 evidence URI 时收集全部 unit ID，直接按 committed revision/Box ID 构造 versioned URI（ADR `0028`），不创建 evidence record，也不为读取增加写事务。
- text search 的 owning Item metadata、Item/DocumentInstance/FileAsset 原始 status、OCR 索引能力、citable 字段和 filter 数据由同一查询投影或按本页 ID 一次批量加载，不得按搜索结果逐项调用 metadata/status 查询。Item 浏览的 primary-document OCR 索引能力同样必须以本页 Item ID 批量聚合，不得形成 N+1 查询。
- compiled Markdown、题录、outline、关系和文件 fingerprint 缓存位于共享 read store；数据库查询返回领域投影，transport envelope、当前 Library revision、warning、权限与 truncation 每次重新组装。

**分阶段交付**：

1. S0 固定 fixture、statement count、query plan、lock wait、WAL 大小、UI heartbeat 和 MCP cold/warm 基线。
2. S1 落地 WAL bootstrap、read/write/admin 三类执行边界、连接池、单写者、持久 revision 与 commit change set。
3. S2 落地首屏分页 read model、复合索引、侧栏延迟计数和按 ID 增量查询。
4. S3 落地 OCR bulk working revision、set-based commit、批量 SearchUnit 与 FTS 后置任务。
5. S4 落地 MCP hash 解耦、Evidence/metadata/status batch 和共享缓存。
6. S5 删除 UI direct SQL、数据库轮询、全量 refresh、逐条写 helper、旧 fallback、无调用者 DTO/service 和经证明冗余的索引。

`V3-T7-D1` 的方向已由 ADR `0027` 决定并并入 V3-T10，不再属于 V3-T7 的 S0-S5 阻塞链。

#### 2.1.9 性能预算与验收

以下为初始预算。基准硬件、操作系统、SQLite 版本、fixture 生成器、冷/热缓存条件和原始结果必须随测试版本化；若预算需要调整，必须以新的测量证据修订 PRD，不能在实现中静默放宽。

| 编号 | 标准 |
|---|---|
| V3-T7-AC1 | 存在可重复的性能基准，分别报告 UI 冷/热启动、首批书库行、OCR working/commit、MCP item/document/page/evidence fetch 的 median、p95、SQL 调用次数、读取行数、分配量、缓存命中率、数据库峰值增长和 UI dispatcher 最大停顿；性能日志不记录正文、查询内容、路径、versioned evidence URI 或 secret |
| V3-T7-AC2 | 在规定 fixture 与基准机上，冷启动 2 秒内出现可交互应用框架、3 秒内出现首批书库行；热启动 1 秒内出现可交互框架。首屏不等待整库聚合、OCR 状态全扫描或全文索引统计 |
| V3-T7-AC3 | 导入并 commit 50 万个 DocumentBox 时，数据库工作不在 UI dispatcher 执行，100 ms UI heartbeat 的最大观测间隔不超过 250 ms；用户可以继续切换视图、滚动和取消尚未进入原子提交点的任务 |
| V3-T7-AC4 | OCR 取消或失败不产生 partial current tree；成功 commit 只发布一次提交后变更批次，并原子更新 current/search/evidence/commit 状态。working/commit 语义由 ADR `0027` 保证，不影响 V3-T7 验收 |
| V3-T7-AC5 | 书库列表、题录编辑、CSL 样式、OCR 队列和 PDF 工作台对宿主提交采用订阅式增量刷新；正常运行不依赖固定周期数据库轮询，连续快速写入不会造成旧结果回覆盖或 UI 查询风暴 |
| V3-T7-AC6 | 在本机 HTTP transport、热缓存和默认响应大小内，单条 item fetch 的 p95 不超过 200 ms，纯文本 document/page/evidence fetch 的 p95 不超过 300 ms；冷缓存相应 p95 不超过 1.5 秒。首次坐标敏感 source validation 单独报告，不伪装进纯文本预算；未变化的 500 MiB PDF 连续读取不重复执行整文件哈希 |
| V3-T7-AC7 | 缓存大小有硬上限，跨 revision、跨 Library、权限变化、资源提交、DocumentTree current pointer 变化和 renderer/fingerprint basis 变化的测试均不返回陈旧或跨库内容；失败、取消和超限响应不会污染缓存 |
| V3-T7-AC8 | cursor 仍为无状态实时 continuation，缓存不会创建物化结果集或客户端可寻址的服务端 session/result handle；每个 MCP 响应返回当前 `meta.library_revision` 并保持 ADR `0024` 的 warning、deadline、cancellation、partial 与 text-only 契约 |
| V3-T7-AC9 | 每个被替代的生产路径都有删除清单；代码、DI/config schema、测试、文档、打包资源和依赖中不存在无调用者残余。契约测试证明 UI/MCP/CLI 对同一领域操作只经过一套宿主查询、投影、validation、revision 与写入语义 |
| V3-T7-AC10 | 不存在无删除条件的旧版 fallback、compatibility toggle、重复轮询/通知、direct-SQL frontend、双写或双缓存路径；因受支持 schema/协议必须保留的兼容代码均有可追踪的契约来源、调用测试和退出条件 |
| V3-T7-AC11 | CI 至少运行小型性能烟测并检测明显回退；完整 fixture 基准在指定 runner 可重复执行，连续三次结果超出预算时构建或发布检查失败，并保存可比较的机器可读报告 |
| V3-T7-AC12 | 同一 FileAsset 在 resolved binding 与 fingerprint basis 未变化的一个 viewing session 内，只有首次坐标敏感访问可以触发一次 full hash validation；纯文本 MCP 读取不触发，其他页面、预取及不带 `rev` 的 evidence URI 复用结果。并发调用共享一个在途 validation |
| V3-T7-AC13 | PDFium document handle 在 viewing session 内复用；首次 UI preview 不执行“PNG render + 第二次 BGRA render”。当前页请求优先于预取，缓存命中切页无需等待 source resolve、full hash 或 document reopen |
| V3-T7-AC14 | 当前页可交互后自动预取默认相邻窗口，并在导航方向变化时调整；快速翻页的旧 generation、已取消预取或已失效 source 不会覆盖当前页。预取失败不改变当前页成功状态 |
| V3-T7-AC15 | document session、page raster 和 page projection cache 均有可测试的硬内存上限、LRU 驱逐和确定性 native handle 释放；500 MiB PDF 不会默认复制进 managed memory，连续浏览长文档不会使进程内存随已访问页数无界增长 |
| V3-T7-AC16 | source validation 具有可测试的惰性触发矩阵；UI 验证期间保持可交互并明确区分“验证中”与 warning，坐标敏感 MCP 调用遵守既有 deadline/cancellation。overlap 只对进入或预取的页面按 revision 惰性计算，Box 编辑只失效受影响页且不触发文件 hash |
| V3-T7-AC17 | V3-T7 的完成、验收和发布不依赖 `V3-T7-D1`；S0-S5 在 ADR `0027`/`0028` 语义内可独立交付，后续 ADR 不得追溯性阻塞已满足的性能预算 |

## 3. V3-T1：宿主生命周期与协议回归

**状态**：结构化 MCP 迁移已落地；CLI 当前连接配置的 HTTP 端点。A/B 选择及历史评测见 ADR `0024`，不再重复评测已淘汰的 Bashkit 路线。四工具的全部参数、响应 schema 和原 V3-AC1–AC28 回归义务见 [mcp-protocol.md](mcp-protocol.md)。

剩余交付：

- V3-AC15：CLI 自动发现同 Library 宿主；无桌面时自启同一二进制的后台 headless 宿主；持久化发现记录与锁保证每库独占；桌面启动终止并接管同库 headless 宿主。共享 bind/CORS/token/工具开关，`0.0.0.0` 仍要求 token。
- V3-AC11：结合 V3-T7 收口提交后的订阅式增量刷新；现有共享服务与跨进程轮询不能替代这一验收。
- 协议变更继续验证 HTTP/CLI 参数映射、TOON/JSON 等价性、封闭 schema、权限、partial/error、deadline/cancellation 与跨库隔离；变更协议 revision 时同步更新契约和 fixture。

现有 `Patchouli.Host` 由桌面与独立 `Patchouli.McpServer` 复用；当前运行机制见 [domain.md](domain.md)。该事实与 ADR `0024` 的最终单宿主目标之间的差距由本任务追踪，不修改既有决策来掩盖差距。

## 4. V3-T2：PDF 工作台与 OCR 文本编辑校注

**状态**：范围重组；校注编辑方案待补全。

本任务统一承载 PDF 工作台内的页面查看、OCR 文本编辑校注和 Markdown 预览，避免把工作台能力拆散在多个桌面 UI 任务中。现有 PDF/Box Tree 工作台之上的选区、修订、批注，以及与 bbox/证据身份的稳定关联仍属于本任务；具体信息架构、命令集、数据模型与校注持久化格式在后续 PRD 修订中补全。

### 4.1 PDF 工作台范围

- PDF 工作台负责页面导航、页面内容与 Box Tree 的联动，以及 OCR/文档内容的工作区展示。
- 目标是增强面向校对与校注的文本编辑体验；编辑结果必须继续遵守 Document Tree、versioned evidence URI 和 revision 的边界。
- 本任务不为 UI 预览另建一套 Markdown、Document Box Tree、SearchUnit 或证据数据源，也不在方案补全前实现范围外的校注持久化格式。

### 4.2 PDF 工作台的 Markdown 预览

- 预计评估并优先采用 `MarkView.Avalonia` 作为 PDF 工作台中的 Avalonia Markdown UI 预览组件；当前仅记录为候选方案，不代表依赖已经加入项目。
- 预览必须正确处理当前 OCR/文档内容实际使用的 Markdown/GFM，包括标题、段落、列表、引用、代码、表格、链接和安全的内联 HTML。
- 预览只是 PDF 工作台的 UI 展示投影，不得修改 canonical Markdown、Document Box Tree、SearchUnit、versioned evidence URI 或 revision 身份。
- 预览不得暴露本地路径、`file:` URL、提供程序密钥或缓存图像路径；外部链接和 HTML 处理必须有明确的安全策略。
- 组件选型必须验证 Avalonia/.NET 版本兼容性、中文字体与布局、PDF 工作台长文档性能、主题适配、测试可控性和发布包体积，再决定是否正式引入依赖。

### 4.3 V3-T2 验收

| 编号 | 标准 |
|---|---|
| V3-T2-AC1 | 代表性 OCR/文档 Markdown/GFM fixture 在 PDF 工作台页面中正确呈现标题、列表、引用、代码、表格、链接和安全内联 HTML |
| V3-T2-AC2 | PDF 工作台预览失败或不支持的语法可解释，不静默丢失正文；canonical Markdown 与领域数据不被修改 |
| V3-T2-AC3 | PDF 工作台预览不会暴露本地路径、file URL、提供程序密钥、缓存图像路径或未允许的 HTML/脚本内容 |
| V3-T2-AC4 | MarkView.Avalonia（或其他候选组件）通过 Avalonia 兼容性、主题、中文文本、PDF 工作台长文档性能、包体积和发布构建验证后才进入生产依赖 |

## 5. V3-T3：集成更多 OCR

**状态**：方向已定，细则待补。

- 使用 **LLMTornado** 集成多模态大语言模型 OCR/理解路径，输出仍必须进入既有 `OcrDocumentTreeCandidate` → 统一 import/commit，禁止 provider 直写 `document_boxes`
- 已交付本地 OCR 引擎：**ndlkotenocr-lite**（ADR [0025](adr/0025-ndlkotenocr-lite-onnx-port.md)）、**RapidOCR**（ADR [0031](adr/0031-native-rapidocr-onnx-port.md)）、**ndlocr-lite**（ADR [0032](adr/0032-ndlocr-lite-onnx-port.md)），见顶部 V3-T9 基线
- 仍待探索接入：**onnxOCR**、**ultimateOCR**
- MinerU 仍为已交付的生产参考路径；新 provider 的打包、模型分发、许可、preset UX、失败分类与密钥边界在后续修订中规定
- 继续遵守：Mock/历史占位不进生产默认；secret 仅 local-only credentials；不规则表等 canonical 规则不因新 provider 回退

## 6. V3-T4：Linux 桌面适配与发行打包

**状态**：正式发行验收未完成；保留以下目标与验收矩阵。

### 6.1 目标与范围

Linux 是 Patchouli 的正式桌面运行与发布目标，不再把 Linux 仅视为“能运行 .NET 程序即可”。V3-T4 至少包含以下四项交付面：

1. **Linux PATH 处理**：桌面应用能够发现随应用或安装包提供的 `patchouli-cli`，准确报告其是否已经位于当前用户的 `PATH`，并提供明确、可逆、幂等的用户级 PATH 注册与移除行为。
2. **Debian 包（`.deb`）**：提供可安装、可卸载、带版本与架构元数据的 Debian 系发行版包。
3. **RPM 包（`.rpm`）**：提供可安装、可卸载、带版本与架构元数据的 RPM 系发行版包。
4. **AppImage**：提供无需系统安装即可运行的 AppImage，并包含桌面启动所需的元数据与 CLI 使用路径。

首个可验收发布矩阵至少覆盖 `linux-x64`；`linux-arm64` 是否纳入同一版本必须以 PDFium、Avalonia、Rust helper 与其他 native payload 的实际验证结果为准，不得只因 .NET publish 成功就宣称支持。

### 6.2 Linux PATH 规则

- PATH 分隔符、路径规范化、可执行文件检查和 `InPath` 判断必须使用 Linux/POSIX 语义，不得复用 Windows Registry 或分号分隔逻辑。
- PATH 注册默认只作用于当前用户，不要求 root，不得未经明确同意修改 `/etc/profile`、`/etc/environment`、系统 `/usr/bin` 或 `/usr/local/bin`。
- 用户级 bin 目录、符号链接或 wrapper 的选定策略必须文档化；添加、移除、重复执行和目标已被用户替换时都必须是可解释且安全的。
- 移除操作只能移除 Patchouli 自己创建且仍指向当前 CLI 的链接或 wrapper，不得删除同名的用户文件或其他版本的 CLI。
- 如果桌面会话的环境变量不会因操作立即刷新，UI 必须报告“下次登录/新终端生效”等实际语义，而不是虚报当前 PATH 已生效。
- AppImage 不得通过指向临时挂载目录的裸符号链接伪造 PATH 支持；必须提供稳定的 CLI launcher、伴随 CLI 或等价的明确集成方式。

### 6.3 Linux 包规则

- `.deb` 和 `.rpm` 必须包含桌面应用、生产所需的 CLI、桌面 entry、图标及必要的 native payload；安装后 `patchouli-cli` 必须通过包定义的标准命令路径可执行。
- 两种系统包必须使用同一版本号、协议 revision 和构建来源；包名、架构、依赖、文件清单和卸载行为必须可检查。
- AppImage 必须包含有效的 `AppRun`、`.desktop` 文件和图标，能在干净的用户目录中启动桌面应用，并能按文档使用对应 CLI；不得依赖开发机的绝对路径、环境变量或未声明的构建目录。
- 三种格式都不得重新引入已移除的 Bashkit shell sidecar；打包产物必须经过内容检查，不能携带 provider secret、开发数据库、缓存图片、调试符号或无关构建文件。
- 发布脚本必须支持干净输出目录、失败即停和可重复构建；至少能在 CI 或隔离 Linux runner 中执行包内容检查与最小启动/CLI smoke test。

### 6.4 V3-T4 验收

| 编号 | 标准 |
|---|---|
| V3-T4-AC1 | Linux `linux-x64` 桌面应用可启动，使用 XDG 约定的配置、数据、缓存和日志位置；CLI 可被发现，且不存在 Windows/macOS 专用路径假设 |
| V3-T4-AC2 | Linux PATH 处理有契约测试：已在 PATH、未在 PATH、重复添加、移除、外部同名文件/链接、不可写目录和新终端生效提示均有明确结果；操作不需要 root 且不破坏用户已有 PATH |
| V3-T4-AC3 | 生成 `.deb`，可在隔离 Debian 系环境安装并启动应用、执行 `patchouli-cli`、卸载后无非预期残留；版本、架构、依赖和文件清单可检查 |
| V3-T4-AC4 | 生成 `.rpm`，可在隔离 RPM 系环境安装并启动应用、执行 `patchouli-cli`、卸载后无非预期残留；版本、架构、依赖和文件清单可检查 |
| V3-T4-AC5 | 生成 AppImage，可在未安装目标依赖的干净用户目录启动应用；`.desktop`、图标、AppRun、CLI launcher/伴随 CLI 和架构均通过 smoke test |
| V3-T4-AC6 | `.deb`、`.rpm`、AppImage 与桌面/MCP/CLI 使用同一版本与协议 revision；产物不包含 shell sidecar、secret、开发数据库、缓存图片、绝对构建路径或调试文件 |

## 12. 明确不做（v3 默认）

- 不把向量化、混合搜索、语义搜索作为 v3 完成标准
- 不做题录/CSL 样式版本控制、diff/compare 或跨 Item 历史拼接
- 不做多层/嵌套集合、集合级权限或集合同步合并；集合仅是一层只读关系，MCP 不能创建、重命名、解散或改成员，只能通过 `patchouli://library.toon` 发现并按精确 `collection_id` 过滤
- 不做程序托管的原文件同步
- 不做账号注册、配额购买、云端计费管理
- 不做自动对象级同步合并或静默 last-writer-wins
- 不做库级加密/主密码方案
- 不让 MCP/CLI 获得 OCR 触发、索引重建、任意删除/重命名资源、或读取提供程序密钥的能力
- macOS 不上架 Mac App Store / 不启用 App Sandbox 作为前提（既有 ADR）

## 13. 版本理念

- **v1**：alpha 可验证基线——保护证据，暴露歧义，拒绝不安全自动化  
- **v2（0.2.x）**：最终用户可用面——UI、CSL、生产 OCR、可配置 MCP、冲突/阻塞  
- **v3（0.3.x）**：迈向 1.0——用评测选择长期 agent 表面，打磨 OCR 编辑校注，扩展可替换 OCR 组合，只留下经得起稳定承诺的能力  
- **1.0**：在 v3 验证通过的能力组合上冻结对外契约与升级策略

## 14. 长期文档索引

| 内容 | 权威位置 |
|---|---|
| 领域词汇、搜索模式、UI 术语与产品边界 | [CONTEXT.md](CONTEXT.md) |
| 文档布局、当前 Host 组成与一致性机制 | [domain.md](domain.md) |
| MCP / CLI 参数、响应 schema、错误与回归义务 | [mcp-protocol.md](mcp-protocol.md)；决策 ADR `0023` / `0024` |
| 数据存储、快照、文件、Document Tree 等架构决策 | [adr/](adr/) |
| NDL 本地 OCR 与文件管理 | [ADR 0025](adr/0025-ndlkotenocr-lite-onnx-port.md) / [0031](adr/0031-native-rapidocr-onnx-port.md) / [0032](adr/0032-ndlocr-lite-onnx-port.md) |
| 统一版本模型、versioned URI | ADR [0027](adr/0027-unified-working-copy-and-immutable-revision-model.md) / [0028](adr/0028-versioned-uri-evidence.md) |
| 题录删除、合并、GC 与快照冲突 | [ADR 0030](adr/0030-item-lifecycle-merge-and-purge.md) |
| 性能 fixture、运行方式与基准限制 | [perf/README.md](perf/README.md) |

已完成能力的新行为约束写入相应领域文档/契约；改变架构决策时更新 ADR。PRD 只保留顶部能力摘要与尚未关闭的产品范围，避免再次堆积已交付的需求和验收表。
