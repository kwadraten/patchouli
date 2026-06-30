# 文献管理程序 PRD v1.1

## 1. 问题陈述

研究者需要一个个人文献管理器，用来管理题录、PDF/扫描本、OCR/HTR 文本、版面结构、全文检索结果和可复现证据引用。现有工具通常在以下方面不足：

- 文件同步、数据库同步和本地运行库容易混在一起，网盘同步可能损坏运行数据库。
- PDF 路径变化后，题录、OCR、索引和原文定位之间容易断裂。
- OCR/HTR 管线难以针对多语言、古籍、手稿、竖排、多栏、边注等材料做细粒度配置。
- OCR 结果很难持续修正，并且修正后的文本、bbox、layout、索引和外部引用缺少统一修订机制。
- 搜索结果往往只返回文本片段，缺少页级、bbox、修订版本和来源证据。
- 外部 agent 需要通过 MCP 检索和读取证据，但不应该获得写权限、本机路径、密钥或执行 OCR 的能力。

目标是开发一个桌面优先、个人使用、可同步、可修正、可检索、可被外部工具读取证据的文献管理程序。

## 2. 解决方案

构建一个个人文献管理器，并增加可编程增强层：

- **题录管理**：以题录（Item/Work）为学术引用身份，支持基础元数据、可扩展标识符、标签、集合和自定义字段。
- **文件管理**：PDF/图像不入库，由用户自己的目录和云盘管理；程序通过哈希值、已知位置和搜索目录重新定位。
- **同步**：运行库与发布快照分离；发布快照由 SQLite 分片 + 清单文件组成，可通过 Google Drive、OneDrive、Syncthing、NAS 等同步。
- **OCR/HTR**：用户手动选择 OCR 预设，可按文档/页面/bbox 运行，支持云端或本地模型。
- **布局 / bbox**：OCR 输出进入可重组布局树；用户可以持续修正文字、bbox、阅读顺序和节点结构。
- **检索**：全文检索以 SQLite FTS5 为第一版提供程序；搜索单元持久化为可重建派生表，本地 FTS 索引可重建。
- **证据**：搜索和 MCP 返回稳定的证据引用（evidence_ref），支持 pinned/current/compare 解析和长期可复制的 evref 字符串。
- **MCP**：第一版只提供检索与证据读取，不提供写入、OCR 触发、本机路径、提供程序密钥或原文打开。

## 3. 目标

- 建立可靠的个人书库数据模型：库（library）、题录（item）、文件资产（file_asset）、文档实例（document_instance）、页面（page）、布局节点（layout_node）、搜索单元（search_unit）、修订版本（revision）。
- 支持数据库快照通过常见同步服务同步，不依赖程序自带文件同步。
- 支持 PDF/图像文件路径变化后的快速定位与深度修复扫描。
- 支持多语言 OCR/HTR 预设、预设版本、重试运行、候选结果和局部采用。
- 支持用户持续修正 OCR 文本、bbox 和布局树。
- 支持页级全文检索、查询重写、搜索配置文件和携带稳定证据引用的搜索结果。
- 提供纯文本 MCP，面向外部 agent 返回可验证文本证据。
- 第一版优先保证证据一致性、修订版本可追踪、同步安全和大库可扩展。

## 4. 非目标与 V1 排除项

以下能力不属于第一版范围。部分能力可以作为 v2/v3/待办事项继续讨论。

| 类别 | V1 排除项 | 后续跟踪 |
|---|---|---|
| 协作 | 团队功能、多人权限、审计日志、机构级后台管理 | v3 |
| 文件同步 | 程序自带 PDF/图像文件同步、托管文件目录 | 待办事项 |
| 搜索降级 | 搜索索引不可用时的 SQL LIKE / 线性扫描降级 | 待办事项 |
| OCR 自动化 | 自动推荐 OCR 预设、自动云成本确认策略 | 待办事项 |
| 引文 | CSL 引文渲染、参考文献导出、复制页面引文 | v2 |
| 布局 | 独立阅读顺序视图、多父节点布局图、bbox 级候选采用 | v2/v3 |
| 表格模型 | 完整表格语义、公式、复杂样式、嵌套表格模型 | v2 |
| 查询排序 | 查询重写权重排序、语义混合排序 | v2 |
| MCP 操作 | MCP 写入、OCR 触发、bbox 编辑、元数据更新、删除操作 | v1 不计划 |
| MCP 媒体/路径 | MCP 返回图片、缓存路径、本机路径、文件 URL | 不计划 |
| 加密 | 库级加密、主密码、每设备凭据解封 | v2+ |
| 模型溯源 | 完整模型指纹/可复现性包 | 待办事项 |

## 5. 用户故事

