# Task: 阻塞/长任务进度上报补齐

## 背景

阻塞进度存在两套并行通道：

| 通道 | 位置 | 作用 |
|------|------|------|
| 领域 `BlockingOperation` | `IBlockingOperationService` + SQLite | Start / UpdateProgress / Complete / Fail / Cancel + `AddLogEntryAsync` |
| UI 模态 | `ModalOperationRunner` / `ModalOperationContext` | 弹窗状态、进度条、「显示详细信息」日志 |

弹窗「详细信息」**只**接收调用方传入的 `Report(..., detail)` / `AddLogAsync`；领域 `AddLogEntryAsync` 不会自动上屏。

PRD（`.agent/PRD.md` §4.1）要求模态日志输出细粒度进度（如具体文件路径）。本 task **不**做「UI 完全绑定 BlockingOperation 为唯一状态源」的重构。

**金标准实现**（应对齐）：

- `FirstRunWorkflow.ScanAndImportAsync` — 每文件「正在导入」/「已导入|失败」+ 路径 detail
- `MainWindowViewModel.RescanFileSearchRootsCoreAsync` — 导入循环已有开始/结果，但仍有缺口

## 目标与原则

| 通道 | 适用 | 要求 |
|------|------|------|
| **模态 + 详细信息** | 已有/应有阻塞弹窗 | `context.Report(current, total, label, detail?)`；`detail` 写入「显示详细信息」 |
| **状态栏** `MainWindowViewModel.Report` | 无模态长任务 | 开始 / 阶段性 / 结束文案；不新造弹窗 |
| **OCR 队列看板** | 入队文档 OCR / 队列执行 | **不**用阻塞弹窗报页进度；保持 `OcrQueueViewModel` |

### 详细信息约定

- 批处理每一项：`正在…：{名}`（detail=路径或键）→ `已… / 失败…：{名}`
- 进度条优先用**项计数**（文件/条目/分片/SearchUnit）
- 用户可见文案中文

### 明确不做

- UI 绑定 SQLite `BlockingOperation` 为唯一状态源
- 自动重扫强制模态
- 扫描遍历中每个子目录实时进度（成本高）
- 把队列 OCR 塞进 `BlockingOperationDialog`
- OCR 队列执行器内的文档 FTS 重建弹窗

---

## 范围分类

```text
长任务
 ├─ 已有/应有模态 → Report + detail 逐步日志          [A]
 ├─ 无模态       → 状态栏 Report                    [B]
 └─ 文档 OCR 入队 → OCR 队列看板 + 状态栏入队结果    [C]
```

### A. 模态：补齐细粒度进度

| ID | 操作 | 现状 | 目标 | 主文件 |
|----|------|------|------|--------|
| A1 | 首次扫描导入 | 已达标 | 可选：跳过/失败路径补 detail | `FirstRunWorkflow.cs` |
| A2 | 手动文件重扫 | 导入达标；skip/排除不上屏；bar 按 root | 导入按文件进度；已知跳过/排除写 detail；BlockingOp 与 UI 对齐 | `MainWindowViewModel.cs` |
| A3 | 快照发布/导出/检查/应用/丢弃/保留副本 | 仅 InitialStatus | 步骤 + 分片/路径级 detail | `SnapshotViewModel.cs` + Snapshot infra |
| A4 | 快照导入验证（领域 BlockingOp） | 仅 start/终态 | 与 Inspect 模态共用 progress；可选 mid UpdateProgress | `SnapshotServices.cs` |
| A5 | 重建 FTS | 无 mid | 按 SearchUnit 计数 Report + BlockingOp.UpdateProgress | `SearchIndexRebuilder.cs` + `MainWindowViewModel.cs` |
| A6 | MCP 启动 | 无 Report | 分步：校验设置 → Shell → HTTP listener | `MainWindowViewModel.cs` |
| A7 | 工作区**交互式** OCR 模态（局部/区域/页级，非入队） | 仅 initial | 短流程：识别中 / 写候选 / 完成；失败 detail | `PdfWorkspaceViewModel.cs` |
| A8 | 首次单 PDF 导入模态 | indeterminate | 步骤：读取 → 导入 → 完成 | `FirstRunViewModel.cs` |

### B. 无模态：仅状态栏

