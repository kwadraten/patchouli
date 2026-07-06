# Patchouli 实现缺口与执行计划

> 基于 `.agent/PRD.md` v1.1、`.agent/CONTEXT.md`、既有 ADR 以及当前代码实现复核。本文不再只是罗列缺口，而是按依赖关系给出可执行路线图：先稳定数据模型和服务契约，再接 UI；先补 v1 必需能力，再处理维护性与 v2 候选能力。

---

## 1. 执行原则

### 1.1 先复核，再实现

每个条目进入开发前必须先做一次代码复核，因为部分缺口可能已经被实现或部分实现。例如 OCR 队列自动重试在当前代码中已经具备 `Classify -> ShouldRetry -> GetNextDelay -> requeue/block` 调用链，并有测试覆盖，不应再作为高优先级缺口重复排期。

### 1.2 按依赖切片，而不是按文档章节切片

推荐顺序：

1. 题录数据模型与服务契约。
2. OCR 后端能力。
3. 搜索、证据与 MCP 契约补齐。
4. UI 壳层清理与菜单/设置入口。
5. 编辑题录、搜索结果、OCR 队列等用户工作流。
6. tombstone/purge、批量操作、收藏、设计系统等后续增强。

### 1.3 每个切片都要有迁移与行为测试

涉及持久化结构时必须包含数据库迁移和回归测试。涉及 MCP、证据、搜索、OCR 生命周期时，测试应优先覆盖外部行为和持久不变量，而不是实现细节。

### 1.4 保持域模型词汇一致

输出、issue 标题、测试名和 UI 文案优先使用 `.agent/CONTEXT.md` 中的词汇：Item（题录）、FileAsset、DocumentInstance、LayoutRevision、SearchUnit、EvidenceRef、OCR Preset、ProviderCredential 等。

---

## 2. 总体路线图

### Phase 0：缺口复核与 backlog 校准

目标：把本文档中的条目与当前代码逐项对齐，避免实现已完成事项。

状态：**已完成（2026-07-01）**。

产出：

- 为每个条目标记 `未开始`、`部分实现`、`已实现`、`无需修改`。
- 修正优先级汇总。
- 将可执行项拆成文档内 coding-agent 任务，并标记 `ready-for-agent`、`blocked` 或 `defer`。

说明：按用户要求，不创建 GitHub Issues；后续 coding agent 直接以本文第 5 节任务拆分为执行入口。

### Phase 1：题录核心模型

目标：先稳定 Item 的持久化模型和服务层，避免后续 UI 返工。

覆盖：

- 3.1 CSL 核心字段。
- 3.2 结构化名称列表。
- 3.3 结构化日期。
- 3.4 `IItemService` 的更新、删除、列表/搜索方法。

完成后，编辑题录 UI、MCP metadata、搜索结果展示都能绑定稳定契约。

### Phase 2：OCR 后端能力

目标：补齐 PRD v1 明确要求的 OCR 触发和状态管理。

覆盖：

- 2.1 文档级 OCR。
- 2.2 区域级 OCR。
- 2.3 unset current / hide OCR run。
- 2.7 skipped 页面状态。

暂不把 tombstone/purge 混入第一批 OCR PR，避免把同步、证据解析、索引可见性全部绑在一起。

### Phase 3：搜索、证据与 MCP 契约

目标：让搜索/MCP 的输出满足“可验证文本证据”的 v1 合同。

覆盖：

- 1.1 `get_search_result_context` 返回 bbox。
- 1.2 `get_page_blocks` current 模式返回临时或稳定 EvidenceRef。
- 4.2 搜索框接入真实搜索管道所需的后端和 ViewModel 契约。
- 4.8 证据 Markdown 导出所需服务入口。

### Phase 4：UI 壳层重构

目标：先移除装饰性入口，建立真实菜单和设置入口。

覆盖：

- 4.1 顶部菜单栏。
- 4.3 侧边栏路径显示。
- 4.11 Developer Tools 移除与 ViewModel 迁移。
- 4.12 设置标签页的第一批区域。
- 4.13 MinerU OCR 右键触发流程。

### Phase 5：用户工作流页面

目标：把核心后端能力变成可用桌面工作流。

覆盖：

- 4.4 编辑题录标签页。
- 4.5 OCR 队列管理标签页。
- 4.8 证据 Markdown 导出 UI。
- 4.9 批量操作的第一版。

### Phase 6：后续增强与维护操作

目标：处理高级维护、组织能力和设计系统一致性。

覆盖：

- 2.4 OCR 逻辑删除。
- 2.5 OCR 数据清除。
- 4.6 收藏管理 UI。
- 4.7 自定义字段编辑器。
- 4.10 设计系统细节。

