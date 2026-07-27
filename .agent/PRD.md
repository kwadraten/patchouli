# Patchouli PRD v2 正式版

状态：正式版
日期：2026-07-13

### 0.2.0 Document Box Tree 修订

0.2.0 是有意的 schema/API 断代：fresh SQLite library 只包含页级 `document_tree_revisions` 与 `document_boxes`。应用必须拒绝 0.1.x/未知 epoch；不提供 LayoutRevision/LayoutNode migration、兼容 adapter、双写或 `evref:v1` 解码。ADR `0015` 与本节覆盖下方 v1.1 历史基线中关于 legacy layout schema 的描述。

每个物理 Page 独立拥有一个 current immutable DocumentTreeRevision。Box sibling 指针是唯一顺序；只有 `logical_page` 可有 children；leaf 使用 typed payload。中央 Markdig 管线统一验证、确定性 Markdown、纯文本和原生 Avalonia 预览，raw HTML 禁用，AST/SourceMap 不持久化。

## 1. 产品定位

Patchouli 是桌面优先的个人文献管理器，围绕题录、用户自有源文件、页级 Document Box Tree 修订、搜索单元和稳定证据引用构建。

v2 的目标是推出最终用户可用版本。这里的“可用”指：

- 用户可以通过 UI 正确管理库数据库、同步根、文件搜索根、快照发布/导入和冲突处理。
- 用户可以管理 CSL 样式，并从题录生成、复制和导出正确的 CSL 题录/参考文献文本。
- MCP 高度可用：可配置、可鉴权、可被局域网或本机写作工具稳定读取，并向 agent 提供题录、证据和 CSL 输出。
- OCR 编辑器可以支持真实生产 OCR、局部识别、候选采纳和 bbox 冲突处理。
- 设置、菜单栏、右键菜单、书库表格和后台任务看板具备一致的信息架构，而不是 alpha 阶段的功能堆叠。

核心假设保持不变：OCR/布局/搜索/证据是用户自有文件之上的一个版本化、可检查的知识层。长期不变量的权威位置是 `.agent/CONTEXT.md` 和 `.agent/adr/`。

PRD 仍保留这些短语作为测试锚点：MCP 从不触发 OCR 或索引重建；搜索配置文件；本地 FTS 索引是可重建的本地缓存；提供程序凭据；缓存图像；MCP never returns cached images or image paths；page_renders；第一版 MCP 是只读且纯文本的；MCP 无法读取提供程序密钥；作为独立分支打开以供检查；v1 不执行自动对象级合并；不得在分支间静默执行最后写入者胜出。

## 2. 成功标准

v2 完成时，用户应能完成以下端到端工作流：

1. 创建或打开本地库，配置同步根和文件搜索根，首次扫描以阻塞式任务完成。
2. 扫描 PDF 后得到可编辑题录和主文档实例；未知题录进入 `general` 类型，用户被引导细分为 CSL 支持的具体类型。
3. 在书库 DataGrid 中排序、调整列、隐藏列、查看 OCR/索引状态、页数和关联文件名。
4. 在题录编辑器中按 CSL type 填写合适字段，保存 creator、date、identifier 和扩展 CSL 字段。
5. 管理 CSL 样式，复制或导出单条/多条题录；遇到 `general` 或渲染错误时得到明确 warning/error，而不是空结果。
6. 配置 MCP 端口、bind、CORS 和 token；MCP 可返回题录、证据、文档状态和 CSL 输出，但不暴露路径、图片、密钥或 OCR 配置。
7. 使用 MinerU 或其他生产 OCR provider 运行 OCR，所有 provider 输出进入同一 `OcrDocumentTreeCandidate` staging/adoption 流水线。
8. 在 OCR 编辑器里局部识别、预览候选、处理 bbox 冲突并采纳结果。
9. 通过清晰菜单、右键菜单、状态栏、阻塞弹窗和冲突弹窗理解当前操作后果。

## 3. v2 功能需求

### 3.1 MCP Server 可配置化

UI 与 MCP server 层必须支持以下配置：

- 端口：用户可配置 MCP HTTP server 监听端口，并显示端口占用/启动失败原因。
- Bind 地址：支持 `127.0.0.1` 和 `0.0.0.0`，其中 `0.0.0.0` 必须带明显的安全说明和鉴权要求。
- CORS：允许配置启用/禁用 CORS、允许源列表和预检请求行为。
- 鉴权 token：用户可直接输入自定义 Token，或点击生成随机 Token。Token 作为桌面应用的本地凭据，允许用户在本机 UI 查看明文以便复制核对。
- Server 状态：设置页显示 stopped/running/error、监听地址、端口、CORS 状态、鉴权状态和最近错误。
- 工具开关：设置页允许用户针对性启用或禁用 MCP 中的特定工具；禁用后的工具不得出现在可调用工具列表中，直接调用时返回明确的 disabled/tool_unavailable 错误。
- MCP server 不得因配置读取失败而静默降级为无鉴权公网监听。
- MCP 配置必须有独立模型，例如 `McpServerSettings`：port、bind_address、cors_enabled、allowed_origins、auth_required、token、tool_overrides、updated_at。
- 鉴权 token 是本机 MCP 访问 secret，不是 OCR provider credential。它默认保存在本机 `appsettings.json` 的 local-only 设置范围，不进入库快照或 branch import。
- HTTP 请求鉴权使用 `Authorization: Bearer <token>`；失败返回 401，不把 token 写入日志。
- `/health` 可以无鉴权返回 minimal status；MCP JSON-RPC endpoint 必须鉴权，除非 bind 为 `127.0.0.1` 且用户显式关闭鉴权。

