# Task: 文档 Box Tree 迁移与 PDF 工作台 Markdown 浏览／Box 编辑

状态：已完成
优先级：高
范围：以版本化、页内有序的 `DocumentBox` Tree 取代 `LayoutRevision` / `LayoutNode`，并迁移 OCR 导入、候选采纳、搜索、证据、MCP、快照和 PDF 工作台。本文是当前 PRD 的后续设计任务；实现完成时必须将相应 PRD、CONTEXT 与 ADR 术语一并更新。

> **破坏性版本边界：** 当前产品版本已经提升为 **0.2.0**。本任务定义 0.2.0 的新 SQLite schema epoch；不支持打开、转换或同步 0.1.x 的 SQLite library。旧数据库必须被明确识别为不受支持，并要求用户创建新库／重新导入源文档，不能以兼容 adapter、表迁移、旧 ID 保留或双写路径换取表面升级。

## 1. 结论

当前 `LayoutNode` 同时承载几何、`reading_order: int`、文本、表格 cell、类型与父子关系。它无法稳定表达多栏、竖排及人工校正后的真实阅读顺序；当前工作台又将“有 bbox”误作“应在 bbox 旁编辑文字”。

新模型的唯一真相是：

> 每个物理页的 current committed `DocumentTreeRevision` 中，按明确 sibling 指针排列的一组 Box。Box 保存类型、规范 bbox、叶子内容和少量显式结构字段；所有 Markdown、搜索、MCP 和 UI 文本均由它派生。

系统不重建原 PDF 版式：页面只操作 bbox，文字只在普通横排的 Markdown 源码窗口编辑。`writing_mode`、`angle`、行/span/字符几何、column/flow/region、第二棵内容树、数字 `reading_order`、`text_policy`、表格行/单元格模型及完整 MinerU JSON 都不进入规范存储。

多栏日文竖排的自动顺序错乱是正常输入。排序算法只能提出建议；一旦指针写入树，Markdown、索引和 MCP 都只读取指针，绝不在读取时依据几何或方向重排。

## 2. 已确认的产品行为

### 2.1 查看模式

- 主区域显示当前 PDF 物理页和所有 Box bbox；bbox 不绘制 OCR 文字。
- 右侧是当前**物理页**的只读 Markdig Markdown 预览。提供“复制 Markdown”动作，复制同一份规范编译结果；没有右侧文本编辑器，也不提供页面级标题大纲。
- 选择非 `suppressed` bbox 时，右侧预览高亮该 Box 对应的 Markdown 渲染范围；在预览中选择相应范围时，页面滚动并高亮 bbox。此映射是本次编译的短生命周期 `SourceMap`，不持久化。
- 选择 `suppressed` Box 时，右侧预览保持不变；任何反馈只写应用的统一状态栏，工作台不自行增加提示卡片、辅助预览或通知。
- 标题大纲是未来从规范编译 Markdown 派生的**文档级**功能，本任务不实现页面大纲，也不把标题变成树父节点。

### 2.2 编辑模式

- 一次编辑会话只针对一个物理页，克隆该页 current committed tree 为 page draft。查看模式、搜索、证据和 MCP 永远读取 committed tree。
- 右栏只显示真实 Box 树：节点项显示阅读序号、类型徽标和去格式化后的首行摘要；`suppressed` 项整体灰显。它不是一串内联 `TextBox`，也不是整页 Markdown 源码编辑器。
- 单击树节点或页面 bbox，二者双向同步选择。双击**叶子**节点打开一个可拖动的 Box 编辑窗口，用户可将窗口移开，以查看中间 PDF 原图中该 bbox 的原始内容。
- 编辑窗口把 `type`、`heading_level`、代码语言及该叶子的类型化源码放在同一表单内；不强迫用户把这些可共同编辑的字段拆成多步。`type` 和标题级别仍是显式结构字段，绝不由 Markdown `#` 推断。
- 右击树节点提供所有显式结构命令；拖放只移动树节点。bbox 几何只在页面画布编辑，不在右栏编辑器中显示或修改。
- 点击保存时校验整个 page draft，成功后原子提交为新 revision 并退出编辑模式；取消或退出但不保存则丢弃整个 draft 和其中所有局部 OCR 候选。

