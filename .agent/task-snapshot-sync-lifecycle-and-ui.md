# Task: 快照同步生命周期、发布/导出与同步中心

状态：待实施
优先级：高（让快照、分支导入和冲突 UI 成为最终用户可完成的真实工作流）
范围：库内容及已启用 non-secret setting record 的快照发布、导出、接收、验证、staging、分支检查、同步中心、菜单入口和状态表达。设置 JSON→数据库的 opt-in owner 迁移仍由设置 task 实现；本任务不实现 setting record 合并或 SC-* 冲突。

## 1. 背景与已确认事实

现有 Infrastructure 已能创建内容分片、manifest 和 `current.json`，验证 manifest/hash，并把 incoming snapshot 导入 staging 数据库。`SnapshotBranchInspectionService` 还能构建选择性导入计划，并在没有 blocking conflict 时使用 transaction 写入活动运行库。

但这些能力尚未组成用户工作流：

1. `SnapshotViewModel` 需要手动输入 `SyncRoot`、`ManifestPath`、`StagingRoot` 和固定的 `device-ui`，没有真实的设置、菜单或工作区入口。
2. 主窗口没有 Snapshot ViewModel 的 data template、tab kind 或 Sync 菜单命令；用户不能从产品 UI 发起发布、导出或接收。
3. 发布器在 parent mismatch 时只写 branch metadata；调用方没有把该结果转化为“先检查 incoming”的可理解状态。
4. `SnapshotBranchViewModel` 只输出 JSON。CF-01/CF-02 推荐动作尚不可执行，且 unresolved blocking conflict 必然阻止 apply。
5. `SnapshotImporter` 假定 manifest 位于 `sync-root/manifests/` 下；不存在可移动导出包的正式格式和入口。
6. 发布工作流尚未把稳定 sync root 的 shard 复用、原子 current pointer 更新和未来多目标发布统一为产品可用的流程。

相关现有文件：

- `.agent/PRD.md`
- `.agent/task-device-settings-state-and-sync.md`
- `.agent/task-unified-conflict-resolution.md`
- `src/Patchouli.Infrastructure/Snapshots/SnapshotModels.cs`
- `src/Patchouli.Infrastructure/Snapshots/SnapshotServices.cs`
- `src/Patchouli.Infrastructure/Snapshots/SnapshotBranchInspection.cs`
- `src/Patchouli.UI/ViewModels/Core/SnapshotViewModel.cs`
- `src/Patchouli.UI/ViewModels/Core/SnapshotBranchViewModel.cs`
- `src/Patchouli.UI/ViewModels/MainWindowViewModel.cs`
- `src/Patchouli.UI/MainWindow.axaml`
- `tests/Patchouli.Tests/SnapshotTests.cs`
- `tests/Patchouli.Tests/SnapshotBranchInspectionTests.cs`

## 2. 与其他文档的职责关系

| 文档 | 拥有的职责 | 本任务的消费方式 |
|---|---|---|
| `.agent/task-device-settings-state-and-sync.md` | 默认 JSON owner、稳定 device ID、SyncRootDeviceBinding、随库同步 opt-in、effective resolver 和数据库 setting record | 读取已保存的本机 binding、device identity 和同步范围；启用的非 secret settings 已在 metadata shard 中，发布与接收不创建独立 settings plan。 |
| 本任务 | 库内容与已启用设置的 snapshot transport/lifecycle、Sync 菜单、同步中心、发布/导出/接收、staging 和状态 | 将同一 snapshot 中的内容与已启用设置作为一个整体编排；不使用 settings manifest 或 SC-* 合并。 |
| `.agent/task-unified-conflict-resolution.md` | `IConflictCoordinator`、executor registry、CF-01 到 CF-06 的 executor、状态机和统一模态对话框 | 接收/分支检查后调用 coordinator；UI 不解释 CF action，不直接写 import SQL。 |
| `.agent/task-macos-filesystem-and-app-storage.md` | 平台路径、folder picker、authorization capability 和可移植性规则 | 同步目录与导出目录通过其 device adapter 选择、验证和恢复授权。 |

依赖顺序：设置 task 的本机 JSON owner、binding、device identity 和可同步 setting record 是发布 UI 的硬依赖；真实 incoming 内容冲突处理依赖统一冲突 task 的 coordinator 与 snapshot CF executor。

## 3. 产品信息架构与用户流程

### 3.1 设置页的“同步与快照”分组

该分组只编辑和保存本机 JSON 中的同步配置：

- 当前库的同步目录及其 authorization/availability；
- stable `device_id` 和可编辑的设备名称；
- 最近发布、接收、检查、错误和 remote current 摘要；
- “随库同步”范围与其状态；
- “打开同步中心”“立即发布”“导出快照包”快捷动作。