1. 作为一名研究者，我希望管理我的个人文献库，以便在一个地方组织书籍、论文、扫描件和元数据。
2. 作为一名研究者，我希望数据库可以通过 Google Drive 或类似服务同步，以便在多台设备上使用同一个文献库。
3. 作为一名研究者，我希望 PDF 保留在我自己的文件夹结构中，以便程序不会接管我的文件组织。
4. 作为一名研究者，我希望程序能够通过哈希值和搜索根目录找到已移动的 PDF，以便文件移动后元数据和 OCR 仍然可用。
5. 作为一名研究者，我希望缺失的 PDF 不会删除或隐藏元数据、OCR 和搜索结果，以便我的文献库在离线时仍然可用。
6. 作为一名研究者，我希望题录元数据带有可扩展标识符，以便我可以存储 DOI、ISBN、JPNO、NDLBibID、索书号和自定义目录 ID。
7. 作为一名研究者，我希望一个题录下有多个文档实例，以便扫描件、OCR PDF、部分文件和补充材料可以属于同一个被引作品。
8. 作为一名研究者，我希望不同版本、翻译、卷册和预印本在引用身份不同时作为不同的题录，以便引用保持准确。
9. 作为一名研究者，我希望手动选择 OCR/HTR 预设，以便为手稿、古典日语文本或现代 PDF 选择合适的模型。
10. 作为一名研究者，我希望风险较高的 OCR 结果在我采纳之前保持候选状态，以便低置信度的运行不会污染搜索和证据。
11. 作为一名研究者，我希望部分失败的 OCR 运行中成功的页面被保留，以便少数坏页面不会丢弃整个大型运行。
12. 作为一名研究者，我希望能够修正 OCR 文本、布局、bbox 和阅读顺序，以便搜索和引文随着时间的推移指向更好的证据。
13. 作为一名研究者，我希望搜索返回页级结果，带有匹配文本和证据引用，以便我能在源上下文中验证主张。
14. 作为一名研究者，我希望搜索支持历史拼写、异体字、OCR 混淆和词典，以便多语言或历史材料可以被检索到。
15. 作为一名研究者，我希望过时或部分索引状态能够被显示，以便我知道搜索结果何时可能不完整。
16. 作为外部 agent，我希望 MCP search_library 返回证据引用和匹配单元，以便我能够引用证据。
17. 作为外部 agent，我希望 get_search_result_context 返回附近的单元及其自己的证据引用，以便我能够独立引用上下文。
18. 作为外部 agent，我希望 get_page_text 默认返回纯文本，以便我可以低成本地请求页面上下文。
19. 作为外部 agent，我希望 get_page_blocks 仅在请求时返回结构化文本和 bbox，以便在需要时验证布局。
20. 作为外部 agent，我希望 MCP 只返回纯文本，以便不暴露本地图像、文件路径或缓存。
21. 作为外部 agent，我希望证据引用默认是 pinned，以便 OCR 修正后引用不会漂移。
22. 作为一名研究者，我希望从搜索结果和块中复制证据 Markdown，以便我可以将可复制的引文粘贴到笔记中。
23. 作为一名研究者，我希望稍后实现正常的题录级引文生成，以便在核心证据系统稳定后添加参考文献和 CSL 样式。

## 6. 功能需求

### 6.1 产品范围

- 应用程序是一个个人桌面文献管理器，具有可编程增强功能。
- 团队工作流、共享权限、审计日志和机构管理不在范围内。
- 桌面应用程序是主要的；优先选择 .NET 生态系统，在实际可行的情况下优先选择 F#。
- UI 栈：目前使用原生 Avalonia 12。

### 6.2 库标识与多重性

- 库是元数据、文档实例、OCR/布局/搜索制品、证据引用、快照和 MCP 解析的顶层持久边界。
- v1 在桌面应用程序中一次只支持打开一个活动库。
- v1 数据模型仍必须支持磁盘上的多个库，每个库有唯一的 `library_id`。
- `library_id` 在库创建时生成一次，并且在该库的整个生命周期中必须保持稳定。
- `library_id` 不从路径、设备名称、同步根目录或用户账户派生。
- 库显示名称可以在不更改 `library_id` 的情况下重命名。
- 库可以移动到另一个文件夹或同步服务，而不更改 `library_id`。
- v1 不支持自动库合并或拆分。
- v1 不允许跨库证据解析。如果某个 evidence_ref 属于另一个库，解析返回 `library_mismatch`。
- 快照是每个库独立的。一个快照清单不能包含来自多个库的对象。
- `device_id` 标识一个库内的写入设备；它不是引用身份的一部分。

### 6.3 库数据库与快照同步

- 数据库是元数据、OCR/HTR、布局、bbox、修订版本、脏队列、OCR 运行、搜索单元元数据和可选向量的唯一真实来源。
- PDF/图像原件和大二进制缓存不存储在数据库中。
- 运行数据库位于同步文件夹之外，可以使用 WAL。
- 发布的快照数据库以 SQLite 分片加清单文件的形式检查点到同步根目录中。
- 分片目标大小为 512-768 MB，硬上限低于 1 GB。
- 分片大小依据：
  - 保持单个同步文件低于常见云客户端的压力阈值；
  - 减少活动数据变化时的重新上传成本；
  - 保持验证/哈希和修复操作在有限范围内；
  - 避免快照发布期间出现大的 WAL/检查点停顿。
- 旧的大数据分片应尽量不可变；变化作为新的修订版本/增量数据写入活动分片。
- 快照标识必须包含 library_id、device_id、snapshot_id、parent_snapshot_id、schema_version、分片列表、分片哈希和逻辑代次。
- 从应用程序角度看，快照发布是原子性的：写入候选清单，验证分片哈希，然后更新当前指针。
- 快照导入不会就地替换活动运行数据库。它导入到一个临时区域，在验证后应用。

### 6.4 快照冲突解决

- 第一版同步使用单写入者约定，通过检测和警告而非硬分布式锁来强制执行。
- 每个设备写入 `device_id`、最后本地代次、parent_snapshot_id 和发布时间戳。
- 在发布时，如果同步根目录的当前快照不再是本地父快照，应用程序不得覆盖它。
- 该条件创建一个快照分支。
- v1 不执行自动对象级合并。
- v1 分支操作：
  - 作为独立分支打开以供检查；
  - 通过显式用户操作将选中的题录/文档实例导入当前分支；
  - 丢弃本地分支；
  - 将分支保留为独立的库副本。
- v1 不得在分支间静默执行最后写入者胜出。

v1 中的对象策略：