---

## 3. Phase 0 复核结果

### 3.1 已完成事项

| 条目 | 状态 | 说明 |
|---|---|---|
| 3.1/3.4 Item 基础字段与服务 CRUD | 已实现 | `items` 已扩展 CSL 核心字段和 `deleted_at`；`ItemMetadata`、`IItemService`、`ItemService` 已覆盖 create/update/delete/list，`BibliographicCoreTests` 覆盖 citation_key 自动生成、更新、软删除、分页列表。 |
| 2.1 文档级 OCR | 已实现 | `IOcrRunCoordinator.RunPresetOnDocumentAsync` 已列出 DocumentInstance 下全部页面并复用页面级 OCR；`OcrLifecycleTests` 覆盖全页面运行和无页面不创建空 run。 |
| 2.2 区域级 OCR | 已实现 | `IOcrRunCoordinator.RunPresetOnRegionAsync` 已验证 `NormalizedBBox` 并通过 region adapter 生成 staging/candidate；`OcrAdapterReadinessTests` 覆盖有效区域和越界区域不创建 run。 |
| 2.6 OCR 自动重试与手动修复分类执行 | 已实现 | `OcrQueueScheduler` 已调用 `IOcrRetryPolicy.Classify`、`ShouldRetry`、`GetNextDelay`，并会将手动修复类错误标记为 `Blocked`。`OcrQueueSchedulerTests` 已覆盖瞬时重试、手动修复阻塞和 timeout 行为。 |
| 3.5 标识符存储模型 | 无需修改 | 当前 `item_identifiers` 表优于 PRD 中的 JSON map：可索引、可唯一约束、可扩展。应在 PRD 或 ADR 中记录该设计差异，而不是改回 JSON map。 |
| 4.1 MCP bbox/evidence 小缺口 | 已实现 | `get_search_result_context` 已从 `bbox_union_json` 解析 `NormalizedBBox`；`get_page_blocks` current 模式已对可映射 SearchUnit 的块生成 EvidenceRef；`McpReadApiTests` 覆盖 bbox、EvidenceRef 和无路径泄露边界。 |
| 4.4.1/4.4.11 菜单栏与 Developer Tools 清理 | 已实现 | `MainWindow.axaml` 已使用 Avalonia `Menu`，删除 Developer Tools 和 inline MinerU token prompt；菜单命令已接入 ViewModel，未完成工作流保留为占位入口。 |
| 4.4.2 搜索 UI 第一版 | 已实现 | 顶部搜索框已调用 `ISearchService.SearchLibraryAsync`，搜索结果工作区展示页级结果、matched units、EvidenceRef、index status 和 affected scopes summary；`UiViewModelTests` 覆盖入口。 |
| 4.4.3 侧边栏路径真实化 | 已实现 | 侧边栏显示真实 `RuntimeDatabasePath`、配置的同步根目录和数据库中的 FileSearchRoot；移除最近更改、回收站、WPS Drive、`/Documents/Papers`、`/Downloads/Scan` 等无数据源占位。 |
| 4.4.13 MinerU OCR 右键触发流程 | 已实现 | 右键/菜单运行 OCR 已优先读取 ProviderCredential，回退 appsettings；缺 token 时打开设置页 MinerU 区域；设置保存同时更新 ProviderCredential 和 appsettings。 |

### 3.2 Coding Agent Backlog

状态含义：

- `ready-for-agent`：可由后续 coding agent 直接实现。
- `blocked`：需要先完成依赖任务。
- `defer`：不是当前主线，后续 milestone 再做。
- `done`：当前代码已满足或无需修改。