MCP 能力继承长期边界：MCP 从不触发 OCR 或索引重建，不暴露本地路径、缓存图像、提供程序凭据或 OCR provider 配置。v2 新增 CSL 渲染能力仍是只读读取和格式化，不得写入题录、样式或布局。

### 3.2 题录编辑 UI 与 CSL Type Profiles

当前题录编辑页把所有 `ItemType` 共享为一套表单，这会导致 journal article、book、chapter、thesis、webpage、manuscript 等类型显示大量不相关字段，也会隐藏某些类型关键字段。v2 必须改为 type-aware 编辑器。

CSL JSON schema 只把 `id` 和 `type` 作为通用必需字段；字段全集由 CSL 变量集合定义，实际样式会按 item type 和变量存在性渲染。因此 Patchouli UI 不能把“所有 CSL 字段”简单铺成一个巨大表单，也不能假设 schema 会给出每种 type 的唯一必填字段。v2 需要维护自己的 `CslItemTypeProfile`。

`CslItemTypeProfile` 至少包含：

- `item_type`
- `display_name`
- `description`
- `primary_fields`：该类型首屏核心字段。
- `recommended_fields`：常用但非首屏字段。
- `advanced_fields`：低频 CSL 字段，进入高级/更多字段区。
- `creator_roles`：该类型常用 name variables，例如 author、editor、translator、interviewer。
- `date_roles`：该类型常用 date variables，例如 issued、accessed、original-date、event-date、submitted。
- `identifier_schemes`：该类型优先显示的标识符，例如 DOI、ISBN、ISSN、URL、PMID、patent number。
- `field_labels`：类型内字段标签覆盖，例如 chapter 的 `container-title` 显示为“书名/论文集名”，article-journal 显示为“期刊名”。
- `hidden_by_default_fields`：不在默认 UI 展示但保留数据的字段。

v2 首批必须支持这些 profile：

| Type | 首屏核心字段 | 典型推荐字段 |
|---|---|---|
| `general` | title、author/editor、issued、publisher、publication-title、DOI、ISBN、URL、note | 所有 CSL 对齐字段和 `extra_csl` 均可通过“更多字段”填写 |
| `book` | title、author/editor、issued、publisher、publisher-place、edition、ISBN | collection-title、volume、number-of-volumes、language、original-title |
| `article-journal` | title、author、issued、container-title、volume、issue、page、DOI、ISSN | container-title-short、status、PMID、PMCID、URL |
| `chapter` | title、author、container-title、editor、issued、publisher、publisher-place、page | chapter-number、collection-title、edition、translator、DOI/ISBN |
| `thesis` | title、author、issued、publisher、publisher-place、genre | archive、URL、language |
| `report` | title、author/institution、issued、publisher、number、genre | collection-title、URL、DOI |
| `webpage` | title、author、issued、accessed、URL、container-title | publisher、language |
| `manuscript` | title、author、issued/original-date、archive、archive_location、call-number | archive-place、genre、language |
| `paper-conference` | title、author、issued、container-title、event-title、event-place、page、DOI | publisher、publisher-place、ISBN |
| `patent` | title、author/inventor、issued、number、authority、jurisdiction | status、references、URL |
| `standard` | title、author/institution、issued、number、publisher | authority、version、URL |

UI 整理要求：

- 顶部保留 item type selector；切换 type 时不删除任何已有字段值。
- 首屏显示 profile 的 `primary_fields`；“更多字段”区域显示 recommended/advanced。
- 不适用于当前 type 的已有字段要进入“其他已保存字段”区域，提示“该字段不会丢失，但当前类型通常不使用”。
- 字段保存必须仍写入现有 Item 模型或 `extra_csl`，不能只存在于 UI。
- creator 编辑器必须支持 family/given/literal，而不是只支持一个 Literal 文本框。
- 日期编辑器必须支持 literal 和 date-parts 两种输入；至少能保留当前 literal 行为。
- identifiers 不再是保存后才能添加的孤立动作；新建题录时可以暂存 identifiers，保存 item 时一起提交。
- 编辑器右侧应有 CSL 预览，使用当前默认 CSL 样式渲染；缺字段时显示 warnings，而不是阻止保存。若 item type 是 `general`，预览区必须显示不可渲染警告，不得伪造 CSL 结果。
- `ItemEditorViewModel` 不应继续暴露一组固定字段作为全部 UI；v2 应引入 `ItemFieldDescriptor` / `EditableItemField`，由 `CslItemTypeProfile` 生成可见字段。

实现层需要新增 `ICslItemTypeProfileService` 或等价模块。profile 可以先用内置 JSON/代码常量提供，后续再允许用户自定义；但 v2 不要求自定义 profile UI。

### 3.3 初次 PDF 扫描与 `general` 类型

