# Task: 统一冲突解决工作流

状态：待实施
优先级：高（阻断 snapshot branch 导入与 OCR 编辑器达到 PRD v2 可用标准）
范围：CF-01 到 CF-06 的统一展示、动作执行、状态转换、领域副作用和测试覆盖；为快照同步中心提供 coordinator/executor seam。

## 1. 背景与已确认事实

当前代码已经建立统一冲突模型，但只完成了部分端到端接入：

- `ConflictCode` 稳定定义 CF-01 到 CF-06。
- `ConflictDescriptor` 已包含 domain、severity、对象、摘要、本地/传入状态、推荐动作、已选动作和解决状态。
- `Result` / `Result<T>` 可以携带结构化冲突。
- `ConflictDescriptorMapper` 可以为六类冲突创建 descriptor。
- CF-04 文件多候选已经使用 `ConflictResolutionDialog` 并执行候选确认。
- CF-05 的 changed-file 候选可以复用相同对话框，但动作不完整。
- Infrastructure 的 bbox validation 已能返回 CF-06 descriptor。

尚未完成的关键链路：

1. CF-01/CF-02 虽有推荐动作，但 mapper 明确标注动作尚不可执行；`SnapshotBranchViewModel` 只显示 JSON，没有解决冲突的 UI 或 API。`ApplyImportPlanAsync` 又拒绝 unresolved blocking conflicts，因此相关 branch plan 无法完成导入。
2. CF-03 在分支没有凭据时仍会创建 `CredentialNotImported("*")`，并且没有导航到凭据设置或把相关 preset 标记为 `credential_missing`。
3. CF-05 只有“确认变化文件”，缺少重新绑定和保留旧证据。
4. CF-06 UI 使用页面内自定义 flyout 和自定义动作，没有消费领域服务返回的 descriptor；其中“删除重叠框后覆盖”不属于 PRD 允许动作，并可能删除用户布局数据。
5. 没有统一 conflict coordinator/action dispatcher。当前动作执行散落在 ViewModel，`SelectedAction` 和 `ResolutionStatus` 也没有统一状态转换或持久化规则。
6. 现有测试主要验证 mapper 形状，没有覆盖 CF-01 到 CF-06 的完整展示、动作、副作用和状态转换。

相关文件：

- `src/Patchouli.Core/Conflicts/ConflictCode.cs`
- `src/Patchouli.Core/Conflicts/ConflictDescriptor.cs`
- `src/Patchouli.Core/Conflicts/ConflictAction.cs`
- `src/Patchouli.Core/Results/Result.cs`
- `src/Patchouli.Infrastructure/Conflicts/ConflictDescriptorMapper.cs`
- `src/Patchouli.Infrastructure/Snapshots/SnapshotBranchInspection.cs`
- `src/Patchouli.Infrastructure/Files/FileResolutionService.cs`
- `src/Patchouli.Infrastructure/Layout/LayoutTreeService.cs`
- `src/Patchouli.UI/ViewModels/Core/SnapshotBranchViewModel.cs`
- `src/Patchouli.UI/ViewModels/Library/FileDocumentViewModel.cs`
- `src/Patchouli.UI/ViewModels/Ocr/PdfWorkspaceViewModel.cs`
- `src/Patchouli.UI/ViewModels/Dialogs/ConflictResolutionDialogViewModel.cs`
- `src/Patchouli.UI/Views/ConflictResolutionDialog.axaml`
- `tests/Patchouli.Tests/ConflictDescriptorTests.cs`

## 2. 目标与非目标

### 目标

1. CF-01 到 CF-06 都使用同一个 `ConflictDescriptor` 契约、同一个模态对话框入口和同一套状态转换规则。
2. 每个推荐动作都有明确的领域执行器；UI 不解析错误字符串，也不直接拼接领域 SQL。
3. blocking conflict 在解决前阻止危险操作；warning conflict 可继续，但必须明确显示后果和恢复动作。
4. snapshot branch conflict 可以逐项解决，解决结果进入 import plan，并由 apply 阶段执行确定的导入策略。
5. 文件变化和 bbox 冲突保持证据可复现性，不静默覆盖源身份、DocumentBox 或 EvidenceRef v2 语义。
6. CF-01 到 CF-06 均有端到端状态转换和领域副作用测试。