| ID | 操作 | 目标 | 主文件 |
|----|------|------|--------|
| B1 | 文件监视器自动重扫 | 开始/结束 `Report`（可含汇总）；不弹模态 | `MainWindowViewModel.cs` |
| B2 | BibLaTeX 批量 apply | 循环中 `Report($"正在导入 {i}/{n}：{key}")`，结束汇总 | `BiblatexImportService.cs` + `MainWindowViewModel.cs` |
| B3 | 元数据批量 | 已有页内进度；可选 Message 含题录标题 | `LibraryShellViewModel.cs` |
| B4 | CSL 样式安装 | 保持 StatusText；失败/成功一句 | 基本不动 |
| B5 | 添加搜索根登记阶段 | 登记开始/完成状态栏；重扫模态负责细进度 | `LibrarySettingsViewModel.cs` |

### C. OCR 队列：不改为阻塞详细信息

| 操作 | 处理 |
|------|------|
| 文献列表「运行 OCR」/ 入队 | 维持入队 + `OcrQueue` 刷新 + 状态栏「已加入队列」；**禁止**加阻塞页进度 |
| 工作区「文档级 OCR」若走队列 | 同上：入队后结束/不报页级阻塞进度 + Report + 刷新队列 |
| `OcrQueueTaskExecutor` 内 FTS | 不弹窗；队列任务状态即可 |

---

## 实现设计

### 进度回调形状

沿用已有四元组，避免新抽象：

```csharp
Action<int?, int?, string, string?>? progress
// 模态：
context.Report(current, total, label, detail);
```

基础设施方法**可选**增加 progress（默认 null，兼容测试）：

| API | 文件 | 钩子点 |
|-----|------|--------|
| `RebuildFtsForLibraryAsync` | `SearchIndexRebuilder.cs` + `ISearchIndexRebuilder`（`SearchModels.cs`） | 清表后 total=units；循环 current/total |
| `PublishSnapshotAsync` / Export | `SnapshotServices.cs` / Coordinator | checkpoint → 建分片(i/n) → 校验 → manifest → current |
| Import/Validate staging | `SnapshotServices.cs` | 验 manifest → 每 shard 复制/合并 → 验树 |
| Inspect/Apply | `SnapshotSyncCoordinator.cs` | progress 下传 |
| `ApplyBatchAsync` | `BiblatexImportService` + `IBiblatexImportService` | 每 group 开始/成功 |
| MCP 启动 | `MainWindowViewModel` 模态 lambda 内分步 Report | 可不改领域服务签名 |

有 BlockingOp 的路径（FTS、snapshot validation、重扫）：在 `operationId` 存在时同步 `UpdateProgressAsync`。

### A2 重扫细化

文件：`MainWindowViewModel.RescanFileSearchRootsCoreAsync`（约 760–964）

1. **导入阶段进度条**：用文件计数（全局或当前 root 候选），勿仅用 root 数。
2. **已知路径 skip**：`progress(..., $"跳过已存在：{name}", path)`；若单次 >500 条可改为汇总 + 仅新导入/失败逐条（实现时二选一，默认逐条 detail）。
3. **排除规则**：`AddLogEntryAsync` 后同时写 UI detail（按 rule 汇总或路径列表）。
4. **BlockingOp.UpdateProgress**：导入循环内按文件更新，不只在 root 结束。

### A3/A4 快照

`SnapshotViewModel` 各 `ModalOperations.RunAsync` 把 `context.Report` 传入 Sync API。

步骤文案示例：`正在检查点数据库` → `正在创建分片 2/5`（detail=相对路径）→ `正在校验分片` → `正在写入清单` → `完成`。

丢弃/保留副本：阶段文案即可。

### A5 FTS

- 扩展 `RebuildFtsForLibraryAsync(..., progress)`
- 模态：`context.Report` + 已有 `operationId` 的 `UpdateProgressAsync`
- label：`正在写入 FTS：{current}/{total}`

### A6 MCP

在启动模态 lambda 内，校验 / start shell / start listener 前后：

`context.Report(null, null, label, detail)`（不定进度）。失败进 detail。

### A7 工作区交互 OCR

`RunOcrModalAsync` 改为可接收 `ModalOperationContext`：