当前初次 PDF 导入路径会把 CSL type 直接写成 `document`。这把“文件是一个 PDF 文档实例”和“书目条目是 CSL document 类型”混在了一起。v2 必须把 PDF 文件发现、文档实例创建和题录类型分类拆开处理。初次导入应效仿 NoteExpress 一类文献管理器的宽松录入思路，使用 Patchouli 内部 `general` 类型承接未知题录，而不是把未知 PDF 静默确认为 CSL `document`。

- `PdfDiscoveryService` 只发现文件候选、页数、大小、mtime、hash 等文件事实，不直接声明 CSL item type。
- `PdfImportWorkflow` 创建的是 skeleton item + primary `DocumentInstance`。如果没有可靠书目信息，题录 `item_type` 写为 Patchouli 内部类型 `general`，并进入“待细分/待补全”状态，而不是静默确认为 CSL `document`。
- `general` 是 UI/数据录入类型，不是 CSL 类型。它的编辑表单必须宽松：所有 CSL 对齐字段、identifier、creator、date 和 `extra_csl` 都允许填写；字段不因当前 type 不匹配而隐藏到不可编辑状态。
- 保存 `general` 题录时 UI 必须显示明确警告：该条目尚未细分为 CSL 支持的类型，后续无法生成正式 CSL 题录；建议用户选择 book、article-journal、chapter、thesis、report、webpage 等更具体类型。该警告不阻止保存。
- CSL renderer 不得把 `general` 自动映射成 CSL `document`。复制 CSL 题录、导出题录和 MCP CSL 渲染遇到 `general` 时必须阻止渲染，返回 warning/error：`general_type_not_renderable`，并给出“请先细分题录类型”的恢复动作。
- 自动类型推断只能产生“建议”，不能在低置信度时直接确认。可用信号包括 DOI/ISBN/ISSN 或外部元数据 lookup 返回的类型、导入的 CSL JSON/BibTeX 类型、PDF/XMP 内嵌 metadata、文件名/目录名启发式、OCR 或文本层首屏抽取结果。
- 只有高置信度来源或用户选择可以把 `general` 转换为具体类型。建议状态至少包括 `general`、`suggested`、`confirmed`、`rejected`。
- 建议新增 `ItemTypeInference` 或等价模型：`item_id`、`suggested_type`、`confidence`、`source`、`evidence_summary`、`created_at`、`accepted_at`。
- 题录列表和导入结果页应提供 `general` / “待细分”过滤器；批量导入后优先引导用户确认 type、title、creator、issued 和 identifier。
- MCP 的题录元数据响应应暴露 `general` 状态和 warnings，让 agent 知道该 item 还不是可渲染的 CSL item。

### 3.4 CSL 样式管理与题录生成

v2 必须实现独立的 CSL 样式管理 UI 和题录生成功能。

- 接入 Zotero 中文社区 CSL 样式入口 `https://zotero-chinese.github.io/styles/`，并保留接入 Zotero 官方 CSL 样式仓库的能力。
- 样式索引应缓存到本地，支持刷新、搜索、安装、更新、禁用/删除本地样式。
- 样式详情至少显示名称、id、来源、更新时间、语言/地区提示、是否已安装和本地版本。
- CSL 样式文件应存储在库或用户配置的可管理位置，并记录来源 URL 与内容哈希。
- 题录详情页和题录列表右键菜单支持“复制 CSL 题录”。
- 复制时使用当前默认 CSL 样式；右键二级菜单允许选择最近使用样式。
- CSL 样式管理器允许选择默认 CSL 样式和输出 locale；它是独立工作区，不属于全局设置页。
- 题录生成必须基于 Item 的 CSL 对齐字段、creator/date/identifier 结构化数据和 `extra_csl`。
- v2 应新增 CSL 服务边界，例如 `ICslStyleCatalog`、`ICslStyleStore`、`ICslItemMapper`、`ICslRenderer`。
- 需要持久化 `csl_styles` 与 `csl_settings`，至少记录 style_id、display_name、source_url、source_kind、content_hash、installed_at、updated_at、locale、is_default。
- Item 到 CSL JSON 的转换必须是可测试的纯映射：不在 renderer 中临时猜字段。
- CSL renderer 失败必须返回 warning/error，不允许复制空字符串并报告成功。
- MCP 对外提供 CSL 题录信息，供人类和 agent 学术写作使用。建议新增只读能力：`list_csl_styles`、`get_csl_style`、`render_item_bibliography`、`render_items_bibliography`。
- MCP 返回 CSL 输出时必须包含 style id、style display name、locale、item ids、rendered text/html 和可选 warnings。

### 3.5 OCR 编辑器与局部识别

v2 必须把 OCR 编辑器从 alpha 预览推进到可用工作台。

- PDF 工作台显示页面图像、当前 DocumentBox、bbox、sibling 顺序和选中 Box 属性，并通过中央 Markdig 提供无 WebView 的原生预览。
- 支持框选区域后运行局部 OCR。
- 支持局部 OCR 结果作为短生命周期 leaf payload diff；接受前不得修改 bbox、parent、sibling 或 suppressed，也不污染 current/search/MCP。
- 支持候选结果对比、按区域/页面采纳、撤销采纳前操作。
- 支持显式 insert/update/move/split/merge/delete、bbox 与 suppressed 命令；所有命令只修改 page draft。
- 对普通 bbox 重叠显示冲突，而不是静默保存。
- ruby、边注等允许重叠类型必须有显式节点类型或样式声明。
- OCR 编辑器中所有会改变证据 current 的操作都应显示 search index stale/partial 的后果。
- 局部 OCR 的输入是 page + normalized bbox + OCR Preset Version；输出仍进入 MinerU-compatible 中间结构。
- 局部 OCR 采纳默认只替换用户显式选中的节点或空区域，不自动推断删除周边节点。
- 普通 sibling bbox overlap 必须产生结构化冲突 `CF-06`，且失败命令不得部分修改 draft。

