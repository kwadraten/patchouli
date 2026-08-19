# Patchouli PRD v3

状态：正式版  
版本线：0.3.x（迈向 1.0）  
日期：2026-08-02

仓库只维护这一份 PRD。已完成能力压缩为顶部 walkthrough；长期不变量以 `.agents/CONTEXT.md` 与 `.agents/adr/` 为准。

## Walkthrough（已完成基线）

v1/v2 已交付、可视为 0.2.x 稳定面的能力。细节以代码、测试与 ADR 为准，不在此复述需求正文。

- 桌面栈：.NET + Avalonia；首轮初始化、库页、题录编辑、搜索、OCR 队列、PDF 工作台、设置、About
- 库与文件：路径无关 `library_id`；Item / FileAsset / DocumentInstance；FileSearchRoot 与文件解析冲突
- Document Box Tree：0.2.0 fresh schema；页级 immutable revision；typed leaf；sibling 顺序；Markdig 中央编译
- 搜索与证据：SearchUnit + 可重建本地 FTS；搜索配置文件；versioned URI `?rev=&box=`；带 `rev` 读固定版本、无 `rev` 读 HEAD
- OCR：MinerU 为首选生产路径；`OcrDocumentTreeCandidate` 统一进入 working revision；队列看板；局部 OCR 与工作台编辑
- CSL：type-aware `CslItemTypeProfile`；`general` 不可静默当 CSL `document`；样式管理与复制/导出；渲染失败不空成功
- MCP（当前实现）：可配置端口/bind/CORS/token/工具开关；v1/v2 首发 MCP 是只读且纯文本的，v3 生产路线已选定为结构化 `patchouli.find` / `patchouli.fetch` / `patchouli.put` / `patchouli.cite`（ADR `0024`），并允许可选的有限题录/样式写入
- UI 信息架构：设置五分组；`UiCommandDescriptor`；书库 DataGrid（列宽/顺序/排序/显隐/持久化）；阻塞与冲突模态
- 同步：快照分片；分支检查与显式导入；无自动对象级合并

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

## 2. v3 任务总览

| 序号 | 任务 | 状态 |
|---|---|---|
| V3-T7 | 性能与响应性治理（首屏、OCR 入库、MCP 读取、PDF 查看） | **P0；高优先级；立即开始** |
| V3-T1 | MCP 路线选择与完善 | B 已择优；结构化生产迁移进行中 |
| V3-T2 | PDF 工作台与 OCR 文本编辑校注 | 范围重组；校注编辑方案待补全 |
| V3-T3 | 集成更多 OCR provider | 方向已定；细则后续补全 |
| V3-T4 | Linux 桌面适配与发行打包 | 新增；未开始 |
| V3-T5 | 书库 UI 与题录管理 | 已实现；AC1~AC19 验收通过 |
| V3-T8 | 搜索功能强化 | 从原 V3-T5 拆分；方案评估中 |
| V3-T6 | 版本控制、证据引用及其 UI 表示 | 已决策；见 ADR `0027`/`0028` |
| V3-T10 | 统一 working/commit 版本模型与 versioned URI 证据 | 新增；准备实现 |

### 2.1 V3-T7：性能与响应性治理

**状态**：P0，高优先级；立即开始。该任务优先于其他尚未开始的 v3 体验增强任务，但不得以削弱 Document Tree、working/commit 生命周期、versioned evidence URI、快照或 MCP 契约为代价换取表面速度。持久化模型重设计已由 ADR `0027`/`0028` 决策并转入 V3-T10，不再是 V3-T7 的阻塞后续项。

#### 2.1.1 问题与目标

当前性能问题集中在四个用户可感知路径：

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

- UI、MCP 与 CLI 的一致性以 ADR `0024` 的单一 Library runtime host 为边界。UI 与 MCP 复用宿主内部同一套查询、投影、revision 和写入服务；CLI 仍是本地 MCP HTTP 瘦客户端，不得直连 SQLite 或新增第二套领域数据源。
- 所有改变 protocol-visible canonical 状态的成功写入经宿主写服务提交后发布类型化 `resource-changed` 通知。书库列表、打开的题录、CSL 样式、OCR 队列与 PDF 工作台订阅相关变更并增量刷新，不以固定周期轮询作为主要一致性机制；FTS rebuild、预取和运行时缓存维护只发布内部状态，不伪造 Library commit。
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
- 页面 Box/SearchUnit 一次批量读取；需要生成 evidence URI 时收集全部 unit ID，并最多执行一次 `CreateFromSearchUnits` batch write。名义读取接口中的必要副作用必须显式经过 host write service，不能在循环中偷偷创建连接和事务。
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

## 3. V3-T1：MCP 路线选择与完善

### 3.1 问题

评测前生产 MCP 表面是 Bashkit 只读虚拟 shell（`patchouli_shell`，ADR `0022`）：对熟悉 shell 的 agent 友好，但存在：

- 探索路径长、组合命令成本高、边界与资源限制复杂
- CLI 与 MCP 工具不是同一套动词/参数/错误码
- 写回题录/样式没有一等、可审计的协议（现状以只读为主）

v3 已通过可重复评测决定：采用 CLI/MCP 同构的 `patchouli-cli` 资源协议，并在该方案上重构为 1.0 对外 agent 表面。

### 3.2 候选方案

| 方案 | 摘要 |
|---|---|
| A. Bashkit 虚拟 shell | 单工具 `patchouli_shell`；VFS + 域内建命令；现状 ADR `0022` |
| B. CLI/MCP 同构 `patchouli-cli` | 四动词 `find` / `fetch` / `put` / `cite`；URI 资源树；CLI 与 MCP 共享参数、校验、权限、响应与错误码 |

**已完成的决策证据**：

在 `feature/mcp-ab-benchmark` / `083b0b5` 的外部 UUID-chain 复杂任务中，B 完成率 25/25（100%），平均 6.68 次调用、14.2 秒；A 完成率 11/25（44%），平均 41.16 次调用、49.4 秒。持久 UI/Library 会话中的 A shell 出现错误循环。结论见 ADR `0024`：B 为唯一生产主路径；A 的 shell 实现已从 main 彻底清除（实现与评测证据仅保留在 `feature/mcp-ab-benchmark` 分支），不再作为长期双主路径。

### 3.3 评测基准（先于重构）

基准必须可脚本化、可回归，覆盖人类操作员与 agent 轨迹。至少包含：

| 维度 | 度量 |
|---|---|
| 任务完成率 | 固定任务集（发现样式、检索文献页、取证、渲染题录、有条件写回 `.bib`/`.csl`）一次成功率 |
| 轨迹效率 | 达到目标的中位工具调用次数、中位 token/字符吞吐、中位墙钟时间 |
| 可发现性 | 无先验 URI 时，从根浏览到目标资源的步数与失败率 |
| 正确性 | 错误 URI/越权/校验失败时的错误码稳定性；不把截断内容静默呈现为完整内容 |
| 安全边界 | 不出现本地路径、file URL、提供程序密钥、缓存图像、OCR/索引触发；日志不落查询正文与 secret |
| 权限模型清晰度 | 只读 vs 可写资源是否可被 agent 从响应字段（如 `writable`）推断 |
| 实现与运维成本 | 依赖面、打包物、协议复杂度、故障恢复（sidecar fault 等） |
| 同构性 | CLI 与 MCP 是否可对同一输入得到同一 JSON schema 与同一 exit/error code |

固定任务集示例（可扩展，但变更需版本化）：

1. 列出/搜索 CSL 样式并 `fetch` 一个 style
2. 在 documents 范围搜索关键词，再 `fetch` outline 与单页 range
3. 解析 evidence 并 `cite`
4. 对可写 item `.bib` 做 `fetch` → 本地修改 → `put` 原子成功；并验证不完整、无效或截断内容不能写回
5. 对 `general` 题录验证 `@misc` cite fallback，对只读 document/page 验证 `put` 拒绝而 `cite` 通过所属 Item 解析成功
6. 故意越权与超大响应：越权必须失败；超大响应必须返回受限 partial 内容并显式报告 `RESPONSE_TRUNCATED`，不得当作完整成功

评测产出写入 issue 或 ADR 草案：分数表、样例轨迹、选择理由、迁移计划。

### 3.4 生产方案规格：`patchouli-cli`（CLI/MCP 同构）

> 本节是已选定的权威规格。生产 MCP 必须符合；CLI 与 MCP 共享同一组服务、参数、响应和错误语义。

**定位**：`patchouli-cli` — access an academic literature library powered by patchouli.net

#### 3.4.1 全局

```text
USAGE
  patchouli-cli [--json] <COMMAND> [ARGUMENTS]

COMMANDS
  find      Discover or search resources
  fetch     Retrieve known resources
  put       Replace one writable resource
  cite      Render citations from citation-capable item/document/page/evidence refs

GLOBAL OPTIONS
  --json       Return JSON instead of the default TOON listing; does not change the unified response schema or entry projection
  --help       Show help
  --version    Show version

RULES
  Use find when the URI is unknown.
  Use fetch when the URI is known.
  Use put to replace one writable resource.
  Use cite only to render citations.

  Empty find queries browse a scope.
  Non-empty find queries search the same scope.
  Resources are always identified by URI.
```

**Help 与初次握手**：CLI `--help` 和 MCP 的初次握手/initialize 响应必须明确说明统一响应外壳及 `message` 的 Unix 哲学：正常且没有需要报告事项的成功响应**不返回 `message` 字段**；调用方看到没有 `message` 即可将该响应视为干净成功，不应期待成功文本。只有有稳定 warning 或 error 需要机器处理时才返回 `message`；warning 不改变成功 exit/error code，error 则按错误码决定失败。帮助、握手、示例与工具 schema 均不得把空字符串、`"OK"`、空 `message` object 或人类成功文案当作成功信号。

**三个操作界面、一个 Library runtime host**：桌面 UI、`patchouli-cli` 和 MCP 是同一 Library 的不同操作界面；Library 数据库、cursor、revision 与领域规则的唯一运行时权威是 .NET **宿主**。桌面宿主承载全功能人类 UI 与本地 MCP HTTP 端点；MCP 是供 agent 在本地或远程协作时使用的网络前端；CLI 同时面向人类和 agent，是该本地 MCP HTTP 端点的瘦客户端。三者因此在构造上共用同一次服务端 URI 解析、投影、校验、revision、错误和写入语义。

CLI 先发现选定 Library 的本地宿主并调用其 MCP HTTP 端点；桌面未运行时，CLI 必须自启同一二进制的无 UI headless 宿主，待其就绪后再执行同一请求。headless 宿主以后台守护程序持续运行。每个 Library 同时只能有一个宿主，由持久化发现记录和互斥锁保证；桌面启动同一 Library 时必须终止 headless 宿主、取得数据库所有权后接管服务。CLI 不得直连 SQLite、另建领域实现或绕过宿主写服务。`--from`/`--stdin` 仍只是 CLI 客户端读取内容的本地适配器，随后必须形成内联 MCP `content` 请求。