| 编号 | 任务 | 状态 | 依赖 | 指导后续 agent 的边界 |
|---|---|---|---|---|
| CA-01 | Item 基础字段与服务 CRUD | done | 无 | 已完成扩展 `items`、`ItemMetadata`、`IItemService` 与 `ItemService`；覆盖 `citation_key` 自动生成、update/delete/list；带 migration 和服务测试。 |
| CA-02 | 结构化 creators | ready-for-agent | CA-01 done | 新增 `item_creators`；新写入以结构化 name-list 为准；旧 `creators_json` 仅作兼容回退；更新列表显示和 MCP metadata。 |
| CA-03 | 结构化 dates | ready-for-agent | CA-01 done | 新增 `item_dates`；支持 `issued`、`accessed`、`original-date`；旧 `date` 仅作 issued 显示回退。 |
| CA-04 | 文档级 OCR | done | 无 | 已完成 `RunPresetOnDocumentAsync`；列出 DocumentInstance 下所有 Page 后复用 `RunPresetOnPagesAsync`；无页面不创建空 run。 |
| CA-05 | 区域级 OCR | done | 无 | 已完成 `RunPresetOnRegionAsync`；验证 `NormalizedBBox`；v1 生成 staging/candidate，不做 bbox 级替换采纳。 |
| CA-06 | OCR unset/hide | ready-for-agent | CA-04 done | 增加 current OCR 重置和 hide run 服务；新增 `ocr_runs.hidden`；更新 search dirty、普通 UI 和 MCP 可见性。 |
| CA-07 | MCP bbox/evidence 小缺口 | done | 无 | 已补 `get_search_result_context` bbox；current `get_page_blocks` 对 SearchUnit 块生成 EvidenceRef；不得暴露路径、secret、图片。 |
| CA-08 | 菜单栏与 Developer Tools 清理 | done | 无 | 已用 Avalonia `Menu` 替代装饰性 TextBlock；删除 Developer Tools 按钮；移除 inline MinerU token prompt 的 UI 壳。 |
| CA-09 | MinerU credential flow | done | CA-08 done | 右键 OCR 从 `ProviderCredential` 优先读取 token，回退 appsettings；缺失时打开设置页 MinerU 区域。 |
| CA-10 | 搜索 UI 第一版 | done | CA-07 done | 搜索框调用 `ISearchService`；展示页级结果、matched units、EvidenceRef、index status。 |
| CA-11 | 编辑题录标签页第一版 | ready-for-agent | CA-01 done | 新建/编辑共用表单；覆盖元数据、标签、标识符、关联文件注册；不暴露开发者手动 attach/resolve 入口。 |
| CA-12 | OCR 队列标签页 | ready-for-agent | CA-08 done | 文件菜单打开 OCR 队列页；显示状态、任务列表、暂停/恢复/取消；复用现有队列后端。 |
| CA-13 | 证据 Markdown 导出 | ready-for-agent | CA-07 done, CA-08 done | 右键和编辑菜单导出 pinned Evidence Markdown；使用 file picker；导出内容必须与 pinned EvidenceRef 匹配。 |
| CA-14 | 设置标签页第一版 | ready-for-agent | CA-08 done | 第一批只做库信息、路径、MinerU、OCR 预设、搜索配置、MCP；快照/缓存/许可证后置。当前仅 MinerU 区域部分完成。 |
| CA-15 | OCR 逻辑删除 | defer | CA-06 | tombstone 是同步传播语义，需同时处理搜索、MCP、EvidenceRef、快照。 |
| CA-16 | OCR 数据清除 | defer | CA-15 | purge 是高级维护操作，需保留最小 marker 并让 EvidenceRef 返回 `purged`。 |
| CA-17 | skipped 页面状态 | defer | CA-04 CA-05 | 将源文件缺失、预设不适用、bbox 越界等路径标记 skipped，确保不污染 current/search/MCP。 |
| CA-18 | 批量操作第一版 | defer | CA-10 CA-11 | 多选题录后支持批量 OCR、批量重建索引、批量标签。 |
| CA-19 | 收藏管理 UI | defer | CA-11 | 侧边栏收藏分组；第一版只做添加/移除题录到收藏。 |
| CA-20 | 自定义字段编辑器 | defer | CA-11 | 编辑题录页增加 key-value 编辑器，校验 key 非空。 |
| CA-21 | 设计系统细节 | defer | CA-08 | 建立 Avalonia Style token，逐步替换硬编码字号、间距、圆角。 |
| CA-22 | OCR 自动重试 | done | 无 | 当前实现和测试已覆盖，不再排入开发主线。 |
| CA-23 | 标识符存储模型 | done | 无 | 保留 `item_identifiers` 规范化表；无需改成 JSON map。 |

### 3.3 执行顺序

下一批可并行启动：

1. CA-02 结构化 creators。
2. CA-03 结构化 dates。
3. CA-06 OCR unset/hide。
4. CA-11 编辑题录标签页第一版。
5. CA-12 OCR 队列标签页。
6. CA-13 证据 Markdown 导出。
7. CA-14 设置标签页第一版。

已完成并不再排入主线：

1. CA-01 Item 基础字段与服务 CRUD。
2. CA-04 文档级 OCR。
3. CA-05 区域级 OCR。
4. CA-07 MCP bbox/evidence 小缺口。
5. CA-08 菜单栏与 Developer Tools 清理。
6. CA-09 MinerU credential flow。
7. CA-10 搜索 UI 第一版。

---

## 4. 详细缺口与执行建议

## 4.1 MCP