### 3.6 生产 OCR Provider

v2 不再把 Mock、历史本地 CLI OCR、本地占位 OCR 作为用户可见或生产可选 OCR。MinerU 仍然是首选生产 OCR/provider，但 MinerU JSON 只是导入格式，Document Box Tree 才是 OCR 文本、编辑、bbox、搜索和证据的 canonical model。

- Mock OCR 只允许存在于测试工程或明确的开发测试路径。
- 历史本地 CLI OCR 和 local placeholder 不应出现在最终用户 UI、默认设置、首轮初始化或生产 preset 中。
- v2 可新增多模态 LLM OCR provider，作为 MinerU 之外的生产 OCR 路径之一。
- 任何新增 OCR provider 都必须输出 provider-neutral `OcrDocumentTreeCandidate`，再进入 shared tree importer/adoption service。
- candidate 表达 page、leaf type/subtype、typed payload、bbox、confidence、source order 与 suppressed；provider order 只初始化 sibling pointers。
- Provider-specific 完整原始响应不进入 canonical database、snapshot、编辑、搜索或 MCP 数据面。
- MinerU 规则表格转为单一 GFM leaf；不规则表格保存 `[Table]` + diagnostic；不得保存 table-cell rows。
- 不允许 provider 直接写 `document_boxes`；所有 provider 必须走同一个 import/adoption service。
- OCR provider 配置只负责保存和使用用户提供的 token/secret key、endpoint、model id 和必要参数。
- Patchouli 不负责账号注册、配额购买、余额检查、成本估算或云端计费策略。
- provider 返回的认证、限流、配额、模型不可用等错误按普通 provider 错误展示，不进入账号管理流程。
- 所有 provider secret 必须由本机 `appsettings.json` 的 local-only Credentials 范围唯一保存：不进 MCP、不进日志、不进快照或不可变历史分片。

### 3.7 设置 UI

设置 UI 需要变成最终用户可理解的控制台。

- 设置页包含五个有直接编辑模型和持久化所有权的分组：库与本机路径、同步与快照、MCP 服务与安全、OCR Provider、元数据来源。
- 文件搜索根、排除规则和“记住上次打开的数据库”属于“库与本机路径”，不再拆成单独分组。
- “同步与快照”保存本机同步目录、稳定设备身份、随库同步范围和同步状态；发布、导出、接收、分支检查与冲突解决仍在菜单栏的 Sync 工作流和同步中心完成，不能塞进设置页的 Save/Discard。
- 本机 `appsettings.json` 是设置的默认且唯一 owner。用户明确启用某个“随库同步”范围后，才将该范围的同步基础值迁入运行库数据库；JSON 此后保留同步策略、设备 override、设备 binding 和所有 local-only secret value。关闭同步时必须把当前 effective value 物化回 JSON，不能保留两个可写 base。
- CSL 样式管理器是独立工作区；重建索引、刷新 CSL 索引、清理缓存和打开日志目录是各自领域的维护动作。它们不应为了导航完整性被塞入空的设置分组。
- “关于”保持为独立标签页，不属于设置分组。
- 在“库与本机路径”分组中，必须提供“记住上次打开的数据库”开关，并确保状态能持久化保存。
- 每个设置分组显示保存状态、验证状态、上次错误和需要重启/重载的提示。
- 增加文件搜索根时必须触发阻塞式扫描流程；扫描完成前不得假装配置已完全可用。
- Provider secret 属于最终用户；可信本机设置 UI 允许明文显示、复制和修改，同时继续遵守不进 MCP、不进日志和不进不可变历史分片的边界。
- 维护动作在其所属工作区或菜单中提供：搜索提供重建索引，CSL 管理器提供刷新索引；缓存清理和打开日志目录在本机维护入口提供。
- v2 设置页不直接拼接数据库 SQL；所有设置变更走服务接口，以便阻塞/冲突/secret 处理可测试。
- 每个可保存分组必须显示 dirty/saving/saved/failed 状态，避免用户误以为已经持久化。

### 3.8 菜单栏、右键菜单与反馈

v2 需要整理菜单信息架构。

- 菜单栏按任务组织：Library、Sync、Items、OCR、Search、MCP、Settings、Help。
- Sync 菜单至少提供“发布到同步目录”“导出快照包”“接收/检查快照”和“打开同步中心”；所有入口复用同一 command descriptor 和状态模型。
- 右键菜单按对象组织：题录、文档实例、页面、布局节点、搜索结果、证据引用。
- 题录右键菜单必须包含：编辑元数据、打开文档、运行 OCR、复制证据 Markdown、复制 CSL 题录、导出题录、查看同步/冲突状态。
- 与当前选择无关的命令隐藏或禁用，并显示禁用原因。
- alpha/dev 命令不得出现在生产默认菜单中。
- v2 应引入 UI command 描述层，例如 `UiCommandDescriptor`：id、label、scope、enabled、disabled_reason、danger_level、handler。
- 菜单栏、右键菜单和快捷键应复用同一命令描述，避免同一个动作在不同入口有不同可用性判断。
- 不引入 Toast 组件。轻量级成功/错误提示走 MainWindow 底部状态栏，阻塞式长任务走独立模态弹窗。