| 对象类型 | 同分支更新 | 跨分支冲突 |
|---|---|---|
| 库元数据 | 分支中最新已提交变更 | 手动选择 |
| 题录元数据 | 修订版本化更新 | 手动导入/选择 |
| 文件资产位置 | 本地 known_locations 可追加 | 身份冲突时手动选择 |
| 文档实例 | 修订版本化更新 | 手动导入/选择 |
| 页面元数据 | 从文档实例/文件派生 | 禁止自动合并 |
| OCR 运行 | 分支内仅追加 | 仅随所属文档实例导入 |
| OCR/布局修订版本 | 分支内仅追加/当前指针 | 手动选择；无自动指针合并 |
| 搜索单元 | 分支内派生/持久化 | 随所属布局修订版本重建/导入 |
| 证据引用 | 仅在所属库/分支上下文中解析 | 歧义时返回分支候选项 |
| 提供程序凭据 | 可变凭据存储，选中的分支中取最新 | 手动选择/重新输入 |
| 缓存/索引 | 本地可重建 | 从不合并 |

### 6.5 凭据同步与信任边界

- 提供程序凭据是用户拥有的云/本地提供程序密钥和令牌，供 OCR/HTR 适配器使用。
- 根据产品决策，v1 可以在受信任的用户设备间同步提供程序凭据。
- 从应用程序角度看，凭据以明文存储；v1 不实现库级加密、主密码或每设备解封。
- 凭据不得写入不可变的历史内容寻址数据分片。
- 凭据存在于最新清单引用的可变凭据存储/分片中，并标记为 `sensitive_mutable`。
- 凭据变更重写/轮换可变凭据存储，而不是将秘密追加到历史数据分片中。
- 快照发布必须保持普通不可变数据分片和 `sensitive_mutable` 凭据分片在逻辑上分离。
- 紧急凭据清除/撤销是 v1 需求：
  - 从活动运行数据库中删除提供程序凭据行；
  - 重写可变凭据存储，不包含该秘密；
  - 更新清单引用；
  - 将受影响的 OCR 预设/提供程序标记为 `credential_missing`。
- 应用程序可以删除其管理的同步根目录下的凭据分片和清单引用，但无法擦除云提供程序的历史版本、外部备份或用户复制的文件。
- 用户负责信任设备、同步服务和同步文件夹的访问控制。
- MCP 无法读取提供程序密钥或提供程序配置详情。
- MCP 仅报告文档证据能力，不报告提供程序状态。

### 6.6 文件资产与文件解析

- 应用程序不拥有托管的文件目录。
- 用户分别配置 file_search_roots 和 database_sync_roots。
- PDF 文件缺失不意味着文献缺失。
- 文件状态：available、moved_candidate、missing、offline_root、conflict、changed。
- 文件标识使用快速哈希和可选的完整 BLAKE3；SHA-256 不是核心字段。
- 导入存储路径、名称、大小、修改时间、快速哈希、页数，以及可能存在的 PDF trailer ID。
- 完整 BLAKE3 在空闲后台工作中稍后计算。
- 重新定位首先检查 known_locations，然后是大小 + 快速哈希，必要时再进行完整 BLAKE3。
- 扫描模式：
  - 启动时快速扫描。
  - 增量式监控驱动扫描。
  - 用户触发的深度修复扫描。
- 必须使用统一的文件解析 API 来打开原件、渲染页面、运行 OCR 和验证哈希。
- `resolve_file(file_asset_id, purpose)` 向可信的内部调用者返回状态、解析路径、候选项、置信度和所需操作。
- MCP 从不接收解析路径。
- conflict 和 changed 状态不自动打开；它们需要用户确认。
- 如果 file_asset 状态变为 `changed`，依赖的页面渲染/OCR/bbox 证据仅作为先前已提交的证据可用，并且必须标记为 `source_changed`。
- `source_changed` 不会自动使 pinned 证据失效，但 current 模式的消费者必须收到警告，提示 bbox/页面的基准可能不再与当前源文件匹配。

### 6.7 题录元数据与书目模型