所有 UI、CLI 和 MCP 写入均流经宿主的单一写服务；成功提交会发布资源变更通知，桌面 UI 据此刷新。桌面与 headless 宿主共用 MCP 设置；headless 监听 `0.0.0.0` 时同样必须有 token，且 bind、CORS、工具开关和认证策略与桌面宿主一致。宿主生命周期命令（例如显式 `serve-mcp`）可以提供给 CLI，但不属于 `find` / `fetch` / `put` / `cite` 四个资源动词，也不映射为 MCP 工具。

#### 3.4.2 资源树

```text
RESOURCE TREE
  patchouli://items/
  patchouli://items/{item-id}.bib

  patchouli://texts/
  patchouli://texts/{document-instance-id}/
  patchouli://texts/{document-instance-id}/page-{page-index}.md
  patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree-revision-id}&box={box-id}

  patchouli://csl-styles/
  patchouli://csl-styles/{style-id}.csl
```

无参数 `find` 是 VFS 根目录发现，只返回 `/items`、`/texts`、`/csl-styles` 三个 directory 条目及各自的 canonical URI。根目录不暴露 `/evidence`、`/AGENTS.md`、`/library.yml`、`collections`、`profiles` 或其他虚拟 skill 文件。Evidence 仅通过 text page URI 的 `?rev={tree-revision-id}&box={box-id}` 访问。该 VFS/URI 发现层不恢复 Bashkit 或 `patchouli_shell`。

`page-index` 是指定 DocumentInstance 内与物理 PDF 页对应的**一基**页码。DocumentInstance 的物理页顺序稳定，不因 UI、CLI 或 MCP 的访问而重排；因此 `page-1.md` 是人类报告和程序调用共用的第一页。`?rev=&box=` 是 evidence 的规范消费形式：服务在 `fetch` 或 `cite` 消费它时必须验证 `tree_revision_id`/`box_id` 实际归属所声明的 DocumentInstance 和 page；不存在或不归属时返回 `NOT_FOUND`，不得把其他页面的 evidence 作为成功结果返回。

TOON 使用 MIT `Corvus.Toon.SystemTextJson` NuGet 包作为唯一编码/解码实现，并遵循 TOON specification **v3.0**；不得维护自定义 TOON parser 或 encoder。协议编码固定使用 UTF-8/LF、`ToonWriterOptions` 的字面 TAB delimiter 与 `KeyFolding=Off`；默认 uniform entries 使用 TOON v3 tabular form，声明的 `[N]` 必须与实际行数一致。`text/toon` 是其媒体类型。本资源发现与格式决策已由 ADR `0024` 的 2026-08-01 修订同步；后续改变 URI 根、evidence 消费形式、TOON 库或默认编码时，必须同时更新 PRD、ADR 与运行时契约。

#### 3.4.3 `find`

```text
USAGE
  patchouli-cli find [QUERY] [OPTIONS]

ARGUMENTS
  QUERY                Optional search query

OPTIONS
  --in <URI>           Scope to search or browse
  --where <KEY=VALUE>  Filter structured metadata; repeatable
  --literal            Disable query rewriting
  --limit <N>          Maximum results; default: 20
  --cursor <CURSOR>    Continue a previous real-time result page
  --long               Return the detailed entry projection

BEHAVIOUR
  Without QUERY: lists resources directly inside --in.
  With QUERY: searches resources inside --in.
  The root scope (`patchouli://`) is discovery-only: it accepts no QUERY or --where;
  choose a returned VFS directory before searching or filtering.
  Text search defaults to ranked SearchUnit search with query rewriting (library-configured search behaviour; not exposed as profile URIs in v3); item and style search use the scope matrix below.
  --literal: direct plain-text matching in the declared scope's supported fields, with no query rewriting.
  --literal requires a non-whitespace QUERY.
  A null, empty, or whitespace-only QUERY is browse, like POSIX `ls`; a non-whitespace
  QUERY is search, like POSIX `find`. A supplied whitespace-only QUERY is normalized
  to browse and returns `WHITESPACE_QUERY_TREATED_AS_BROWSE`.
  Regular-expression search is not part of the CLI/MCP protocol. Agents that need it
  must match the returned text locally after find/fetch.
  Cursors are opaque, stateless, real-time continuation tokens. They bind the
  declared scope, query, filters, ordering, and continuation position, but do not
  retain a materialized result set or server-side handle.
  A cursor resumes its embedded normalized scope, query, filters, and order. If a
  continuation request supplies conflicting values, cursor context wins and the
  response returns `CURSOR_CONTEXT_RESTORED`; an invalid cursor still returns
  INVALID_ARGUMENT. limit is not bound and may change the next page size.
  A text search in `patchouli://texts/` returns one file entry per matching
  SearchUnit. Its default `uri` is the canonical evidence-consumption page URI:
  `patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree-revision-id}&box={box-id}`.
  The `rev`/`box` query parameters are therefore present in the default three-field entry projection,
  not only in --long metadata or a separate match field.

DEFAULT SCOPE
  patchouli://

DEFAULT RESULTS
  A root object with meta, continuation, optional message, and entries.
  meta.domain_total: all directly discoverable entries in the current VFS scope,
                       before QUERY/--where, evaluated when this page is read.
  meta.filtered_total: all QUERY/--where matches before pagination, evaluated
                       when this page is read.
  meta.shown_total: entries returned in this page.
  meta.library_revision: the current persistent Library revision.
  continuation: next cursor or null.
  message: omitted for a clean success; when present, contains stable warnings and/or an error.
  entries: exactly uri, title, type; type is file or directory.

DETAILED RESULTS (--long)
  Preserve uri, title, type and add only metadata that is applicable to the
  returned resource. URI already identifies the DocumentInstance, physical
  page and (when present) versioned evidence URI, so long entries never repeat
  document_instance_id, page_index or rev/box. The only public status
  fields are item_status, document_status and source_status; each reflects
  the original status of Item, DocumentInstance or FileAsset respectively.
  OCR/search-index readiness is an independent shared FSM value, not a
  fourth status or an alternate document_status. The stable English values
  are no_primary_document, ocr_failed, ocr_running, no_ocr, ocr_not_indexed
  and indexed; the UI renders the same value as a concise Chinese label plus
  an explanatory detail.
  Because detailed fields differ by resource, detailed entries use TOON list
  form rather than a uniform table; default entries always remain the compact
  table.
```

| `--in` scope | 无 QUERY | 普通 QUERY | `--literal` | 可用 `--where` |
|---|---|---|---|---|
| `patchouli://` | 仅发现三个根目录；`--limit`/`--cursor` 按普通分页处理并返回 `ROOT_DISCOVERY_PAGINATED` | `INVALID_ARGUMENT` | `INVALID_ARGUMENT` | `INVALID_ARGUMENT` |
| `patchouli://items/` | 浏览题录资源 | 题名、作者、identifier 元数据搜索 | 相同字段的直接字面匹配 | `item_type`、`item_status`、`primary_document_ocr_index_status`、`citable` |
| `patchouli://texts/` | 浏览 text document 资源 | 带 query rewrite 的 SearchUnit 全文搜索；每个命中 SearchUnit 一项 | canonical indexed SearchUnit text 的直接字面匹配 | `item_type`、`item_status`、`document_status`、`source_status`、`ocr_index_status`、`citable` |
| `patchouli://csl-styles/` | 浏览 CSL style 资源 | style id、display name 搜索 | 相同字段的直接字面匹配 | `style_enabled` |
| 已知 file URI | 当作单资源 scope 返回该 entry，并返回 `FILE_URI_SINGLETON_SCOPE` | 仅在该资源的矩阵内搜索字段上匹配，并返回同一 warning | 相同的单资源直接字面匹配 | 使用其所属 scope 的 filter 键 |

`--in` file URI 的单资源处理只返回 discovery entry，不自动 fetch 内容或改写为父目录 scope。scope、flag 或 filter 不在矩阵中的组合必须返回 `INVALID_ARGUMENT`，不得回退为成功空列表。`--regex` 是未知选项并返回 `INVALID_ARGUMENT`；它不会被当作 literal query 或由服务端解释。

`--where` 每个 clause 在**第一个** `=` 分割，余下的 `=` 全部属于 value；发生此归一化时返回 `WHERE_VALUE_CONTAINS_EQUALS`。同一 key 重复出现时按 CLI 常见的最后一项覆盖先前项，而不是 AND/OR；发生覆盖时返回 `DUPLICATE_WHERE_KEY_LAST_WINS`。这些 warning 只说明输入被隐式处理，不改变成功响应或 exit/error code。

默认列表是导航视图，不返回 `path`、`kind`、`label`、`revision`、`writable`、匹配片段、关系字段、状态字段或能力字段。`meta` 与 `continuation` 是导航协议，不属于 entry 详细字段，始终返回；`message` 仅在有 warning 或 error 时出现。CLI 普通输出和 MCP 默认文本结果均为 TOON；CLI `--json` 和 MCP `format=json` 可以选择 JSON，但不会隐式返回详细字段。`format=json` 是 agent 批量读取、爬取或交给其他编程语言处理时的等价回退格式：它必须完整表达相同的统一外壳、entry projection、分页、message 与 error 语义，调用方无需解析 TOON。格式选择只改变编码，不改变 query、filter、权限、返回字段或默认/详细投影。MCP 以 `detail=long` 选择与 CLI `--long` 相同的详细投影。

**TOON 确定性 profile**：TOON 和 JSON 先共享同一份严格类型化的 JSON data model，再编码为各自文本。`Corvus.Toon.SystemTextJson` 是唯一的 encoder/strict decoder；它按 TOON v3 词法规则对 string 进行引用和 escape，数值 string、enum 与 bare token 均不得通过自定义字符串后处理改变语义。协议固定 UTF-8/LF、literal TAB 与 `KeyFolding=Off`；`integer`/`number` 保持 JSON number 类型，`boolean` 为无引号小写 `true`/`false`，null 为无引号 `null`。升级依赖或改变 profile 必须提高协议 revision，并以 JSON↔TOON round-trip 与逐字 golden fixture 验证。

**实时分页与计数**：cursor 不创建服务端快照、结果集句柄、TTL 或 agent 专属命名空间。每一页都对当前 Library 状态重新求值；只要响应发出 continuation，或请求消费 cursor，响应必须包含 `RESULT_SET_MAY_HAVE_CHANGED` warning，表示后续页面的 entries、`domain_total`、`filtered_total` 可能因 UI、CLI 或其他 agent 的修改而与此前页面不同，也可能出现遗漏或重复。调用方需要稳定结果时，应在无并发修改的时段自行完成遍历；v3 不提供跨页快照一致性保证。

`citable` 的含义是“该 URI 可以直接作为 `cite.refs` 输入”，不等同于资源可写，也不等同于资源本身就是 Item。只读资源可以是 citable；协议不暴露 `citation_target`。`cite` 在宿主内部按持久化关系解析 document、page 和 evidence 到所属 Item。全文搜索默认条目中的 `?rev=&box=` page URI 本身就是可直接交给 `fetch` 或 `cite.refs` 的 canonical URI，不需要先请求 `--long` 来发现 evidence。