### 3.9 OCR 队列看板

v2 必须将 OCR 队列标签页从“开发者控制台”重构为面向最终用户的“后台任务看板”，严格贯彻删除而非隐藏不直觉内容的原则。

- 完全删除所有依赖手动输入散列 ID（如 `DocumentInstanceId`、`PresetId`、`TaskId` 等）的“添加任务”、“暂停范围”和“取消任务”表单面板。
- 顶部面板使用直观卡片/看板展示健康度：排队中、运行中、已完成、失败/阻塞。
- 仅保留“刷新列表”、“全部暂停”、“全部恢复”的全局操作按钮。
- 列表不再仅展示散列 ID。ViewModel 必须请求关联的真实文献标题 (Item Title) 并展示在行内。
- 直接在任务列表每一行末尾提供“暂停”、“恢复”、“取消”的行级操作按钮，支持针对单一任务的直接操作。

### 3.10 书库 DataGrid

当前基于 ListBox 的伪表格无法满足现代文献管理器的高效数据操作需求。v2 必须引入 Avalonia 官方 `DataGrid` 控件重构书库列表。

- 利用 DataGrid 提供列宽拖拽调整、列顺序拖动交换、点击表头升/降序排序等原生功能。
- 除基础题录信息外，新增“OCR/索引状态”、“页数”和“关联文件名”等列。
- 允许用户自由隐藏或显示特定列，例如通过主菜单栏“视图 -> 书库列”或表头右键菜单。
- 列可见性和顺序设置必须跨重启持久化，建议通过 `LibraryPreferences` 或等价模型保存到应用配置中。

## 4. 阻塞与冲突语义

v2 UI 必须把阻塞和冲突作为一等状态表达，而不是普通状态字符串。

### 4.1 强制阻塞语义

强制阻塞表示用户必须等待、取消或完成处理，否则下一步会产生不完整或错误状态。

| 场景 | UI 语义 |
|---|---|
| 初始化时扫描整个文件夹根 | 显示阻塞式进度弹窗；允许取消；完成前不得进入“库已准备好”状态。 |
| 设置页面加入文件搜索根触发扫描 | 显示阻塞式扫描任务；完成前该 root 标记为 `scanning`，相关文件解析显示 pending。 |
| 快照导入验证 | 显示阻塞式验证任务；失败不得修改当前活动库。 |
| MCP server 绑定 `0.0.0.0` 但无 token | 阻止启动，并要求先配置鉴权 token。 |
| CSL 样式刷新或安装失败 | 不阻塞库使用，但阻塞该样式成为默认样式。 |

在 Avalonia 桌面应用中，阻塞组件不应是页面内遮罩，而必须是独立的模态对话框。阻塞弹窗应统一显示：标题、原因、影响范围、进度、可取消性、失败后的恢复动作，并提供可滚动日志/详情区域，以输出细粒度执行进度，例如正在处理的具体文件路径。

实现层需要统一阻塞状态模型，例如 `BlockingOperation`：

- `operation_id`
- `operation_type`：initial_root_scan、file_search_root_scan、snapshot_import_validation、mcp_start_validation、csl_style_install
- `scope_type` / `scope_id`
- `status`：pending、running、blocked_waiting_user、succeeded、failed、cancelled
- `progress_current` / `progress_total` / `progress_label`
- `can_cancel`
- `failure_code` / `failure_message`
- `next_actions`

UI 不得为每个阻塞流程自造状态字符串；所有阻塞弹窗、设置页 banner 和状态栏都读同一模型。

### 4.2 冲突语义

| 编号 | 领域 | 冲突 | 触发条件 | UI 处理 |
|---|---|---|---|---|
| CF-01 | 快照同步 | 书目内容 ID 冲突 | 同一 `item_id` 在分支与目标库中存在，但 Title/Type 不同（`same_id_different_content`）。 | 分支检查页列出两侧题录摘要；要求用户选择保留本地、导入为新题录或跳过。 |
| CF-02 | 快照同步 | 主要文档实例冲突 | 导入分支 `is_primary=1` 文档，但本地该题录已有主要文档（`primary_document_conflict`）。 | 显示两侧文档信息；允许保留本地主要文档、改为备用文档、替换主要文档。 |
| CF-03 | 快照同步 | 凭据丢失警告 | 分支数据库中关联的 Preset 凭据不进行合并（`credential_not_imported`）。 | 非阻塞警告；导入后对应 preset 标记 `credential_missing`，引导用户在设置页重新配置。 |
| CF-04 | 文件解析 | 文件多路径冲突 | relocation 扫描匹配到多个大小与快速哈希相同的本地文件（`ChooseCandidate` / `FileAssetStatus.Conflict`）。 | 文件解析面板列出候选路径、mtime、大小、hash 置信度；要求用户选择或保持 unresolved。 |
| CF-05 | 文件解析 | 源文件被修改 | 绑定路径上的文件哈希改变，导致版面 bbox 对应发生漂移（`FileAssetStatus.Changed`）。 | 文档页显示 source_changed/bbox_basis_stale；current 证据显示警告；允许重新绑定、确认新源或保留旧证据。 |
| CF-06 | 版面编辑 | BBox 选区普通重叠 | 手工编辑或局部重新 OCR 时 bbox 与已有文本块重合，且不属于允许重叠类型。 | OCR 编辑器阻止采纳；高亮冲突节点；提供调整 bbox、改为允许类型、跳过候选的动作。 |