### 4.1.1 `get_search_result_context` 补充 BBox

- **PRD 引用**：6.14、6.16。
- **当前状态**：部分实现。DTO 已有 `BBox` 字段；搜索单元中已有 bbox union；上下文响应仍未完整填充。
- **建议实现**：
  - 查询上下文 SearchUnit 时读取 `bbox_union_json`。
  - 使用结构化 JSON 解析为 `NormalizedBBox`，不要手写字符串切割。
  - 对无 bbox 的单元返回 `null`。
  - 添加 MCP 契约测试：上下文单元携带 bbox，且不会泄露路径。
- **优先级**：低。
- **建议切片**：与搜索/MCP 小缺口合并为一个 PR。

### 4.1.2 `get_page_blocks` current 模式返回 EvidenceRef

- **PRD 引用**：6.15、6.16。
- **当前状态**：部分实现。pinned/compare 可通过已有证据解析返回引用；current 模式块级引用仍需确认和补齐。
- **建议实现**：
  - current 模式下，如果 block 能映射到 SearchUnit，则调用 `IEvidenceReferenceService.CreateFromSearchUnitAsync`。
  - EvidenceRef 默认为 pinned，current 只是读取模式，不改变 EvidenceRef 的稳定语义。
  - 如果 block 来自非 SearchUnit 临时文本，返回 `null` 并在测试中固定该行为。
- **优先级**：低。
- **建议切片**：与 4.1.1 同 PR。

---

## 4.2 OCR

### 4.2.1 文档级 OCR：`RunPresetOnDocumentAsync`

- **PRD 引用**：6.8。
- **当前状态**：未实现。`IOcrRunCoordinator` 只有页面级、图片页、PDF 渲染页入口。
- **建议实现**：
  - 在 `IOcrRunCoordinator` 增加 `RunPresetOnDocumentAsync(DocumentInstanceId, OcrPresetId)`。
  - 实现中通过 `IPageService` 或 SQL 列出 DocumentInstance 的所有 Page。
  - 无页面时返回 validation/not_found 类错误，不创建空 OCR Run。
  - 内部复用 `RunPresetOnPagesAsync`，避免重复 OCR 生命周期逻辑。
- **优先级**：高。
- **建议切片**：单独 PR，带测试。

### 4.2.2 区域级 OCR：`RunPresetOnRegionAsync`

- **PRD 引用**：6.8、6.12。
- **当前状态**：部分底座已存在。`OcrInputKinds.RegionImage` 和 adapter 输入模型支持区域 bbox，但 coordinator 无公开入口。
- **建议实现**：
  - 在 `IOcrRunCoordinator` 增加 `RunPresetOnRegionAsync(DocumentInstanceId, OcrPresetId, PageId, NormalizedBBox)`。
  - 先验证 bbox 在 normalized page 范围内。
  - v1 可只支持“对选定区域生成 staging/candidate”，是否替换当前 layout node 另行切片。
  - bbox 越界或无法裁剪时使用 `skipped` 或明确错误码。
- **优先级**：中。
- **建议切片**：跟文档级 OCR 后端相邻，但不要与 UI 混合。

### 4.2.3 OCR 当前修订重置与隐藏

- **PRD 引用**：6.11。
- **当前状态**：未实现。数据库已有 `layout_revisions.is_current`，但无服务层封装；`ocr_runs` 无 hidden 标记。
- **建议实现**：
  - 新增 `IOcrRevisionService`，或扩展 `IOcrRunCoordinator`：
    - `UnsetCurrentOcrAsync(DocumentInstanceId)`。
    - `HideOcrRunAsync(OcrRunId)`。
  - 为 `ocr_runs` 增加 `hidden` 列。
  - SearchUnit / FTS dirty 范围需要跟随当前修订变化。
  - MCP 和普通 UI 默认过滤 hidden run。
- **优先级**：高。
- **建议切片**：单独 PR，因为它会影响搜索和 MCP 可见性。

### 4.2.4 OCR 逻辑删除

- **PRD 引用**：6.11、6.15。
- **当前状态**：部分基础存在。`EvidenceResolutionStatus` 已有 `tombstoned`，EvidenceRef 记录也支持 tombstone；但 OCR/layout/search 数据层没有完整 tombstone 语义。
- **建议实现**：
  - 为 `ocr_runs`、`layout_revisions`、必要的 `layout_nodes` 或关联表增加 tombstone 标记。
  - 新增服务方法 `TombstoneOcrRunAsync(OcrRunId)`。
  - tombstone 后从普通 UI、搜索、MCP 默认视图隐藏。
  - 旧 EvidenceRef 解析应返回 `tombstoned`，不静默复活内容。
  - 快照发布需携带 tombstone 标记。
