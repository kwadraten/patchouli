---

```markdown
---
name: grill-me
description: Interview the user relentlessly about a plan or design until reaching shared understanding, resolving each branch of the decision tree. Use when user wants to stress-test a plan, get grilled on their design, or mentions "grill me".
---

Interview me relentlessly about every aspect of this plan until we reach a shared understanding. Walk down each branch of the design tree, resolving dependencies between decisions one-by-one. For each question, provide your recommended answer.

Ask the questions one at a time.

If a question can be answered by exploring the codebase, explore the codebase instead.

需求：开发文献管理程序；书库（数据库和PDF图书）应当能通过类似 Google Drive 的服务直接同步；可以进行题录管理；可以高度自定义的多语言 OCR（使用云端或本地模型），针对原始文献可以使用 HTR 模型（Transkribus 或 ndlkotenocr-lite），针对一般文献使用 MinerU 之类的通用 OCR；强大的全文索引和检索，召回精确到页级别，提供 MCP 供外部使用；OCR 结果持续修正，用户可随时更改边界框和文字。

以下是当前已经达成的决策树状态，请从“问题 116”之后继续，一次只问一个问题，并给出你的推荐答案。
```

# 当前决策树状态

## 1. 产品定位

已确定：

```text
在 A 的基础上实现 C：
- 本体是个人文献管理器，类似 Zotero/Calibre/DEVONthink 的个人书库工具。
- 增强层提供可编程能力，例如 MCP、全文检索、OCR/HTR 管线、外部工具访问。
- 不做团队功能。
```

明确不做：

```text
- 多人协作
- 团队权限
- 审计日志
- 多人冲突编辑
- 机构级后台管理
```

---

## 2. 真相源与数据库原则

已确定：

```text
数据库是唯一真相源。
PDF/图像文件不入库。
文件位置无足轻重，靠 hash 和搜索目录重新定位。
```

数据库保存：

```text
- 题录元数据
- item/work
- file_asset
- document_instance
- OCR/HTR 结果
- layout tree
- bbox
- 用户修正
- revision/delta
- dirty queue
- OCR run
- search unit metadata
- 可选向量
```

数据库不保存：

```text
- PDF 原文
- 图像原文
- 缩略图
- 页图缓存
- 除向量以外的二进制数据
```

---

## 3. 数据库同步与分片

已确定：

```text
程序维护 active working database 和 published snapshot database。
运行数据库不直接放进网盘同步目录。
数据库快照发布到同步目录。
```

运行库：

```text
active/
- 位于应用运行目录
- 可使用 WAL
- 支持频繁写入
- 不被网盘直接同步
```

发布库：

```text
publish/
- checkpoint 后生成
- 不包含 WAL/SHM
- shard 内容寻址命名
- manifest 管理
- 由 Google Drive / OneDrive / Dropbox / Syncthing / NAS / 对象存储等同步
```

已确定分片原则：

```text
- SQLite shards
- 目标 shard 大小：512–768MB
- 硬上限：1GB 以下
- 不能有太多小 shard
- 旧大 shard 尽量不可变
- 修改以新 revision/delta 写入活跃 shard
```

快照同步采用：

```text
内容寻址增量快照：
- 未变化 shard 复用
- 新增/变更 shard 生成新文件
- manifest 记录当前组合
- current 指向最新 snapshot
```

典型结构：

```text
LiteratureDB/
  current.json
  manifests/
    snapshot_000001.json
    snapshot_000002.json
  shards/
    core_000012_abcd.db
    data_0001_hashA.db
    data_0002_hashB.db
    vec_0001_hashC.db
```

同步身份：

```text
library_id
device_id
snapshot_id
parent_snapshot_id
schema_version
shard list
shard hash
logical generation
```

第一版同步冲突策略：

```text
single-writer-by-convention：
- 默认同一时间只有一个设备写入
- 多写冲突时不自动覆盖
- 保留 snapshot branch
- 用户显式选择导入、合并或丢弃
```

---

## 4. PDF 文件管理

已确定：

```text
程序本身不维护 files/ 目录。
PDF 文件由用户自己的目录结构管理。
程序接受一个或多个文件搜索目录。
```

配置示例：

```text
file_search_roots:
- GoogleDrive/MyPDFs
- OneDrive/Research
- ExternalSSD/Books
- NAS/Archive

database_sync_roots:
- GoogleDrive/LiteratureDB
- optional OneDrive/LiteratureDBBackup
- optional S3 bucket
```

文件状态机：

```text
available
moved_candidate
missing
offline_root
conflict
changed
```

规则：

```text
PDF 缺失不等于文献缺失。
题录、OCR、layout、全文索引、MCP 检索仍可用。
只有打开原文、重新渲染页面、重新 OCR 受影响。
```

---

## 5. 文件 hash 与扫描策略

已确定：

```text
去除页面级 fingerprint。
完整文件 hash 只保留一种。
保留 BLAKE3，不保留 SHA-256 作为核心字段。
```

文件身份模型：

```text
file_asset
- file_id
- blake3_full nullable
- quick_hash
- size_bytes
- mtime_hint
- file_name_hint
- mime_type
- page_count nullable
- pdf_trailer_id nullable
- known_locations[]
- hash_status: none | quick_only | full_pending | full_done | failed
```

默认流程：

```text
导入时：
1. 保存路径、文件名、大小、mtime
2. 计算 quick_hash
3. 尝试读取 page_count / pdf_trailer_id
4. 立即可用

后台空闲时：
5. 计算 full BLAKE3
6. 更新 hash_status

重新定位时：
7. 先用 size + quick_hash 匹配
8. 必要时用 full BLAKE3 确认
```

文件扫描策略：

```text
watcher + 轻量启动扫描 + 用户触发深度修复扫描
```

三种扫描：

```text
Light scan:
- 启动时运行
- 检查根目录可用性
- 检查已知文件路径是否存在
- 不做昂贵递归
- 不计算 BLAKE3

Incremental scan:
- watcher 捕获新增/删除/移动
- 新增候选文件计算 quick_hash
- 后台限速

Deep repair scan:
- 用户手动触发
- 递归扫描所有 search roots
- 计算 quick_hash
- 必要时补全 BLAKE3
- 用于找回文件、重建 known_locations、去重
```

---

## 6. 题录模型

已确定：

```text
Zotero-like item + Calibre-like library/file/custom fields + document_instance 承载 OCR。
```

核心三层：

```text
item/work
- 学术题录与引用身份
- Zotero-like metadata
- creators, title, date, publication, identifiers
- tags, collections, custom fields

file_asset
- 文件 hash
- 文件大小、mime、历史路径、当前路径
- 不承载 OCR 语义，只负责定位和校验文件

document_instance
- 某个 item 对应的具体 PDF/扫描本/影印本
- page、OCR、layout tree、全文索引、向量都挂在这里
```

item 拆分原则：

```text
学术引用上有差异的对象，应当拆成不同 item。
同一引用对象的不同文件表现，才属于同一个 item 下的多个 document_instance。
```

应拆为不同 item：

```text
- 分卷，如果引用中有区别
- 不同版本
- 初版/再版/校订版
- 译本
- preprint 与正式出版版，如果引用信息不同
```

可放在同一 item 下：

```text
- 同一版本的不同扫描源
- 高清/低清扫描
- 彩色/黑白扫描
- OCR PDF / 原始扫描 PDF
- 一本书拆成多个 PDF，但引用对象仍是同一本
- 缺页补页文件
```

检索规则：

```text
默认只检索 primary document_instance。
高级搜索可包含 alternate scan、partial file、OCR PDF、deprecated instance，或限定到某个 document_instance。
```

---

## 7. OCR/HTR Profile

已确定：

```text
用户面对的是 OCR/HTR Profile，而不是单个 OCR 引擎。
完全手动选择 profile。
系统不自动推荐 profile。
用户通过题录元数据、标签、collection、语言、文献类型自行决定对哪些文献运行哪个 profile。
```

Profile 作用范围必须支持三层：

```text
document default profile
→ page override profile
→ bbox override profile
```

优先级：

```text
bbox profile override
> page profile override
> document default profile
> collection/tag batch assignment
```

支持任务：

```text
RunProfileOnDocument
RunProfileOnPages
RunProfileOnRegion
```

每次 OCR/HTR run 记录：

```text
scope_type: document | page | bbox
scope_id
profile_id
profile_version_id
engine_id
model_id
parameters
parameters_snapshot
source_revision_id
output_revision_id
```

Profile 版本化：

```text
采用：
OCR/HTR Profile 的参数变化生成 profile_version。
历史 run 绑定具体 profile_version。
profile 本体只保存可见名称和当前版本指针。
```

ocr_profile：

```text
ocr_profile
- profile_id
- name
- current_version_id
- archived
```

ocr_profile_version：

```text
ocr_profile_version
- profile_version_id
- profile_id
- version_number
- engine_id
- model_id
- model_path nullable
- parameters
- apply_on_success
- created_at
```

规则：

```text
- 改 engine / model / parameters / apply_on_success → 新 profile_version
- 改 profile 名称、描述、标签 → 可原地修改 profile
- 历史 run 永远指向当时的 profile_version
- ocr_run 可额外保存 parameters_snapshot，便于审计和调试
```

模型身份记录：

```text
采用：
第一版简化为 model_id + model_path。
model_path 可以是本地路径或 URL。
```

```text
ocr_model_ref
- model_id
- model_path nullable
```

本地模型：

```text
model_path = local filesystem path
```

云端模型：

```text
model_path = provider URL / endpoint / model page URL
```

原则：

```text
第一版不引入 model fingerprint / provider revision / container digest。
先记录足够让用户和系统知道使用了哪个模型、从哪里调用或加载。
后续如需要更强可复现性，再扩展 fingerprint 字段。
```