对于 CF-01 到 CF-05，UI 处理应采用独立的冲突解决模态对话框。弹窗必须提供双栏对比视窗（左侧本地现状，右侧传入状态），并在底部提供明确的互斥行动按钮，例如：替换为主文档、作为备用文档保留、跳过。

所有冲突都必须有稳定 code、用户可读描述、影响对象、推荐动作和可测试的状态转换。

实现层需要统一冲突模型，例如 `ConflictDescriptor`：

- `conflict_code`：CF-01 到 CF-06。
- `domain`：snapshot_sync、file_resolution、layout_edit。
- `severity`：blocking、warning、info。
- `object_type` / `object_id`
- `summary`
- `local_snapshot`
- `incoming_snapshot`
- `recommended_actions`
- `selected_action`
- `resolution_status`：unresolved、resolved、ignored、superseded。

现有 `BranchImportConflict` 可以作为快照同步冲突来源，但 v2 UI 应转换到统一 `ConflictDescriptor`，文件解析和版面编辑也应使用同一结构。

## 5. 实现决策

| 主题 | 决策 |
|---|---|
| MCP 配置 | server 运行配置是本机设置；端口/bind/CORS/token 不进入快照。UI 保存后启动 server 使用同一 `McpServerSettings`。 |
| MCP token | 不进同步，不写日志。但在本地 UI 中允许直接回显和配置明文，因为这是桌面单机软件。 |
| MCP CORS | `0.0.0.0` + 无 token 是强制阻塞。 |
| MCP tool 开关 | 设置页的工具启用/禁用必须由 MCP transport/enumerator 和 tools/call 共同执行，不能只隐藏 UI；禁用工具不可出现在工具列表中，直接调用返回 disabled/tool_unavailable。 |
| 反馈 | 不引入 Toast；轻量成功/错误提示走底部状态栏，阻塞式长任务走独立模态弹窗。 |
| CSL renderer | 渲染失败不复制空结果；返回失败状态和 warnings。UI 保持旧剪贴板内容不变。 |
| CSL 数据模型 | v2 需要显式 `extra_csl` 语义。可以先复用 `custom_fields_json` 存储，但 mapper 必须把低频 CSL 变量作为 `extra_csl` 处理并测试。 |
| 题录编辑字段 | CSL schema 不能直接决定每个 type 的表单；v2 维护 `CslItemTypeProfile`。 |
| 初次 PDF 导入 type | 扫描只产生文件事实；导入 skeleton item 使用内部 `general` 类型；保存时警告用户细分；CSL 渲染遇到 `general` 必须阻止并返回 `general_type_not_renderable`。 |
| 切换 item type | 不删除隐藏字段。隐藏字段保留并显示在“其他已保存字段”区域。 |
| 新建题录标识符 | 新建时 identifiers 暂存，保存时与 item 一起提交。 |
| OCR schema | provider 不能直接写 `document_boxes`；必须先转换为 `OcrDocumentTreeCandidate`，再走统一 import/adoption。 |
| 局部 OCR | 区域 OCR 不自动删除重叠块；只替换显式选中节点；普通重叠生成 CF-06。 |
| FileSearchRoot 扫描 | AddSearchRoot 不是单纯 insert；添加 root 必须创建 scan run，并以阻塞操作表达。 |
| 冲突 code | UI 不解析错误字符串；CF-01 到 CF-06 必须是结构化 code/DTO。 |
| 分支导入 | `credential_not_imported` 是非阻塞 warning/conflict item；导入可继续，但相关 preset 必须进入 `credential_missing`。 |
| 菜单 | v2 需要集中 command descriptor，菜单/右键/快捷键共享。 |
| Mock/历史本地 CLI OCR | 不删除测试能力；从生产 UI、默认 preset、首轮初始化和用户菜单清除。 |

## 6. 明确不做

- 向量化、混合搜索和语义搜索推迟到 v2 之后的版本。
- 不做程序托管的原文件同步。
- 不做账号注册、配额购买、余额检查或云端计费管理。
- 不做 MCP 写入、OCR 触发、bbox 编辑、元数据更新或删除操作。
- 不做自动对象级同步合并；分支冲突仍必须显式处理。
- 不做库级加密、主密码或每设备凭据解封。

## 7. 验收标准