默认根目录响应示例：

```toon
meta:
  library_revision: "lib:42"
  domain_total: 3
  filtered_total: 3
  shown_total: 3
continuation: null
entries[3	]{uri	title	type}:
  "patchouli://items/"	"/items"	"directory"
  "patchouli://texts/"	"/texts"	"directory"
  "patchouli://csl-styles/"	"/csl-styles"	"directory"
```

`--where` 可重复，但只接受上表中当前 scope 的键。`item_status` 保留 `items.status` 的用户自定义值，并将 null 显式映射为 `unset`；`document_status` 原样反映 `document_instances.status`（`active`、`deprecated`、`partial`、`missing_source`）；`source_status` 原样反映关联 `file_assets.status`，无 FileAsset 时映射为 `unavailable`。`primary_document_ocr_index_status` 与 `ocr_index_status` 接受共享 FSM 的 English value；前者检查 Item 的 `is_primary=1` DocumentInstance，后者检查当前 text DocumentInstance。它们是可实时重算的能力，不是 status。所有过滤可在默认视图使用；需要解释状态或能力时再请求详细视图。公共协议中不存在裸 `status` 字段、过滤键、别名、重定向或兼容层。

#### 3.4.4 `fetch`

```text
USAGE
  patchouli-cli fetch <URI>... [OPTIONS]

OPTIONS
  --range <RANGE>      Restrict textual content
  --limit-bytes <N>    Maximum serialized response size per URI; over-limit responses are returned as explicit partial results

RANGES
  lines:<START>-<END>
  pages:<START>-<END>

CANONICAL REPRESENTATIONS
  item URI             BibLaTeX inspection / editable projection
  text document URI    Document outline, owning item link, and page links
  text page URI        Canonical Markdown and owning item/document link
  text page ?rev=&box= URI  Versioned evidence text, source mapping, and owning item link
  style URI            CSL XML

BEHAVIOUR
  fetch never searches.
  fetch never follows links automatically.
  fetch never returns another representation of the same URI.
  `limit_bytes` remains a hard safety cap, but an over-limit response is not all-or-nothing.
  The server returns the largest safe prefix it can produce at a UTF-8/line/page boundary,
  marks the resource as incomplete, and reports `RESPONSE_TRUNCATED` with a continuation
  token or next range. A partial response must never be presented as a complete resource.
  For `fetch <URI>...`, each URI has an independent result; one missing or oversized URI
  must not discard successful results for the other URIs.
  Fetch always returns the current resource projection unless the URI carries `rev`,
  in which case it returns the immutable revision named by `rev`. Historical selection
  by `rev` is authoritative; there is no separate pinned/current/compare mode.
```

`pages` range 的 START/END 是包含端点的一基物理 PDF 页码，与 `page-{page-index}.md` 使用同一编号；不得在 API 边界使用零基页码。

#### 3.4.5 `put`（有限写入）

```text
USAGE
  patchouli-cli put <URI> --from <PATH>
  patchouli-cli put <URI> --stdin

MCP INPUT
  patchouli.put { "uri": "<URI>", "content": "<complete replacement>" }

WRITABLE
  patchouli://items/*.bib        (includes general items, mapped to @misc)
  patchouli://csl-styles/*.csl

READ-ONLY
  patchouli://texts/**

BEHAVIOUR
  put replaces exactly one resource.
  put never creates, deletes, or renames resources.
  For an existing item resource, the URI is authoritative: the BibLaTeX entry key in
  replacement content is ignored and the Item's existing citation key is preserved.
  put validates the complete replacement before writing.
  put commits the replacement atomically.
  put has no --base option and does not use optimistic concurrency.
  Concurrent valid replacements are serialized by commit order; the last successfully
  committed complete replacement becomes the current resource.
  put never accepts a partial or truncated fetch result as a complete replacement.
  After every successful put, the host write service emits a resource-changed notification;
  a connected desktop UI refreshes affected library rows, open item editors, and CSL style
  views without requiring a process restart.
  failed validation never modifies the library.
```

`--from <PATH>` 与 `--stdin` 是 CLI 的本地输入适配器，二者互斥：CLI 在本地读取完整文本后，向本地宿主 MCP HTTP 端点发送相同的内联 `content`；路径不进入领域命令、响应或 MCP。`patchouli.put` 只接受网络请求中内联的 `uri` 与完整 `content` 字符串；MCP schema 不存在 `from`、`stdin`、`path`、multipart、streaming 或 file-reference 参数。

为限制网络写入请求，MCP host 必须配置 `max_mcp_request_bytes`，默认 1 MiB、硬上限 4 MiB；超过当前上限的 HTTP 请求在调用工具前以 HTTP `413 Payload Too Large` 拒绝，不产生部分写入。宿主写服务还必须以同一当前上限检查完整 UTF-8 replacement content，因此 CLI 从路径/stdin 读取的内容与 MCP 内联 content 有相同的可接受大小、校验、原子提交和响应语义。

**`general` 题录的 agent 可访问性**：为消除 agent 与人类之间的信息不对称，`general` 类型题录对 CLI/MCP 可读可写——`fetch` 以 `@misc` BibLaTeX 投影返回，`put` 以 `@misc` 回写时保留原 `general` 类型。若 agent 明确将完整投影改为受支持的非 `misc` 类型（例如 `@book` 或 `@article`），这是显式的类型修正，应按该 BibLaTeX 类型映射并持久化为新的 Patchouli 类型；未知或仍映射为 `general` 的类型必须失败。`@misc` 投影路径必须是 MCP 专用路径，不得改变 UI 的 `general` 导出/导入限制。`general` 可以在具备最低可渲染字段时通过显式 `@misc` fallback 参与 `cite`，响应必须带有 `general_as_misc` warning；字段不足或 renderer 拒绝时返回 `NOT_CITABLE`，不得静默把它当作 `book`、`article` 或其他类型。`put` 不得绕过该限制把它当作可渲染类型。

**可写 MCP 产品意图**（ADR `0023`）：v1/v2 首发 MCP 只能“访问”库；v3+ 的可写 MCP 才能让 agent **与库交互并辅助人类**——例如样式库缺少合格 CSL 时，由能读全文件的 agent 起草并 `put` 样式；题录字段错误时，agent 修正完整 `.bib` 投影后 `put` 回写。`put` 仍是窄范围、原子性的整资源替换，不得扩展为 OCR 触发、bbox 编辑、索引重建、创建/删除/重命名资源。它不以 resource revision 作为写入前置条件；并发的合法整资源替换按成功提交顺序生效。设置中写入工具可关闭；实现须符合 ADR `0023`，并保留文本-only、无路径/密钥/图像等安全边界（ADR `0010` 中仍有效的条款）。只读 document/page 仍不可 `put`，但不因此失去 `cite` 能力。

#### 3.4.6 `cite`

```text
USAGE
  patchouli-cli cite <REF>... [--style <URI>] [OPTIONS]

OPTIONS
  --style <URI>        optional CSL style URI override; omitted uses the user's configured default style
  --locale <LOCALE>
  --bibliography
  --html

RESTRICTIONS
  Item, document, page, and evidence URIs are accepted citation references.
  A document resolves through `document_instances.item_id`; a page first validates
  ownership by its document and then resolves through the owning Item.
  A general Item may cite only through the explicit `@misc` fallback described above.
  Inspection-only .bib projections are not formal exports.
```

If `--style` is omitted, `cite` first uses the CSL style configured by the user as
the library default. If that style is unavailable, it may use a deterministic built-in
fallback style or another explicitly designated enabled default, and must return the
effective style URI plus a fallback warning. It must never silently produce an unstyled
result. If no configured or fallback style is available, citation rendering fails with
an actionable error.

For multiple `REF` arguments, resolution and rendering are independent per reference.
Successful references should be returned with per-reference results while invalid,
unresolvable, or non-citable references are reported in warnings/errors. The whole
request fails only when the request is invalid, the citation style cannot be loaded,
or no reference can be rendered.

#### 3.4.7 MCP 映射与共享契约

```text
CLI                                             MCP
patchouli-cli find                              patchouli.find
patchouli-cli fetch                             patchouli.fetch
patchouli-cli put <URI> --from <PATH>|--stdin   patchouli.put { uri, content }
patchouli-cli cite                              patchouli.cite
```

CLI 是本地 MCP HTTP 端点的客户端，四个 CLI 动词分别映射为四个 MCP tool request；因此服务端的默认值、URI 格式、校验规则、资源投影、revision、响应语义与错误码只有一份实现。CLI 契约测试验证参数解析与 MCP request 的映射，而不是维护两套领域实现的一致性。`find --long` 与 `patchouli.find(detail=long)` 是同一详细投影；MCP `format=json` 与 CLI `--json` 是同一 JSON 回退格式。CLI 的 `put` 输入适配器如上表：它读取本地内容后发送内联 content，MCP 从不接受或暴露本地路径。宿主统一执行 bind、CORS、token、工具开关与所有写入策略；本地 CLI 也不得绕过这些服务端规则。

共享响应外壳：CLI 的 JSON、MCP `format=json` 与默认 TOON 使用**同一**逻辑 schema；TOON 仅是该对象的确定性编码，不能省略、提升或重命名字段。干净成功的 JSON 例如：

```json
{
  "meta": { "library_revision": "lib:42" },
  "continuation": null,
  "entries": []
}
```

#### 3.4.7.1 统一响应 schema（规范性）

以下 schema 是 CLI `--json`、MCP `format=json` 与默认 TOON 的唯一逻辑模型。每个工具响应严格由 `meta`、`continuation`、可选 `message` 与 `entries` 组成；object 不得出现未声明字段。`String` 是 Unicode string，`Uri` 是 canonical `patchouli://` string，`NonNegativeInt` 是非负 JSON integer，`PositiveInt` 是大于零的 JSON integer；它们在 TOON 中分别按 string 与 integer 规则编码。`message.error` 与 `message.warnings` 都是紧凑的终端诊断文本：错误固定为 `NAME [code N]: detail`，其中 `NAME` 与非零 `N` 必须来自同一错误码表；仅内部错误可在方括号追加 `; ref <correlation-id>`。warning 固定为 `NAME: detail`。诊断 detail 必须由宿主白名单模板生成并脱敏，不得直接输出 exception、stack、路径、file URL、secret 或原始 content。

```text
Response<TMeta, TEntry> = {
  meta: TMeta & { library_revision: LibraryRevision },
  continuation: String | null,
  message?: {
    error: String | null,
    warnings: String[],
  },
  entries: TEntry[]
}
```