分组中的目录、身份和 scope draft 使用标准 Save/Discard。发布、导出、接收和冲突解决是操作，单独显示 operation state，不得被 Save/Discard 暗示为可撤销。

### 3.2 Sync 菜单与同步中心

菜单栏新增或补全 `Sync` 菜单，至少有：

- 发布到同步目录；
- 导出快照包；
- 检查/接收同步目录中的 current snapshot；
- 从快照包打开；
- 打开同步中心。

所有入口复用同一 `UiCommandDescriptor` 和同一命令状态。同步中心是独立 workspace tab，显示当前库、同步目录、设备、最新本地/remote snapshot、branch 状态、incoming 摘要、冲突数量、可执行动作和最近错误。现有开发型 `SnapshotViewModel` / `SnapshotBranchViewModel` 应收敛为面向该页面的状态模型，而不是继续暴露手填路径和 JSON 输出。

### 3.3 两种输出

**发布到同步目录**在一个长期复用的 sync root 创建或复用内容寻址 shard、写入 manifest 并更新 `current.json`。只有分片 hash 验证和 current pointer 原子提交均成功才显示 published。未来一次已验证的 snapshot 可复制到多个已配置 sync root，并分别报告各目标结果。

**导出快照包**创建一个可移动目录包，保留 `manifests/<snapshot>.json` 与所有引用 `shards/` 的相对布局，但不写或改动任何 sync root 的 `current.json`。首期只支持目录包，以复用现有 importer；压缩包是后续可替换 adapter，不阻塞本任务。

### 3.4 接收与应用

```text
选择 current 或导出包
  -> 验证 manifest、shard hash、library identity 和路径
  -> 导入 staging
  -> 打开分支检查并构建不可变 plan
  -> 解决 CF-01/CF-02；保留 CF-03 warning
  -> 重新验证 plan 前提
  -> 显式确认并事务性 apply 内容与已启用的 synced base settings
  -> 更新本机 lineage/status，标记 FTS stale
```

未在本机 opt-in 的 incoming 设置不改变 effective runtime settings。已启用的非 secret synced base settings 位于 metadata shard，随接受的 snapshot 与内容在同一 transaction 中应用；JSON local-only 设置、secret、设备 binding 与 device override 不受该操作影响。

## 4. 快照协调模块

新增 `ISnapshotSyncCoordinator` 作为 UI 与现有 publisher/importer/branch-inspection modules 之间的唯一 seam：

```csharp
public interface ISnapshotSyncCoordinator
{
    Task<Result<SnapshotSyncStatus>> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<Result<SnapshotPublishResult>> PublishAsync(CancellationToken cancellationToken = default);
    Task<Result<SnapshotExportResult>> ExportAsync(
        SnapshotExportRequest request,
        CancellationToken cancellationToken = default);
    Task<Result<SnapshotIncomingPlan>> InspectIncomingAsync(
        SnapshotIncomingRequest request,
        CancellationToken cancellationToken = default);
    Task<Result<SnapshotApplyResult>> ApplyAsync(
        SnapshotContentResolutionPlan plan,
        CancellationToken cancellationToken = default);
}
```

该 interface 的 caller 不传 runtime path、staging path、device id、parent id 或手工 manifest 路径；协调模块从设置 lifecycle 读取 binding/identity，从 app paths 取得安全 staging 路径。它负责稳定 root 的 shard 复用、current pointer、hash 验证、staging 清理、plan freshness、状态转换和审计摘要。`ISnapshotPublisher`、`ISnapshotImporter` 与 `ISnapshotBranchInspectionService` 保持为内部可替换 implementation，不向 ViewModel 泄漏细节。

状态至少包括 `not_configured`、`ready`、`validating`、`publishing`、`published`、`exporting`、`checking_incoming`、`inspecting_branch`、`awaiting_content_conflicts`、`applying`、`applied`、`failed`。运行状态不进入 settings SaveState，也不进入 snapshot payload。

## 5. 数据完整性、安全与并发

### 5.1 本机 lineage 与稳定同步目录

`last_published_snapshot_id`、`last_applied_snapshot_id`、`last_seen_remote_snapshot_id`、目录 availability 和最近错误是本机同步状态，随 SyncRootDeviceBinding 保存于 JSON，不进入内容 shard。lineage 仅按 `(library_id, sync_root_id)` 追踪；`device_id` 不参与 snapshot lineage，也不创建协作分支。

本软件不管理多设备协作或网盘客户端的分布式协调。每台设备只显式选择发布本地状态到 stable sync root，或接受 root 中的 remote current。发布必须：