- 核心模型有三层：**题录（Item/Work）**、**file_asset**、**document_instance**。
- **题录（Item/Work）** 是引用身份。
- **file_asset** 是文件身份、位置和验证。
- **document_instance** 是一个题录下的具体 PDF/扫描表现形态，拥有页面/OCR/布局/搜索/向量制品。
- 不同的引用身份必须是不同的题录：版本、翻译、卷册（当引用不同时）、预印本与正式版（如果元数据不同）。
- 相同的引用身份可以有多个文档实例：不同扫描件、OCR PDF、拆分 PDF、缺页补充材料。
- 默认搜索仅针对主要文档实例；高级搜索可以包含备选、部分、已弃用的实例或特定文档实例。
- 题录元数据字段命名与 CSL 变量（CSL variable）对齐，以便与 CSL 样式处理引擎和 citeproc 工具链互操作。存储策略遵循以下原则：

  | 原则 | 策略 |
  |---|---|
  | 高频、查询常用、排序常用 | 单列 |
  | 结构化变量（name-list、日期） | 独立表 |
  | 低频 CSL 变量 | `extra_csl` JSON |
  | 除 URL 外的标识符 | `identifiers` JSON/map |
  | 可派生变量 | 不落库 |
  | 运行时变量 | 交给 CSL processor |

  第一版核心字段及分组如下：

  **1. 条目核心字段（单列）**

  | 字段 | CSL 对应 | 说明 |
  |---|---|---|
  | `id` | `id` | 内部主键，可导出为 citeproc JSON id |
  | `type` | `type` | book、article-journal、chapter、thesis、report、webpage 等 |
  | `citation_key` | `citation-key` | 供 Markdown、Typst、LaTeX、外部引用的稳定引用键 |
  | `language` | `language` | 多语种大小写转换、排序、locale 判断 |
  | `title` | `title` | 主标题 |
  | `title_short` | `title-short` | 可选，短标题需人工指定 |
  | `container_title` | `container-title` | 期刊名、论文集名、书章所在书名 |
  | `container_title_short` | `container-title-short` | 可选，期刊缩写 |
  | `collection_title` | `collection-title` | 丛书名、系列名 |
  | `publisher` | `publisher` | 出版社、发布机构 |
  | `publisher_place` | `publisher-place` | 出版地，人文学科常用 |
  | `edition` | `edition` | 版次 |
  | `volume` | `volume` | 卷 |
  | `issue` | `issue` | 期 |
  | `page` | `page` | 页码范围；`page-first` 由此派生 |
  | `genre` | `genre` | 学位论文类型、报告类型、文献体裁 |
  | `number` | `number` | 报告号、标准号、专利号等（非 DOI/ISBN） |
  | `chapter_number` | `chapter-number` | 可选，章节号 |
  | `version` | `version` | 可选，软件/数据集/在线资源版本 |
  | `status` | `status` | 可选，forthcoming、in press 等 |
  | `note` | `note` | 用户备注、注释 |
  | `abstract` | — | 扩展用于检索，不参与引用输出 |
  | `keyword` | — | 扩展用于检索和分类 |

  **2. 标识符字段：统一 map**

  不为每个标识符建固定列，以 `scheme → value` map 存储：

  ```json
  {
    "DOI": "10.1234/example",
    "ISBN": "9780000000000",
    "ISSN": "1234-5678",
    "PMID": "12345678",
    "PMCID": "PMC1234567",
    "arXiv": "2401.00001",
    "JSTOR": "123456"
  }
  ```

  - 内置常见 scheme：DOI、ISBN、ISSN、PMID、PMCID、arXiv、JSTOR、URL、archive_id、call_number、jpno、ndlbibid。
  - URL 因其访问日期、快照、死链检查等逻辑特殊，可单独表或 map 内附 metadata。
  - 导出为 citeproc JSON 时将 map 展开为顶层属性即可。

  **3. 人名字段：结构化 name-list**

  第一版支持的角色：

  | 角色 | CSL 变量 |
  |---|---|
  | 作者 | `author` |
  | 编者 | `editor` |
  | 译者 | `translator` |
  | 容器作者 | `container-author` |

  - `author` 等不是字符串，而是 `name-list`，每个 name 包含 `family`、`given`、`literal`、`suffix`、`particles` 等子字段。
  - 不将作者平铺为 `author_name`、`editor_name` 等字符串列。

  **4. 日期字段：结构化日期**

  第一版支持的 date role：

  | 角色 | CSL 变量 |
  |---|---|
  | 出版日期 | `issued` |
  | 访问日期 | `accessed` |
  | 原始出版日期 | `original-date` |

  - 不拆分为 `issued_year`、`issued_month`、`issued_day` 等独立列。
  - 遵循 citeproc JSON date-parts 模型，支持 season、circa、literal 等。
- CSL 渲染、参考文献导出、规范控制、创作者消歧、多语言标题和详细的版本/历史字段是重要的待办事项。

### 6.8 OCR/HTR 预设、模型与提供程序配置

- "OCR Preset"（OCR 预设）是用户面向的可复用 OCR/HTR 配置的名称。
- `ocr_preset` 替换了早期的"OCR Profile"术语，以避免与搜索配置文件混淆。
- 用户手动选择 OCR/HTR 预设；系统不会自动推荐预设。
- 预设作用域优先级：
  - bbox 覆盖
  - 页面覆盖
  - 文档默认
  - 集合/标签批量分配
- 支持的任务：
  - RunPresetOnDocument（在文档上运行预设）
  - RunPresetOnPages（在页面上运行预设）
  - RunPresetOnRegion（在区域上运行预设）
- 预设版本对于 OCR 溯源是不可变的。
- preset 包含名称、current_version_id 和归档状态。
- preset_version 包含 engine_id、model_id、model_path、parameters、apply_on_success 和 created_at。
- 更改引擎/模型/参数/apply_on_success 会创建一个新的 preset_version。
- 更改名称/描述/标签可以就地更新 preset。
- ocr_run 记录 preset_id、preset_version_id、engine_id、model_id、parameters_snapshot、source_revision_id 和 output_revision_id。
- 第一版的模型标识仅为 model_id + model_path。
- model_path 可以是本地文件系统路径或 URL/端点/模型页面 URL。
- 更强的模型哈希/指纹识别是待办事项，不是 v1 需求。
- 本地模型路径缺失/不可访问会阻止 OCR 并允许用户重新绑定；重新绑定会创建新的 preset_version。
- 云提供程序认证/模型/端点故障会阻止 OCR，不会自动降级。

### 6.9 OCR/HTR 运行生命周期

- OCR 结果按页面保存。
- ocr_run 状态：pending、running、completed、completed_with_errors、failed、cancelled。
- ocr_page_result 状态：pending、processing、succeeded、failed、skipped、cancelled。
- 运行 OCR 写入临时结果；临时结果可预览但不进入当前布局、全文索引或 MCP。
- cancelled OCR 回滚整个运行并删除该运行的 staging/暂存结果。
- apply_on_success=true 将临时结果提升为当前 OCR/布局修订版本，将相关 search_unit 标记为脏，并调度本地索引重建。
- apply_on_success=false 将结果提升为候选结果；在用户采纳之前，它不能通过默认 MCP 检索。
- 候选采纳支持整个运行或选定的页面；第一版不支持 bbox 级候选采纳。
- bbox 坐标转换失败会拒绝整个页面：不产生文本、布局、search_unit、索引条目或 MCP 暴露。
- 部分页面失败产生 completed_with_errors；成功页面被保留。
- 源修复后重试会创建一个新的重试运行，包含 retry_of_run_id 和 retry_scope_pages；原始运行不被重写。
- 重试运行的采纳遵循其自己记录的 apply_on_success。
- 自动重试仅适用于瞬时故障：network_timeout、temporary_provider_error、rate_limited、retryable quota_exceeded、worker_crashed。
- 以下情况需要手动修复：auth_failed、model_not_found、bad endpoint config、model_path missing/inaccessible、source_file missing/changed/conflict、bbox_coordinate_transform_failed、unsupported_file、invalid_page_box。