### 非目标

- 不增加 CF-07 或重新编号现有 conflict code。
- 不实现自动对象级 merge 或静默 last-writer-wins。
- 不把 warning 全部升级为 blocking。
- 不通过保存 UI 文案字符串代替结构化 action/result。
- 不在冲突对话框中直接执行 SQL、文件写入或布局树修改。
- 不改变 ProviderCredential、FileAsset、page-local DocumentTreeRevision/DocumentBox、EvidenceRef v2 和 snapshot branch 的长期边界。

### 与快照和设置 task 的协调

- `.agent/task-snapshot-sync-lifecycle-and-ui.md` 负责把真实的接收/分支检查流程带到 Sync 菜单和同步中心，并调用本任务的 coordinator 解决 CF-01 到 CF-03；它不在 ViewModel 中解释 CF action 或直接修改 import plan。
- `.agent/task-device-settings-state-and-sync.md` 负责 JSON→数据库 opt-in 后的 setting record 和本机 override。设置随 metadata shard 整体接受，不创建 SC-* code、settings executor 或 settings conflict UI。
- 本任务先交付 coordinator、executor registry、状态转换和 branch-plan resolution seam；随后快照 task 接入 CF executor，设置 task 接入 SC executor。三个 task 不重复定义平行的 dialog、action dispatcher 或 persistence owner。

## 3. 统一冲突模块

### 3.1 Coordinator 与 executor

新增统一协调边界，例如：

```csharp
public interface IConflictCoordinator
{
    Task<ConflictResolutionResult> ResolveAsync(
        ConflictDescriptor conflict,
        CancellationToken cancellationToken = default);
}

public interface IConflictActionExecutor
{
    string ConflictCode { get; }

    Task<Result<ConflictDescriptor>> ExecuteAsync(
        ConflictDescriptor conflict,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default);
}
```

具体命名可以调整，但必须保持以下边界：

- coordinator 负责展示统一对话框、收集 action/option、调用对应 executor，并返回更新后的 descriptor。
- executor 负责验证 action、执行领域副作用和产生状态转换；ViewModel 不直接解释 action id。
- action id 必须来自 descriptor 的 `RecommendedActions`；未知 action 返回稳定错误，不能默认执行。
- blocking conflict 的 `leave_unresolved` 保持 unresolved；warning 可以进入 ignored 或保持 unresolved，但不能伪装成 resolved。
- `SelectedAction` 和 `ResolutionStatus` 必须由 executor 生成，不由 UI 任意赋值。
- 重复执行 resolved/ignored/superseded conflict 必须有明确幂等或拒绝语义。

### 3.2 状态转换

允许的基础转换：

| 当前状态 | 用户/领域动作 | 新状态 |
|---|---|---|
| unresolved | 成功执行解决动作 | resolved |
| unresolved | 明确忽略非阻塞 warning | ignored |
| unresolved | 暂不处理 | unresolved |
| resolved | 新扫描或新 incoming state 使旧冲突失效 | superseded |
| ignored | 同一风险再次出现且输入已变化 | unresolved 或 superseded 后创建新冲突 |

失败的领域动作不得修改 descriptor 状态，也不得产生部分持久化副作用。

### 3.3 统一对话框

`ConflictResolutionDialog` 继续作为唯一冲突解决模态入口，并补充：