原则：

```text
OCR/HTR 输出必须可追溯、可解释。
参数原地改写会让历史 run 的来源变模糊。
版本化 profile 保证 provenance 清楚，同时 UI 仍可只显示某个 Profile 的当前版本。
```

修正后的示例 Profile：

```text
Profile: European Historical Manuscript
- material_type: manuscript
- script/language: Latin-script / German / Dutch / English / French / etc.
- primary engine: Transkribus
- granularity: line / word
- preserve original line breaks
- allow manual region and line correction

Profile: Japanese Classical Printed Text
- material_type: printed_book
- script/language: Japanese
- layout_type: vertical / multi-column
- primary engine: NDL古典籍OCR-lite
- fallback/general layout: MinerU if useful
- preserve column, line, char/block structure

Profile: General Academic PDF
- material_type: modern_pdf
- script/language: multilingual
- primary engine: embedded text extraction → MinerU/OCR fallback
```

Profile 第一版不强制声明输出粒度：

```text
OCR/HTR Profile 不强制声明 output_granularity 和 fulltext_search_ready。
运行后根据实际导入结果判断。
```

---

## 8. OCR/HTR run、staging、candidate、采用规则

已确定：

```text
OCR/HTR 结果按页部分保存。
整次 OCR run 可以 completed_with_errors。
```

状态：

```text
ocr_run:
- pending
- running
- completed
- completed_with_errors
- failed
- cancelled

ocr_page_result:
- pending
- processing
- succeeded
- failed
- skipped
- cancelled
```

取消规则：

```text
用户取消 OCR/HTR 任务时，整次 run 回滚。
已完成但属于该 run 的页面结果不保留。
```

实现方式：

```text
- 每页 OCR 可以写入 staging 表或 staging revision
- run 状态仍为 running
- 用户取消时删除该 run 的 staging/临时结果
- 只有 run completed 时，才 promote 为正式或 candidate 结果
```

预览规则：

```text
OCR/HTR 运行中可以预览已完成页面的 staging 结果。
staging 结果不进入正式 layout tree。
不进入全文索引。
不暴露给 MCP。
完成后统一提交。
取消则全部丢弃。
```

完成后采用规则：

```text
由 OCR Profile 配置决定：
apply_on_success = true / false
```

如果 true：

```text
completed
→ promote staging
→ 设置为 current OCR/layout revision
→ 标记相关 search units dirty
→ 局部重建全文索引
```

如果 false：

```text
completed
→ promote as candidate result
→ 不改变 current revision
→ 不进入全文索引
→ 不暴露给 MCP 默认检索
→ 用户手动 adopt 后才生效
```

candidate 采用粒度：

```text
支持整次 run 采用。
支持按页采用。
第一版暂不支持按 bbox 采用 candidate run。
```

采用规则：

```text
adopt candidate run
- 可选择全部页面
- 可选择部分页面
- 被采用页面设置 current OCR/layout revision
- 未采用页面保持原 current revision
- 被采用页面进入 dirty queue
- 仅局部重建这些页面的全文索引
```

---

## 9. OCR 修正、清除与 revision

已确定：

```text
原始 OCR/HTR 输出默认不可变。
用户修正作为 revision/delta 保存。
当前视图由 current revision 指针决定。
```

用户可强制清除 OCR 状态，但默认不物理删除：

```text
Level 1: Unset Current OCR
- 取消 current_revision 指针
- 页面/文献显示为未 OCR
- 不删除历史 revision

Level 2: Hide OCR Run
- 隐藏某个 OCR run
- 不参与 current view、全文索引、MCP 输出
- 原数据仍保留

Level 3: Tombstone OCR Data
- 标记 OCR revision 已删除
- 普通 UI、索引、MCP 不显示
- 保留墓碑处理同步和引用

Level 4: Purge OCR Data
- 物理删除 OCR 文本、layout、向量
- 高级维护操作
```

默认功能：

```text
重置 OCR 状态：
让该页/文献回到未 OCR，但保留历史结果，可恢复。
```

危险功能：

```text
永久清除 OCR 派生数据。
仅高级维护操作。
```

---

## 10. Layout tree / bbox 模型

原先讨论过 MinerU 的双层模型：

```text
canonical layout graph + derived reading-order view
```

但后来为降低第一版实现复杂度，已修正为：

```text
第一版不实现独立 reading-order view。
layout hierarchy 采用可重组树结构。
检索、OCR 编辑、MCP 先直接基于 layout node / search unit 工作。
```

当前第一版模型：

```text
page
└── layout_node tree
    ├── region
    │   └── block
    │       └── line
    │           └── span / char
```

每个 node：

```text
node_id
document_instance_id
page_id
parent_node_id
node_type
bbox
text / own_text
reading_order
source
revision_id
confidence
ignored
```

layout hierarchy：

```text
第一版采用 tree：
- 每个 node 一个 parent
- 允许合并、拆分、移动、重排、改类型
- 暂不做多 parent 的复杂图关系
```

支持操作：

```text
merge nodes
split node
move node under new parent
change node type
change reading_order
adjust bbox
create parent node from selection
detach node
mark node ignored / non-text
```

节点类型采用半开放 ontology：

```text
内置标准类型 + 用户自定义类型 + 映射到标准大类
```

内置类型可包括：

```text
page
region
column
paragraph
line
word
char
table
table_row
table_cell
figure
caption
footnote
marginalia
seal
stamp
annotation
ruby
warichu
header
footer
page_number
unknown
```

用户可自定义：

```text
朱批
夹注
返点
訓点
蔵書印
欄外書入れ
版心
魚尾
```

但自定义类型应映射到 base_type。

---

## 11. bbox overlap 约束

已确定：

```text
bbox 默认不可重叠。
只有特定类型允许重叠，例如 ruby、warichu、annotation、seal、用户自定义允许重叠类型。
```

类型字段：

```text
layout_node_type
- allows_overlap: true / false
- overlap_scope: same_parent | same_page | unrestricted
```

默认不可重叠：

```text
paragraph
block
line
word/span
char within same parent
```

允许重叠：

```text
ruby
warichu
annotation
marginalia
seal/stamp
user-defined type if configured
```

阶段规则：

```text
OCR 导入 / staging 阶段允许 bbox overlap，并标记 conflict。
正式 current layout tree 中，普通 bbox 强制不可重叠。
允许重叠的 node_type 例外。
```

staging adopt 规则：

```text
无冲突页面可 adopt。
存在不允许重叠 conflict 的页面必须先解决或跳过。
不能直接进入 current layout tree。
```

---

## 12. 局部 OCR/HTR 与 current tree

已确定：

```text
一页默认有一个 current layout tree。
如果用户对该页局部 bbox/region 使用其他 OCR/HTR，新识别结果作为 current tree 的一部分，而不是另一套并列 current layer。
```

示例：

```text
page current layout tree
├── 正文 block：来自 MinerU
├── 边注 block：来自 HTR Profile X
├── 表格 block：来自 Table OCR
└── 用户修正 line：来自 manual edit
```

每个 node 可有来源：

```text
layout_node
- node_id
- page_id
- parent_node_id
- node_type
- bbox
- text
- reading_order
- source_type: ocr | htr | manual | imported
- source_run_id
- source_profile_id
- revision_id
```

局部 OCR 流程：

```text
Run OCR on selected bbox
→ generate staging nodes
→ preview
→ adopt
→ insert/replace nodes inside current tree
→ mark affected page/search units dirty
```

adopt mode：

```text
replace / append
默认 replace。
```

replace mode：

```text
- 选区/父节点范围内被替换的旧 nodes
- old nodes: current = false
- old nodes: hidden_from_index = true
- old nodes: superseded_by = new_revision_id
- new nodes 插入 current layout tree
- affected page/search units 标记 dirty
```

append mode：

```text
- 不替换旧 nodes
- new nodes 作为 selected parent/bbox 的子节点加入 current tree
- 用户之后可手动调整 reading_order、node_type、bbox
```

关于 replace 判定：

```text
局部 OCR replace 第一版倾向只替换用户显式选中的节点。
未来可增加 bbox overlap 候选替换，但必须预览确认。
```

---

## 13. layout node 文本继承

已确定：

```text
采用 C：
每个节点可保存 own_text，但父节点必须通过 text_policy 声明文本来源。
```

layout_node 增加：

```text
layout_node
- node_id
- parent_node_id
- node_type
- bbox
- own_text nullable
- text_policy:
  - own
  - aggregate_children
  - none
- reading_order
- index_policy
```

text_policy 规则：

```text
text_policy = own
→ 使用该节点 own_text

text_policy = aggregate_children
→ 按子节点 reading_order 拼接文本

text_policy = none
→ 该节点没有文本，仅作结构容器
```

默认示例：

```text
region / column:
- text_policy = aggregate_children 或 none
- index_policy = ignore

line:
- text_policy = own
- index_policy = self

paragraph:
- 如果 OCR 直接给 paragraph text：text_policy = own
- 如果用户从 lines 创建 paragraph：text_policy = aggregate_children
```

原则：

```text
resolved_text 由 text_policy 计算。
own_text 可以存在，但不意味着一定进入全文索引。
全文索引由 index_policy / search unit 生成规则决定。
必须避免父子文本重复进入同一默认索引视图。
```

````

---

## 14. 全文索引

已确定：

```text
全文索引是本地可重建缓存，不是同步数据库的一部分。
数据库保存 canonical layout/text/revision。
索引可从数据库重建。
````

但第一版不做独立 reading-order view，因此：

```text
全文索引直接从 layout tree 生成 search units。
```

默认索引策略：

```text
采用：
类型默认值 + 节点级覆盖。
```

类型默认值：

```text
layout_node_type.default_index_policy:
- container
- self
- ignore
- ignore_subtree
```

节点级覆盖：

```text
layout_node.index_policy_override nullable:
- container
- self
- ignore
- ignore_subtree
```

语义：

```text
container
→ 当前节点不建 search unit，但继续检查子节点。