OCR 采纳事务边界：

- 采纳按 document_instance 序列化。
- 一个 document_instance 可以有多个 OCR 运行正在进行，但一次只能有一个采纳事务更新当前的 OCR/布局指针。
- 事务必须一起提交当前 OCR/布局修订版本指针、search_unit 重新生成或脏标记，以及证据后继链接。
- 本地 FTS 重建在提交后进行，允许滞后；search_index_status 必须变为 stale/partial，直到重建赶上。
- search_library 必须只返回与已提交布局/文本修订版本关联的 search_unit。
- MCP read_mode=current 必须从一个已提交的修订版本集中读取，并且不得在一次响应中混合旧文本和新 bbox/布局。

### 6.10 OCR/HTR 队列

- 队列支持全局、本地、云、按提供程序、按引擎和按预设的并发限制。
- 默认建议：
  - global_max_concurrent = min(4, max(2, logical_cpu_count / 4))。
  - local_max_concurrent = 1，除非本地引擎声明了安全并行能力。
  - cloud_max_concurrent = 2。
  - per_provider_max_concurrent = 1 或提供程序特定的配额。
- 队列支持优先级 + 老化。
- 优先级顺序：
  - interactive_current_page
  - interactive_selected_pages
  - user_started_document
  - background_retry
  - batch_collection
  - maintenance
- 队列支持暂停范围：global、local、cloud、provider、task。
- 第一版不支持预设级暂停。
- 暂停影响尚未开始的任务；取消中断正在运行的任务并遵循 OCR 回滚规则。
- 恢复使用优先级 + 老化重新计算有效优先级。
- 云端 OCR/HTR 没有成本/页面/调用估算确认，也没有额外的隐私/成本警告。UI 只能显示提供程序类型/名称。

### 6.11 OCR 修订版本、重置、隐藏、逻辑删除、清除

- 原始 OCR/HTR 输出默认不可变。
- 用户修正保存为修订版本/增量。
- current_revision 指针控制当前视图。
- 重置级别：
  - 取消设置当前 OCR：移除当前指针；保留历史。
  - 隐藏 OCR 运行：从当前视图/索引/MCP 隐藏；保留数据。
  - 逻辑删除 OCR 数据：从普通 UI/索引/MCP 隐藏；保留逻辑删除标记用于同步/引用处理。
  - 清除 OCR 数据：物理删除 OCR 文本/布局/向量，作为高级维护操作。

跨设备语义：

- 逻辑删除是正常的同步状态，通过快照传播。
- 逻辑删除在导入设备上隐藏目标以免出现在当前 UI/搜索/MCP 中，同时保留足够的身份以将旧证据引用解析为 `tombstoned`。
- 清除在可能的情况下删除有效载荷数据，并留下一个最小清除标记用于证据解析。
- 在 v1 中，清除不得要求重写不可变的历史分片。如果历史分片仍包含有效载荷，应用程序必须在清除后将其视为从当前清单不可达。
- 完整的历史压缩是待办事项。
- 如果设备 B 从较旧的分支中仍有对已清除数据的引用，则在选中的当前分支中解析返回 `purged` 或分支候选项，而不是静默复活。

### 6.12 布局树、文本、表格和 BBox

- 第一版使用可变树层次结构，而不是独立的阅读顺序视图，也不是多父节点图。
- layout_node 支持 node_id、document_instance_id、page_id、parent_node_id、node_type、bbox、own_text、text_policy、reading_order、source、revision_id、confidence、ignored。
- 支持的操作：合并、拆分、移动到新的父节点下、更改类型、更改 reading_order、调整 bbox、从选择创建父节点、分离、标记为忽略/非文本。
- 节点类型是半开放的：内置标准类型加上映射到基类型的用户定义类型。
- 从另一台设备导入的未知自定义节点类型必须被保留，以其基类型显示，不得丢弃。
- text_policy：
  - own
  - aggregate_children
  - none
- index_policy 是类型默认值 + 节点覆盖：
  - container
  - self
  - ignore
  - ignore_subtree
- 普通 bbox 重叠在当前布局树中是被禁止的；ruby、warichu、注释、旁注、印章/戳记和已配置的自定义类型可以重叠。
- OCR 导入/暂存可以临时包含重叠冲突，但非允许的重叠必须在采纳前解决或跳过。
- 在选定的 bbox 上运行的本地 OCR 在单页当前树中插入/替换节点。
- 替换模式在第一版中仅替换显式选中的节点。
- 规范 bbox 使用 normalized_page 坐标 x/y/width/height，范围 0..1。
- normalized_page 以视口为先：相对于应用程序的页面渲染器用于该已提交页面修订版本的实际可见/渲染页面框。
- 降级基准顺序为 crop_box、media_box，然后是 image_bounds。
- 每页修订版本必须记录页面坐标基准、基准尺寸、页面旋转和渲染器基准版本。
- 规范 bbox 归一化为 upright_view；source_bbox 可以保留原始引擎坐标。
- 如果源文件更改，现有 bbox 仅相对于记录的页面基准有效，而不是自动相对于新的源文件。
- 当当前文件验证不再匹配记录的页面基准时，证据和 MCP 响应必须显示 `source_changed` 或 `bbox_basis_stale`。
- MCP 返回 bbox，不生成自然语言位置描述。
- 表格在布局树中使用 table/table_row/table_cell 表示；第一版没有独立的表格模型。
- table_cell 可以存储 row_index、col_index、row_span、col_span、is_header。
- 纯文本输出在安全时默认为 Markdown 表格。
- 不规则表格降级为 `[Table]` 块或按需返回结构化块；应用程序不得编造虚假的规则 Markdown 表格。

### 6.13 页面文本与结构化块