### 2.3 明确不做

- 不从整页 Markdown、单个 Box Markdown 或 Markdig AST 推断/修改树、parent、阅读顺序、bbox、标题级别或任何关系。
- 不做富文本编辑器、WYSIWYG Markdown、Word/WPS 式排版编辑、文字选择、竖排原位编辑、原版 HTML/CSS 重建或字符级几何。
- 本期不创建、重绑或编辑 ruby/phonetic。MinerU `phonetic` 在导入时扁平为普通文本并记录诊断，日后再以独立 annotation 设计处理。
- 不保存 `caption_of`、`footnote_of` 等关系。图注、表注、脚注都是独立的有序叶子，类型与 sibling 顺序是唯一的输出依据。
- 不实现 RAG 二次切块或页面级标题大纲。

## 3. 与现有 PRD、ADR 和 task 的关系

| 文档 | 实现后的调整 |
|---|---|
| `.agent/CONTEXT.md` | 以 `DocumentTreeRevision` / `DocumentBox` 取代 LayoutRevision/LayoutNode；保持“只有 current committed 内容进入搜索、证据和 MCP”。 |
| `.agent/adr/0008-use-layout-tree-as-source-for-search-units.md` | 决策保留，但术语改为 Box Tree；SearchUnit 仍是派生与可重建缓存。 |
| `.agent/adr/0014-use-mineru-as-first-product-ocr-provider.md` | 保留 MinerU 与 provider-neutral adapter 边界；删除 table cell 作为规范持久化模型的要求。 |
| `.agent/PRD.md` §3.5 | 以本任务的查看/编辑工作台、指针排序、draft/commit、局部 OCR 和 `suppressed` 规则替换当前 LayoutNode 编辑要求。 |
| `.agent/PRD.md` §3.6、§5、§7 | `OcrLayoutDocument → LayoutNode` 改为 `OcrDocumentTreeCandidate → DocumentBox`。 |
| snapshot/conflict task | snapshot allow-list、分支 staging、CF-06 和 MCP/evidence schema 一并迁移；不得保留双写的 canonical OCR 数据。 |

## 4. 规范数据模型

### 4.1 页级 revision

树没有跨物理页 parent，因此 revision 也必须是**页级**。一个文档的规范状态是各物理页 current committed revision 组成的 forest；全文 Markdown 仅按物理页顺序遍历该 forest。

```text
document_tree_revisions
- tree_revision_id
- document_instance_id
- page_id
- parent_tree_revision_id       // draft / staging / committed 的来源
- source                        // import | manual_edit | ocr_adopted | migration
- status                        // staging | draft | committed | discarded
- is_current
- created_at
- committed_at
```

约束：

- 每个 `(document_instance_id, page_id)` 至多一个 current committed revision。
- committed revision 不可改。编辑页时只 clone 该页；取消不得触碰 current。
- staging 是 OCR 候选，不能进入默认搜索、证据或 MCP。用户采纳后才生成新的 committed page revision。
- 新 revision ID 按 0.2.0 schema 正常生成；不为 0.1.x 的 LayoutRevision 或旧 evidence 保留 UUID 兼容层。

### 4.2 Box 行与稳定身份

```text
document_boxes
- tree_revision_id
- box_id                         // clone 后保留的稳定 DocumentBoxId
- document_instance_id
- page_id
- parent_box_id                  // 可空：该物理页的根 Box
- next_sibling_box_id            // 同父节点下唯一规范顺序

- box_type
- sub_type                       // 例如 code + algorithm；可空
- base_type                      // 未知 MinerU 类型的降级类型；可空
- bbox_x, bbox_y, bbox_width, bbox_height
- payload_json                   // 严格 type-specific DTO
- heading_level                  // 仅 title，1..6
- code_language                  // 仅 code；可空
- confidence
- suppressed                     // 默认 false；页面辅助类型导入时为 true
```