`LibraryRevision` 是 `"lib:<positive decimal integer>"`。`message` 遵循 Unix 哲学：当且仅当没有 warning 且没有 request-level error 时必须省略；没有 `message` 即表示干净成功。存在 warning 时 `message.error` 为 null，仍可为成功；存在 request-level error 时 `message.error` 非 null。逐项操作的失败保留在相应 entry 的同格式 `error` 字符串；只有所有逐项操作均失败、请求本身无效，或必须以非零状态报告的 partial/truncated 情况，才同时在 `message.error` 给出对应错误。warning 与 error 都是供人和 agent 直接读取的终端式诊断，不承载成功文案或本地实现细节。`message.error` 非 null 时，MCP tool response 必须标记 `isError=true` 并保留可用的 partial `entries`；CLI 将结构化响应写入 stdout，并从错误文本中的 `[code N]` 使用相应非零退出码报告错误。

**旧 envelope 清理**：实现必须删除 `McpEnvelope<T>.Revision` 及其序列化顶层 `revision` 字段，并将 CLI、MCP transport、契约测试和所有调用方迁移为读取 `meta.library_revision`。在新的版本控制 ADR 明确前，不得以旧字段替代或重新引入 `fetch --revision`、`resource_revision` 或其他资源级历史版本选择字段。

只有 `find` 可以在顶层 `continuation` 中返回 cursor；`fetch` 的继续读取信息只属于其单个 entry，`put`/`cite` 的顶层 continuation 必须为 null。

| Tool | `meta` 与 `entries` schema | 成功与逐项失败规则 |
|---|---|---|
| `find` | `meta` 为 `{ library_revision: LibraryRevision, domain_total: NonNegativeInt, filtered_total: NonNegativeInt, shown_total: NonNegativeInt }`；`entries` 为 `FindEntry[]` | 默认 `FindEntry` **严格只有** `{ "uri": Uri, "title": String, "type": "file" \| "directory" }`。`detail=long` 使用按资源种类判别的精确投影：`ItemLongEntry = { uri, title, type: "file", item_status, primary_document_ocr_index_status, citable }`；`TextLongEntry = { uri, title, type: "file"\|"directory", item_uri, item_status, document_status, source_status, ocr_index_status, citable }`；`StyleLongEntry = { uri, title, type: "file", style_enabled }`。除 `item_status` 可为 `"unset"` 外，三个 `*_status` 均为对应实体的原始持久化值；没有关联 FileAsset 的 text 资源使用 `source_status: "unavailable"`。字段只在其适用 variant 中出现，严禁用 null 填充其他 variant，也不得重复 URI 已表达的 document/page/evidence locator。`meta.shown_total` 必须等于 entries 长度。 |
| `fetch` | `meta` 为 `{ library_revision: LibraryRevision }`；`entries` 为 `FetchResult[]` | 每个输入 URI 产生一个同序 `FetchResult`，不得因其他 URI 失败而丢失。见下方 discriminated variant。 |
| `put` | `meta` 为 `{ library_revision: LibraryRevision }`；成功时 `entries` 为单元素 `PutResult[]`，失败、取消或超限写入时为 `[]` | `PutResult = { uri: Uri, resource_type: "item_bib" \| "csl_style", committed: true, content_bytes: NonNegativeInt }`。失败详情由 `message.error` 表达。`content_bytes` 是已提交 UTF-8 content 的实际字节数。 |
| `cite` | `meta` 为 `{ library_revision: LibraryRevision, effective_style_uri: Uri, effective_locale: String, render_format: "text" \| "html", bibliography: String\|null }`；`entries` 为 `CitationResult[]` | 每个输入 REF 产生一个同序 result；可渲染 result 与不可解析/不可引用 result 可以共存。`bibliography` 仅在请求时为 String，否则为 null；其去重不影响 entries 长度与顺序。 |

上述 long entry 由固定宽表改为资源专属 variant 是协议 schema 变更：实现该条款时必须提升 MCP protocol revision，并在同一变更中更新 MCP tool schema、CLI help、DTO、TOON/JSON fixture 与所有调用方；不得保留输出旧 null 填充字段的兼容分支。

```text
FetchResult = CompleteFetch | TruncatedFetch | FailedFetch

CompleteFetch = {
  uri: Uri, resource_type: ResourceType,
  item_uri: Uri | null, content: String,
  complete: true, truncated: false,
  returned_bytes: NonNegativeInt, limit_bytes: PositiveInt,
  continuation: null, next_range: null, error: null
}

TruncatedFetch = {
  uri: Uri, resource_type: ResourceType,
  item_uri: Uri | null, content: String,
  complete: false, truncated: true,
  returned_bytes: NonNegativeInt, limit_bytes: PositiveInt,
  continuation: String | null, next_range: String | null,
  error: "RESPONSE_TRUNCATED [code 7]: detail"
}

FailedFetch = {
  uri: Uri, resource_type: null, item_uri: null,
  content: null, complete: false, truncated: false,
  returned_bytes: 0, limit_bytes: PositiveInt,
  continuation: null, next_range: null, error: "NAME [code N]: detail"
}

CitationResult = {
  ref: String, item_uri: Uri | null, citation: String | null, error: String | null
}
```

`ResourceType` 是 `item_bib`、`text_document`、`text_page`、`evidence` 或 `csl_style`。`CompleteFetch.returned_bytes` 必须等于 content 的 UTF-8 字节数且不超过 `limit_bytes`；`TruncatedFetch` 有相同字节约束，且 `continuation` 与 `next_range` 至少一个为非 null。任一 `TruncatedFetch` 使顶层 `message.error` 为 `RESPONSE_TRUNCATED [code 7]: …`，但仍保留完整 `entries`；`FailedFetch.error` 不得包含 `[code 7]`。`CitationResult` 成功时 `item_uri`、`citation` 非 null 且 `error` 为 null，失败时前两者为 null 且 `error` 为终端错误行。若所有 citation result 均失败，`message.error` 为相应错误；否则 `message.error` 为 null（若也没有 warning，省略 `message`）。错误码、schema 或类型新增/变更均为协议 revision 变更。资源级历史版本与 `fetch --revision` 在新的版本控制 ADR 明确前不属于 v3 协议。

`meta.library_revision` 是当前 Library 的宿主权威 revision，格式固定为 `lib:<十进制正整数>`；它持久化于该 Library，且每次成功、会改变协议可见资源或关系的 Library 写入后严格单调递增，即使桌面/headless 宿主交接也不得回退或复用。它不是默认 `find` entry 的资源 revision，也不是 `put` 的写前置条件。客户端保存的 fetch 内容只是该 revision 时的本地快照；v3 不推送或撤回其已交付内容。cursor 继续按实时语义读取，且其创建 revision 与当前 revision 不同时继续在 `message.warnings` 返回 `RESULT_SET_MAY_HAVE_CHANGED`。MCP 会话中宿主发现上一次已观察 revision 已落后于当前 revision 时，也必须在 `message.warnings` 追加 `LIBRARY_CHANGED_SINCE_LAST_RESPONSE`；无会话或断线客户端可通过 `meta.library_revision` 自行检测陈旧性并按需重新 fetch。

`patchouli://` 始终解析到处理请求的宿主当前 Library。当前 UI 尚不支持切换 Library，但宿主的生命周期内仍必须固定一个 `library_id`。cursor、versioned evidence URI（包括 `?rev=&box=` 内含的 Library 绑定）或未来显式 Library 上下文若与该 `library_id` 不匹配，解析器必须丢弃已经准备的内容，以 `NOT_FOUND` 返回，不得混入旧 Library 的 entries、partial entries 或 citation 结果。

`find` 的 warning 使用稳定名称：`RESULT_SET_MAY_HAVE_CHANGED`（实时分页可能漂移）、`WHITESPACE_QUERY_TREATED_AS_BROWSE`、`CURSOR_CONTEXT_RESTORED`、`ROOT_DISCOVERY_PAGINATED`、`FILE_URI_SINGLETON_SCOPE`、`WHERE_VALUE_CONTAINS_EQUALS` 与 `DUPLICATE_WHERE_KEY_LAST_WINS`。所有工具还可在 `message.warnings` 返回 `LIBRARY_CHANGED_SINCE_LAST_RESPONSE`。每一项按 `NAME: detail` 输出，使 agent 可立即知道宿主如何解释或调整了请求；warning 是成功响应的一部分，不改变 exit/error code；没有 warning/error 时不返回 `message`。

Exit / error codes：

| Code | Name | 含义 |
|---|---|---|
| 0 | OK | 成功（含空 find） |
| 1 | INTERNAL | 未预期的宿主、数据库或内部 helper 异常；只返回稳定错误码及可选 correlation id，绝不泄露异常详情、堆栈、本地路径或 secret |
| 2 | INVALID_ARGUMENT | 非法或冲突参数 |
| 3 | NOT_FOUND | URI 不存在 |
| 4 | PERMISSION_DENIED | 资源权限或策略禁止当前操作 |
| 5 | RESERVED | v3 `put` 不使用 base revision，也不返回 revision conflict |
| 6 | INVALID_CONTENT | BibLaTeX 或 CSL 校验失败 |
| 7 | RESPONSE_TRUNCATED | 响应超过安全大小上限，已返回显式 partial 内容 |
| 8 | UNAVAILABLE | Patchouli 服务不可用 |
| 9 | NOT_CITABLE | 资源存在，但无法解析为可渲染的引用目标 |
| 10 | DEADLINE_EXCEEDED | 宿主在请求 deadline 前未能完成；不返回 timeout 导致的 partial data |
| 11 | CANCELLED | 调用方取消且宿主在原子提交前停止了操作；不产生写入 |

推荐探索顺序：裸 `find` 发现 `/items`、`/texts`、`/csl-styles` → 进入一个返回的 URI，以小 `limit`、query、`--where` 和 `continuation` 缩小范围 → 对 Item 以 `primary_document_ocr_index_status=indexed` 筛选可全文检索的主文档，对 text 以 `ocr_index_status=indexed` 筛选可全文检索文本 → 仅 `fetch` 已返回的 URI → 按需以 `--long`/`detail=long` 检查状态、关系与引用能力 → 本地处理 → 仅对最终合法 `.bib`/`.csl` 执行 `put`。全文搜索返回的 `?rev=&box=` page URI 可直接 `fetch` evidence 或作为 `cite.refs` 输入；其他资源引用前使用 `where citable=true`。

#### 3.4.8 Agent 可用性与关系解析