self
→ 当前节点建 search unit；默认不再索引子孙，避免父子重复。

ignore
→ 当前节点不建 search unit，但子节点仍可检索。

ignore_subtree
→ 当前节点与全部子孙都不进入默认全文索引。
```

默认类型映射：

```text
优先索引：
- paragraph
- block
- line

默认不索引：
- char
- page
- region
- column
- ignored node

特殊节点按配置：
- table_cell
- marginalia
- annotation
- footnote
- seal
- caption
```

这些类型默认对应：

```text
page: container
region: container
column: container
paragraph: self
block: self
line: self
word/span: ignore
char: ignore
table: container
table_row: container
table_cell: self
figure: ignore_subtree 或 container，按类型配置
caption: self
footnote: self
marginalia: self 或 ignore，按类型配置
annotation: self 或 ignore，按类型配置
seal/stamp: self 或 ignore，按类型配置
ignored node: ignore_subtree
```

关键约束：

```text
避免父子重复索引。
若 paragraph 已作为 search unit，则其子 line 默认不重复进入同一索引视图。
若只有 line/char，则可聚合为较大 search unit，但第一版不自动聚合。
用户可以用 index_policy_override 将某个特殊节点纳入或排除默认索引。
```

OCR/HTR 导入后：

```text
不自动聚合。
模型输出什么层级，就保存什么层级。
用户需要更大单位时，手动重组 layout tree。
```

char-only OCR：

```text
第一版要求可检索 OCR 至少具备 line-level 节点。
char-only OCR/HTR 结果可以保存、查看、编辑，但不保证良好全文检索。
```

search unit 保存策略：

```text
采用：
search_unit 持久化为数据库中的派生表，但可从 canonical layout tree / revision 重建。
```

search_unit 示例字段：

```text
search_unit
- unit_id
- document_instance_id
- page_id
- root_node_id
- text_revision_id
- bbox_revision_id
- resolved_text
- bbox_union
- node_type
- reading_order
- status: current | dirty | stale | deleted
```

规则：

```text
layout tree / text revision 是 canonical。
search_unit 是 derived artifact。
search_unit 写入数据库，随 snapshot 同步。
本地 FTS index 仍然是可重建缓存，不随 snapshot 同步。
```

理由：

```text
MCP、搜索结果、页面级召回、bbox evidence 都需要稳定 unit_id 和可追踪 revision。
每次临时从 layout tree 生成会让结果 ID、上下文、dirty queue 和增量索引变得不稳定。
持久化 search_unit 但标记为派生物，可以兼顾稳定引用与可重建性。
```

search unit identity：

```text
采用：
稳定身份优先，结构性变化才生成新的 unit_id。
```

unit_id 尽量延续：

```text
- 修改文字
- 调整 bbox
- 修改 node_type
- 修改 reading_order
- 小范围移动节点位置
```

这些情况生成新的 unit_id：

```text
- 一个 unit 被 split 成多个 unit
- 多个 unit 被 merge 成一个 unit
- root_node_id 被替换为完全不同的节点
- 局部 OCR replace 明确替换了原 search unit
- 用户删除并重建该结构
```

版本与继承字段：

```text
search_unit_identity
- unit_id 稳定代表“这个可检索内容单位”
- text_revision_id 表示文字版本变化
- bbox_revision_id 表示位置版本变化
- layout_revision_id 表示结构版本变化
- supersedes_unit_id nullable
- superseded_by_unit_id nullable
```

原则：

```text
普通 OCR 修正、bbox 微调、类型调整不破坏 MCP 和搜索结果引用。
split / merge / replace / delete-recreate 属于语义结构变化，生成新 unit_id。
新旧 search_unit 通过 supersedes / superseded_by 保留可追踪继承关系。
```

搜索结果与 MCP evidence reference：

```text
采用：
搜索结果和 MCP 返回稳定 evidence_ref，同时可附带短期 result_id。
```

稳定引用：

```text
EvidenceReference
- library_id
- document_instance_id
- page_id
- unit_id
- text_revision_id
- bbox_revision_id
- layout_revision_id
- snapshot_id nullable
```

短期 UI 引用：

```text
result_id
- 只在当前搜索结果会话内有效
- 用于 UI 展开、翻页、临时缓存
- 不作为长期引用
```

规则：

```text
MCP 和外部工具主要使用 evidence_ref。
result_id 可以作为 UI 便利字段返回，但不能要求外部工具持久保存。
evidence_ref 指向某个 snapshot / revision 下的文本与 bbox。
当 OCR 修正或 layout 变更后，可以判断旧 evidence 是否仍 current、已 stale、或已被 superseded。
```

evidence_ref 解析规则：

```text
采用：
默认严格解析引用中的历史 revision；可显式请求 current-follow。
```

resolve_mode：

```text
- pinned
- current
- compare
```

默认 pinned：

```text
pinned
→ 返回 evidence_ref 指定的 text_revision / bbox_revision / layout_revision。
→ 如果 revision 已 tombstone 或 purge，则返回 unavailable + reason。
```

可选 current：

```text
current
→ 根据 unit_id 找当前最新 search_unit / revision。
→ 如果 unit 已 superseded，返回 successor chain 和当前候选。
```

可选 compare：

```text
compare
→ 同时返回 pinned evidence 与 current evidence。
→ 标注 text_changed / bbox_changed / layout_changed / superseded。
```

原则：

```text
证据引用默认必须可验证、可复现。
外部工具保存的 evidence_ref 不应在 OCR 修正后静默指向不同文本。
需要当前版本时，调用方必须显式请求 current 或 compare。
```

页面级读取 API 版本模式：

```text
采用：
get_page_text / get_page_blocks 支持 read_mode，但默认 current。
```

read_mode：

```text
- current
- pinned
- compare
```

页面级 API 默认：

```text
get_page_text(document_instance_id, page_id)
get_page_blocks(document_instance_id, page_id)

默认 read_mode = current
→ 返回该页当前 layout-derived text / blocks。
```

证据回查：

```text
get_page_text(evidence_ref, read_mode = pinned)
get_page_blocks(evidence_ref, read_mode = pinned)

→ 返回 evidence_ref 指定 revision 所在上下文中的页面文本 / blocks。
```

compare：

```text
read_mode = compare
→ 同时返回 pinned page view 与 current page view。
→ 标注 page_text_changed / block_changed / bbox_changed / unit_superseded。
```

原则：

```text
日常页面读取默认 current，符合“查看当前书库状态”的直觉。
从搜索结果或 MCP evidence 回查时必须支持 pinned，保证证据可复现。
get_search_result_context 可以内部调用 pinned page/block 读取。
```

page text 输出形态：

```text
采用：
get_page_text 默认返回 plain text。
当 agent / 外部工具需要确认页面结构、bbox、OCR 边界或页面本身是否有误时，再显式请求结构化结果。
```

默认：

```text
get_page_text(...)
→ 返回全页 layout-derived plain_text
→ 适合复制、LLM 上下文、快速阅读、普通 MCP 调用
```

结构化请求：

```text
get_page_text(..., format = structured)
或
get_page_blocks(...)

→ 返回 blocks / search_units / bbox / evidence_ref
→ 用于核对版面、定位 OCR 错误、检查阅读顺序、确认页面结构
```

可选 format：

```text
- plain
- structured
- markdownish
```

PageTextPlainResult：

```text
PageTextPlainResult
- page_id
- page_label
- read_mode
- text_revision_id
- layout_revision_id
- plain_text
- evidence_refs[] optional
```

PageTextStructuredResult：

```text
PageTextStructuredResult
- page_id
- page_label
- read_mode
- text_revision_id
- layout_revision_id
- plain_text optional
- blocks[]
  - unit_id
  - node_id
  - node_type
  - text
  - bbox
  - reading_order
  - evidence_ref
```

原则：

```text
默认响应轻量，降低 agent 和 MCP 的上下文成本。
结构化信息按需显式请求，不在普通页面读取中强行返回。
需要验证证据、OCR 边界、页面结构时，调用方应请求 structured 或 get_page_blocks。
```

plain_text 拼接规则：

```text
采用：
按 reading_order 拼接主文本，特殊内容用轻量标记分隔。
```

默认 plain_text：

```text
- 按 page 内 search_unit.reading_order 拼接
- 同一段/连续正文 unit 用单换行
- 段落、区块、栏切换用空行
- 页眉、页脚、页码默认不进入 plain_text
- ignored / ignore_subtree 不进入 plain_text
```

特殊内容：

```text
footnote
→ 放在正文后，前置 [Footnotes]

marginalia / annotation
→ 默认不混入正文
→ 可用 include_annotations = true 显式包含

table
→ 默认转成 Markdown 表格文本
→ 结构化表格需 get_page_blocks(format = structured)

caption
→ 跟随 figure/table 附近的 reading_order 输出
```

原则：

```text
默认 plain_text 优先适合阅读、复制和 LLM 上下文。
不把页眉、页脚、页码、边注、批注默认搅进正文。
需要严格版面判断、表格结构、bbox 或边注关系时，请求 structured。
```

表格模型：

```text
采用：
第一版只用 layout_node tree 表达表格，不引入独立 table model。
```

结构：

```text
table
└── table_row
    └── table_cell
        └── line / paragraph