- **优先级**：中。
- **建议切片**：放在 OCR 当前/隐藏之后。

### 4.2.5 OCR 数据清除

- **PRD 引用**：6.11。
- **当前状态**：未实现。
- **建议实现**：
  - 新增 `PurgeOcrDataAsync(OcrRunId)`。
  - 物理删除可清除 payload，保留最小 purge marker。
  - EvidenceRef 解析返回 `purged`。
  - 不要求重写不可变历史分片。
- **优先级**：低。
- **建议切片**：维护操作，后置。

### 4.2.6 skipped 页面状态

- **PRD 引用**：6.9。
- **当前状态**：常量存在，但使用路径不足。
- **建议实现**：
  - 页面源文件缺失或冲突时可标记 `skipped`。
  - 预设不适用页面类型时可标记 `skipped`。
  - bbox 在页面外或裁剪不可用时可标记 `skipped`。
  - 增加 OCR 生命周期测试，确保 skipped 不污染 current layout/search/MCP。
- **优先级**：低。

---

## 4.3 Item（题录）

### 4.3.1 CSL 核心字段

- **PRD 引用**：6.7。
- **当前状态**：部分实现。`items` 表和 `ItemMetadata` 缺少若干 v1 核心字段。
- **建议实现**：
  - 在 `items` 增加：
    - `citation_key`
    - `title_short`
    - `container_title_short`
    - `collection_title`
    - `edition`
    - `genre`
    - `number`
    - `chapter_number`
    - `version`
    - `status`
    - `note`
  - `citation_key` 创建 Item 时自动生成，默认不可由普通 UI 编辑。
  - `ItemMetadata` 与 `ItemService` 映射同步更新。
  - MCP `get_item_metadata` 同步暴露必要字段。
- **优先级**：高，其中 `citation_key` 必须优先。
- **建议切片**：和 `IItemService.UpdateItemAsync/ListItemsAsync` 合并更合理。

### 4.3.2 结构化名称列表

- **PRD 引用**：6.7。
- **当前状态**：未实现。当前使用 `creators_json` 字符串列，没有角色和结构化 name-list。
- **建议实现**：
  - 新建 `item_creators` 表：
    - `creator_id`
    - `item_id`
    - `role`
    - `family`
    - `given`
    - `literal`
    - `suffix`
    - `particles`
    - `sequence_index`
  - 第一批角色：`author`、`editor`、`translator`、`container-author`。
  - `creators_json` 可暂时保留为兼容缓存，但新写入路径应以 `item_creators` 为准。
  - UI 列表中的 Authors 显示值由结构化 creators 派生。
- **优先级**：高。
- **建议切片**：单独 PR，因为会影响列表显示、编辑题录、MCP metadata。

### 4.3.3 结构化日期

- **PRD 引用**：6.7。
- **当前状态**：未实现。当前只有单个 `date` 字符串。
- **建议实现**：
  - 新建 `item_dates` 表：
    - `date_id`
    - `item_id`
    - `role`
    - `date_parts_json`
    - `circa`
    - `season`
    - `literal`
    - `created_at`
  - 第一批角色：`issued`、`accessed`、`original-date`。
  - `date` 字符串可保留为 issued 显示回退。
- **优先级**：中。
- **建议切片**：可与 creators 同一个 milestone，但建议不同 PR。

### 4.3.4 `IItemService` CRUD/List

- **PRD 引用**：6.7。
- **当前状态**：部分实现。已有 create/get/add identifier/list identifiers；缺少 update/delete/list。
- **建议实现**：
  - 新增 `UpdateItemAsync`，至少覆盖第一批编辑 UI 需要的字段。
  - 新增 `DeleteItemAsync`。v1 推荐先做软删除，避免破坏证据和同步语义。
  - 新增 `ListItemsAsync` 或 `SearchItemsAsync`，带分页和基础过滤。
  - 将 UI 中直接 Dapper 查询逐步收敛到服务层。
- **优先级**：高。
- **建议切片**：Phase 1 首批。

---

## 4.4 UI

### 4.4.1 顶部菜单栏

- **PRD 引用**：6.1。
- **当前状态**：未实现。当前 文件/编辑/视图/工具/帮助 是装饰性 `TextBlock`。
- **建议实现**：
  - 使用 Avalonia `Menu` / `MenuItem`。
  - 文件：
    - 设置。
    - OCR 队列。
  - 编辑：
    - 新建题录。
    - 编辑题录。
    - 运行 MinerU OCR。
    - 重建 FTS 索引。
    - 导出证据 Markdown。
  - 视图：
    - 显示/隐藏右侧详情面板。
  - 帮助：
    - 关于。
    - 许可证。
  - 删除“工具”顶级菜单，除非有真实功能。