主键为 `(tree_revision_id, box_id)`。同一 Box 在 draft clone 中保留 `box_id`；新建 Box 才有新 ID。`parent_box_id` 和 `next_sibling_box_id` 均为同 revision 的复合外键，并为非空 `next_sibling_box_id` 建立唯一前驱约束。

不保留 `ignored`、`reading_order`、`text_policy`、`phonetic_start/end`、cell 元数据、未定位 Markdown Box 或聚合缓存。当前确认的创建、拆分和合并流程均要求 bbox，故 committed leaf Box 与 logical page 均必须有 bbox。

### 4.3 树形与扁平内容规则

唯一主动引入的 Box 类型是 `logical_page`。其他规范类型优先采用 MinerU 的已有名称：

```text
text, title, ref_text, equation, list, image, table, chart,
code, algorithm, image_caption, image_footnote, table_caption,
table_footnote, chart_caption, chart_footnote, code_caption,
code_footnote, header, footer, page_number, aside_text, page_footnote
```

`algorithm` 可以以 `code` 加 `sub_type=algorithm` 存储。未知 MinerU 类型必须保留原 `box_type` 和 `base_type=text|image|table|code|unknown`，不得因 UI 不认识而删除。

内容一律扁平：`table` 就是可编辑表格叶子，`code` 就是可编辑代码叶子；不创建 `table_body`、`code_body`、`list_item`、caption/footnote 容器或任意推断关系。除 `logical_page` 外，正常业务 Box 都是叶子。

物理页只允许以下两种互斥形状：

```text
模式 A：普通物理页
PhysicalPage
├─ leaf Box
├─ leaf Box
└─ leaf Box

模式 B：逻辑页计划／逻辑页内容
PhysicalPage
├─ logical_page                  // 可以暂时为空
│  ├─ leaf Box
│  └─ leaf Box
└─ logical_page
   └─ leaf Box
```

一页不能同时有直属叶子与 `logical_page` 根。逻辑页没有 payload，可为空，必须有 bbox；它们按根 sibling 链排序。不得实现跨物理页逻辑页关系。

### 4.4 类型化 payload 与 Markdig 输入契约

`payload_json` 是严格 DTO，不是原 MinerU JSON 暂存区。所有用户可编辑正文只存在于叶子 payload；Markdown 只是该 payload 的输入和验证语言，不能改变结构。

| 类型 | 规范 payload / 编辑输入 |
|---|---|
| `text`、`title`、`ref_text`、caption、footnote、auxiliary | 单个 text-like Markdown 内容；不允许用块语法创建兄弟。`title` 的 `#` 不可输入，层级只读写 `heading_level`。 |
| `equation` | LaTex 源码；不输入 `$$`，编译器添加。 |
| `list` | 单个合法 GFM Markdown 列表。 |
| `table` | 单个合法 GFM pipe table；禁止 HTML 输入、rowspan、colspan 和伪造单元格。 |
| `code` | 原始代码内容；不输入 fenced-code 标记，`code_language` 为同一编辑窗口字段。 |
| `image`、`chart` | 资产引用和可选文本说明；原 PDF 仍是主要视觉证据。资产不可用时编译为稳定 `[Image]` / `[Chart]` 占位。 |

MinerU 表格 HTML 只在 adapter 内临时读取：可无损规整为规则网格时转换为 GFM；否则规范 payload 为 `[Table]` 并产生 `table_not_representable_as_gfm` 诊断。不得保存原 HTML，也不得让用户写 HTML 表格。

### 4.5 bbox 与重叠

- bbox 是相对 `Page` 既有 upright render basis 的 `(x, y, width, height)`，范围 `0..1`。MinerU `[x0,y0,x1,y1]` 或 `0..1000` 坐标只在 adapter 中换算。
- 不保存 `angle`、polygon、writing mode、text direction 或原始 MinerU bbox 副本。
- `logical_page` 不参与碰撞检查。其他同页、同父、普通 leaf Box 的显著重叠是 `CF-06` validation error，阻止保存；画框、移动或调整 bbox 永不删除已有 Box。
- 只允许 `phonetic/ruby`、`warichu`、annotation/aside、seal 和明确配置为允许的自定义类型重叠。当前 `phonetic` 被扁平化，但保留规则以兼容未来独立 annotation task。
- `Page` 的 source hash、basis 与 renderer version 继续决定 `source_changed` / `bbox_basis_stale`。MCP 返回 bbox 和状态，不生成自然语言位置描述。