1. 验证目录 binding、设备授权、运行数据库与 sync/staging/cache 路径不重叠；
2. checkpoint、创建或复用内容寻址 shard，验证每个 hash，并原子写 manifest 与 current pointer；
3. 成功后才更新 JSON lineage；失败或取消不更新 lineage；
4. 不在 sync root 创建 `publish.lock`，不定义 expiry、接管或跨设备 parent-mismatch 协议。若要限制同机重复运行，使用本机进程锁。

未来的多目标发布对每个已配置 sync root 独立执行上述发布，并显示每个目标的结果。importer 仍必须拒绝 manifest pointer/shard filename 逃出所选 root、空 shard 列表、不支持 schema 和未验证的 export layout。

### 5.2 内容、设置与 secret

用户启用的非 secret synced base settings 存在运行库数据库中，随正常 metadata shard 发布；不使用独立 settings manifest。接受一个 snapshot 时，内容与这些 settings 同一 transaction 成功或失败。未启用同步的字段、JSON local-only 设置、secret、设备 binding 和 device override 均不受影响。

导出包、内容 shard、日志、branch plan 和 conflict descriptor 不得含 provider/MCP secret、JSON Credentials 数据、device path、authorization payload、缓存、原始文件或 runtime state。内容 shard 的 table allow-list 和 exclusion 必须显式维护；新增本机 sync-state 表或字段也必须显式排除。

## 6. 分支与冲突接入

`BranchImportPlan` 继续只处理库内容 CF-01 到 CF-03。它需要 stable conflict identity、resolution map、incoming manifest fingerprint 和 local content revision；apply 前任何一方变化都使旧 plan/descriptor 为 `superseded`。它不承载独立 settings plan 或设置合并。

本任务必须调用统一 conflict coordinator：

- CF-01：保留本地、导入为新题录或跳过；由 executor 转换 plan，不立即写活动库。
- CF-02：保留本地主文档并导入传入文档作为 secondary，或保留本地主文档且不导入传入文档；由 executor 保证一个 Item 最多一个 primary。
- CF-03：可继续的 warning；只导航至本机凭据设置，不携带 secret。

协调模块只编排对话框与 plan readiness；CF action 的合法性和数据库副作用属于统一冲突 task。设置不使用独立的 SC-* conflict 流程。

## 7. 分阶段实施计划

### 阶段 A：状态和 lifecycle 基线

1. 为同步中心增加 workspace tab kind、data template 和可测试的 ViewModel；移除开发型手填路径入口。
2. 接入设置 task 提供的 binding、device identity、lineage 和安全 staging root。
3. 增加 status 查询、目录验证和 Sync 菜单 command descriptors。

### 阶段 B：真实发布与导出

1. 实现 `ISnapshotSyncCoordinator.PublishAsync`、稳定 sync root 的 shard 复用、原子 current pointer 与 JSON lineage 更新；同机重复运行使用本机进程锁。
2. 为未来多目标发布保留一个快照向多个已配置 sync root 复制的 coordinator seam，并逐目标报告状态。
3. 实现与 importer 兼容的目录导出包；验证它不推进 current pointer。
4. 使用统一 blocking operation/modal 展示进度、取消和恢复动作。

### 阶段 C：接收与分支检查

1. 从 sync current 或 manifest picker 创建 incoming request，校验所有路径和 hash。
2. staging 后展示 branch 内容、影响范围和 warnings，替代 JSON 输出。
3. 创建含 freshness precondition 的内容 import plan，并支持 discard staging 或保留为独立库副本。

### 阶段 D：真实冲突应用

1. 依赖统一冲突 task 的 coordinator、CF-01/CF-02 executor 和 branch-plan resolution map。
2. 将同步中心接入逐项冲突对话框、apply readiness 与 superseded 重新检查。
3. 在单个内容 transaction 中 apply resolved plan，完成后标记 FTS stale 并更新 lineage。

### 阶段 E：设置 snapshot 集成与回归

1. 将 opt-in 的非 secret setting record 放入 metadata shard；未启用时明确显示不发布/不应用设置。
2. 验证接受 snapshot 时内容与已启用设置使用同一 transaction；不提供 settings plan、SC-* 状态或三方合并。
3. 验证秘密、设备 binding、路径和运行状态从所有输出与诊断中排除。

## 8. 验收标准