```

table_cell 增加少量属性：

```text
table_cell
- row_index nullable
- col_index nullable
- row_span default 1
- col_span default 1
- is_header nullable
```

规则：

```text
- 表格仍属于 layout tree
- table_cell 可以作为 search unit
- plain_text 默认输出 Markdown 表格文本
- structured 输出 table/table_row/table_cell/bbox
- 不在第一版支持公式、复杂样式、嵌套表格语义模型
```

理由：

```text
表格 OCR 第一版容易不稳定。
独立 table model 会显著增加编辑、同步、revision、MCP 的复杂度。
先把表格作为 layout tree 的一种结构节点，足够支持检索、定位和人工修正。
后续需要 CSV/DataFrame 时，可从 table nodes 派生。
```

Markdown 表格降级规则：

```text
采用：
优先尽量输出 Markdown 表格；不可靠时降级为标记块文本。
```

可输出 Markdown 表格的条件：

```text
- row/col 索引基本完整
- 每行列数可归一
- row_span / col_span 不复杂，或可安全展开
```

降级规则：

```text
轻微缺格
→ 用空单元格补齐

合并单元格
→ 第一格保留文本
→ 被覆盖单元格留空
→ structured 中保留 row_span / col_span

严重不规则
→ 不强行伪造成 Markdown 表格
→ 输出标记块：

[Table]
row 1: ...
row 2: ...
row 3: ...
[/Table]
```

原则：

```text
Markdown 表格优先服务阅读和 LLM 上下文。
不能为了格式漂亮而伪造确定的行列关系。
当 plain_text 无法可靠表达表格结构时，应提示调用方请求 structured 查看 bbox 和 cell 结构。
```

文件解析 / 定位 API：

```text
采用：
提供统一 File Resolution API。
```

接口：

```text
resolve_file(file_asset_id, purpose)
```

purpose：

```text
- open_original
- render_page
- run_ocr
- verify_hash
```

返回：

```text
FileResolutionResult
- file_asset_id
- status:
  - available
  - missing
  - offline_root
  - moved_candidate
  - conflict
  - changed
- resolved_path nullable
- candidates[]
- confidence
- required_action nullable
```

规则：

```text
- UI、OCR、页面渲染、MCP 状态读取都走同一个 resolution service
- 不直接信任旧路径
- 先检查 known_locations
- 再用 size + quick_hash 匹配候选
- 必要时用 BLAKE3 full hash 确认
- conflict / changed 不自动打开，要求用户确认
```

原则：

```text
文件不入库、路径可变是核心原则。
resolve_file 是所有“需要原始文件”操作的统一入口。
题录、OCR、layout、全文检索和 MCP evidence 在文件 missing 时仍可用。
只有打开原文、渲染页面、重新 OCR 等依赖原始文件的操作需要 file resolution。
```

MCP 文件能力边界：

```text
采用：
第一版 MCP 只暴露文件状态，不返回本机路径，不提供 open_original。
```

MCP 提供：

```text
- get_document_status
- file_status
- missing / offline_root / conflict / changed 状态
- 是否可打开原文
- 是否可渲染页面
- 是否可重新 OCR
```

MCP 不提供：

```text
- resolved_path
- local filesystem path
- open_original
- file:// URL
- 自动触发文件扫描或修复
```

原则：

```text
MCP 面向外部工具，本机路径会泄露用户目录结构、云盘路径和资料组织方式。
第一版 MCP 定位为检索与证据读取，不做写入和动作触发。
打开原文留在桌面 UI 内，由用户显式操作。
```

页面渲染与 OCR 中间图缓存：

```text
采用：
页面渲染图像、缩略图、OCR 中间页图作为本地可重建缓存保存，但不随 snapshot 同步。
```

本地缓存目录：

```text
cache/
- page_renders/
- thumbnails/
- ocr_intermediate_images/
- overlays/
```

规则：

```text
- 不写入主数据库 shard
- 不进入 published snapshot
- 不由 Google Drive / OneDrive 等同步
- 可随时删除
- 原始文件可用时可重建
- 原始文件 missing / offline 时缓存可继续用于 UI 预览，但标记 stale_possible
```

数据库只保存缓存元数据：

```text
render_cache_entry
- cache_key
- document_instance_id
- page_id
- file_asset_id
- file_hash_hint
- render_profile
- source_revision_id
- status
- created_at
```

原则：

```text
页图、缩略图和 OCR 中间图体积大、变化频繁，不适合进入 snapshot。
它们是派生缓存，不是真相源。
保留本地缓存可以提升 UI 性能，并在原文件暂时离线时继续显示旧预览。
```

离线原文件与缓存图证据规则：

```text
采用：
UI 可以把旧 page render cache 作为预览显示。
MCP 第一版只接受文本 / 结构化文本证据，不返回缓存图像作为证据。
```

UI：

```text
- 可以显示旧 page render cache
- 必须标记 original_file_missing / offline_root / stale_possible
- 不能把缓存图显示成已验证原文
```

MCP：

```text
- 不返回缓存图像
- 不返回缓存图路径
- 不返回本机文件路径
- 不返回 file:// URL
- 不把缓存图像作为 evidence
- 只返回 OCR/layout/text/bbox 等数据库中的文本与结构化文本证据
- 在 document_status 中标记 source_file unavailable
```

原则：

```text
缓存图像是本地派生物，不随 snapshot 同步，不能作为可复现证据交给外部工具。
UI 预览用于帮助用户定位材料，但必须明确标注原文件不可验证。
MCP 第一版保持 text-only evidence surface。
```

MCP bbox evidence text-only 表达：

```text
采用：
只返回 bbox，不做额外可读定位描述或页面区域推断。
```

MCP 返回：

```text
BBoxEvidence
- page_id
- page_label
- bbox
- bbox_revision_id
```

MCP 不返回：

```text
- page_region_hint
- top / middle / bottom
- left / center / right
- paragraph N / line N 等自然语言定位描述
- 自动生成的位置解释
```

原则：

```text
MCP 保持机械、可验证、低加工的 evidence surface。
调用方如果需要解释 bbox 位置，应自行基于 structured blocks 或外部渲染能力处理。
系统不在 MCP 层对 bbox 做额外语义化描述。
```

bbox 坐标系：

```text
采用：
canonical bbox 使用页面归一化坐标 0..1。
原始 OCR/渲染引擎坐标只作为 debug/source metadata 可选保存。
```

canonical bbox：

```text
bbox
- x
- y
- width
- height
- coordinate_space: normalized_page
```

规则：

```text
- x / y / width / height 均为 0..1
- 原点为页面左上角
- x 向右增大
- y 向下增大
- bbox 相对于当前 page 的 crop/visible page box
```

可选保留：

```text
source_bbox
- engine_id
- coordinate_space: pixel | pdf_point | engine_specific
- raw_bbox
- source_image_width nullable
- source_image_height nullable
```

原则：

```text
MCP、索引、layout 编辑和不同 OCR 引擎之间使用统一 canonical bbox。
归一化坐标不依赖 DPI、渲染 profile 或特定 OCR 引擎。
原始像素 / PDF point 坐标可保留作调试，但不作为 canonical。
```

normalized_page 基准：

```text
采用：
视口优先。
normalized_page 相对于用户实际可见 / 渲染的页面框归一化。
```

page_coordinate_basis：

```text
- viewport_box
- crop_box
- media_box
- image_bounds
```

规则：

```text
PDF：
- 优先使用实际渲染/显示采用的 viewport_box。
- 如果 viewport_box 等同于 CropBox，则记录 crop_box。
- 如果无 CropBox 或无明确 viewport_box，则退到 MediaBox。
- 记录 page_width / page_height / rotation / coordinate_basis。

image：
- normalized_page = image_bounds。
- 记录 image_width / image_height / coordinate_basis。

render：
- 渲染器必须按同一 page_coordinate_basis 映射 bbox overlay。
- UI 显示、bbox 编辑、OCR overlay、MCP bbox 均使用同一 canonical basis。
```

原则：

```text
用户看到什么页面区域，bbox 就优先相对于什么区域表达。
CropBox 通常最接近 PDF 的可见页面区域，但不硬编码为唯一基准。
必须记录 coordinate_basis，避免不同渲染器或 OCR adapter 对页面边界理解不一致。
```

页面 rotation 处理：

```text
采用：
canonical bbox 写入前统一归正到用户可见方向。
原始 rotation 只作为 page metadata / source metadata 保留。
```

canonical page orientation：

```text
upright_view
```

规则：

```text
- canonical bbox 永远对应用户正常看到的页面方向
- PDF / image 原始 rotation 记录在 page metadata
- OCR adapter 导入时负责把 engine bbox 转到 canonical orientation
- render overlay 时不再临时猜测 rotation，只按 canonical bbox 映射
```

保存：

```text
page
- rotation_degrees_original
- rotation_degrees_applied
- coordinate_basis
- canonical_orientation: upright_view
```

原则：

```text
MCP、UI 编辑、搜索结果高亮、局部 OCR 都使用 canonical orientation。
source_bbox 可保留原始引擎坐标与原始 rotation 信息。
canonical 层统一归正，避免每个消费者重复做旋转转换。
```

OCR/HTR bbox 转换失败处理：

```text
采用：
bbox 坐标转换失败时，拒绝整页导入。
用户需要修复 PDF / 图像源文件、page box、rotation 或渲染配置后重跑 OCR/HTR。
```

规则：

```text
- 不导入该页文本
- 不导入该页 layout tree
- 不生成 search_unit
- 不进入全文索引
- 不暴露给 MCP
- 保留失败状态与错误原因
```

状态：

```text
ocr_page_result = failed
failure_code = bbox_coordinate_transform_failed
failure_scope = page
required_action = fix_source_file_or_render_basis
```

适用情况：

```text
- 无法确定 page_coordinate_basis
- 无法确定 canonical orientation
- OCR adapter 输出坐标无法映射到 normalized_page
- bbox 大量越界或坐标系统明显不一致
```

原则：

```text
第一版不接受“有文本但无可信 bbox”的 OCR 页面进入正式库。
OCR 文本、layout、bbox evidence 应保持同一页面坐标系统下的可验证一致性。
如果源文件或渲染基准坏了，应让用户修复源文件或配置后重跑，而不是混入半坏数据。
```

OCR run 部分页面失败处理：

```text
采用：
部分页面因为 bbox_coordinate_transform_failed 被拒绝时，整次 OCR run 为 completed_with_errors。
保留成功页，失败页不导入。
```

规则：

```text
- 成功页正常 promote / candidate
- 失败页保持 failed
- 失败页不导入文本、layout、search_unit
- run summary 列出 failed pages、failure_code、required_action
- 用户修复源文件或渲染基准后可只重跑失败页
```

状态示例：

```text
ocr_run = completed_with_errors