- **优先级**：高。
- **建议切片**：和 Developer Tools 移除同 PR。

### 4.4.2 搜索接入完整搜索管道

- **PRD 引用**：6.14、6.16，用户故事 13-15。
- **当前状态**：未完成。顶部搜索框未触发 `ISearchService.SearchLibraryAsync`，也没有搜索结果视图。
- **建议实现**：
  - `TextBox` 绑定搜索文本与 `SearchCommand`。
  - 调用 `ISearchService.SearchLibraryAsync`。
  - 搜索结果以页为组展示 matched units、证据引用、截断状态。
  - 展示 `index_status` 和 `affected_scopes_summary`。
  - 支持搜索配置文件选择；高级预览后置。
- **优先级**：高。
- **建议切片**：先实现只读搜索结果视图，再做配置文件选择。

### 4.4.3 侧边栏路径显示

- **PRD 引用**：6.2、6.3、6.6、6.11。
- **当前状态**：已实现。侧边栏显示当前 `RuntimeDatabasePath`、配置的同步根目录，以及数据库中的 FileSearchRoot 列表；已移除无数据源的最近更改、回收站、WPS Drive 和静态 PDF 扫描路径占位。
- **建议实现**：
  - 后续如需“最近扫描时间”，先增加明确的扫描审计字段，不要复用伪数据。
  - “最近更改”和“回收站”在有真实数据源前继续隐藏。
- **优先级**：done（路径真实化）；defer（最近更改、回收站）。

### 4.4.4 编辑题录标签页

- **PRD 引用**：6.7，用户故事 1、4、6。
- **当前状态**：未完成。标签、标识符、关联文件等后端能力缺少普通用户 UI。
- **建议实现**：
  - 与“我的库”“阅读”并列新增“编辑题录”标签页。
  - 元数据区域绑定 `IItemService`。
  - 标签区域支持添加/删除 tag。
  - 标识符区域支持 scheme/value 添加删除。
  - 关联文件区域支持注册 FileAsset，并自动挂载 DocumentInstance。
  - 不暴露手动 attach document instance / resolve file 的开发者入口。
- **优先级**：高。
- **依赖**：Phase 1 的 Item 服务契约。

### 4.4.5 OCR 队列管理标签页

- **PRD 引用**：6.10。
- **当前状态**：后端较完整，ViewModel 存在，缺少 AXAML 视图。
- **建议实现**：
  - 文件 -> OCR 队列 打开标签页。
  - 显示 queued/running/succeeded/failed/cancelled/blocked。
  - 展示并发限制和暂停范围。
  - 支持暂停/恢复、取消任务。
  - 自动刷新可配置。
- **优先级**：中。

### 4.4.6 收藏管理 UI

- **PRD 引用**：6.7。
- **当前状态**：存储字段存在，UI 不存在。
- **建议实现**：
  - 侧边栏收藏分组。
  - 第一版先支持添加/移除题录到收藏。
  - 拖拽可后置。
- **优先级**：低。

### 4.4.7 自定义字段编辑器

- **PRD 引用**：6.7。
- **当前状态**：存储字段存在，UI 不存在。
- **建议实现**：
  - 编辑题录页增加 key-value 编辑器。
  - 校验 key 非空，value 可为空。
- **优先级**：低。

### 4.4.8 证据 Markdown 导出

- **PRD 引用**：6.17，用户故事 22。
- **当前状态**：后端已有 Evidence Markdown 生成能力；普通 UI 不可用。
- **建议实现**：
  - 将“复制 Markdown”改为“导出 Markdown”。
  - 右键菜单和编辑菜单提供导出入口。
  - 使用 file picker 选择目标文件。
  - 内容来自 pinned EvidenceRef 的文本、来源、evref。
  - 阅读工具栏可增加“导出当前页证据 Markdown”，但需先明确当前页 EvidenceRef 的生成规则。
- **优先级**：高。

### 4.4.9 批量操作

- **PRD 引用**：6.8、6.14。
- **当前状态**：未实现。
- **建议实现**：
  - 题录列表增加多选。
  - 第一版批量操作只做：
    - 批量运行 OCR。
    - 批量重建索引。
    - 批量添加/移除标签。
  - 批量导出可后置。
- **优先级**：中。

### 4.4.10 设计系统细节