### 4.6 不变量

`IDocumentTreeService` 必须在每次 draft 命令和 commit 前验证受影响物理页：

1. 所有 Box 引用同一 revision、document instance、physical page；parent 无环。
2. 每个 parent 的子节点由一条完整、无环、无分叉、覆盖全部孩子的 sibling 链组成；每个 next 指向同父节点。
3. root 集合满足模式 A 或模式 B，不能混合；只有 `logical_page` 可以拥有 children，且它不能成为 child。
4. 所有 leaf Box 与 logical page 有合法 bbox；类型、payload、heading level、code language 与 `suppressed` 值合法。
5. 普通 leaf bbox 不发生未豁免的显著重叠；未知类型没有可用 `base_type` 时不能自动采纳。
6. Markdown、OCR 或几何操作无法通过“猜测顺序”“补造 bbox”或修改派生 Markdown 修复这些错误，必须阻止 commit。

## 5. Markdig、Markdown 编译与验证

### 5.1 统一 Markdown 引擎

通过中央包管理加入 [Markdig](https://github.com/xoofx/markdig)，并新增唯一的 `IMarkdownEngine`。不得自研 CommonMark/GFM parser、正则 Markdown splitter 或 HTML Markdown renderer。

- 所有页面编译、查看预览、叶子输入验证、纯文本投影和测试使用同一固定 Markdig pipeline。
- pipeline 至少启用 CommonMark、GFM pipe table、fenced code、footnote、emphasis/strikethrough 与 autolink；用户 raw HTML 在本产品 pipeline 中禁用，不作为可执行或持久化内容。
- `IDocumentMarkdownCompiler` 按 pointer 顺序和 type contract **生成**页面 Markdown 与 transient `SourceMap`；Markdig 解析该结果为 AST，负责语法和预览渲染。Markdig AST 不反向生成/拆分 Box。
- 预览使用 Markdig AST 驱动的原生 Avalonia renderer；不引入 WebView，不把 OCR 或用户输入的 HTML 交给浏览器执行。

### 5.2 编译结果与 profile

```csharp
public sealed record CompiledMarkdown(
    string Markdown,
    IReadOnlyList<MarkdownSourceMapEntry> SourceMap,
    IReadOnlyList<MarkdownDiagnostic> Diagnostics);
```

`SourceMap` 仅在当前 UI 编译会话存在，将 Box ID 映射至 Markdown UTF-16 range 和预览渲染节点。它不进入 database、snapshot、MCP 或 evidence。

物理页编译规则：

1. 模式 A 遍历物理页 root sibling 链；模式 B 遍历 logical page 根，再遍历各自的 child 链，并在逻辑页之间插入固定 `---`。
2. `title` 由 `heading_level` 输出 `#`；`code` 由 `code_language` 输出 fence；`equation` 输出 `$$` 包裹；其余依 type contract 输出。
3. `suppressed=true` 的 Box 不参与默认 Markdown、全文索引与 MCP。用户显式“纳入文档流”后将其设为 false，随后按普通顺序编译。
4. 不建立或显示页面级标题树。未来文档级标题大纲只能从 canonical compiled Markdown 派生。

## 6. 工作台命令与交互

### 6.1 创建、修改与移动

所有动作只修改 page draft，且必须走 `IDocumentTreeEditor`，不得直接更新 committed SQLite rows。

**新建 leaf Box** 是强制流程：

1. 用户在 PDF 画出 bbox；
2. 用户必须将它插入真实树的合法 parent 与明确 sibling 位置；不知道位置则取消新建；
3. 打开 Box 编辑窗口，同一窗口填写 type、可选 heading level / code language 与内容，或运行局部 OCR 预填；
4. 只有有效内容与结构共同通过校验，Box 才留在 draft。

树拖放的语义固定：同父拖放只改 sibling 顺序；跨 parent 拖放只能落到同物理页的合法目标（逻辑页之间），bbox 不变。跨物理页移动不在本任务范围内。

### 6.2 拆分与合并

**拆分**只能作用于单个 leaf Box，结果继承原 `type` 与 `heading_level`：

1. 原 Box 在向导中只读保留为内容参照；
2. 用户必须画两个替代 bbox；
3. 用户分别填写两个新 Box 内容，或分别运行局部 OCR；
4. 验证通过后，两个 Box 在原 parent 中原子替换旧 Box，并继承其位置。

不得复制旧 bbox，也不得创建未定位 Box。

**合并**只能选择同一 parent 下、阅读顺序连续且同类型的 leaf Box：

- 结果 bbox 为全部原 bbox 的最小外接矩形；
- 结果继承类型与标题级别；
- 用户必须提供结果内容。可执行“按既有阅读顺序合并文本”作为可编辑初值，或运行局部 OCR；
- 验证通过后一个新 Box 原子替换原集合。若顺序或类型不正确，用户先显式重排/改类型，再合并。

### 6.3 局部 OCR

右击 leaf Box 的“局部 OCR”只使用当前文档配置的 MinerU `profile_id`，不提前提供无实际后端的 Profile 选择器。它将选中 bbox 裁剪后提交，生成仅包含下列字段的短生命周期候选：

```text
box_type + leaf payload + optional heading_level
```

局部 OCR 不得改 bbox、parent、sibling 指针、`suppressed` 或其他结构字段。候选在现有 Box 编辑窗口显示为 diff；用户接受后才写入 draft。对新建/拆分 Box，候选只能预填编辑窗口。取消页级编辑会话时一并丢弃。

### 6.4 删除与 `suppressed`

- 删除从 draft 移除 Box；保存后旧内容只通过历史 revision 可追溯。
- `suppressed` 是可逆的“从文档流排除”，不是删除。右击可切换“从文档流排除／纳入文档流”。
- 按 MinerU 的语义，`header`、`footer`、`page_number`、`aside_text`、`page_footnote` 导入时默认 `suppressed=true`。查看模式的 bbox 为灰色；默认 Markdown、SearchUnit/FTS 和 MCP 不返回它们。
- MCP 查询显式给出 `include_suppressed=true` 时才返回被排除 Box；这不能修改树或触发 OCR。

### 6.5 逻辑页与 OCR 输入

逻辑页是扫描页无法合理分页时的例外工具，而不是普通页的默认节点。它允许为空，以便用户先为整份文档配置切分方案。

- 编辑模式中，物理页和其中每个逻辑页都可执行“页级 OCR”。无逻辑页时识别整张物理页；有多个逻辑页时，对其 bbox 裁剪分别识别，并映射回原物理页坐标与对应 logical page。
- “文档级 OCR”构造临时虚拟文档：普通物理页成为一个虚拟页；有逻辑页的物理页按逻辑页 sibling 顺序切成多个虚拟页；一次提交 MinerU 后，将每个虚拟页结果逆映射到原物理页/逻辑页。
- OCR 结果始终是 staging tree；采纳才替换对应物理页 current revision。逻辑页可先保持空，待整本文档划分完成再运行上述文档级 OCR。
- 当现有普通页要转为逻辑页模式时，用户必须在 draft 中显式用 logical page 根替换直属内容；旧页面内容保留在旧 revision。系统不得依据 bbox 自动把旧 Box 分配到新逻辑页。

## 7. MinerU adapter

MinerU JSON 只是一次导入格式。完成导入后，数据库只保存 tree revision、Box、OCR run/profile/version、置信度与必要诊断；不保存整份 `content_list*.json`、`middle.json`、`model.json`，也不从其回读覆盖用户更正。

版本化 adapter 产出短生命周期 `OcrDocumentTreeCandidate`：

```text
OcrDocumentTreeCandidate
└─ OcrPageCandidate
   └─ OcrBoxCandidate
      - type / sub_type
      - source order
      - leaf payload
      - bbox
      - optional heading level
      - confidence
      - suppressed flag
```

adapter 规则：

1. `content_list_v2` 提供 page grouping、标题等级和统一 payload；`content_list` / `middle` 仅补齐稳定缺失字段。不得将任何一个 MinerU schema 直接作为数据库 schema。
2. MinerU 数组顺序或 `index` 只初始化 sibling 指针；导入后不是第二个顺序来源。
3. 所有复合关系扁平化为 sibling leaf；不使用 `middle.json` 的 block 包含关系建立长期父子。
4. VLM table HTML 只尝试转换规则 GFM；失败存 `[Table]` + 诊断。``phonetic`` 扁平为普通 text + `phonetic_flattened` 诊断。
5. `discarded_blocks` 和 page auxiliary 类型照常导入，但设置 `suppressed=true`；它们保留 bbox、文本、类型和 confidence。
6. 没有可验证树 artifact 时，禁止把 `full.md` 当作单个伪 `text` Box 导入。必须返回可处理诊断。

## 8. 搜索、证据、MCP 与快照

- 每个非 `suppressed` leaf Box 生成一个 SearchUnit；SearchUnit、FTS、未来 embedding 均是可失效的派生物。文本、type、heading level、parent、sibling 或 suppressed 改动使对应页的文本派生物 stale；只改 bbox 不重建文本索引。
- `EvidenceRef` 改指向 `(tree_revision_id, box_id)`；0.2.0 直接定义新的 codec/schema，不承担 0.1.x `evref:v1` 或旧 LayoutNode 引用的解码兼容。revision 内的稳定 Box ID 用于后续 revision 的 evidence successor 匹配。
- `get_page_text` 返回 current page 的默认 compiled Markdown 文本投影；`get_page_blocks` 返回 current non-suppressed leaf Box 的 type、文本、派生序号、revision 与 bbox。所有 MCP 操作仍只读、纯文本、安全，且不会触发 OCR/编译重建。
- snapshot / sync / branch staging allow-list 只带新 tree、revision、SearchUnit/evidence 与现有 OCR lifecycle metadata；不包含 legacy layout 表、完整 MinerU JSON、PDF、渲染缓存、Markdig AST 或 UI source map。

## 9. 现有实现的替换点

| 现有位置 | 替换工作 |
|---|---|
| `src/Patchouli.Core/Layout/LayoutNode*.cs`、`ILayoutTreeService` | 新建 Box Tree domain、页级 revision service、tree validator、明确命令和 clone/commit 生命周期；删除 LayoutNode、reading-order/text-policy/cell API。 |
| migrations `005`、`006`、`007`、`008`、`014` | 以 0.2.0 fresh schema 定义替换 legacy layout/revision/search/evidence 表；不写旧表转换 migration。数据库 epoch guard 必须拒绝 0.1.x library。 |
| `OcrLayoutDocument`、`MinerULayoutNodeMapper`、`OcrLayoutImporter`、`MinerUResultImporter`、`OcrRunCoordinator` | 改为 candidate adapter/importer、表格 GFM 转换、auxiliary suppression、逻辑页虚拟输入和 staging adoption。 |
| `SearchUnitBuilder` | 读取 Box sibling 顺序与 compiled text，不读取 LayoutNode/reading_order。 |
| MCP page text/blocks 与 Evidence codec | 改用 Box IDs、current page revision 和 `include_suppressed`。 |
| `PdfWorkspaceViewModel` / `PdfWorkspacePage.axaml` | 删除每 bbox `TextBox`、新增页面 bbox canvas、Markdig read-only preview、Box tree、拖动 Box 编辑窗口、右击命令、draft Save/Cancel 和 staged OCR diff。 |

建议的 UI/service seam：

```csharp
BeginPageEditAsync(DocumentInstanceId documentId, PageId pageId)
CompilePageMarkdownAsync(DocumentTreeRevisionId revisionId, PageId pageId)
UpdateLeafAsync(PageEditSessionId sessionId, UpdateLeafCommand command)
DrawAndInsertLeafAsync(PageEditSessionId sessionId, DrawAndInsertLeafCommand command)
BeginSplitAsync(PageEditSessionId sessionId, DocumentBoxId boxId)
MergeLeavesAsync(PageEditSessionId sessionId, MergeLeavesCommand command)
MoveBoxAsync(PageEditSessionId sessionId, MoveBoxCommand command)
RunLocalOcrAsync(PageEditSessionId sessionId, DocumentBoxId boxId)
CommitPageEditAsync(PageEditSessionId sessionId)
DiscardPageEditAsync(PageEditSessionId sessionId)
```

ViewModel 仅管理页面渲染、选择、窗口状态、draft session 与全局状态栏消息；领域服务负责全部树变更、Markdig 校验、候选采纳、碰撞校验和 Search dirty 标记。

## 10. 0.2.0 破坏性 schema 切换

这是有意的 schema/API 断代，不是数据库升级功能。0.2.0 的正确性优先于保留 0.1.x LayoutNode、table-cell、EvidenceRef 或 snapshot 格式。

1. 定义 0.2.0 fresh SQLite schema：`document_tree_revisions`、`document_boxes`、新的 SearchUnit/evidence/OCR lifecycle 外键和 snapshot allow-list 只指向新模型。
2. 删除 `layout_revisions`、`layout_nodes`、table-cell metadata、旧 SearchUnit root-node/revision 字段及其生产 service/API；不写任何从旧表读取、映射或回填新表的代码。
3. 在打开 library 的最早边界检查 schema epoch：0.1.x 或任何未知 epoch 返回明确的“此库不受 0.2.0 支持，请新建库并重新导入”错误；不得静默删除、部分转换或继续运行。
4. 新建库、全新导入、OCR staging adoption、SearchUnit/FTS/evidence/MCP/snapshot 必须全部只覆盖 0.2.0 schema。测试使用 fresh database fixture，不维护 upgrade fixture。
5. 任何已有用户数据的实际迁出、导出或人工再导入工具如果未来需要，必须以独立任务和明确的用户数据策略设计；它不属于本次模型实现，也不能污染 runtime canonical model。

## 11. 分阶段实施

### A. 领域与 schema

1. 实现 page-local `DocumentTreeRevision`、扁平 `DocumentBox`、fresh 0.2.0 schema epoch guard、foreign key、sibling validator、bbox collision validator 和 page draft lifecycle。
2. 实现模式 A/B、空 logical page、显式根转换和不可变 committed revision。
3. 删除旧 Layout schema/service/API，建立 fresh-database regression fixture 与旧 epoch 明确拒绝测试。

### B. Markdig 与编译

1. 接入集中 `IMarkdownEngine` 与固定 Markdig pipeline。
2. 实现 type-driven compiler、SourceMap、native Avalonia preview renderer、复制 Markdown 和 type-specific validation。
3. 覆盖 GFM table 转换/验证、HTML table 降级、code/equation/title wrapper、suppressed 排除和不执行 raw HTML 的测试。

### C. MinerU 与 OCR lifecycle

1. 替换 layout DTO/importer 为 candidate adapter 和 staging tree。
2. 实现 auxiliary suppression、phonetic flatten、unknown type 保存、禁止 `full.md` 伪导入。
3. 实现物理页/逻辑页页级 OCR、全书虚拟文档 OCR、bbox inverse mapping、局部 OCR `type+payload+level` diff 与显式 adoption。

### D. 搜索、证据、MCP、snapshot

1. 让 SearchUnit/FTS、Evidence、MCP 和 snapshot 全部改读 Box Tree。
2. 增加 `include_suppressed`，保持 MCP 只读，并实现 0.2.0 EvidenceRef codec。
3. 清除 LayoutNode/reading_order/cell API 的生产依赖和 snapshot 表。

### E. PDF 工作台

1. 查看模式实现 PDF bbox overlay、灰色 suppressed、Markdig 只读预览、copy 与 transient 双向选择。
2. 编辑模式实现树节点摘要、右击菜单、拖放、浮动 Box 编辑窗口、统一状态栏、Save/Cancel。
3. 实现强制新建、拆分、合并、type/level 编辑、suppression、碰撞错误、局部 OCR diff 和 logical page OCR 命令。
4. 删除现有逐 bbox 文本框、编辑时新建空 LayoutRevision、按 `BoundingBoxes.Count + 1` 赋顺序及“覆盖就删除相交框”的逻辑。

### F. 文档与收口

1. 更新 PRD、CONTEXT、ADR 0008/0014（或新增 superseding ADR）、README 与 Alpha regression workflow，明确 0.2.0 不支持旧 SQLite schema。
2. 运行 C# cleanup、InspectCode、单元/集成/UI/fresh-database 测试；验证 snapshot 没有 legacy layout 表、完整 MinerU JSON、PDF、渲染缓存或 secret。

## 12. 验收标准

| ID | 验收条件 |
|---|---|
| BOX-00 | 0.2.0 fresh SQLite schema 不包含 LayoutNode/legacy table-cell canonical 表；0.1.x 或未知 schema epoch 被明确拒绝，不发生自动转换、双写或静默删库。 |
| BOX-01 | 每个物理页仅有一个 current immutable page tree revision；draft/staging 不影响查看、搜索、证据或 MCP。 |
| BOX-02 | sibling 指针是唯一阅读顺序；没有 canonical `reading_order`、writing mode 或读取时几何重排。 |
| BOX-03 | 页面严格满足普通根 leaf 或 logical page 根两种互斥形状；logical page 可为空。 |
| BOX-04 | 所有内容叶子均扁平，无 table/cell、list-item、caption relation 或 ruby relation；未知 MinerU 类型不丢失。 |
| BOX-05 | 新建、拆分、合并分别遵循本任务强制 bbox／插入／内容工作流；不会产生未定位 Box、复制 bbox 或隐式删除。 |
| BOX-06 | table 只存并编辑 GFM；规则 MinerU HTML 可转换，不规则表降级 `[Table]`，无 HTML 输入/存储/伪造单元格。 |
| BOX-07 | 局部 OCR 仅能候选更新 type、payload、heading level；其余结构字段保持不变，取消会话后候选消失。 |
| BOX-08 | 查看模式显示当前物理页 Markdig 预览；正常 Box 双向高亮，suppressed Box 灰色且不改变预览。 |
| BOX-09 | 编辑模式右侧只有 Box 树；双击叶子打开可拖动源码编辑窗口，右击/拖放执行显式结构操作。 |
| BOX-10 | 保存原子提交并退出，取消不提交；所有 validation / collision 错误阻止保存并由统一状态栏报告。 |
| BOX-11 | header/footer/page number/aside/page footnote 默认 suppressed，排除 Markdown/搜索/MCP；MCP 仅显式 `include_suppressed` 时返回。 |
| BOX-12 | 页级 OCR 和文档级虚拟 OCR 正确处理空/多个逻辑页，并将 bbox 映射回原物理页。 |
| BOX-13 | SearchUnit、Evidence、MCP 与 snapshot 均不再依赖 LayoutNode/layout revision/cell schema；Evidence codec 仅以 0.2.0 Box Tree 为依据。 |
| BOX-14 | Markdig 是唯一预览与输入验证语法引擎；preview/复制/验证使用同一 pipeline，无 WebView 和可执行 raw HTML。 |

## 13. 参考

- MinerU 的 [输出文件格式](https://opendatalab.github.io/MinerU/reference/output_files/)：`discarded_blocks`、页面辅助类型、page-oriented content list、VLM bbox 和表格 HTML 输出边界。
- MinerU [项目说明](https://github.com/opendatalab/MinerU)：其主 Markdown 面向语义连贯性，默认排除页眉、页脚、页码与页级辅助噪声。
- 当前 `.agent/PRD.md`、`.agent/CONTEXT.md`、ADR 0008/0014、snapshot/conflict task，以及本任务确认的 PDF 工作台交互决定。