- 展示 `Severity`，blocking 显示“必须处理”，warning 显示“可继续但有风险”，info 显示信息语义。
- 本地/传入状态使用用户可读字段，而不是只展示原始 JSON；可以保留可展开的技术详情。
- 动态展示 descriptor 的 actions，不在 XAML 或 ViewModel 中为某个 CF code 硬编码另一套动作。
- 需要候选项的动作由结构化 option provider 提供；没有选择时动作禁用并显示原因，而不是点击后静默无响应。
- `leave_unresolved` 的文案和关闭语义按 severity 区分。
- 所有对话框通过 `IDialogService` 打开，不使用页面内 flyout 替代冲突解决流程。

## 4. 各冲突的完成要求

### 4.1 CF-01：同一 `item_id` 内容不同

必须实现：

- `keep_local`：保留本地题录，不覆盖其 Title/Type，并从本次 plan 中排除 incoming item、其 DocumentInstance 和无法独立导入的依赖对象。需要保留 incoming PDF 时，用户必须显式新建题录；不得将其隐式挂到本地 Item。
- `import_as_new_item`：为 incoming item 创建新 `item_id`，并重映射本次计划中属于它的 DocumentInstance 和依赖对象；不得静默复用旧 identity。
- `skip`：从本次 import plan 排除 incoming item 及无法独立导入的依赖对象。

解决动作必须更新 import plan，而不是立即绕过 plan 向活动数据库写入。

### 4.2 CF-02：主要 DocumentInstance 冲突

必须实现：

- `keep_local_with_incoming_secondary`：本地主文档保持 primary；导入 incoming DocumentInstance，并设置 `is_primary = 0`。
- `keep_local_without_incoming`：本地主文档保持 primary；从 plan 排除 incoming DocumentInstance 及其 page/layout/search/evidence 导入。

任何动作执行后，Item 下最多只能有一个 primary DocumentInstance。

### 4.3 CF-03：凭据未导入

修正产生条件：

- 只有 branch 中存在与选中 OCR Preset/Provider 相关、但按边界不导入的 credential 时才创建 CF-03。
- branch 没有相关 credential 时不得创建 `CredentialNotImported("*")` 占位 warning。

必须实现：

- 导入可以在 CF-03 unresolved/ignored 状态下继续。
- 导入后相关 preset 明确进入 `credential_missing`。
- `reenter_credential` 导航到对应 provider 的设置页；不把 secret 放入 descriptor、日志或 snapshot。
- 该设置页成功保存后，直接将 CF-03 标为 resolved；不额外验证 credential 是否可用，后续 OCR 运行自行报告 credential 失败。
- 用户选择稍后处理时，warning 仍可在 preset 状态和设置页恢复。

### 4.4 CF-04：文件多路径候选

保留现有行为并迁入统一 executor：

- `choose_candidate` 必须要求选择候选路径。
- 候选仍显示路径、mtime、大小、hash/置信度和原因。
- 选择后调用文件解析领域服务确认位置和指纹。
- 仅大小或快速哈希相同不能自动合并，仍保持 CF-04。
- ViewModel 不再直接调用 `ConfirmMovedCandidateAsync` 解释 action id。

### 4.5 CF-05：源文件变化或 bbox basis stale

补齐动作：

- `rebind_source`：选择并验证另一个符合原 FileAsset 身份的路径。
- `confirm_changed_file`：用户明确接受新源和新 fingerprint，并触发 source_changed/bbox_basis_stale 后续状态。
- `reuse_revision_for_new_fingerprint`：用户明确确认新 PDF 仅有不影响实质内容的改动后，仅更新 FileAsset 绑定并将相关 SearchUnit 标记 stale；0.2.0 不复制或改写 immutable page tree revision。不得修改旧修订或 pinned EvidenceRef。
- `keep_old_evidence`：不更新 FileAsset fingerprint，不让 current evidence 静默跟随新文件；保留旧证据并维持缺失/变化警告。

FileAsset 的完整 hashed fingerprint 是源身份。UI 必须说明确认新源会使哪些 page/Box/bbox/search/evidence 状态需要重新验证；旧 page-local revision 保持 immutable。任何动作都不得静默重写 pinned EvidenceRef。

### 4.6 CF-06：普通 bbox 重叠