ocr_page_result:
- page 1: succeeded
- page 2: failed, bbox_coordinate_transform_failed
- page 3: succeeded
```

原则：

```text
OCR 结果按页部分保存。
有效页面不应因为少数坏页而被丢弃。
失败页面必须硬拒绝，不能混入有文本但无可信 bbox/layout 的半坏数据。
```

OCR retry run：

```text
采用：
用户修复源文件后重跑失败页时，创建新的 retry run，并关联原 run。
```

ocr_run 增加：

```text
ocr_run
- run_id
- retry_of_run_id nullable
- retry_scope_pages[]
- reason: retry_failed_pages
```

规则：

```text
- 原 run 保持 completed_with_errors，不被改写
- 新 retry run 只跑失败页或用户选择的页面
- retry 成功后按正常 adopt / promote 规则进入 current 或 candidate
- run history 能看到原失败原因和后续修复结果
```

原则：

```text
原 run 是历史事实，不应被补写成“从没失败过”。
retry run 有独立 provenance，符合 revision / snapshot 的不可变思路。
用户可以清楚看到哪次 OCR 失败、何时修复、修复后生成了哪些 revision。
```

OCR retry run 采用策略：

```text
采用：
retry run 成功后，按 retry run 自己记录的 apply_on_success 决定。
不因为原 run 的其他页面已经 promote 为 current 就自动补入 current。
```

如果 true：

```text
retry_run.apply_on_success = true
→ 成功页 promote 为 current
→ 标记对应 search_units dirty
→ 局部重建索引
```

如果 false：

```text
retry_run.apply_on_success = false
→ 成功页作为 candidate
→ 用户手动 adopt 后才进入 current
```

UI 默认：

```text
UI 可以默认继承原 run 的 profile / apply_on_success 设置。
但真正执行时，以 retry run 记录下来的配置为准。
```

原则：

```text
retry run 是独立 provenance，应有自己的采用策略。
UI 可以帮助用户沿用原设置，但数据模型不能隐式依赖原 run 状态。
```

本地模型 path 失效处理：

```text
采用：
本地模型 path 失效时阻止运行，并允许用户重新绑定 model_path。
重新绑定 model_path 后生成新的 profile_version。
```

model_path_status：

```text
- available
- missing
- inaccessible
- unknown
```

规则：

```text
- 运行前检查本地 model_path
- missing / inaccessible 时不启动 OCR/HTR
- UI 提示用户重新绑定模型路径
- 重新绑定 model_path 后生成新的 profile_version
- 历史 profile_version 保留原 model_path，不改写
```

原则：

```text
模型路径失效时继续运行会得到不可控结果。
用户换机器或同步配置后重新绑定模型路径是常见恢复动作。
因为 model_path 是 profile_version 的一部分，重新绑定必须产生新版本。
```

云端模型 / provider 失败处理：

```text
采用：
云端模型 URL / endpoint 失效或鉴权失败时阻止运行，要求用户修复 provider 配置。
不自动降级到其他模型。
```

cloud_model_status：

```text
- available
- auth_failed
- endpoint_unreachable
- quota_exceeded
- model_not_found
- unknown
```

规则：

```text
- 运行前或运行时检测 cloud provider 状态
- auth_failed / model_not_found / endpoint_unreachable 时标记 run failed
- quota_exceeded 可标记 retryable_failed
- 不自动换模型
- 不自动改 profile_version
- 用户修复 API key / endpoint / model_path 后重新运行
```

原则：

```text
OCR/HTR 结果和模型强相关，自动降级会污染 provenance。
云端问题应清楚失败，让用户修复配置或显式选择另一个 profile / profile_version。
系统不隐式替用户更换 OCR/HTR 模型。
```

OCR/HTR 自动重试策略：

```text
采用：
只自动重试 transient failure。
source / config / model 类失败必须等待用户修复后手动重跑。
```

可自动重试：

```text
auto_retry:
- network_timeout
- temporary_provider_error
- rate_limited
- quota_exceeded if provider says retry_after
- worker_crashed
```

必须用户修复：

```text
manual_fix_required:
- auth_failed
- model_not_found
- endpoint_unreachable due bad config
- model_path missing / inaccessible
- source_file missing / changed / conflict
- bbox_coordinate_transform_failed
- unsupported_file
- invalid_page_box
```

自动重试规则：

```text
- max_attempts: 3
- exponential backoff
- respect provider retry_after
- retry 仍失败后标记 retryable_failed
```

原则：

```text
网络和临时服务错误适合自动重试。
源文件、模型路径、鉴权、bbox 坐标系统这类问题重试只会空转。
确定性失败应明确提示用户修复，而不是由队列反复尝试。
```

OCR/HTR 队列并发限制：

```text
采用：
OCR/HTR 队列支持全局并发限制 + 按 provider / engine / profile 的并发限制。
```

ocr_queue_limits：

```text
ocr_queue_limits
- global_max_concurrent
- local_max_concurrent
- cloud_max_concurrent
- per_provider_max_concurrent
- per_engine_max_concurrent
- per_profile_max_concurrent
```

默认建议：

```text
global_max_concurrent = 2
local_max_concurrent = 1
cloud_max_concurrent = 2
per_provider_max_concurrent = 1 或按 provider 配额配置
```

规则：

```text
- 本地模型默认保守，避免打满 CPU / GPU / 内存
- 云端模型遵守 provider rate limit / quota
- 用户可在设置中调整
- 队列 UI 显示 waiting_due_to_limit
```

原则：

```text
OCR/HTR 是重资源任务，云端调用还涉及配额和费用。
并发限制是一等配置，不能只靠后台任务自然排队。
批量 OCR 应可控，避免系统卡死或 API 费用失控。
```

OCR/HTR 队列优先级：

```text
采用：
OCR/HTR 队列支持 priority + aging。
交互任务优先，但避免批量任务长期饿死。
```

priority：

```text
- interactive_current_page
- interactive_selected_pages
- user_started_document
- batch_collection
- background_retry
- maintenance
```

默认排序：

```text
interactive_current_page
> interactive_selected_pages
> user_started_document
> background_retry
> batch_collection
> maintenance
```

规则：

```text
- 用户当前操作触发的任务优先
- 批量任务低优先级
- retry 不高于用户当前操作
- aging 让等待太久的低优先级任务逐步提升
- 队列 UI 允许用户手动置顶 / 暂停 / 取消
```

原则：

```text
交互任务要快，批量任务要稳。
priority + aging 兼顾体感和公平性。
后台批量 OCR 不应阻塞用户当前页面修正和局部 OCR。
```

OCR/HTR 队列暂停粒度：

```text
采用：
支持全局、本地 / 云端、provider、单任务暂停。
第一版不做 profile 级暂停。
```

pause_scope：

```text
- global
- local
- cloud
- provider
- task
```

规则：

```text
- global pause：暂停所有未开始任务
- local pause：暂停本地模型任务
- cloud pause：暂停云端模型任务，便于控制费用
- provider pause：暂停某个 provider，例如 Transkribus
- task pause：暂停单个排队任务
- running 任务默认不中断，除非用户明确 cancel
```

暂不做：

```text
- profile-level pause
```

原则：

```text
pause 只影响尚未开始的任务。
cancel 才表示中断 running 任务，并触发既定的 run 回滚规则。
暂停粒度覆盖省钱、释放本地资源、避开 provider 故障、临时搁置单个任务等常见场景。
```

OCR/HTR 队列恢复调度：

```text
采用：
恢复后重新按 priority + aging 计算调度顺序。
```

resume：

```text
- 清除对应 pause_scope
- 保留任务 created_at / queued_at / priority
- 重新计算 effective_priority
- 按 effective_priority 调度
```

规则：

```text
- 不简单恢复旧 FIFO 顺序
- aging 继续计算等待时间
- 用户手动置顶的任务仍保持高优先级
- 已 running 的任务不受影响
```

原则：

```text
恢复后环境可能已变化，例如有新的交互任务、更久等待的批量任务或 provider 限制。
重新计算调度顺序更符合用户当下意图。
保留 created_at / queued_at 可避免低优先级任务失去等待时间积累。
```

云端 OCR/HTR 启动确认：

```text
采用：
云端 OCR/HTR 任务启动前不需要成本 / 页数 / 调用量预估确认。
```

规则：

```text
- 用户启动云端 OCR/HTR 后直接进入队列
- 不因预计页数、调用量或成本而额外弹窗确认
- 队列 UI 可显示 document_count / page_count / provider / profile 等状态信息
- 不实现 estimated_cost / warning threshold / budget confirmation
```

原则：

```text
第一版保持 OCR/HTR 启动流程轻量。
用户选择云端 profile 本身即表示接受该 provider 的调用行为。
费用、配额和 provider 使用边界先由用户自行在 provider 配置和队列暂停中管理。
```

云端 OCR/HTR 隐私/费用提示：

```text
采用：
只通过 provider 类型和 provider 名称显示，不做额外 privacy / cost warning 或确认流程。
```

provider_type：

```text
- local
- cloud
```

规则：

```text
- Profile / 队列 UI 显示 provider_type
- cloud provider 可显示 provider 名称
- 不弹出 privacy / cost warning
- 不要求用户逐次确认
- 不增加额外启动确认
```

原则：

```text
用户选择 cloud profile 本身即表示接受该 provider 的调用行为。
第一版保持云端 OCR/HTR 使用路径轻量。
费用、隐私、配额管理暂不做额外确认层。
```

云端 provider 密钥同步：

```text
采用：
云端 provider 的 API key / token 等敏感配置进入同步 snapshot。
如果密钥泄露，视为用户对其云同步服务、设备安全或库同步目录的使用风险。
```

provider_config：

```text
provider_config
- provider_id
- provider_name
- provider_type
- endpoint_url nullable
- model_id defaults
- api_key / token / credential material
- credential_status
```

规则：

```text
- API key / token 可写入 SQLite shard
- API key / token 可进入 published snapshot
- API key / token 可随 Google Drive / OneDrive / Syncthing 等同步
- 多设备打开同一 library 后可直接复用 provider 配置
- UI 不强制要求每台设备单独配置 credential
```

原则：

```text
library snapshot 是用户选择同步的完整工作库状态。
用户需要自行信任其同步服务、设备和同步目录访问控制。
第一版不额外引入本机 keychain / credential manager 作为 provider 密钥真相源。
```

provider 密钥存储形式：

```text
采用：
第一版不做库级加密，provider 密钥明文保存。
```

provider credential：

```text
- stored in SQLite shard
- included in snapshot
- synced as part of library state
- no app-level encryption in v1
```

规则：

```text
- 不引入主密码
- 不实现库级加密
- 不实现每设备 credential unwrap
- 不实现密钥轮换机制
```

原则：

```text
第一版目标是同步后直接可用。
库级加密会引入主密码、恢复、换设备、忘记密码、密钥轮换等复杂问题。
安全边界交给用户的设备、云盘、同步目录和文件权限。
```

MCP provider 密钥边界：

```text
采用：
MCP 不可读取 provider_config 中的 API key / token。
provider 密钥只供 OCR/HTR adapter 内部使用。
```

MCP 不提供：

```text
- get_provider_secret
- api_key
- token
- credential material
```

MCP 可提供：

```text
provider_status:
- provider_id
- provider_name
- provider_type
- credential_status
```

原则：

```text
密钥进入 snapshot 是同步模型选择，但不代表外部工具可以读取密钥。
MCP 第一版只做检索与证据读取，不暴露 credential material。
同步便利和外部工具权限边界分开处理。
```

MCP provider 状态可见性：

```text
采用：
MCP 不暴露 provider 配置详情。
只在 get_document_status 中返回 OCR 可用性摘要。
```

get_document_status：

```text
- has_ocr_text: true | false
```

MCP 不返回：

```text
- provider_id
- provider_name
- endpoint_url
- credential_status
- api_key
- token
- credential material
```

原则：

```text
MCP 第一版不触发 OCR，也不管理 provider。
外部工具只需要知道当前文档是否已有 OCR、是否可检索、是否缺源文件。
provider 配置属于桌面应用内部设置，不进入 MCP evidence surface。
```

MCP OCR 状态字段：

```text
采用：
get_document_status 使用 has_ocr_text。
不使用 ocr_available。
```

语义：

```text
has_ocr_text
→ 当前文档是否已有可读取的 OCR/HTR 文本。
```

规则：

```text
- has_ocr_text 不表示当前能否运行 OCR
- has_ocr_text 不暴露 provider 配置或 credential 状态
- MCP 不提供 run_ocr，因此不表达 OCR runnable capability
```

原则：

```text
MCP 状态围绕“可读取 / 可检索证据”表达。
不表达 provider 是否可运行、API key 是否有效、模型是否可调用。
ocr_available 容易歧义，因此不用。
```

MCP document status 最小字段：

```text
采用：
get_document_status 返回 has_ocr_text、has_current_layout、is_search_indexed、source_file_status。
```

字段：

```text
get_document_status:
- has_ocr_text
- has_current_layout
- is_search_indexed
- source_file_status
```

语义：

```text
has_current_layout
→ 是否可用 get_page_blocks / bbox evidence