| 编号 | 条件 | 验证 |
|---|---|---|
| SNAPSYNC-01 | Sync 菜单和同步中心可从产品 UI 打开；不要求用户手填内部 staging/device/path 字段。 | XAML、command descriptor 和 headless UI 测试。 |
| SNAPSYNC-02 | 同步设置分组保存 JSON binding、device identity、scope 和状态；发布/导出不是 Save/Discard 的副作用。 | settings lifecycle/ViewModel 测试。 |
| SNAPSYNC-03 | 发布在 hash 验证与 current pointer 提交均成功后才更新 lineage；失败/取消不显示 published。 | 故障注入与状态机测试。 |
| SNAPSYNC-04 | stable sync root 复用已有同 hash shard，发布原子更新 current pointer；不创建 sync-root publish lock 或协作分支。 | shard-reuse、pointer-commit 与本机进程锁测试。 |
| SNAPSYNC-05 | 导出目录包可在另一设备验证和打开，不修改源或目标 sync root 的 current pointer。 | export/import round-trip 测试。 |
| SNAPSYNC-06 | incoming manifest 在 staging、library identity、hash、schema 和路径 containment 验证后才可检查或 apply。 | 篡改/路径逃逸/错误 schema 测试。 |
| SNAPSYNC-07 | 接收流程显示内容摘要、warning、branch 状态和明确的 discard/keep-copy/apply 动作，不以 JSON 文本作为最终 UI。 | ViewModel/headless UI 测试。 |
| SNAPSYNC-08 | CF-01/CF-02 通过统一 coordinator 解决；未解决 blocking conflict 阻止内容 apply，resolved plan 在单 transaction 中应用。 | coordinator、executor 与 transaction 集成测试。 |
| SNAPSYNC-09 | local/incoming 前提变化使 plan superseded；旧 resolution 不能写入活动库。 | stale plan/CAS 测试。 |
| SNAPSYNC-10 | 未启用随库同步时，发布/接收内容不读写数据库 setting record，也不改变 effective settings。 | settings owner 与 snapshot contract 测试。 |
| SNAPSYNC-11 | 已启用范围的非 secret setting record 与内容一起进入 metadata shard，并与内容在同一 transaction 接受；不存在独立 settings manifest、SC plan 或设置三方合并。 | snapshot apply transaction 与 settings owner 测试。 |
| SNAPSYNC-12 | secret、JSON Credentials 数据、设备路径、authorization payload、原始文件、缓存与 runtime state 不进入 shard、导出包、日志或 conflict payload。 | redaction/serialization/security 测试。 |

## 9. 非目标

- 不实现云账号、远程对象存储 adapter、后台自动轮询或实时双向同步；首期由用户显式发布、检查、接收和导出。
- 不同步原始 PDF、缓存、WAL/SHM、操作系统授权 payload、MCP/Provider secret 或运行状态。
- 不把设置 JSON 默认 owner 改回运行库数据库 owner。
- 不将 JSON local-only 设置、secret、设备 binding 或 runtime state 写入 metadata shard。
- 不在本任务新增 CF code、SC code、平行 conflict dialog 或 ViewModel 直写 import SQL。
- 首期不提供 ZIP；目录导出包是正式且可验证的 interchange format。

## 10. 已确认的实施决策

1. sync root 是稳定复用的仓库：持续保存 manifest、内容寻址 shard 与 current pointer；发布复用同 hash shard。移除 sync-root `publish.lock`、expiry、接管和多设备 parent-mismatch 协议；本机重复运行才使用本机进程锁。
2. 一次性导出仍保留。用户通过系统 file picker 选择位置，生成不修改任何 sync root `current.json` 的自包含目录包。常规同步使用已配置的 stable sync root；未来一次发布可写入多个 root。
3. lineage 仅为 `(library_id, sync_root_id)`，不包含 `device_id`，不区分协作分支。
4. CF-02 仅有“保留本地为主并将传入作为次要”和“保留本地为主且不导入传入”两个动作。
5. 接受远端 snapshot 时，内容与已启用的 non-secret synced base settings 一同在一个 transaction 中应用；JSON local-only 设置、secret、binding 和 override 不受影响。

## 11. 参考依据

- `.agent/PRD.md` 第 3.7、3.8、4.2 和第 5 节：同步 UI、菜单、CF 冲突和安全边界。
- `.agent/task-device-settings-state-and-sync.md`：JSON 默认 owner、opt-in setting record、device override 与 device binding。
- `.agent/task-unified-conflict-resolution.md`：coordinator、executor registry、CF-01 到 CF-06 与 resolution map。
- `.agent/task-macos-filesystem-and-app-storage.md`：平台路径、picker、authorization 与 app storage。
- `.agent/CONTEXT.md`：Snapshot、ProviderCredential、FileAsset、DocumentTreeRevision/DocumentBox 和 EvidenceRef v2 长期边界。Snapshot allow-list 只包含 0.2.0 Box Tree，不得包含 legacy layout 表、MinerU JSON、PDF、缓存、Markdig AST 或 UI SourceMap。