- `citable=true` 必须与 `cite.refs` 实际接受的 URI 类型一致。Item、Document、Page 和 Evidence 可以是 citable；CSL style 仅可作为 `cite.style` 使用，绝不是 citable，因此 StyleLongEntry 不包含 `citable`。`writable` 与 `citable` 是两个独立维度。可引用资格由单一确定性规则计算；协议不返回 `citation_target`，宿主在 `cite` 内部解析到所属 Item。
- Document、Page 和 Evidence 的详细 `find`/`fetch` 结果应返回 `item_uri` 或等价的 `parent_uri`。这是关系元数据，不构成自动 link following。
- `cite` 对 text document/page 的解析只使用持久化的 `document_instances.item_id` 关系，不通过标题、文件名或全文搜索猜测 Item。Page URI 必须先验证 page 属于 URI 中声明的 text document。
- 如果多个 REF 解析到同一个 Item，bibliography 默认去重，但响应应保留每个 REF 的解析结果。
- `find` 带 query 时应搜索所声明的 `--in` scope。Item 至少支持题名/作者/identifier 等 metadata search，CSL style 至少支持 id/display name search；`item_status`、`document_status`、`source_status` 与 `style_enabled` 分别按其 Item、DocumentInstance、FileAsset 或 style 配置的权威记录读取，不能以派生索引/布局状态替代原始实体 status。`primary_document_ocr_index_status` 与 `ocr_index_status` 是共享 FSM 的独立能力：前者按 Item 的 primary DocumentInstance 关系解析，后者按 text document/page/evidence URI 所属 DocumentInstance 解析。Evidence 不是可浏览根资源；它只在 text 搜索中通过已含 `?rev=&box=` 的 page URI 表示，long 投影不得再次输出 `rev`/`box`、页码或 document ID。尚未支持的 scope/filter 必须返回明确的 `INVALID_ARGUMENT`，不得以成功的空数组代替“不支持”。
- `find` 在 `patchouli://texts/` 中带 query 时按 SearchUnit 命中逐项返回，不聚合为缺少 evidence 身份的 document 条目；每个默认 entry 的 `uri` 都必须内嵌该命中的 canonical `?rev=&box=`，因此 agent 仅凭 `uri`、`title`、`type` 就能继续 `fetch` 或 `cite`。
- `fetch <URI>...` 和 `cite <REF>...` 均采用逐项结果语义。单个资源的 `NOT_FOUND`、`NOT_CITABLE` 或 `RESPONSE_TRUNCATED` 不得丢弃同一请求中其他成功结果。
- `limit_bytes` 的默认值可以由服务配置，但必须有服务端硬上限；调用者请求超过硬上限时可以钳制并返回 warning，不得因此取消已可安全返回的 partial 内容。
- `general` 的 `@misc` cite fallback 与无默认 style 时的 deterministic style fallback 已同步记录到 ADR `0023`/`0024`；后续实现变更必须同时维护 PRD、ADR 与运行时契约，避免三者分叉。

#### 3.4.9 超时与取消

宿主必须实施较宽松的服务端 deadline：`find`、`fetch`、`cite` 默认最多 60 秒，`put`（含完整内容校验）默认最多 120 秒；部署可配置更严格的上限，但不得让客户端无限等待。deadline 到期返回 `DEADLINE_EXCEEDED`，不把因超时而中断的资源伪装为 `RESPONSE_TRUNCATED` partial 成功。

MCP cancellation、HTTP 断连和 CLI 中断必须传播到宿主的取消令牌。`find`、`fetch`、`cite` 取消后停止工作；`put` 在进入原子提交点之前被取消时返回 `CANCELLED` 且 Library 不变。提交点之后宿主必须完成该原子提交或回滚，绝不留下部分资源；已断连的调用方不得推断结果，必须重新 `fetch` 确认当前内容与 `meta.library_revision`。取消不停止后台 headless 宿主。

### 3.5 实现约束（无论 A/B）

- .NET 仍是唯一领域权威（库、SQLite、搜索、证据、CSL、权限）
- 不暴露本地路径、file URL、提供程序凭据/配置、缓存图像或 image path
- 不因 agent 调用而触发 OCR 或索引重建
- 响应必须受服务端硬大小上限保护；允许显式 partial response，但不得将 partial 内容呈现为完整资源
- Token/鉴权、bind、CORS、工具开关继续由本机 MCP 设置拥有；secret 不进快照与日志
- 每个 Library 只有一个桌面或 headless runtime host；CLI 只经其本地 MCP HTTP 端点操作，启动桌面时接管同 Library 的 headless 宿主
- 打包与版本：CLI、桌面宿主与 headless 宿主同源构建并作为一等发布物；版本与库协议 revision 可观测

### 3.6 V3-T1 验收

| 编号 | 标准 |
|---|---|
| V3-AC1 | 存在可运行的 A/B 评测基准与任务集，结果可重复；外部 UUID-chain 证据已完成 |
| V3-AC2 | ADR `0024` 形成 B 择优结论，含迁移策略 |
| V3-AC3 | B 迁移完成：生产默认 MCP 表面单一；shell/sidecar 已从 main 彻底移除（不再启动、不再打包、不保留实现），仅历史分支 `feature/mcp-ab-benchmark` 存有评测证据 |
| V3-AC4 | 若 B 胜出：CLI 是本地 MCP HTTP 客户端，四个 CLI 命令到四个 MCP tool request 的参数映射、共享 JSON 与错误码有契约测试；未预期宿主/数据库/helper 异常统一映射为 `INTERNAL`，不泄露内部详情；不存在第二套 CLI 领域实现 |
| V3-AC5 | 安全锚点测试仍通过；`put` 若启用则仅限规定 URI、完整内容校验与原子提交，不接受 partial/truncated 内容 |
| V3-AC6 | `general` 可在满足字段要求时通过显式 `@misc` fallback cite，否则返回 `NOT_CITABLE`；只读资源 put 返回 `PERMISSION_DENIED`，但 document/page/evidence cite 可解析到所属 Item |
| V3-AC7 | 超过 `limit_bytes` 的 fetch 返回安全边界内的 partial 内容、`complete=false`/`truncated=true`、continuation 或 next range，以及 `RESPONSE_TRUNCATED`；不得静默呈现为完整内容 |
| V3-AC8 | Document、Page、Evidence 的资源响应暴露所属 Item 关系；`citable` 与 cite 实际接受的 URI 类型一致；document/page cite 验证关系后成功解析 |
| V3-AC9 | 多 URI fetch 与多 REF cite 采用逐项结果语义；单项失败不丢弃同一请求中的成功结果，并对实际使用的 CSL style 返回 effective style |
| V3-AC10 | 裸 `find` 仅返回 `/items`、`/texts`、`/csl-styles` 三个 VFS 根 directory；旧 `AGENTS.md`、`library.yml`、evidence 根和 shell 入口均不可发现或访问 |
| V3-AC11 | 经 UI、CLI 或 MCP 成功写入的宿主写服务均发出资源变更通知；连接到该宿主的桌面书库列表、打开的题录编辑器和 CSL 样式视图无需重启即可显示最新数据 |
| V3-AC12 | 默认 `find` 的 TOON/JSON 条目严格只有 `uri`、`title`、`type`；所有工具的 TOON/JSON 响应均严格使用 `meta`、`continuation`、可选 `message`、`entries` 外壳，干净成功不返回 `message`。`meta` 三项计数反映各页读取时的当前 Library 状态、`shown_total` 与 entries 行数一致、continuation 可继续读取。实时 cursor 的跨页 entries 或计数可能漂移，必须在 `message.warnings` 有 `RESULT_SET_MAY_HAVE_CHANGED`，不承诺 `filtered_total` 跨页稳定 |
| V3-AC13 | `--long` / `detail=long` 才返回状态、能力与必要关系元数据；默认和详细的 CLI/MCP/JSON 输出、schema、help 与示例均不存在 `citation_target`、`preview` 或裸 `status`。Long 投影按 Item、Text、Style 资源种类精确省略不适用字段，绝不重复 URI 已表达的 DocumentInstance、页码或 `rev`/`box`；Style 不包含 `citable` |
| V3-AC14 | `find` 对声明支持的 scope/query/`--literal`/filter 执行搜索或过滤，严格遵循 scope × flag 合法矩阵；不支持的组合返回 `INVALID_ARGUMENT`，不以成功空数组代替 |
| V3-AC15 | `patchouli-cli` 先连接同 Library 的本地 MCP HTTP 宿主；桌面未运行时自动启动后台 headless 宿主后执行四个资源命令。UI、CLI 与 agent MCP 都经同一宿主服务；CLI 不直连 SQLite。每个 Library 同时只有一个宿主，桌面启动时接管并终止该 Library 的 headless 宿主；headless 的 `0.0.0.0` 监听同样要求 token |
| V3-AC16 | MCP `format=json` 与 CLI `--json` 为批量机器处理返回等价 JSON；无需解析 TOON，且三种编码均使用相同的 `meta`、`continuation`、可选 `message`、`entries` schema。格式切换不改变默认/详细投影、字段、分页、warning 或 error 语义 |
| V3-AC17 | `patchouli://texts/{document-instance-id}/page-{page-index}.md` 和 `pages:` range 均以一基、稳定的物理 PDF 页码寻址；带 `?rev=&box=` 的 fetch/cite 必须校验 `tree_revision_id`/`box_id` 与所声明 document/page 的归属，不归属时返回 `NOT_FOUND` |
| V3-AC18 | cursor 不持有服务端快照、结果集句柄、TTL 或 agent 命名空间，并绑定原 scope/query/filter/order；消费或发出 continuation 的响应在 `message.warnings` 包含 `RESULT_SET_MAY_HAVE_CHANGED` |
| V3-AC19 | `find QUERY --in patchouli://texts/` 的每个全文命中在默认 `uri` 中内嵌 canonical `?rev=&box=` page URI，不依赖 `--long` 或独立 evidence 字段；该 URI 可直接 fetch evidence，并可作为 `cite.refs` 输入 |
| V3-AC20 | CLI/MCP 的 TOON 输出仅使用 `Corvus.Toon.SystemTextJson` 产生，符合 TOON v3.0；契约 fixture 验证 UTF-8/LF、literal TAB、`KeyFolding=Off`、tabular `[N]` 计数、`text/toon`、TOON v3 词法引用/escape、number/boolean/null 的严格类型与 JSON↔TOON round-trip。不得存在自定义 TOON encoder/parser 或字符串后处理 |
| V3-AC21 | `--regex` 不出现在 CLI help、MCP schema 或协议示例；传入时返回 `INVALID_ARGUMENT`，服务端不执行正则搜索，agent 可在 find/fetch 的返回内容上自行匹配 |
| V3-AC22 | MCP `patchouli.put` schema 仅接受内联 `uri` 与 `content`，不含 `from`、`stdin`、`path`、streaming、multipart 或 file reference；CLI `--from`/`--stdin` 读取后与 MCP content 进入同一写服务，拥有相同的大小检查、校验、原子提交和响应。MCP 超过 `max_mcp_request_bytes`（默认 1 MiB、硬上限 4 MiB）的请求在工具调用前返回 HTTP 413，且不写入 |
| V3-AC23 | find 的边界输入按契约归一化并带稳定 warning：whitespace QUERY 等同 browse；root `--limit`/`--cursor` 可分页；file URI 是单资源 scope；cursor 冲突时恢复其绑定上下文；where 在第一个 `=` 分割且重复 key 最后一项覆盖。无效 cursor 或矩阵外组合仍返回 `INVALID_ARGUMENT` |
| V3-AC24 | `meta.library_revision` 是持久化、严格单调的 `lib:<十进制正整数>` Library revision；每次成功的协议可见 Library 写入及桌面/headless 交接均不重置它。已 fetch 内容仅为客户端快照；cursor 或 MCP 会话观察到 Library 变化时继续执行并在 `message.warnings` 给出相应 warning，不提供服务端推送式内容撤回 |
| V3-AC25 | `patchouli://` 只解析到宿主固定的当前 `library_id`；含有不匹配 Library 绑定的 cursor、versioned evidence URI 或显式上下文必须丢弃已准备 entries 并以 `NOT_FOUND` 失败，不得返回跨库内容。当前 UI 不能切库不构成省略该校验的理由 |
| V3-AC26 | 宿主对 `find`/`fetch`/`cite` 默认执行 60 秒、`put` 默认执行 120 秒的 deadline；超时为 `DEADLINE_EXCEEDED`，取消为 `CANCELLED`。取消或断连能停止校验/查询，且 `put` 要么在提交前不写、要么完整原子完成，绝无部分写入 |
| V3-AC27 | 四个工具均有封闭、逐字段类型化的统一响应 schema fixture；默认与 long find、complete/truncated/failed fetch、put 成功、cite 部分成功/全部失败均验证 `meta`、`continuation`、可选 `message`、`entries` 的 required/null/省略规则、无额外字段、同序逐项结果、UTF-8 byte 计数及 message/error 对应关系；help 和 MCP 初次握手 fixture 还必须验证“无 `message` 即干净成功”的 Unix 语义。迁移 fixture 还必须验证输出中不存在顶层 `revision` 或 `resource_revision`，且 CLI help/MCP schema 不含 `fetch --revision`；Library revision 只在 `meta.library_revision` |
| V3-AC28 | Long `find` fixture 分别验证 Item、Text 与 Style 的精确 variant：仅 `item_status`、`document_status`、`source_status` 是公共 status，且分别等于 Item、DocumentInstance、FileAsset 的原始持久化值；OCR 索引为共享 English FSM 能力，UI 使用同一 FSM 的中文标签与说明。`primary_document_ocr_index_status` 与 `ocr_index_status` 由数据库侧本页批量投影过滤，且不存在逐项 metadata/status 查询 |

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
- 同时探索接入：**onnxOCR**、**ultimateOCR**、**ndlocr-lite**；**ndlkotenocr-lite** 作为首个本地 OCR 实现落地，详见 V3-T9
- MinerU 仍为已交付的生产参考路径；新 provider 的打包、模型分发、许可、preset UX、失败分类与密钥边界在后续修订中规定
- 继续遵守：Mock/历史占位不进生产默认；secret 仅 local-only credentials；不规则表等 canonical 规则不因新 provider 回退