is_search_indexed
→ 当前文本是否已进入本地全文索引

source_file_status
→ 原文件 available / missing / offline_root / changed / conflict
```

原则：

```text
MCP 不需要知道 provider。
MCP 需要知道证据能力边界：有没有文本、有没有结构 / bbox、搜索索引是否可用、原文件是否缺失。
```

MCP 搜索索引状态处理：

```text
采用：
is_search_indexed = false 时，search_library 仍可搜索已有索引，但必须返回 index_status。
结果可能不完整时必须显式标注。
```

SearchResponse：

```text
SearchResponse
- index_status:
  - current
  - stale
  - partial
  - unavailable
- results[]
```

规则：

```text
current
→ 正常返回

stale / partial
→ 返回已有结果，但标记可能不完整

unavailable
→ 返回空结果和原因
```

原则：

```text
MCP 外部工具更适合拿到“尽力结果 + 状态”，而不是直接失败。
只要明确 partial / stale，就不会误导为完整搜索。
调用方可根据 index_status 决定是否继续使用结果。
```

MCP 搜索索引 unavailable 处理：

```text
采用：
索引 unavailable 时，第一版不做自动 fallback。
只返回 index_status = unavailable 和原因。
```

行为：

```text
index_status = unavailable
→ results = []
→ reason = local_search_index_missing | rebuilding | corrupted
```

规则：

```text
- 不自动扫描全部 search_unit
- 不做全库 SQL LIKE fallback
- 可提示用户等待索引重建
```

原则：

```text
产品目标是大书库，线性扫描可能造成 MCP 请求卡死、UI 卡顿或资源暴涨。
第一版保持搜索依赖索引。
索引不可用时明确返回状态和原因，而不是悄悄慢速扫描。
```

搜索索引重建触发：

```text
采用：
默认后台自动局部重建，同时提供手动 rebuild index 维护入口。
```

automatic：

```text
- OCR / text revision changed
- layout / search_unit changed
- snapshot import completed
- dirty_queue has search_index_dirty
```

manual：

```text
- rebuild selected document
- rebuild collection
- rebuild whole library
```

规则：

```text
- 默认自动局部重建
- 手动 rebuild 用于修复 corrupted / missing index
- 全库 rebuild 是维护操作
- MCP 不触发 rebuild
```

原则：

```text
正常使用中用户不应手动维护索引。
索引是本地缓存，可能损坏或缺失，因此需要维护入口。
自动重建优先局部化，避免大库频繁全量重建。
```

索引重建期间搜索行为：

```text
采用：
索引正在重建时，不等待重建完成。
search_library 返回当前可用索引结果，并标记 index_status = stale 或 partial。
```

行为：

```text
rebuilding affected scope
→ search_library returns available results
→ index_status = stale | partial
→ include rebuilding_scopes summary
```

规则：

```text
- 不阻塞 MCP 请求等待 rebuild
- 不返回 unavailable，除非索引整体不可读
- 结果中标明可能缺少正在重建的文档 / 页面
```

原则：

```text
搜索是交互路径，不能被索引重建卡住。
已有索引仍有价值，只要明确 stale / partial 即可。
调用方可根据 index_status 决定是否接受结果或稍后重试。
```

SearchResponse affected scopes：

```text
采用：
search_library 返回 partial / stale 时，返回简要 affected_scopes_summary。
不展开过大列表。
```

SearchResponse：

```text
SearchResponse
- index_status
- affected_scopes_summary
  - document_count
  - page_count
  - sample_document_ids[]
  - truncated: true | false
```

规则：

```text
- 小范围：可列出 document / page id
- 大范围：只返回 count + sample + truncated = true
- 不返回巨大 affected list
```

原则：

```text
外部工具需要知道结果缺口大概在哪里。
搜索响应不能被几千页 dirty scope 塞爆。
affected_scopes_summary 用于提示结果完整性风险，而不是替代重建队列详情。
```

SearchResponse pagination / total：

```text
采用：
第一版不保证精确 total_result_count。
search_library 返回当前页结果 + has_more。
```

SearchResponse：

```text
SearchResponse
- results[]
- page_size
- next_cursor nullable
- has_more
- estimated_total nullable
```

规则：

```text
- 不强制计算精确 total_result_count
- 可返回 estimated_total
- 使用游标分页
- partial / stale 时 estimated_total 也标记不可靠
```

原则：

```text
全文检索、query rewrite、page 聚合和去重都会让精确 total 成本变高。
MCP / agent 通常更需要前 N 个证据和继续翻页能力，而不是精确总数。
避免为了 total_result_count 拖慢大库搜索响应。
```

SearchResponse page_size：

```text
采用：
default_page_size = 20
max_page_size = 100
```

规则：

```text
- 未指定 page_size 时返回 20 条 page-level results
- 用户 / API 可指定 page_size
- 超过 100 时 clamp 到 100
- matched_units 每页结果也应有限制，避免单页塞爆响应
```

原则：

```text
MCP / agent 取证通常 20 条足够判断下一步。
最大 100 支持批量场景，但避免一次响应过大。
搜索结果按 page 聚合时，单页内部 matched_units 也需要截断策略。
```

SearchPageResult matched_units 限制：

```text
采用：
default_matched_units_per_page = 5
max_matched_units_per_page = 20
```

规则：

```text
- 每个 page result 默认返回前 5 个 matched_units
- 可请求最多 20 个
- 超过时返回 matched_units_has_more = true
- get_search_result_context(evidence_ref) 可获取该页 / 该命中的更多上下文
```

原则：

```text
搜索结果按页聚合时，一页可能有大量命中。
默认给 5 个 matched_units 足够判断相关性。
更多内容走 context 工具按需展开，避免搜索响应过大。
```

Search result context 范围：

```text
采用：
default_context_before = 2
default_context_after = 2
max_context_each_side = 10
```

规则：

```text
- 默认返回命中 unit 前 2 个、后 2 个 sibling search_units
- 可请求更多，但每侧最多 10 个
- 同页内取上下文，不跨页
- 需要整页文本时调用 get_page_text
```

原则：

```text
默认上下文应足够判断证据含义，但不能把整页都塞进 context。
跨页上下文和整页文本应显式请求。
context 工具用于围绕命中展开，不替代 get_page_text。
```

Search result context 与整页文本：

```text
采用：
get_search_result_context 不支持 include_page_text。
保持工具职责分离。
```

工具边界：

```text
get_search_result_context
→ 返回命中附近 context units