- **PRD 引用**：`DESIGN.md`。
- **当前状态**：部分实现。配色存在，但排版、圆角、间距、elevation 未统一。
- **建议实现**：
  - 建立 Avalonia Style token。
  - 逐步替换硬编码 FontSize/Margin/CornerRadius。
  - 不作为功能阻塞项。
- **优先级**：低。

### 4.4.11 Developer Tools 移除与 ViewModel 迁移

- **PRD 引用**：代码清理与架构整理。
- **当前状态**：未完成。按钮切换 `ShowDeveloperTools`，但无真实面板。
- **建议实现**：
  - 删除侧边栏 Developer Tools 按钮。
  - 删除或迁移无普通用户 UI 的 ViewModel。
  - 保留并迁移：
    - Library 功能 -> 设置。
    - OCR 队列 -> OCR 队列页。
    - Search Profile -> 设置。
    - Evidence Markdown/Rebuild -> 菜单与右键入口。
    - Snapshot -> 设置。
  - 删除普通用户不应看到的手动页面布局、Mock OCR、PDF render、MCP preview UI 入口。
- **优先级**：高。

### 4.4.12 设置标签页

- **PRD 引用**：6.2、6.3、6.5、6.6、6.8、6.14、6.15、6.19。
- **当前状态**：未实现集中设置界面。
- **建议分批实现**：
  - 第一批：
    - 库信息。
    - 运行时数据库路径。
    - 同步根目录。
    - FileSearchRoot。
    - MinerU OCR。
    - OCR 通用并发配置。
    - OCR 预设管理。
    - 搜索配置。
    - MCP 开关和端口。
  - 第二批：
    - 快照发布/验证/导入。
    - Snapshot Branch 管理。
    - 缓存路径和清理。
    - 关于与许可证。
- **优先级**：高。
- **依赖**：菜单栏入口和 Developer Tools 迁移。

### 4.4.13 MinerU OCR 右键触发流程

- **PRD 引用**：6.5、6.8、6.9。
- **当前状态**：未完成。右键运行 MinerU OCR 时仍要求输入 token。
- **建议实现**：
  - 右键运行 OCR 时不再弹 token 输入框。
  - 读取顺序：
    1. `ProviderCredential` 中 MinerU active secret。
    2. `appsettings.json` 的 `MinerU.Token`。
  - 如果两处都没有 token，提示用户去设置页配置，并自动打开设置页 MinerU 区域。
  - 设置保存时同时写入 ProviderCredential 和 appsettings。
  - 删除 `ShowMinerUTokenPrompt` UI。
- **优先级**：高。

---

## 5. 推荐 Coding Agent 任务拆分

> 本节是后续 coding agent 的领取入口。不要先创建 GitHub Issues；直接按 CA 编号选择任务实现。每个任务都应独立提交为一个 PR 或等价变更集。

### CA-01：Item 基础字段与服务 CRUD

状态：`ready-for-agent`。

- migration：扩展 `items`。
- `ItemMetadata` 添加字段。
- `IItemService` 添加 update/delete/list。
- `ItemService` 实现。
- 测试：创建、更新、删除、列表、citation_key 自动生成。

### CA-02：结构化 creators

状态：`blocked`，依赖 CA-01。

- migration：`item_creators`。
- Core model：creator role/name value object。
- `IItemService` 读写 creators。
- UI 列表显示从 creators 派生作者。
- MCP metadata 输出 creators。

### CA-03：结构化 dates

状态：`blocked`，依赖 CA-01。

- migration：`item_dates`。
- Core model：date role/date-parts。
- `IItemService` 读写 dates。
- MCP metadata 输出 issued/accessed/original-date。

### CA-04：文档级 OCR

状态：`ready-for-agent`。

- `RunPresetOnDocumentAsync`。
- 复用 `RunPresetOnPagesAsync`。
- 测试：列出全部页面、无页面失败、成功创建 run/page results。

### CA-05：区域级 OCR

状态：`ready-for-agent`。

- `RunPresetOnRegionAsync`。
- bbox 验证。
- region input 传入 adapter。
- 测试：有效区域、越界区域、失败不污染 current layout/search。

### CA-06：OCR unset/hide

状态：`blocked`，依赖 CA-04。

- migration：`ocr_runs.hidden`。
- 服务：unset current、hide run。
- search dirty 标记。
- MCP/UI 默认过滤 hidden。
- 测试：隐藏后搜索/MCP 不返回；旧 EvidenceRef 行为明确。

### CA-07：MCP bbox/evidence 小缺口

状态：`ready-for-agent`。

- context unit bbox。
- current page blocks EvidenceRef。
- 测试：结构化块、上下文、无路径/无秘密泄露。

### CA-08：菜单栏与 Developer Tools 清理