- `正在调用识别…` → 成功 `识别完成，正在更新候选…`
- **文档级若 Enqueue**：不报页级阻塞进度；入队后结束模态 + `Report` + 刷新 `OcrQueue`（与列表「运行 OCR」一致）

### B1 自动重扫

`showBlockingDialog: false` 调用处：

- 开始：`Report("检测到文件变化，正在重新扫描…")`
- 结束：沿用/加强 `ApplyFileSearchRootRescanResultAsync` 汇总

### B2 BibLaTeX

`ApplyBatchAsync` 可选 progress；UI：

```csharp
Report($"正在导入 BibLaTeX {i}/{n}：{key}");
```

不新增模态。

---

## 实施顺序

1. **A2** 重扫（最高用户可见收益，模式已有）
2. **A5** FTS
3. **A6** MCP
4. **A3/A4** 快照（API 签名 + 多调用点，工作量最大）
5. **B1/B2** 状态栏
6. **A7/A8 + C 核对** 工作区 OCR 入队 vs 模态
7. **A1** 可选打磨
8. 测试 + `scripts/cleanup-code.ps1` + 提交前 `scripts/inspect-code.ps1`

---

## 主要改动文件

**UI**

- `src/Patchouli.UI/Services/ModalOperationRunner.cs`（一般不改契约）
- `src/Patchouli.UI/ViewModels/MainWindowViewModel.cs`
- `src/Patchouli.UI/ViewModels/Core/SnapshotViewModel.cs`
- `src/Patchouli.UI/ViewModels/Ocr/PdfWorkspaceViewModel.cs`
- `src/Patchouli.UI/ViewModels/Settings/FirstRunViewModel.cs`
- `src/Patchouli.UI/ViewModels/Library/LibraryShellViewModel.cs`（B3 可选）
- `src/Patchouli.UI/ViewModels/Settings/LibrarySettingsViewModel.cs`（B5）

**Infrastructure / Core**

- `src/Patchouli.Infrastructure/Search/SearchIndexRebuilder.cs`
- `src/Patchouli.Search/SearchModels.cs`
- `src/Patchouli.Infrastructure/Snapshots/SnapshotServices.cs`
- `src/Patchouli.Infrastructure/Snapshots/SnapshotSyncCoordinator.cs`
- `src/Patchouli.Infrastructure/Snapshots/SnapshotModels.cs`
- `src/Patchouli.Infrastructure/Bibliography/Biblatex/BiblatexImportService.cs`
- `src/Patchouli.Core/Bibliography/Biblatex/IBiblatexImportService.cs`
- `src/Patchouli.Infrastructure/Workflows/FirstRunWorkflow.cs`（A1 可选）

**Tests**

- `tests/Patchouli.Tests/UiViewModelTests.cs`
- Snapshot / Search / Biblatex 相关测试

---

## 测试计划

| 区域 | 建议 |
|------|------|
| 重扫 detail | `UiViewModelTests`：progress 断言含「正在导入」「已导入」「跳过已存在」 |
| FirstRun | 回归现有测试 |
| FTS progress | progress 回调计数/最终 current==total |
| Snapshot | Publisher/Coordinator 或 UI fake runner 收集 log 步骤 |
| BibLaTeX | ApplyBatch progress 次数与 groups 一致 |
| OCR | 文档 OCR 仍入队；不依赖 BlockingOperationDialog 页进度 |

---

## 验收标准

- [ ] 任一**模态**阻塞任务：展开「显示详细信息」有初始句 + **过程增量**（步骤或逐项）；批量导入类有开始与结果行
- [ ] 文档 OCR：队列看板可见任务/页进度；无假阻塞详细信息
- [ ] 自动重扫 / BibLaTeX 批量等无模态：状态栏有进行中与完成提示
- [ ] 不改变 OCR 队列语义；不做 PRD 全量模型绑定
- [ ] 改动文件经 `scripts/cleanup-code.ps1`；提交前 `scripts/inspect-code.ps1` 无阻塞问题

---

## 参考

- PRD：`.agent/PRD.md` §4.1、V2-AC10 / V2-AC13
- 金标准：`FirstRunWorkflow.ScanAndImportAsync`、`MainWindowViewModel.RescanFileSearchRootsCoreAsync`
- 模态壳：`ModalOperationRunner` / `BlockingOperationDialogViewModel`（「显示详细信息」）