- get_page_text 默认返回布局派生的纯文本。
- 结构、bbox、OCR 边界和证据引用通过结构化格式或 get_page_blocks 显式请求。
- get_page_text/get_page_blocks 支持 read_mode：current、pinned、compare。
- 页面纯文本规则：
  - 按 reading_order 追加页面 search_unit。
  - 连续文本使用单个换行符。
  - 段落/块/列之间使用空行。
  - 默认排除页眉、页脚、页码、忽略的节点。
  - 脚注放在正文之后，带有 `[Footnotes]` 标记。
  - 旁注/注释被排除，除非 include_annotations=true。
  - 表格输出在安全时使用 Markdown 表格。

### 6.14 搜索单元、索引与查询

- search_unit 是持久化的派生表，包含在快照中；本地 FTS 索引是可重建的本地缓存，不同步。
- search_unit 字段包括 unit_id、document_instance_id、page_id、root_node_id、text_revision_id、bbox_revision_id、layout_revision_id、resolved_text、bbox_union、node_type、reading_order、status。
- unit_id 在文本编辑、bbox 编辑、node_type 编辑、reading_order 编辑和小幅移动时保持稳定。
- 拆分/合并/替换/删除-重建会生成新的 unit_id 并使用 supersedes/superseded_by 链接。
- 全文索引从布局树/search_unit 生成。
- SQLite FTS5 是第一个提供程序。SearchProvider 抽象允许以后使用 Lucene.NET/Tantivy。
- CJK 第一版使用字符 n-gram；拉丁文本使用单词令牌；混合文本使用混合分析器。
- 规范文本保持原样。索引文本仅应用最小的技术规范化：Unicode 规范化、大小写折叠、必要的空格处理和拉丁字母数字全角/半角处理。
- 索引文本中没有默认的简体/繁体转换、新旧字形转换、异体替换、历史假名规范化或语义同义词替换。
- 查询重写处理召回：变体、新旧形式、简体/繁体、历史假名、OCR/HTR 混淆、同义词、正则表达式重写、用户词典。
- 搜索配置文件组合重写规则和命令别名。
- 搜索配置文件优先级：显式别名、当前搜索框选择、全局上次使用、系统默认。
- 重写计划默认执行，可在结果中查看；高级设置可以在执行前预览。
- 第一版中重写命中具有相同权重。
- 搜索结果按页面分组，匹配单元在页面内去重。
- search_library 返回游标分页，默认 page_size 为 20，最大 100。
- 不保证精确的 total_result_count；可以返回 estimated_total。
- estimated_total 是近似 FTS/提供程序估算，必须标记为"估算值"。
- 每个 SearchPageResult 默认返回 5 个 matched_unit，最多 20 个；matched_units_has_more 指示截断。
- get_search_result_context 默认返回 2 个前驱和 2 个后继兄弟 search_unit；每侧最多 10 个；不支持跨页上下文。
- get_search_result_context 不包含整个页面文本；请使用 get_page_text。
- 所有上下文单元包括 unit_id、evidence_ref、text、bbox、is_match、reading_order。
- 搜索索引重建是自动的，默认是本地的/部分的；手动重建选中文档/集合/整个库可用于维护。
- 首次同步/导入会调度紧急后台索引重建，但不得阻塞库打开或元数据浏览。
- 脏范围按 document_instance 优先级重建：
  - 当前/打开的文档；
  - 最近修改的文档；
  - 用户固定的集合；
  - 其余库。
- search_library 返回 index_status：current、stale、partial、unavailable。
- stale/partial 返回可用结果，附带 affected_scopes_summary。
- partial 状态必须至少包含 pending_document_count 和 pending_unit_count；当总范围已知时应返回 progress_percent。
- unavailable 返回空结果和原因；不使用 SQL LIKE 或线性扫描降级。

### 6.15 证据引用

- 搜索结果和 MCP 返回稳定的 evidence_ref 以及可选的短生命周期 result_id（用于 UI 会话）。
- EvidenceReference 包括 library_id、document_instance_id、page_id、unit_id、text_revision_id、bbox_revision_id、layout_revision_id、可选的 snapshot_id。
- 默认证据解析模式为 pinned。
- current 通过 unit_id 跟随到当前/最新修订版本。
- compare 返回 pinned 和 current 及其变更标志。
- evidence_ref_id 是长期公开可解析的字符串：`evref:v1:<payload>`。
- 载荷应使用紧凑二进制或 URL 安全的 base64 编码。具体编码是实现定义的但带有版本号。
- v1 接受长的 evref 字符串以保证持久性；短本地别名是待办事项。
- evidence_ref_id 不得包含本地路径、提供程序密钥或未同步的本地状态。
- 旧证据解析返回显式状态：
  - found_pinned
  - superseded
  - tombstoned
  - purged
  - not_found
  - library_mismatch
- superseded 返回后继 evidence_ref 但不自动采纳。
- current/compare 沿着后继链到达 final current，带有最大深度和链摘要。
- 后继分支返回 multiple_current_candidates，不自动选择最新的。

示例证据 Markdown：

```markdown
> 漢字文化圏における書誌記述は...

来源：『近代東亞書誌研究』, p. 42
证据：evref:v1:full:Ab3Z4Q9r7K2mX8pV5nE1sT0uY6cD4fG2hJ9kL3mN8pQ
```

示例载荷仅供说明；实际载荷长度可能因 ID 编码方式而异。

### 6.16 MCP API

- 第一版 MCP 是只读且纯文本的。
- MCP 工具：
  - search_library（搜索库）
  - get_item_metadata（获取题录元数据）
  - get_document_status（获取文档状态）
  - get_page_text（获取页面文本）
  - get_page_blocks（获取页面块）
  - get_search_result_context（获取搜索结果上下文）