状态：`ready-for-agent`。

- 真 Avalonia Menu。
- 删除 Developer Tools 按钮。
- 删除 token inline prompt 的 UI 壳。
- ViewModel 迁移入口打通但可先用占位标签页。

### CA-09：MinerU credential flow

状态：`blocked`，依赖 CA-08。

- 右键 OCR 从 ProviderCredential/appsettings 读取 token。
- 缺失 token 时打开设置页。
- 设置页 MinerU 区域保存 credential。
- 测试：credential 优先级、缺失提示、不会重复要求 token。

### CA-10：搜索 UI 第一版

状态：`blocked`，依赖 CA-07。

- 搜索框 command。
- 搜索结果列表。
- index status。
- matched units 和 EvidenceRef 展示。

### CA-11：编辑题录标签页第一版

状态：`blocked`，依赖 CA-01。

- 元数据。
- 标签。
- 标识符。
- 关联文件注册。
- 新建题录和编辑题录共用表单。

### CA-12：OCR 队列标签页

状态：`blocked`，依赖 CA-08。

- 队列状态。
- 任务列表。
- 暂停/恢复/取消。
- 自动刷新。

### CA-13：证据 Markdown 导出

状态：`blocked`，依赖 CA-07、CA-08。

- 右键和菜单入口。
- file picker。
- pinned Markdown 生成。
- 测试：导出内容与 pinned EvidenceRef 匹配。

### CA-14：设置标签页第一版

状态：`blocked`，依赖 CA-08。

- 库信息、运行时数据库路径、同步根目录、FileSearchRoot。
- MinerU OCR、OCR 通用并发、OCR 预设管理。
- 搜索配置。
- MCP 开关和端口。
- 快照、缓存、许可证先不做。

---

## 6. 优先级汇总

| 优先级 | 条目 |
|---|---|
| 高 | 4.2.1 文档级 OCR；4.2.3 OCR 当前修订重置与隐藏；4.3.1 CSL 核心字段；4.3.2 结构化名称；4.3.4 `IItemService` CRUD/List；4.4.1 菜单栏；4.4.2 搜索管道；4.4.3 侧边栏路径真实化；4.4.4 编辑题录标签页；4.4.8 证据 Markdown 导出；4.4.11 Developer Tools 迁移；4.4.12 设置标签页；4.4.13 MinerU 右键触发流程 |
| 中 | 4.2.2 区域 OCR；4.2.4 OCR 逻辑删除；4.3.3 结构化日期；4.4.5 OCR 队列管理；4.4.9 批量操作 |
| 低 | 4.1.1 上下文 BBox；4.1.2 current blocks EvidenceRef；4.2.5 OCR 清除；4.2.6 skipped 状态；4.4.6 收藏 UI；4.4.7 自定义字段 UI；4.4.10 设计系统细节 |
| 已实现/无需修改 | 2.6 OCR 自动重试；3.5 标识符存储模型 |

---

## 7. 关键风险

### 7.1 Item 模型迁移风险

`creators_json`、`date` 与新结构化表会并存一段时间。需要定义读取优先级和迁移策略，避免 UI 与 MCP 输出不一致。

建议：

- 新写入走结构化表。
- 旧字段作为显示回退。
- 后续再做一次数据迁移或兼容清理。

### 7.2 OCR 删除/隐藏影响证据语义

hide、tombstone、purge 三者语义不同：

- hide：普通视图隐藏，数据仍存在。
- tombstone：同步传播的逻辑删除，旧引用解析为 `tombstoned`。
- purge：有效载荷清除，旧引用解析为 `purged`。

不要把三者合并成一个 delete API。

### 7.3 UI 设置页范围过大

设置页覆盖面广，必须分批做。第一批只做能解除当前阻塞的配置：MinerU、OCR 预设、搜索配置、MCP、路径。

### 7.4 搜索 UI 依赖证据与分页契约

搜索结果展示不要直接绑定 FTS 实现细节，应通过 `ISearchService` 和 MCP/证据 DTO 展示 SearchUnit、EvidenceRef、index status。

---

## 8. 修订历史

| 版本 | 日期 | 变更 |
|---|---|---|
| 2.1 | 2026-07-06 | 同步已完成的 CA-01、CA-04、CA-05、CA-07、CA-08、CA-09、CA-10；完成 4.4.3 侧边栏路径真实化；刷新下一批 ready-for-agent 列表。 |
| 2.0 | 2026-07-01 | 将原缺口清单重写为已复核缺口与执行计划；修正 OCR 自动重试状态；按依赖关系重排实施顺序；新增 PR 拆分和关键风险。 |