## 6. V3-T4：Linux 桌面适配与发行打包

**状态**：新增；未开始。

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

## 7. V3-T5：书库 UI 与题录管理

**状态**：已实现；AC1~AC19 验收通过。

本任务聚焦书库页面和题录生命周期管理，包括题录列表、来源信息、详情展示、标签组织、回收站、FileAsset 清理和题录合并。所有操作复用统一宿主写服务、Library revision 与变更通知，不改变 Document Box Tree、SearchUnit 或 versioned evidence URI 的身份语义。

### 7.1 书库页与题录详情

- **来源列**：当前“来源”列只使用期刊名/出处文献（`publicationTitle`）。改进后按题录类型选择更合适的来源字段：
  - 专著（`book` 等）使用出版社（`publisher`）
  - 期刊文章等继续使用期刊名/出处文献
  - 其他类型使用与该类型对应的来源字段（如会议名、学位授予机构等）
  - 缺失字段回退到现有行为，不得显示空占位误导用户
- **详情面板**：当前右侧详情面板以固定键值网格展示少量字段。改进后使用 TableView 展示更多信息，支持折叠分组、长文本换行与可扩展的字段集，避免一次塞满有限网格列。
- 列宽、列顺序、列显隐等已有持久化行为必须继续生效；新增来源字段策略不得破坏现有列持久化测试。
- 书库列表项右键菜单移除“打开同步中心”入口。

### 7.2 题录生命周期管理

- **回收站**：用户删除 Item 时先执行软删除，Item 进入回收站；活动书库、默认搜索和标签筛选不显示回收站 Item。支持恢复，恢复后重新进入原有列表和筛选结果。
- 软删除不立即销毁 DocumentInstance、Page、DocumentTree、SearchUnit 或已发出的 versioned evidence URI。永久删除另行确认，并在执行前检查证据、快照、分支、OCR working revision 等依赖。
- **永久删除语义**：永久删除是真正的物理删除：删除 Item 及其库内相关数据（DocumentInstance、Page、DocumentTreeRevision、SearchUnit 等载荷），但不动用户原始 PDF 等源文件。证据表随 `evidence_ref_records`/`evidence_successors` 一并删除；purge 后外部已复制的 versioned evidence URI 按 `NOT_FOUND` 解析，不再有 `purged` 态。
- **永久删除与快照**：purge 无法追溯已发布的快照分片；之后导入仍包含该 Item 的旧快照时，该 Item 作为全新内容重新加入（新 `item_id` remap），不与既有 purge 记录关联，也不复活原身份。
- **状态词汇**：软删除与合并重定向是 `items` 行上的墓碑（tombstone）；永久删除是 purged（无 `items` 行，仅 `item_purge_records`）。tombstone 与 purged 在快照语义中必须可区分：前者可参与 Item 级分支冲突（CF-08），后者不走 CF-08，只 remap（见 7.8）。
- 软删除是无损的状态变化，不产生题录版本记录；删除状态只能经回收站还原或永久删除这两个显式入口改变，不得出现由版本控制改写删除状态的第二入口（与 V3-T10 的文档级版本控制设想协调）。
- 删除 Item 不删除用户原始文件。Patchouli 只负责管理自己的题录、FileAsset 记录和可重建派生资源。注意题录（ItemMetadata）本身不是可重建派生资源：它是持久化、可编辑的书目投影，删除后无法从 PDF 或标识符机械重建（ADR `0026`）。

### 7.3 回收站视图与管理入口

- 侧边栏提供回收站入口，与“我的书库”构成同一 section；section 内容使用 DataGrid 展示，不使用设置页式卡片。
- 回收站内不提供“删除”选项，替换为“还原”；“编辑题录”、双击进入 OCR 等会改变题录或文档内容的操作在回收站内一律隐藏。
- 回收站视图与一般书库范围的标签相互隔离：回收站 section 下暂时直接隐藏标签列表。
- 永久删除是阻塞（Blocking）操作：用户必须在阻塞弹窗中处理完毕后才能回到主 UI。确认弹窗将依赖检查结果（证据数量、快照引用、working revision 等）作为可展开的详细信息展示，默认折叠。
- 右键与菜单栏中的删除、还原等操作与其他列表操作一致，同时支持单选与多选。

### 7.4 FileAsset 自动清理

- FileAsset 只有在不再被任何 DocumentInstance、回收站 Item、working revision、已发出的 versioned evidence URI 或快照引用时，才进入清理候选。
- 自动清理只移除无引用的 FileAsset 数据库记录、失效路径记录和可重建派生缓存，不删除用户管理的原始文件。
- 清理采用后台 GC，提供清理前预览、延迟执行、失败重试和操作日志；导入失败、取消或并发操作产生的临时资源不得被误判为孤儿。

### 7.5 标签与书库侧边栏

- 题录详情中展示标签，并支持添加、删除和编辑；导入元数据中的 `keyword` 继续按既有规则进入 Item 标签。
- 书库左侧边栏增加标签区域，分为置顶标签和普通标签；支持置顶、取消置顶和稳定排序。
- 标签置顶状态与顺序作为**库级**书库 UI 偏好独立保存（随库同步），不写入 Item 的标签内容，也不改变题录合并、同步或 MCP 资源语义。
- **标签规范化**：标签文本一律 UTF-8、大小写敏感、写入时强制 trim；重命名到已存在的标签名等同标签合并，同样先经阻塞弹窗确认。
- 点击标签直接进入元数据筛选模式；多标签默认按“同时满足”过滤。默认只统计活动 Item，回收站 Item 不参与标签数量和筛选结果。
- 标签变更后，侧边栏、数量、书库列表和详情视图通过既有变更通知即时刷新。
- “无标签”是标签区域中的一个固定条目，点击筛选所有没有任何标签的活动 Item。
- 支持将单个或多个 Item 拖拽到侧边栏标签上以增加标签（已存在不重复添加）；拖拽到“无标签”上表示清空这些 Item 的全部标签，执行前显示阻塞弹窗二次确认；本来就没有标签的 Item 自然不受影响，不作特殊处理。清空标签在现有框架下不可撤销，属于重操作，暂以阻塞弹窗确认。
- 标签编辑（添加、移除、重命名、合并）不触碰回收站中的 Item；回收站 Item 还原时按当前标签状态呈现，即使其携带的标签已被全局移除也照常还原。
- 标签提供右键菜单：置顶（可同时置顶多个，用置顶标签快速筛选，形成类似 collection 的效果）、移除（同时从所有 Item 上去掉该标签，需阻塞弹窗二次确认）、重命名（同时改写所有 Item 中的该标签）。
- 标签之间可以拖拽合并：放下前显示阻塞弹窗确认，实际行为视同将被合并标签重命名为目标标签。
- 后续需将 dialog 与各类 view 的标准名称整理进 `.agents/CONTEXT.md` 领域文档。
- **集合（collection）暂不做**：v3 专注标签，避免为集合再设计 MCP/CLI 视图。如果置顶标签与标签筛选在后续验证中能完全替代集合功能，则在后续版本中清理 `collections_json` 等集合痕迹；否则再正式引入集合。

### 7.6 右键合并 Item

- 用户可选中题录列表中的两个Item，然后右键点击合并题录；点击后先显示合并预览和确认对话框，用户需在对话框内选择合并目标，不能直接执行。
- 合并后目标 Item 保留；来源 Item **不** purge、**不**物理删除行，而是成为 **合并重定向墓碑**（Merge Redirect）：`items` 行保留身份，记录指向目标的 `merged_into_item_id`，并从活动书库、默认搜索、标签筛选与查重中排除。来源 Item 的 DocumentInstance 转移到目标 Item。
- DocumentInstance、FileAsset、Page、DocumentTree、SearchUnit 和 versioned evidence URI 的身份保持不变；合并不重复 OCR、不重建全文内容，也不修改页面 Box Tree。
- 目标 Item 的非空字段默认优先，来源 Item 只补充目标缺失字段；题名、作者、年份、标识符和 citation key 等冲突字段必须明确展示并由用户确认。
- 两个 Item 的标签取并集；合并操作必须原子提交，存在未保存编辑、活动 OCR 任务或并发写入时阻止合并。
- 题录右键合并是一种有损冲突语义：以牺牲信息较不详细的来源一方为代价完成合并；合并不等于永久删除。文档级版本历史由 V3-T10 在 DocumentInstance 内维护；合并不保留跨 Item 的历史拼接。

### 7.7 重复题录检测