- MCP 不提供：
  - run_ocr、edit_ocr、edit_bbox、reset_ocr、purge_ocr、update_metadata、delete_anything。
  - resolved_path、本地文件系统路径、open_original、file:// URL。
  - 提供程序密钥或提供程序配置详情。
  - 缓存图像或图像路径。
- get_document_status 返回 has_ocr_text、has_current_layout、is_search_indexed、source_file_status。
- 通过 MCP 暴露的 source_file_status 值限于：available、missing、offline_root、changed、conflict、unknown。
- source_file_status 有目的地暴露证据可用性，而不是本地路径或根目录名称。
- has_ocr_text 表示当前文档具有可读的 OCR/HTR 文本；并不意味着可以运行 OCR。
- MCP 从不触发 OCR 或索引重建。
- MCP 搜索使用两步模式：先 search_library 获取结果，然后 get_search_result_context 获取证据上下文。

### 6.17 UI 证据复制

- UI 支持复制证据引用和复制证据 Markdown。
- 第一版不支持复制页面引文或复制页面证据引文。
- 证据 Markdown 包含引用的 pinned 文本、最小来源和证据 evref。
- 来源是标题 + page_label/page_index。
- 证据 Markdown 默认文本必须与 pinned evidence_ref 匹配。
- 如果需要 current 修订版本，复制 Current Evidence Markdown 是一个显式操作。

### 6.18 缓存

- 页面渲染、缩略图、OCR 中间图像和叠加层是本地可重建的缓存。
- 它们不进入数据库分片、发布的快照或同步。
- 数据库只能存储缓存元数据。
- 如果源文件缺失/离线，UI 可能将旧的页面渲染缓存显示为 stale_possible 预览。
- MCP 从不返回缓存的图像或图像路径。

### 6.19 向量

- 嵌入是可选的增强功能。
- 全文搜索是核心功能。
- 默认不为整个库生成向量。
- 可选的生成范围包括：集合、标签、选中文档、语言或 OCR 预设输出。
- text_revision 变更会将嵌入标记为 stale。

## 7. 实现决策

实现依据在 `.agent/adr/` 中；本 PRD 仍然是产品契约。

### 7.1 模块结构

- **Patchouli.UI**：原生 Avalonia 桌面 UI。
- **Patchouli.Core**：领域模型和应用程序契约。
- **Patchouli.Infrastructure**：SQLite、迁移、快照、文件解析、凭据、OCR 编排和具体服务实现。
- **Patchouli.Search**：搜索单元生成、SQLite FTS5 提供程序、查询重写、搜索配置文件和脏索引重建。
- **Patchouli.Ocr**：OCR/HTR 预设、预设版本、适配器、队列契约、重试、暂存和候选采纳。
- **Patchouli.Mcp**：只读纯文本 MCP DTO 和服务接口。

### 7.2 架构决策记录

- `.agent/adr/0001-keep-runtime-database-out-of-sync-roots.md`
- `.agent/adr/0002-use-content-addressed-sqlite-snapshot-shards.md`
- `.agent/adr/0003-keep-original-files-outside-the-database.md`
- `.agent/adr/0004-use-path-independent-library-identity.md`
- `.agent/adr/0005-separate-item-fileasset-and-documentinstance.md`
- `.agent/adr/0006-version-ocr-presets-for-provenance.md`
- `.agent/adr/0007-stage-ocr-before-current-adoption.md`
- `.agent/adr/0008-use-layout-tree-as-source-for-search-units.md`
- `.agent/adr/0009-make-evidence-refs-stable-and-pinned-by-default.md`
- `.agent/adr/0010-keep-mcp-read-only-and-text-only.md`
- `.agent/adr/0011-create-branches-on-snapshot-divergence.md`
- `.agent/adr/0012-use-dapper-and-manual-sql.md`
- `.agent/adr/0013-use-dotnet-and-avalonia-for-the-desktop-app.md`
- `.agent/adr/0014-use-mineru-as-first-product-ocr-provider.md`

## 8. 规模假设与性能目标

以下是 v1 设计目标，而非硬性产品限制。

| 领域 | V1 目标 |
|---|---|
| 题录 | 50k 条 |
| 文档实例 | 100k 个 |
| 页面 | 500 万页 |
| 运行数据库逻辑数据 | 20GB（不包括原始文件和缓存） |
| 快照分片大小 | 目标 512-768MB，硬上限低于 1GB |
| 库打开 | 本地热数据库在 5 秒内可用元数据 |
| 搜索当前索引 | 常见查询返回第一页的 p95 低于 1 秒 |
| 搜索部分索引 | 在同一分页规则下返回可用的已索引结果 |
| get_page_text | 已缓存已提交布局文本的 p95 低于 300ms |
| get_page_blocks | 已缓存已提交结构化块的 p95 低于 800ms |
| 快照发布小增量 | 普通元数据/OCR 增量低于 30 秒 |
| 快照导入验证 | 流式哈希验证，大型库可见进度 |
| OCR 队列 UI 响应性 | 队列操作在 500ms 内反映到 UI |

## 9. 测试决策