| 编号 | 验收标准 | 验证方式 |
|---|---|---|
| V2-AC1 | 用户可以在 UI 中配置 MCP 端口、bind 地址、CORS、鉴权 token 和特定工具启用/禁用，并看到 server 状态与错误。 | UI/ViewModel + MCP server 配置测试 |
| V2-AC2 | MCP 在无 token 时拒绝不安全的 `0.0.0.0` 启动，并且请求鉴权可测试。 | MCP transport/security tests |
| V2-AC3 | 用户可以刷新、搜索、安装、选择默认 CSL 样式，并复制单条或多条 CSL 题录。 | CSL style manager + render tests |
| V2-AC4 | MCP 可以返回 CSL 样式列表和题录渲染结果，且不暴露本地路径或 secret。 | MCP CSL contract tests |
| V2-AC5 | 题录编辑器按 `CslItemTypeProfile` 显示 type-aware 字段，切换 type 不丢字段，creator/date/identifier 可在新建时完整编辑。 | Item editor profile + UI tests |
| V2-AC6 | 初次 PDF 扫描/导入不把未知题录静默确认为 CSL `document`；未知题录进入 `general`，保存时明确警告需要细分，CSL 复制/导出/MCP 渲染必须阻止 `general` 并返回 warning/error。 | Pdf import classification + CSL warning tests |
| V2-AC7 | PDF 工作台使用页级 draft/commit/discard、显式 Box 命令、原生 Markdig 预览、区域候选 diff 和 CF-06 冲突阻止。 | DocumentTree/UI/Markdown tests |
| V2-AC8 | 生产 UI 不再暴露 Mock/历史本地 CLI OCR/local placeholder OCR；测试路径仍可使用 mock。 | UI/menu/settings boundary tests |
| V2-AC9 | MinerU 是首选生产 OCR；所有 provider 把输出规范化为 `OcrDocumentTreeCandidate`，并且无 tree artifact 时拒绝 `full.md` fallback。 | MinerU Document Tree tests |
| V2-AC10 | 初始化扫描和新增文件搜索根扫描以阻塞式任务表达，完成前状态不可误报为 ready。 | Blocking workflow tests |
| V2-AC11 | CF-01 到 CF-06 均有 UI 表达、状态 code、推荐动作和测试覆盖。 | Conflict workflow tests |
| V2-AC12 | 菜单栏和右键菜单按对象/任务组织，不出现 alpha/dev-only 命令。 | XAML/ViewModel menu tests |
| V2-AC13 | AddSearchRoot、首次初始化扫描、快照导入验证和 MCP 启动验证都通过统一 BlockingOperation 模型表达。 | BlockingOperation service tests |
| V2-AC14 | 所有 OCR provider 通过同一 Document Tree import/adoption service 写入 staging，不允许 provider 直写 `document_boxes`。 | OCR schema boundary tests |
| V2-AC15 | CSL item mapper 对核心 CSL 字段、creator/date/identifier、extra_csl、`general` 阻止渲染和 warning 行为有纯单元测试。 | CSL mapper tests |
| V2-AC16 | OCR 队列页面删除手工 ID 输入表单，改为后台任务看板、真实题录标题和行级操作。 | OCR queue UI/ViewModel tests |
| V2-AC17 | 书库列表使用 Avalonia DataGrid，支持列宽、列顺序、排序、列显隐和跨重启持久化。 | Library grid UI/ViewModel tests |
| V2-AC18 | 轻量反馈走底部状态栏，阻塞任务和冲突解决走模态弹窗，且可显示日志/详情。 | UI feedback + blocking modal tests |

## 8. v1.1 完成基线附录

以下条目已从旧 v1.1 PRD 需求正文压缩为完成清单。需要查看长期约束时，优先读 `.agent/CONTEXT.md` 和 `.agent/adr/`。