get_page_text
→ 返回整页 plain_text

get_page_blocks
→ 返回结构化 blocks / bbox
```

原则：

```text
context 工具不带整页文本，避免响应大小和语义变得不可控。
agent 需要整页文本时，应显式调用 get_page_text。
agent 需要结构化页面信息时，应显式调用 get_page_blocks。
```

Search result context units：

```text
采用：
get_search_result_context 返回的 context units 均包含各自 evidence_ref。
```

ContextUnit：

```text
ContextUnit
- unit_id
- evidence_ref
- text
- bbox
- is_match
- reading_order
```

规则：

```text
- 命中 unit 和前后 context units 都带 evidence_ref
- is_match 标明哪些 unit 是原始命中
- bbox 仍只返回原始 bbox，不做自然语言位置描述
```

原则：

```text
上下文里的相邻文本也可能被 agent 引用为证据。
每个 context unit 独立携带 evidence_ref，后续引用更清楚。
context 返回文本、bbox、revision 引用，不生成额外位置解释。
```

EvidenceReference API 表达：

```text
采用：
API 返回结构化 evidence_ref 对象，可另附可复制的 evidence_ref_id 字符串。
```

结构化对象：

```text
evidence_ref:
  library_id
  document_instance_id
  page_id
  unit_id
  text_revision_id
  bbox_revision_id
  layout_revision_id
  snapshot_id nullable
```

字符串形式：

```text
evidence_ref_id: "evref:..."
```

规则：

```text
- MCP / API 主要消费结构化对象
- UI 可显示 / copy evidence_ref_id
- get_search_result_context 接受结构化对象，也可接受 evidence_ref_id
```

原则：

```text
结构化对象便于程序稳定解析。
字符串形式方便复制、日志和外部笔记引用。
两者指向同一证据引用，不表达不同语义。
```

EvidenceReference string 稳定性：

```text
采用：
evidence_ref_id 是公开、版本化、长期可解析的引用字符串。
```

格式：

```text
evref:v1:<base64url-json-or-packed-payload>
```

规则：

```text
- 带 schema version
- 可解析回 evidence_ref 结构化对象
- 不包含本机路径
- 不包含 provider secret
- 不包含用户不可同步的本地状态
```

原则：

```text
evidence_ref_id 可能被复制到笔记、论文草稿或外部 agent 日志。
因此它必须长期可解析，而不是短期 UI display token。
v1 前缀为未来格式变更留余地。
```

EvidenceReference 解析状态：

```text
采用：
解析旧 evidence_ref_id 时返回显式状态，不静默改指向。
```

EvidenceResolveStatus：

```text
- found_pinned
- superseded
- tombstoned
- purged
- not_found
- library_mismatch
```

规则：

```text
found_pinned
→ 返回原 pinned evidence

superseded
→ 返回原状态 + successor reference

tombstoned
→ 返回 tombstone metadata，不返回文本

purged
→ 返回 purged，不返回文本 / bbox

not_found
→ 返回无法解析原因

library_mismatch
→ 当前库不匹配
```

原则：

```text
证据引用的核心是可验证性。
不能在旧引用失效时悄悄跳到新内容。
调用方必须明确知道 evidence 是 current、被替代、墓碑、已清除、找不到，还是库不匹配。
```

EvidenceReference superseded 行为：

```text
采用：
superseded 状态下返回 successor reference，但不自动采用。
```

返回：

```text
superseded:
- original_evidence_ref
- successor_evidence_refs[]
- superseded_reason
```

规则：

```text
- 默认仍返回 superseded 状态
- 附带 successor_evidence_refs
- 不把 successor 当作 found_pinned
- 调用方要显式请求 current / compare 才读取 successor 内容
```

原则：

```text
提供 successor 可以帮助用户追踪新版证据。
不能破坏 pinned evidence 的语义。
旧引用不会静默变成新引用。
```

EvidenceReference current / compare successor chain：

```text
采用：
resolve_mode = current / compare 时，沿 successor chain 追到最终 current，并返回 chain summary。
```

resolve_mode = current：

```text
- pinned_evidence_ref
- current_evidence_ref
- successor_chain[]
- chain_status
```

规则：

```text
- 沿 successor chain 追踪
- 遇到分叉时返回 multiple_current_candidates
- 遇到 tombstone / purge 时停止
- 设置 max_chain_depth，例如 20，防止异常循环
```

原则：

```text
用户请求 current / compare 时，通常想知道“现在对应哪条证据”。
只返回第一层 successor 会让调用方反复解析。
追到最终 current 更实用，但必须保留 chain summary 以便审计。
```

EvidenceReference successor 分叉处理：

```text
采用：
successor chain 出现分叉时，不自动选择 newest。
返回 multiple_current_candidates。
```

返回：

```text
chain_status = multiple_current_candidates
current_candidates[]
```

规则：

```text
- 不自动选择 newest
- 不按时间、score、页面顺序替用户决策
- 返回候选 evidence_ref 列表和 superseded_reason
- 调用方可选择其中一个继续 resolve
```

原则：

```text
分叉通常意味着用户操作或同步冲突产生了多个合理后继。
自动选择 newest 可能错指证据。
应显式交给调用方或用户处理。
```

桌面 UI evidence / citation copy：

```text
采用：
桌面 UI 将 evidence_ref / citation copy 作为一等操作。
```

操作：

```text
- Copy Evidence Reference
- Copy Evidence Markdown
```

默认 Markdown：

```markdown
> quoted text

Source: Title, p. 12
Evidence: evref:v1:...
```

规则：

```text
- 可从搜索结果、page block、context unit 复制
- 默认带 evidence_ref_id
- 不带本机路径
- 不带 provider 信息
```

原则：

```text
evidence_ref_id 是长期可解析字符串，应方便复制到笔记、草稿和外部工具。
复制出的 evidence/citation 应保持可复现证据引用，不泄露本机路径或 provider 配置。
```

Evidence Markdown 文本版本：

```text
采用：
复制 Evidence Markdown 时默认使用 pinned revision 文本。
```

默认：

```markdown
> quoted text from pinned revision

Source: Title, p. 12
Evidence: evref:v1:...
```

规则：

```text
- 从搜索结果复制：使用搜索结果里的 evidence_ref pinned revision
- 从当前页面 / block 复制：先生成当前 revision 的 evidence_ref，再复制
- 如果用户选择 “Copy Current Evidence Markdown”，才显式使用 current
```

原则：

```text
复制出来的 quoted text 应和 evidence_ref_id 指向的证据一致。
默认 pinned 能避免文本和引用对不上。
current copy 必须是显式操作。
```

Copy Page Citation：

```text
采用：
第一版先不做 Copy Page Citation。
只保留围绕 evidence_ref 的复制操作。
```

第一版提供：

```text
- Copy Evidence Reference
- Copy Evidence Markdown
```

第一版不提供：

```text
- Copy Page Citation
- Copy Page Evidence Citation
```

原则：

```text
先把证据引用链做扎实。
普通题录 + 页码引用功能暂不进入第一版 UI。
避免 UI 同时出现 evidence copy 与 citation copy 两套相近但语义不同的操作。
```

Copy Evidence Markdown Source：

```text
采用：
第一版 Copy Evidence Markdown 的 Source 字段使用最小信息：标题 + 页码 / 页标。
```

默认格式：

```markdown
> quoted text