- 测试应验证外部行为和持久不变量，而非实现细节。
- 快照测试应覆盖分片重用、清单正确性、当前指针更新、分支冲突保留和 sensitive_mutable 凭据分片分离。
- 库标识测试应覆盖创建、重命名、移动、跨库证据不匹配和每库快照边界。
- 文件解析测试应覆盖 known path available、moved_candidate、missing file、offline_root、changed file、conflict、quick hash match、完整 BLAKE3 确认和 source_changed 传播。
- 元数据测试应覆盖题录/文档实例/文件资产关系以及可扩展标识符。
- 凭据测试应覆盖同步包含性、不包含在不可变分片中、紧急清除/撤销和 MCP 不暴露。
- OCR 生命周期测试应覆盖暂存预览隔离、取消回滚、apply_on_success true/false、按页面候选采纳、completed_with_errors、重试运行溯源、bbox 转换失败导致的硬页面拒绝以及序列化采纳。
- 队列测试应覆盖并发限制、优先级排序、老化、暂停/恢复范围、取消行为、瞬时重试和需要手动修复的故障。
- 布局测试应覆盖 text_policy 解析、index_policy 遍历、bbox 重叠约束、树形变更操作、自定义类型保留和表格 Markdown 降级。
- 页面坐标测试应覆盖 crop/media/image 基准降级、upright_view 归一化、source_changed 和 bbox_basis_stale 警告。
- 搜索单元测试应覆盖编辑时稳定的单元身份以及拆分/合并/替换时创建新单元。
- 搜索测试应覆盖查询重写、搜索配置文件选择、页面聚合、匹配单元截断、分页、stale/partial/unavailable 索引状态、进度字段和无线性降级。
- 证据测试应覆盖 evref 编码/解码、pinned/current/compare、superseded 后继、tombstone、purge、not_found、library_mismatch、分支候选项和最大链深度。
- MCP 契约测试应验证没有写入工具、没有 OCR 触发器、没有提供程序密钥、没有提供程序配置、没有本地路径、没有文件 URL、没有图像以及正确的纯文本响应。
- UI 聚焦测试应覆盖证据 Markdown 生成、pinned/current 复制行为、索引重建状态、分支警告、credential_missing 状态以及第一版中没有复制页面引文。

## 10. 验收标准

| 编号 | 验收标准 | 验证方式 |
|---|---|---|
| AC1 | 用户可以创建/导入具有稳定 library_id 的个人库，并可以重命名/移动而不更改标识。 | 库标识测试 |
| AC2 | 用户可以添加题录元数据，附加文档实例，并通过搜索根目录定位文件。 | 元数据 + 文件解析测试 |
| AC3 | 数据库同步可以发布内容寻址的 SQLite 数据分片快照，而无需同步 WAL/SHM 运行时文件。 | 快照发布测试 |
| AC4 | 提供程序凭据可以通过可变凭据存储同步，不会写入不可变的历史数据分片。 | 凭据分片测试 |
| AC5 | 多写入者快照分歧创建分支，从不静默执行最后写入者胜出。 | 分支冲突测试 |
| AC6 | 缺失或已变更的源文件不会移除元数据、OCR、布局、搜索单元或证据引用。 | 文件状态 + 证据测试 |
| AC7 | 已变更的源文件在证据依赖于旧页面基准时显示 source_changed/bbox_basis_stale 警告。 | 页面坐标测试 |
| AC8 | OCR/HTR 运行可以暂存、取消、按页面完成/失败、重试和根据预设版本设置采纳。 | OCR 生命周期测试 |
| AC9 | OCR 采纳以每个 document_instance 为单位原子性地提交当前修订版本、脏 search_unit 和后继链接。 | 序列化采纳测试 |
| AC10 | 坏的 bbox 坐标转换阻止该页面进入 OCR/布局/搜索/MCP。 | OCR 硬失败测试 |
| AC11 | 布局节点可以被修正并产生搜索单元，而不会导致父子节点重复索引。 | 布局 + 搜索单元测试 |
| AC12 | 搜索返回页级结果，带有证据引用、匹配单元截断、游标分页、感知进度的索引状态，以及在不可用时没有线性降级。 | 搜索契约测试 |
| AC13 | MCP 可以搜索、读取元数据/状态/页面文本/页面块/上下文，并且不能改变库状态或暴露本地路径/密钥/图像。 | MCP 契约测试 |
| AC14 | 证据引用是长期可解析的，默认为 pinned，并显式解析旧/superseded/tombstoned/purged 引用。 | 证据测试 |
| AC15 | UI 可以复制证据引用和证据 Markdown（带 pinned 文本和 evref），但在 v1 中不暴露复制页面引文。 | UI 测试 |

## 11. 重要待办事项

### v2 候选

- 题录级引文生成。
- CSL 样式支持。
- 参考文献导出。
- 引用键 / Better BibTeX 类似工作流。
- 完整 CSL 字段映射。
- 多语言标题和音译标题。
- 详细的版本/历史字段。
- 短本地证据别名。
- 区域级 OCR 合并/采纳。
- 派生的阅读顺序视图。
- 查询重写加权。
- Lucene.NET / Tantivy SearchProvider 评估。

### v3 候选

- 团队/共享库工作流。
- 规范控制和创作者消歧。
- 更复杂的多写入者同步合并。
- 多父节点布局图。
- 完整表格语义模型。
- 向量/混合/语义搜索作为一等工作流。
- 库级加密或每设备凭据解封。

### 待办事项/研究

- MeCab / Sudachi / Jieba 分析器。
- BBox 重叠候选替换。
- OCR 数据清除压缩。
- 如果可复现性要求增加，进行更强的模型指纹识别。
- 程序管理的文件同步。
- 如果首次同步体验被证明过于严格，在 FTS 重建前提供受控的小范围子串降级。

## 12. 版本管理理念

- v1 应保守：保护证据，暴露歧义，拒绝不安全的自动化。
- v1.1 PRD 明确了开发契约；不应静默扩展产品范围。
- v2 应关注引文工作流、更好的搜索/排序和受控的证据 UX 改进。
- v3 可能重新审视协作、自动合并、更强的加密和机构工作流。
- 削弱证据可复现性的功能必须是选择加入并显式标记。

## 13. 补充说明

- 第一版针对个人使用、证据正确性、本地优先存储和显式溯源进行优化。
- 核心产品假设是：OCR/布局/搜索/证据可以被视为用户自有文件之上的一个版本化、可检查的知识层。
- 应用程序应在可能导致证据变得模糊的情况下优先采用保守的失败行为。
- MCP 接口应保持小巧、可预测、纯文本且对外部 agent 安全使用。