| 领域 | 已完成能力 | 主要证据 |
|---|---|---|
| 项目边界 | agent 文档集中在 `.agent/`，根目录只保留 `AGENTS.md` 入口；无 `docs/` 依赖。 | `AlphaPackagingTests`、`.agent/domain.md` |
| 桌面技术栈 | .NET + Avalonia 桌面应用、版本信息、设置、首轮初始化、库页面、题录编辑、搜索、OCR 队列、PDF 工作台与 About 页面。 | `src/Patchouli.UI`、`UiViewModelTests`、`FirstRunViewModelTests` |
| 库身份 | `library_id` 创建后稳定，重命名/移动不改变身份；跨库证据解析返回 mismatch。 | `LibraryIdentityServiceTests`、`EvidenceRefV2Tests` |
| 题录模型 | Item/FileAsset/DocumentInstance 三层模型；CSL 对齐字段；可扩展标识符；结构化 creator/date；标签来自导入 keyword 但不保留独立 keyword 字段。 | `BibliographicCoreTests`、迁移 `003`、`010`、`013` |
| 文件资产 | 原文件不入库；known locations、search roots、快速哈希、BLAKE3、缺失/移动/变更/冲突状态与统一文件解析服务。 | `FileResolutionServiceTests`、`FileFingerprintServiceTests` |
| PDF 导入与页面 | PDF 扫描、导入 workflow、页数读取、页面记录、页面坐标基准。 | `PdfDiscoveryServiceTests`、`PdfImportWorkflowTests`、`DocumentTreeServiceTests` |
| 页面渲染缓存 | PDF 页面渲染、缓存命名空间 `page_renders`、渲染失败/超时结果化、渲染输出用于 OCR 输入。MCP never returns cached images or image paths. | `PdfPageRenderingTests`、`RealPdfRendererTests` |
| OCR Preset | OCR Preset 和不可变 Preset Version；模型/路径/参数变更创建新版本；ready/missing/rebind 状态。 | `OcrLifecycleBoxTreeTests`、`UiViewModelTests` |
| OCR 提供程序 | Mock、本地占位、MinerU client/downloader/importer/Document Tree candidate mapper/content list v2 解析已存在；MinerU 是第一产品 OCR 路径的 ADR 已记录。 | `MinerUClientTests`、`MinerUUploadPreparerTests`、`MinerUDocumentTreeTests`、`MinerUContentListParserTests`、ADR `0014` |
| OCR 生命周期 | 运行按页面保存；staging/candidate/current；取消、completed_with_errors、失败页面保留与按页候选采纳。 | `OcrLifecycleBoxTreeTests` |
| OCR 队列 | 并发限制、优先级、老化、暂停/恢复、取消、瞬时重试、需要人工修复的失败分类。 | `OcrQueueSchedulerTests` |
| Document Box Tree | 0.2.0 fresh schema、页级 immutable revision、typed leaf、sibling pointer、logical page、draft command、Markdig compiler/SourceMap。 | `DocumentTree*Tests`、迁移 `005`、ADR `0015` |
| 搜索单元与 FTS | non-suppressed leaf Box 生成 search_unit；本地 FTS 索引是可重建的本地缓存；索引状态 current/stale/partial/unavailable；无线性 SQL LIKE 降级。 | `SearchUnitFtsBoxTreeTests`、`BoxTreeReadSurfaceTests` |
| 搜索配置文件 | 搜索配置文件、rewrite rule、alias/effective profile、rewrite plan、预览/执行边界。 | `SearchProfileRewriteTests`、迁移 `011` |
| 证据引用 | `evref:v2` 以 `(tree_revision_id, box_id)` 编码；pinned 默认，current/compare 显示漂移。 | `EvidenceRefV2Tests`、`BoxTreeReadSurfaceTests` |
| MCP Read API | 第一版 MCP 是只读且纯文本的；search_library、get_item_metadata、get_document_status、get_page_text、get_page_blocks、get_search_result_context；HTTP transport。 | `BoxTreeReadSurfaceTests`、`McpVerificationServiceTests`、`McpServerTransportTests` |
| MCP 安全边界 | MCP 从不触发 OCR 或索引重建；MCP 无法读取提供程序密钥；不返回本地路径、file URL、提供程序配置、缓存图像或图像路径。 | `AlphaSecurityBoundaryTests`、ADR `0010` |
| 凭据 | local-only `Credentials` appsettings 范围是 provider secret 的唯一持久化 owner；credential module 只提供读取、验证和日志脱敏，快照/导出/branch import 均排除凭据。 | `CredentialStoreTests`、`AlphaSecurityBoundaryTests` |
| 快照 | 运行库与同步快照分离；内容寻址 SQLite 分片、manifest、current pointer、导入验证、缓存排除。 | `SnapshotTests`、ADR `0001`、`0002` |
| 快照分支 | 分歧作为独立分支打开以供检查；选择性导入题录/文档实例；v1 不执行自动对象级合并；不得在分支间静默执行最后写入者胜出。 | `SnapshotBranchInspectionTests`、ADR `0011` |
| 端到端 Alpha | 创建库、导入题录/文件/页面、布局、搜索、证据、OCR 队列、MCP、PDF 渲染 OCR、快照分支等主路径有 smoke/regression 覆盖。 | `AlphaEndToEndWorkflowTests`、`scripts/alpha-*.sh` |

## 9. 长期约束索引

| 约束 | 权威位置 |
|---|---|
| 领域词汇、Item/FileAsset/DocumentInstance/SearchUnit/EvidenceRef/MCP Read API 的含义 | `.agent/CONTEXT.md` |
| 运行数据库不得放入同步根；快照发布/导入而非同步 WAL/SHM | ADR `0001` |
| 内容寻址 SQLite 分片与 manifest | ADR `0002` |
| 原始 PDF/图像与缓存不进数据库/快照；`page_renders` 只属于本地缓存 | ADR `0003` |
| library_id 路径无关 | ADR `0004` |
| Item/FileAsset/DocumentInstance 三层模型 | ADR `0005` |
| OCR Preset Version 溯源 | ADR `0006` |
| OCR staging 先于 current adoption | ADR `0007` |
| page-local Document Box Tree 生成 SearchUnit；本地 FTS 是缓存；搜索配置文件只影响查询召回 | ADR `0008` |
| 0.2.0 fresh schema epoch、Box Tree、中央 Markdig 与 Evidence v2 | ADR `0015` |
| EvidenceRef 默认 pinned，旧引用不静默漂移 | ADR `0009` |
| MCP 只读、纯文本、无 OCR/索引动作、无路径/密钥/图片 | ADR `0010` |
| 快照分歧创建分支，无自动对象级合并，无 last-writer-wins | ADR `0011` |

## 10. 版本管理理念

- v1 已经形成 alpha 可验证基线：保护证据，暴露歧义，拒绝不安全自动化。
- v2 是最终用户可用版：优先 UI 完整性、同步可理解性、CSL 输出、MCP 可用性和生产 OCR。
- v2 PRD 不应把所有研究方向都纳入；向量化和语义搜索留到 v2 之后。
- 削弱证据可复现性的功能必须选择加入，并在 UI 和 MCP 响应中显式标记。