- 重复题录检测视为手动触发的冲突处理：检测结果逐对进入冲突处理弹窗，用户可以选择不处理其中部分冲突，跳过不产生任何写入。
- 触发入口：菜单栏“题录”菜单与书库列表右键菜单。
- 判定规则（满足任一即列为候选重复）：标识符（DOI、ISBN 等）精确匹配；题名/作者/年份相似度，复用既有 Biblatex 导入的字段冲突/相似度检测实现；对应 Document 重复，即 FileAsset 内容 hash 相同——FileAsset 的 hash 鉴定即为此类判重服务。
- 已合并墓碑与回收站 Item 不参与查重。
- 用户确认处理某对重复后，进入 7.6 的合并预览与确认流程，合并规则不变；合并预览中默认目标 Item 为题录列表排序中较早的一边，信息量相当时不引入额外启发式，由用户在预览中自行选择与调整。

### 7.8 与其他任务协调

- **V3-T2 PDF 工作台**：合并只改变 DocumentInstance 所属 Item，不修改页面、Box Tree 或 versioned evidence URI；已打开的来源文档应切换到目标 Item 或显示明确的“已合并”状态。
- **V3-T8 搜索功能强化**：左侧标签是元数据搜索模式的快捷入口；全文搜索继续使用现有 SearchUnit 和 FTS，不因题录合并重复索引正文。
- **V3-T7 性能与响应性治理**：删除、恢复、标签修改和合并都通过统一写服务完成，并在一次成功提交后发布一次 Library revision 和变更通知。
- **V3-T10 文档级版本控制**：合并是有损冲突处理，来源 Item 历史随合并丢弃；软删除是无损状态变化，不产生题录版本。题录级版本控制的范围扩大设想仍处于早期，本任务不为其预留实现，也不提前承诺版本粒度。
- **MCP/CLI**：本任务首版只扩展桌面 UI，不新增删除、恢复或合并命令；默认资源发现和搜索排除回收站及合并墓碑，versioned evidence URI 解析规则不变。对已合并墓碑或回收站 Item 执行 `patchouli.put`/`patchouli.fetch` 必须失败：使用新的错误码（如 `ITEM_MERGED`、`ITEM_IN_TRASH`），Item 当前状态与重定向目标等附加信息按既有惯例放入 message 文本。
- **同步与快照**：回收站状态、标签和合并重定向属于 Library 持久化内容；跨分支仍采用显式冲突处理，不自动执行对象级合并。
  - **CF-08（Item 级分支冲突）**：仅当两边仍存在同一 `item_id` 的 `items` 行，且在 active / trash（软删除墓碑）/ merged（合并重定向）之间不一致时生成。冲突对话框在字段/链接选择之外提供 Item 级分支选择（保留本地或采用传入）。软删除与合并重定向均按墓碑参与 CF-08。
  - **purged 不走 CF-08**：本地已有该 `item_id` 的 purge 记录时，传入快照中仍携带的同一身份内容固定 remap 为新 `item_id` 后作为全新 Item 加入，不与 purge 记录关联，不复活原身份，也不弹出 Item 级 keep/use 选择。purged 与 tombstone 由此在快照语义中可区分。

### 7.9 V3-T5 验收

| 编号 | 标准 |
|---|---|
| V3-T5-AC1 | 书库页“来源”列按题录类型展示出版社/期刊名/对应来源字段；缺失字段有明确回退，不破坏现有列持久化测试 |
| V3-T5-AC2 | 书库页详情面板使用 TableView 展示更多字段，支持折叠分组与长文本换行；展示投影不修改领域数据 |
| V3-T5-AC3 | 书库列表、题录详情与既有编辑入口之间的导航和状态保持一致；题录写入后，书库列表与打开的详情视图可通过既有变更通知刷新 |
| V3-T5-AC4 | 删除 Item 进入回收站，默认书库、搜索和标签筛选隐藏回收站内容；恢复后重新可见；软删除不破坏既有 DocumentInstance、SearchUnit 或已发出的 versioned evidence URI |
| V3-T5-AC5 | FileAsset 只有在不存在任何文档、回收站 Item、working revision、已发出的 versioned evidence URI 或快照引用时才可进入 GC；GC 不删除用户原始文件，并具备预览、延迟和日志语义 |
| V3-T5-AC6 | 题录详情显示并可编辑标签；左侧边栏展示标签、置顶标签和稳定顺序，置顶状态与顺序为库级偏好；点击标签可执行元数据过滤，多标签按约定组合 |
| V3-T5-AC7 | 标签编辑、删除、恢复和合并后，标签列表、数量、书库结果和详情视图通过变更通知保持一致 |
| V3-T5-AC8 | 右键合并题录先展示预览和冲突字段；确认后目标 Item 保留、来源 Item 成为合并重定向墓碑（保留 `items` 行与 `merged_into_item_id`，不 purge），操作原子完成且可阻止未保存编辑或活动 OCR 冲突 |
| V3-T5-AC9 | 合并保留 DocumentInstance、FileAsset、Page、DocumentTree、SearchUnit 和 versioned evidence URI 身份；不重复 OCR 或重建全文；目标非空字段优先、缺失字段补全、标签取并集 |
| V3-T5-AC10 | 题录删除、恢复、标签修改和合并均发布一次 Library revision 与变更通知；V3-T2、V3-T7、V3-T8 和 MCP/CLI 边界符合本节协调规则 |
| V3-T5-AC11 | 永久删除经阻塞弹窗确认，依赖检查结果（证据数量、快照引用、working revision）以默认折叠的可展开详情展示；确认后删载荷、留 purge 记录行，外部已复制的 versioned evidence URI 按 NOT_FOUND 解析 |
| V3-T5-AC12 | 侧边栏回收站与“我的书库”构成 section 并以 DataGrid 展示；回收站内删除入口替换为还原，编辑题录、进入 OCR 等改动操作隐藏，标签列表隐藏；删除/还原支持单选与多选 |
| V3-T5-AC13 | 书库列表项右键菜单不再包含“打开同步中心” |
| V3-T5-AC14 | “无标签”作为标签区域固定条目参与筛选；拖拽单个/多个 Item 到标签可加标签且不重复添加；拖到“无标签”清空标签前经阻塞弹窗二次确认，本来无标签的 Item 不作特殊处理 |
| V3-T5-AC15 | 标签右键支持置顶（可同时多个）、移除、重命名；移除与重命名波及所有 Item 但不触碰回收站 Item，移除需阻塞确认；标签间拖拽合并及重命名到已存在名字均视同合并/重命名并先经阻塞确认；标签文本 UTF-8、大小写敏感、写入强制 trim |
| V3-T5-AC16 | 重复题录检测可从菜单栏“题录”与右键菜单手动触发；结果逐对进入冲突处理弹窗，允许跳过部分冲突；标识符精确匹配、题名/作者/年份相似度（复用 Biblatex 检测实现）与 FileAsset hash 重复三类规则均有命中用例；已合并墓碑与回收站 Item 不参与；合并预览默认目标为排序中较早的一边 |
| V3-T5-AC17 | 对已合并墓碑或回收站 Item 的 `patchouli.put`/`patchouli.fetch` 以新错误码失败，Item 状态与重定向目标等附加信息放入 message 文本 |
| V3-T5-AC18 | 跨分支 active/trash/merged 不一致时生成 CF-08，冲突对话框提供 Item 级分支选择（保留本地或采用传入）；软删除与合并重定向按墓碑参与 CF-08 |
| V3-T5-AC19 | 本地已 purge 的 `item_id` 再从旧快照导入时不走 CF-08：内容 remap 为新身份后加入，不与既有 purge 记录关联，不复活原身份 |

## 8. V3-T8：搜索功能强化

**状态**：从原 V3-T5 拆分；方案评估中。

本任务只承载搜索入口和搜索交互的强化，元数据筛选与全文搜索是两个可明确切换的桌面模式。它不改变 SearchUnit、FTS 索引、证据或 MCP 表面，也不把向量化、混合搜索或语义搜索纳入 v3 范围。

### 8.1 搜索模式与交互

- 在顶部搜索框旁加入快捷下拉菜单，可切换两种模式：
  - **元数据筛选**：在书库题录元数据范围内筛选。
  - **全文搜索**：现有的全文检索行为。
- 无论处于哪种模式，搜索框都必须继续解析 `patchouli://` URI 并导航到对应资源；URI 解析路径不因模式切换而失效。
- 搜索由回车键触发，不再依赖或要求用户点击“搜索”按钮；按钮仍可保留作为备选触发方式，但回车是主交互路径。
- 模式切换是纯 UI 状态；两种模式复用既有搜索服务与搜索配置文件，不改变 SearchUnit、FTS 索引、证据或 MCP 表面。
- 切换模式时必须保持当前输入文本，不得清空用户已输入内容；空查询行为按模式各自定义。
- 下拉菜单必须有明确的当前模式标识和可访问性提示，不得依赖仅靠图标无法区分的控件。

### 8.2 V3-T8 验收

| 编号 | 标准 |
|---|---|
| V3-T8-AC1 | 搜索框下拉菜单可在元数据筛选与全文搜索间切换；当前模式有明确文字和可访问性提示 |
| V3-T8-AC2 | 两种模式均能解析 `patchouli://` URI 并导航；URI 解析路径不因模式切换而失效 |
| V3-T8-AC3 | 回车键触发搜索；切换模式不清空输入文本，空查询按模式返回明确结果 |
| V3-T8-AC4 | 模式切换不改变 SearchUnit、FTS 索引、证据或 MCP 表面；复用既有搜索服务与搜索配置文件 |

## 9. V3-T6：版本控制、证据引用及其 UI 表示

**状态**：已决策；实现细节见 V3-T10，ADR `0027`/`0028`。

V3-T6 的决策结论：

- 页级 `DocumentTreeRevision` 版本控制采用 working/commit 两状态模型，由 ADR `0027` 定义；题录内容和 CSL 样式的版本控制仍按早期设想延后，不在 v3 实现。
- 证据引用取消独立的 `evref:v2` token 机制，改为 versioned URI `?rev={tree-revision-id}&box={box_id}`，由 ADR `0028` 定义。带 `rev` 读固定版本，不带 `rev` 读 HEAD；没有 pinned/current/compare 模式，也没有漂移 surface。
- 版本 UI 范围限定为历史 + 恢复，不做 diff/compare：页面级版本历史入口在 PDF 工作台；文档级 commit 历史入口在题录编辑器的文件管理部分。恢复以 revert-as-new-commit 实现，current 指针不倒拨。

已记录的边界（与 V3-T5 协调）：右键合并题录是有损冲突语义，来源 Item 成为合并重定向墓碑（非 purge），其历史版本随合并一并丢弃，不做跨 Item 的历史拼接；软删除是无损状态变化，不产生版本，删除状态只能经回收站还原或永久删除改变，不得由版本控制改写。题录级批量操作（如标签重命名波及所有 Item）会放大版本数量，题录级版本控制的粒度、写入放大与保留策略延后；在这些问题有结论前，扩大版本控制系统范围仍属早期设想，不做提前优化，也不预留实现。