Source: Title, p. 12
Evidence: evref:v1:...
```

规则：

```text
- title 缺失时用 item_id 或 Untitled
- page_label 有则用 page_label
- 无 page_label 时用 page_index
- 不拼复杂 citation style
- 不包含作者、年份、出版社等格式化引用
```

原则：

```text
Evidence Markdown 用于复制可复现证据，不承担普通题录 / 引文生成功能。
普通题录生成应针对 item，是 item 级功能。
第一版 Source 只需要让人看懂证据来自哪里；真正可解析和可复现的是 Evidence: evref:v1:...
```

普通题录 / citation 生成：

```text
采用：
普通题录 / citation 生成功能暂缓，不进第一版核心。
但作为重要 TODO 保留在产品路线中。
```

第一版：

```text
- 管理 item metadata
- 支持 evidence copy
- 不做 CSL citation rendering
- 不做 bibliography export
```

重要 TODO：

```text
- item-level citation generation
- CSL style support
- bibliography export
- citekey / Better BibTeX-like workflow
```

原则：

```text
第一版核心优先完成同步、OCR、layout、全文检索、MCP 和 evidence。
普通题录生成是 item 级功能，应在 item 模型和元数据编辑稳定后推进。
该功能重要，但不应分散第一版核心实现焦点。
```

item metadata 第一版字段：

```text
采用：
第一版支持 Zotero-like 基础字段。
identifiers 使用可扩展 scheme，不限制为固定列。
```

item：

```text
item
- item_id
- item_type
- title
- subtitle
- creators[]
- date
- publication_title
- publisher
- place
- volume
- issue
- pages
- language
- abstract
- tags[]
- collections[]
- custom_fields
```

identifiers：

```text
identifier
- item_id
- scheme
- value
- note nullable
```

内置常见 scheme：

```text
- DOI
- ISBN
- ISSN
- URL
- archive_id
- call_number
- jpno
- ndlbibid
```

规则：

```text
- identifiers 可扩展
- scheme 不限于内置列表
- 用户可添加机构目录号、古籍目录号、馆藏号等自定义 scheme
```

TODO：

```text
- CSL 完整字段映射
- 多语标题 / 转写标题
- edition / history 细粒度字段
- authority control
- creator disambiguation
```

原则：

```text
item metadata 第一版提供足够常用字段。
古籍、机构目录、档案号等标识符需要可扩展 scheme。
复杂 citation rendering 与 authority control 暂缓。
```

---

## 15. 检索引擎与 analyzer

技术路线：

```text
SearchProvider 抽象。
第一版 SQLite FTS5。
后续可增加 Lucene.NET / Tantivy provider。
```

接口：

```text
ISearchIndexer
ITextAnalyzer
IQueryParser
ISearchHighlighter
ISearchResultAggregator
```

中日文第一版：

```text
CJK 使用字符 n-gram。
拉丁文字语言使用 word token。
混合文本使用混合 analyzer。
后续再接入 MeCab/Sudachi/Jieba 等专用 analyzer。
```

Normalization 原则：

```text
canonical text 保持原文。
index text 尽量贴近原文。
索引层只做最低限度技术性 normalization。
```

允许：

```text
- Unicode normalization
- 大小写折叠
- 必要空白处理
- 拉丁字母/数字的全角半角处理
```

不默认做：

```text
- 繁简转换
- 新旧字体转换
- 異體字替换
- 历史假名替换
- 语义同义词替换
```

---

## 16. 查询重写与 Search Profile

已确定：

```text
查全率主要依赖可定制 query rewriting。
靠牺牲查询速度提升查全率。
不通过修改 canonical text 或 index text 来规范化历史文字。
```

Query rewriting 支持：

```text
- 異體字
- 新旧字体
- 繁简
- 歴史的仮名遣い
- OCR/HTR 混淆
- 同义词
- 正则改写
- 用户词典
```

规则独立保存：

```text
rewrite_rule
- rule_id
- name
- rule_type
- pattern
- replacements[]
- enabled
```

Search Profile 组合规则：

```text
search_profile
- profile_id
- name
- command_aliases[]
- description
- enabled_rewrite_rules[]
- default_match_mode
- max_expansions
- last_used_at
```

Search Profile 选择：

```text
用户手动选择。
只做全局历史记忆。
不按 collection/tag/language/item_type 自动选择。
```

优先级：

```text
显式 :alias
> 当前搜索框手动选择
> 全局最近使用 Search Profile
> 系统默认 Search Profile
```

命令式 pattern：

```text
:komonjo 異国船
:modern "digital accessibility"
:european-ms "Johann Müller"
```

Query rewrite 执行：

```text
默认自动执行。
结果页可展开查看 rewrite plan。
高级设置可开启执行前预览。
```

Rewrite plan 示例：

```text
原始查询: 異国船
扩展查询:
- 異国船
- 異國船
- 異国舩
- 異國舩
```

排序策略：

```text
第一版所有 rewrite 命中同权。
有需求再加权重。
```

搜索结果合并：

```text
搜索结果列表按 page 聚合。
页内 matched units 去重。
每个 unit 保留所有命中词与 query rewrite 来源。
```

结果结构：

```text
SearchPageResult
- result_id
- item_id
- document_instance_id
- page_id
- page_label
- score
- matched_units[]
  - evidence_ref
  - unit_id
  - display_text
  - bbox
  - text_revision_id
  - bbox_revision_id
  - layout_revision_id
  - matched_terms[]
    - raw_query_term
    - expanded_query_term
    - rewrite_rule_id
```

---

## 17. 检索模式

已确定：

```text
默认关键词 / 短语 / 字段检索。
向量启用后提供 hybrid / semantic 模式。
所有结果必须标注命中来源，并提供可验证证据。
```

搜索结果围绕证据组织：

```text
item
document_instance
page
matched content unit
bbox
revision
match_type
```

---

## 18. 向量 embeddings

已确定：

```text
向量 embeddings 是可选增强功能。
全文检索是核心。
默认不为全库生成向量。
```

可选生成范围：

```text
- collection
- tag
- selected documents
- specific language
- specific OCR profile output
```

向量服从 revision 机制：

```text
text_revision changed
→ BM25/fulltext index 立即局部重建
→ embedding marked stale
→ 用户或后台任务按策略重算
```

embedding model：

```text
embedding_profile
- model_id
- provider: local | cloud
- dimensions
- language/script suitability
- chunking_strategy
- privacy/cost flags
```

---

## 19. dependency graph / dirty queue

已确定：

```text
内部使用 revision dependency graph + dirty queue。
UI 只显示简化状态。
```

内部 artifact：

```text
source artifacts:
- file_asset
- page render source
- OCR/HTR run output
- user correction revision

canonical artifacts:
- layout_node_revision
- text_revision
- bbox_revision

derived artifacts:
- search_units
- embedding_chunks
- local search index entries
```

derived artifact 保存：

```text
artifact_id
artifact_type
scope_type: document | page | bbox | content_block
scope_id
input_revision_ids[]
output_revision_id
status: current | dirty | stale | failed
created_at
generator
```

用户修改文字：

```text
text_revision 更新
→ affected search_units dirty
→ local fulltext index entries invalidated
→ embeddings stale
```

用户修改 bbox：

```text
bbox_revision 更新
→ layout tree dirty
→ search units 视情况 dirty
→ page overlay/render cache dirty
```

---

## 20. MCP

已确定：

```text
MCP 第一版只提供检索与证据读取能力。
不提供写入。
不触发 OCR。
不修改题录。
不改 bbox。
```

第一版工具：

```text
search_library
- 输入关键词/短语/字段条件
- 返回 item + document_instance + page + matched_units + evidence_ref

get_item_metadata
- 返回题录、标签、collection、自定义字段

get_document_status
- 返回 has_ocr_text / has_current_layout / is_search_indexed / source_file_status
- 不返回本机 resolved_path
- 不返回 provider 配置或 credential 状态

get_page_text
- 默认返回指定页的 layout-derived plain text
- 需要页面结构、bbox、OCR 边界或 evidence_ref 时显式请求 structured
- 支持 read_mode: current | pinned | compare
- 默认 current；使用 evidence_ref 时可 pinned 到历史 revision

get_page_blocks
- 返回指定页的 content blocks / bbox evidence / evidence_ref
- 支持 read_mode: current | pinned | compare
- 默认 current；使用 evidence_ref 时可 pinned 到历史 revision

get_search_result_context
- 根据 evidence_ref 返回上下文、bbox、revision、来源
- 可兼容当前搜索会话内的短期 result_id
```

不提供：

```text
run_ocr
edit_ocr
edit_bbox
reset_ocr
purge_ocr
update_metadata
delete_anything
resolved_path
local filesystem path
open_original
file:// URL
```

MCP 检索采用两步式：

```text
第一步 search_library：
- item metadata summary
- document_instance_id
- page_id / page_label
- match score
- match_type
- matched_units[]
- evidence_ref
- optional result_id
- short snippet
- bbox
- text_revision_id / bbox_revision_id / layout_revision_id

第二步 get_search_result_context：
- 输入 evidence_ref，或当前搜索会话内的 result_id
- 命中 block 前后 N 个 block
- 指定 bbox 子树
- 整页 layout-derived text
- 页内结构化 blocks
```

---

## 21. 桌面应用与技术栈

已确定：

```text
桌面应用优先。
尽可能使用 .NET 生态。
尽可能使用F#, it compiles, it works.
UI 暂时使用原生 Avalonia 12。
```

架构：

```text
LiteratureApp.UI                  原生 Avalonia 12
LiteratureApp.Core                领域模型、题录、OCR、layout tree
LiteratureApp.Infrastructure      SQLite、文件 watcher、快照同步、OCR adapters
LiteratureApp.Search              全文索引、查询、局部重建
LiteratureApp.Mcp                 MCP 检索接口
LiteratureApp.Ocr                 OCR/HTR profiles and engines
```

内部仍保持 service/core 分层：

```text
.NET desktop app
├── UI layer
├── application/core services
├── local database/shard manager
├── search index service
├── OCR/HTR task service
├── file watcher service
├── snapshot publish/import service
└── optional MCP server
```

---

## 22. 数据库技术

已确定：

```text
主数据库采用 SQLite。
多个 SQLite shard 组成逻辑书库。
```

数据访问：

```text
Dapper + 手写 SQL 为主。
不把 EF Core 作为核心依赖。
```

适用原因：

```text
- SQLite 分片
- append-only revision
- OCR/layout 批量写入
- dirty queue
- 局部索引重建
- 内容寻址 snapshot
- 跨 shard manifest
```

---

## 23. 已经明确暂缓或不做的内容

暂不做：

```text
- 团队功能
- 多人权限
- 审计日志
- 页面级 fingerprint
- 程序自带 PDF 文件同步
- 独立 reading-order view
- 自动 OCR Profile 推荐
- OCR 导入后自动聚合 char/line/block
- char-only OCR 的良好全文检索
- MCP 写权限
- bbox/region 级 candidate run adoption
- 多 parent 复杂 layout graph
- query rewrite 权重排序
```

未来可扩展：

```text
- Lucene.NET / Tantivy SearchProvider
- MeCab/Sudachi/Jieba analyzer
- 向量/hybrid search
- reading-order view / content_list-like 派生层
- bbox overlap 自动候选替换
- region-level OCR merge/adopt
- 更复杂同步合并
- OCR result physical purge / database compaction
```