删除 PDF 工作台的自定义冲突 flyout 和 `ResolveConflictOverwriteAsync` 删除已有框行为。统一使用 `IDocumentTreeEditor` 的 tree validator 返回的 CF-06 descriptor。

只允许：

- `adjust_bbox`：保留 staging candidate，让用户调整 bbox；在冲突消失前不得采纳到 current layout。
- `change_to_allowed_type`：用户明确选择允许重叠的 node type 后重新验证。
- `skip_candidate`：删除或放弃 staging candidate，不修改已有节点。

不得提供“删除所有重叠 Box 后覆盖”动作。任何已有 DocumentBox 的删除必须是独立、明确、可撤销的 draft 编辑操作，不得作为 CF-06 快捷解决方式。

## 5. Snapshot import plan 设计

`BranchImportPlan` 必须能保存逐冲突选择，例如使用更新后的 immutable descriptor 或独立 resolution map：

```csharp
IReadOnlyDictionary<string, ConflictActionSelection> ConflictResolutions
```

key 必须稳定标识同一次 plan 中的冲突，不能只使用 CF code，因为一个 plan 可以有多个 CF-01/CF-02/CF-03。

要求：

- build plan 创建 unresolved conflicts。
- coordinator 逐项解决并产生新 plan，不直接修改原 plan。
- apply 前重新验证 plan 对应的 local/incoming state 未变化；变化后旧 resolution 标记 superseded，并要求重新检查。
- apply 拒绝 unresolved blocking conflicts，但允许 unresolved/ignored warning，并在结果中保留 warning。
- apply 按 resolution map 生成 SQL/领域操作，不依赖 UI 文案。
- 所有 branch import 修改在单个 transaction 中完成；任一动作失败则整体回滚。
- resolution map 仅保存在当前内存 plan；关闭或重启应用即丢弃，用户必须重新检查并解决冲突。

## 6. 分阶段实施计划

### 阶段 A：建立失败基线

1. 增加测试证明 CF-01/CF-02 当前 plan 无可执行解决路径。
2. 增加测试证明无 credential 的 branch 不应产生 CF-03。
3. 增加测试证明 PDF 工作台不能删除已有重叠节点来解决 CF-06。
4. 增加统一 dialog 的 blocking/warning/option-required 行为测试。

### 阶段 B：统一 coordinator 和状态转换

1. 实现 `IConflictCoordinator`、action selection/result 和 executor registry。
2. 将 `ConflictResolutionDialog` 接入 coordinator。
3. 实现 action 合法性、状态转换、幂等和错误语义。
4. 将 CF-04/CF-05 从 `FileDocumentViewModel` 的 action-id 分支迁到 executor。

### 阶段 C：Snapshot conflict

1. 为 branch plan 增加稳定 conflict identity 和 resolution map。
2. 实现 CF-01/CF-02 executor 及 plan transformation。
3. 修正 CF-03 产生条件、preset 状态和设置页恢复动作。
4. 为快照同步中心提供冲突列表、逐项模态解决和 apply readiness 的 coordinator 集成；具体菜单、工作区和发布/接收生命周期由快照 task 实现。

### 阶段 D：Layout conflict

1. 删除 PDF 工作台重复的 bbox overlap 判断和自定义 flyout。
2. 保存/采纳 staging candidate 时调用领域服务，以其返回的 CF-06 descriptor 打开统一对话框。
3. 实现 adjust/type-change/skip executor，并删除 overwrite 动作。
4. 验证冲突动作不会删除已有 layout node 或污染 current revision。

### 阶段 E：回归与 UI 验收

1. 运行 CF-01 到 CF-06 领域、ViewModel 和 headless UI 测试。
2. 验证所有冲突对话框使用同一 view/model/coordinator。
3. 验证 warning 可继续、blocking 不可绕过、取消/暂不处理不产生副作用。
4. 验证 snapshot transaction 回滚和 EvidenceRef/OCR/Document Box Tree 长期边界。

## 7. 验收标准