## 10. V3-T9：ndlkotenocr-lite 本地 OCR 与本地文件管理

**状态**：新增；准备实现。

### 10.1 目标与范围

- 集成 NDL 古典籍 OCR lite（`ndl-lab/ndlkotenocr-lite`）作为首个真实本地 OCR 引擎。
- 采用 C# 原生 ONNX Runtime 移植，不依赖 Python 运行时。
- 模型与配置文件按需从上游 GitHub 下载，存储在应用数据目录。
- 在桌面端设置页新增“本地文件管理”区块，让用户手动清理模型文件和 OCR 临时文件（含 MinerU 与 NDL koten 工作文件）。
- 输出必须归一化为 `OcrDocumentTreeCandidate` 并走既有统一 importer，禁止 provider 直写 `document_boxes`。

### 10.2 设计约束

- 上游模型/配置文件共 4 个，均使用 **CC-BY-4.0** 许可证；UI 必须展示署名信息，下载/使用前提示许可。
- 模型下载采用固定清单（URL + 期望字节数），下载到临时文件后原子改名，提供进度与取消；不做断点续传。
- 本地引擎实现 `IRealOcrAdapter`（`local_library` kind），`SupportsVerticalText = true`，支持 `page_image` 与 `region_image`。
- 存储目录约定（基于 `PlatformAppPaths`）：
  - 模型：`{DataDirectory}/models/ndl-koten/`
  - MinerU 工作文件：`{CacheDirectory}/ocr-work/mineru/`
  - NDL koten 工作文件：`{CacheDirectory}/ocr-work/ndl-koten/`
- `OcrRunEngine` 的 MinerU 工作根从 OS temp 改为上述约定路径；旧 `%TEMP%/patchouli/mineru` 残留由 OS 自行清理。
- 设置页“本地文件管理”区块 `SupportsEditing = false`，纯操作型；切换分类时 `LoadAsync` 扫描目录大小。
- 全局设置“OCR 引擎”页允许用户为 **文档级、页面级、区域级** OCR 分别选择当前可用的引擎；选项来自运行时已注册的 `IRealOcrAdapter` 能力清单，保存后写入用户设置并即时生效。

### 10.3 管线移植要点

- **RTMDet-s 1280×1280**：letterbox 缩放、归一化、输出张量解码、置信度阈值、NMS。
- **阅读顺序**：竖排从右至左排序（移植上游 `src/reading_order`）。
- **PARSeq-ndl-32×384-tiny**：行区域裁剪、缩放至 32×384、字符集解码（字符集来自 `NDLmoji.yaml`）。
- 图像处理使用仓库已有 `SkiaSharp`。

### 10.4 设置页“本地文件管理”

- 行项：
  1. **NDL koten 模型文件**：安装状态、占用大小、下载/重新下载（带 CC-BY-4.0 说明）、删除、打开目录。
  2. **MinerU OCR 临时文件**：大小、清空、打开目录。
  3. **NDL koten 工作临时文件**：大小、清空。
- 删除/清空均走 `ConfirmDialog`（danger 模式）；扫描目录大小在 `LoadAsync` 完成。

### 10.5 V3-T9 验收

| 编号 | 标准 |
|---|---|
| V3-T9-AC1 | `ndl-koten` 适配器以 `local_library` 注册，`CheckEnvironmentAsync` 在模型缺失时返回 `missing_model_path` 并指引用户到设置页下载 |
| V3-T9-AC2 | 设置页可一键下载 4 个模型/配置文件到 `{DataDirectory}/models/ndl-koten/`；下载失败不残留半成品，进度可取消 |
| V3-T9-AC3 | 模型下载按钮旁清晰展示 CC-BY-4.0 署名信息，满足上游许可证要求 |
| V3-T9-AC4 | `NdlKotenOcrAdapter` 的 `RunPageAsync` 对页面/区域图像执行检测→阅读顺序→识别，输出归一化为 `OcrDocumentTreeCandidate`，经既有 importer 进入 Document Box Tree |
| V3-T9-AC5 | 竖排文本（从右至左列）的阅读顺序与上游 Python 输出一致；提供至少一个 fixture 或合成用例验证 |
| V3-T9-AC6 | `OcrRunEngine` 的 MinerU 与 ndl-koten 分支分别使用 `{CacheDirectory}/ocr-work/mineru/` 与 `{CacheDirectory}/ocr-work/ndl-koten/` 作为工作根 |
| V3-T9-AC7 | 设置页“本地文件管理”显示模型文件、MinerU 临时文件、NDL 临时文件的大小与条目数，并提供打开目录与清理按钮 |
| V3-T9-AC8 | 清理操作前显示危险确认弹窗；清理后即时刷新显示；仅删除应用自身管理的目录内容，不误删用户其他文件 |
| V3-T9-AC9 | 模型被删除后，`ndl-koten` preset 运行自动进入 `missing_model_path` 状态，用户可重新下载 |
| V3-T9-AC10 | 新增 `Microsoft.ML.OnnxRuntime` 包版本由 `Directory.Packages.props` 集中管理；改动文件经过 `scripts/cleanup-code.ps1`，提交前 `scripts/inspect-code.ps1` 零错误零阻塞 |
| V3-T9-AC11 | 代码改动包括对应单元测试（NMS/阅读顺序/字符集解析/下载服务/目录清理），且 `dotnet test` 通过；端到端测试在模型不存在时自动跳过 |
| V3-T9-AC12 | 全局设置“OCR 引擎”页提供文档级、页面级、区域级三个下拉选项；选项为当前已注册适配器；保存后写入用户设置并即时生效；未选择或选择无效时回退到默认引擎 |

## 11. V3-T10：统一版本化资源模型

### 11.1 目标与范围

- 统一 OCR 与手动编辑的产出物为单一 **working revision**，通过 in-place commit 提升为 committed current，替代原有的 `staging`/`draft`/`discarded`/`adoption` 多层状态。
- 以 **versioned URI** `patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree-revision-id}&box={box-id}` 取代 `evref:v2` token 体系，作为证据的长期身份与消费形式。
- 引入 **document-wide `DocumentCommit`**，将一次提交中涉及的各页 `DocumentTreeRevision` 分组；HEAD 为最新 commit，历史 append-only，revert 产生新 commit。
- 版本 UI 范围限定为 **历史 + 恢复**，不做 diff/compare：PDF 工作台提供页面级版本历史；题录编辑器的文件管理部分提供文档级 commit 历史。

### 11.2 设计约束

- `DocumentTreeRevisionStatus` 只存在 `working` 与 `committed`；legacy 状态行物理保留但不再读取。
- working revision 的 ID 在 commit 前后保持不变（in-place commit），从而 versioned URI 在 commit 后仍然可解析。
- search、MCP、evidence 只读 `status='committed' AND is_current=1`；working revision 永远不进入这些只读面。
- revert 是新 commit：`R{n+1}.content = R{old}.content`、`parent = 旧 HEAD`、`source='revert'`、`reverted_from = R{old}`；current 指针不倒拨。
- `LibraryRevision` 仍只是全库单调 change counter；DocumentInstance 的版本历史由 `DocumentCommit` 维护。
- `evref:v2` 完全移除：codec、`evidence_ref_records`、`evidence_successors`、successor 链、resolution mode 全部删除，无旧格式解码。
- 带 `rev` 的 URI 永远读到该不可变 revision 原文；不带 `rev` 读 HEAD。没有 pinned/current/compare 模式，没有漂移检测或提示。
- purge 后证据 URI 解析为 `NOT_FOUND`，不再有 `purged` 态。
- 快照分片必须排除 working 行与 legacy-status 行；未提交内容不参与同步。

### 11.3 V3-T10 验收

| 编号 | 标准 |
|---|---|
| V3-T10-AC1 | working revision 永远不进入 search、MCP、evidence 读取面；这些只读面只返回 `committed` 且 `is_current=1` 的 revision |
| V3-T10-AC2 | commit 原地提升 working revision，`tree_revision_id` 与 Box ID 保持不变；commit 前后同一页 Box 行数不变，不产生 staging→committed 的 Box 复制 |
| V3-T10-AC3 | versioned URI 带 `rev` 时始终返回该 immutable revision 的原始文本；后续对同一页的编辑、删除或恢复不影响旧 URI 的解析结果 |
| V3-T10-AC4 | 不带 `rev` 的 URI 返回当前 HEAD；HEAD 变更后旧 URI（带 `rev`）仍可复现原文 |
| V3-T10-AC5 | purge 后外部已复制的 versioned evidence URI 解析为 `NOT_FOUND`，系统中不存在 `purged` 态 |
| V3-T10-AC6 | revert 产生新 commit，parent 指向原 HEAD，`source='revert'`，`reverted_from` 指向目标 revision；current 指针不倒拨 |
| V3-T10-AC7 | `DocumentCommit` 正确分组一次提交涉及的所有页面 revision；HEAD 为最新 commit；`LibraryRevision` 仍只是全库 change counter |
| V3-T10-AC8 | 快照分片排除 working 行与 legacy `staging`/`draft`/`discarded` 行；导入端接受“无 current 的页”为未提交状态 |
| V3-T10-AC9 | PDF 工作台提供页面级版本历史面板：可列出该页 revision/commit 历史、查看旧版文本、执行 revert |
| V3-T10-AC10 | 题录编辑器的文件管理部分提供文档级 commit 历史：可列出 DocumentInstance 的 commit 历史、查看单次 commit 涉及的页面、执行 revert |
| V3-T10-AC11 | MCP/CLI 的 evidence 读取按 URI 的 `rev`/`box` 直接解析，不再依赖 `evref` token 或 successor 链；help 与 schema 中不存在 `evref`、`pinned`、`current`、`compare`、`drift` 等旧概念 |
| V3-T10-AC12 | 删除或修改与旧 staging/evref/pinned 语义相关的代码、测试、文档与 help；`scripts/inspect-code.ps1` 对改动文件零错误零阻塞规则 |

## 12. 明确不做（v3 默认）

- 不把向量化、混合搜索、语义搜索作为 v3 完成标准
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

## 14. 长期约束索引

| 约束 | 权威位置 |
|---|---|
| 领域词汇与产品边界 | `.agents/CONTEXT.md` |
| 运行库与快照分离、分片、library_id、三层模型、OCR/证据/MCP 等 | `.agents/adr/`（`0001`–`0011`、`0014`、`0015`、`0022`、`0023`、`0027`、`0028` 等） |
| 已移除的 Bashkit MCP 实现 | ADR `0022`（已由 ADR `0024` 取代；实现已从 main 删除，仅存于 `feature/mcp-ab-benchmark` 分支） |
| 有限可写 MCP（item `.bib` / style `.csl` put） | ADR `0023`（修订 `0010` 的“绝对只读”后果） |
| 统一 working/commit 版本模型 | ADR `0027` |
| versioned URI 证据 | ADR `0028` |