| 编号 | 条件 | 验证 |
|---|---|---|
| CONFLICT-01 | CF-01 到 CF-06 都由 `ConflictDescriptor` 表达，并通过同一 coordinator 打开统一模态对话框。 | 契约测试和 ViewModel/UI 测试。 |
| CONFLICT-02 | action 必须属于 descriptor 推荐动作；成功后产生合法 resolution 状态，失败不产生部分副作用。 | executor 状态机测试。 |
| CONFLICT-03 | CF-01 的保留本地会排除 incoming Item 及其 DocumentInstance；导入为新题录仍正确重映射 identity/dependency。 | Snapshot branch import 集成测试。 |
| CONFLICT-04 | CF-02 只有“本地为主、传入为次要”与“本地为主、不导入传入”两个动作；每个 Item 最多一个 primary。 | DocumentInstance transaction 测试。 |
| CONFLICT-05 | 无相关 credential 时不创建 CF-03；有 credential 时导入可继续，设置页保存成功即清除 warning。 | Credential/snapshot 测试。 |
| CONFLICT-06 | CF-04 选择候选通过统一 executor 执行，快速哈希相同的不同文件不会自动合并。 | 文件解析冲突测试。 |
| CONFLICT-07 | CF-05 以完整 hashed fingerprint 标识 source basis，支持重新绑定、确认新源、显式衍生旧修订到新 fingerprint 和保留旧证据；pinned EvidenceRef 不静默漂移。 | FileAsset、bbox、evidence 回归测试。 |
| CONFLICT-08 | CF-06 只允许调整 bbox、改为允许类型或跳过；不存在删除已有重叠 Box 后覆盖的路径。 | Document Tree service 和 PDF workspace 测试。 |
| CONFLICT-09 | unresolved blocking conflict 阻止 apply；warning 可继续但在结果和 UI 保留。 | Branch apply 和 dialog severity 测试。 |
| CONFLICT-10 | local/incoming state 变化会使旧 resolution superseded，并要求重新检查。 | Plan stale/superseded 测试。 |
| CONFLICT-11 | Snapshot conflict actions 在单 transaction 中执行，失败整体回滚。 | 故障注入集成测试。 |
| CONFLICT-12 | UI 不解析 conflict 错误字符串，不直接执行领域 SQL，不为单个 CF code 创建平行 flyout。 | 架构边界测试。 |

## 8. 已确认的实施决策

1. CF-01 的 `keep_local` 排除 incoming Item、DocumentInstance 和依赖对象；PDF 若需保留，用户手动新建题录。未来可增加拖入 PDF 快捷创建题录，不能以冲突处理隐式创建或绑定。
2. CF-02 仅提供“保留本地主文档，传入文档作为次要文档”和“保留本地主文档，不增加传入文档”两个互斥动作。
3. CF-05 以完整 hashed fingerprint 作为 source basis。确认新 fingerprint 后旧修订保留旧 basis 并变为 stale；用户可显式确认衍生一份内容相同、指向新 fingerprint 的新修订。
4. conflict resolution 只存于内存 plan，重启后必须重新检查和解决。
5. CF-03 的凭据设置页成功保存后直接清除 warning；凭据可用性由后续 OCR 运行报告。

## 9. 参考依据

- `.agent/PRD.md` 第 4.2 节：CF-01 到 CF-06 的冲突语义与统一 UI 要求。
- `.agent/PRD.md` 第 5 节：UI 不解析错误字符串，冲突必须使用结构化 code/DTO。
- `.agent/task-snapshot-sync-lifecycle-and-ui.md`：真实快照发布、导出、接收和同步中心。
- `.agent/task-device-settings-state-and-sync.md`：JSON 默认 owner、opt-in setting record 与 metadata-shard 整体接受边界。
- `.agent/CONTEXT.md`：Snapshot Branch、ProviderCredential、FileAsset、DocumentTreeRevision/DocumentBox 和 EvidenceRef v2 长期边界。
