# 领域模型

Patchouli 把用户自己的源文件当作承载证据的文献。下面的术语决定"该用哪个 URI、改哪一层、引用落到哪里"。

## 题录与文件

- **Library（书库）**：元数据、文献实例、OCR/检索派生数据与证据 URI 的持久边界。改名或移动书库不改变其身份。`patchouli://library.yml` 给出 `library_id`、`display_name`，以及用户允许暴露时的标签与集合。
- **Item（题录）**：研究者引用的书目身份，例如一本书、一篇文章、一个版本或一份手稿。它不是 PDF。没有任何文献实例的 Item 也合法，表示已知文献但尚未持有文件。URI：`patchouli://items/{item-id}.yml`。
- **ItemMetadata**：Item 的可编辑书目投影（CSL 字段、标识符、作者等）。它包含用户知识，不是可以重建的缓存。
- **Tag（标签）**：用户可任意编辑的标签；导入时来源的 `keyword` 并入标签。
- **Collection（集合）**：单层、多对多的 Item 分组，可以为空，不能嵌套。通过 MCP 只读。
- **FileAsset**：原始文件的身份与校验记录（BLAKE3 指纹），与文件当前位置无关。原文件始终留在用户自己的文件夹中；文件移动或丢失不会删除题录、OCR 历史或证据。

## 文献实例与页面

- **DocumentInstance（文献实例）**：Item 的一个具体载体，例如一份扫描件、一份 OCR PDF、残本或补遗。它拥有页面、修订、检索单元和证据 URI。URI：`patchouli://texts/{document-instance-id}/`。一个 Item 可以有多个实例；引用时由宿主解析回所属 Item。
- **Page（物理页）**：实例内按 PDF 顺序排列的页面。对外编号一律是**一基**的物理页码：`page-1.xml` 就是第一页，与印刷页码无关。
- **Logical Page（逻辑页）**：一张扫描页上有多个版面区域（例如对开扫描）时，用 `logical_page` 根 box 表示的子页。它不是新的物理页。
- **印刷页码**：书上印的页码，由文献的 `map.yml` 推算（见 [CITATIONS](CITATIONS.md)）。没有 map 时不存在印刷页码，不能用物理页码冒充。

## 修订与 Box Tree

- **DocumentTreeRevision（页面修订）**：一张物理页 Box Tree 的不可变快照，分为 `working` 与 `committed` 两种；每页只有一个当前的 committed 修订。检索、证据和 MCP 默认读取都只看当前 committed 修订。OCR 结果和手工编辑先形成 working 修订，提交后才可见。
- **DocumentCommit**：把一个实例各页的修订组合成一次文献级提交。历史只追加；恢复旧版本会产生新的提交。
- **LibraryRevision**：整个书库的变更计数，出现在每个响应的 `meta.library_revision`（形如 `lib:42`）。它与页面修订是两回事。
- **DocumentBox（Box）**：修订中具有稳定 ID 的节点，包含归一化 bbox、类型、可选的类型化 payload 和 `suppressed` 标记。兄弟指针是唯一的规范顺序；只有 `logical_page` 可以有子节点。payload 类型包括 Markdown 文字、列表、表格（Markdown 或受限 HTML）、算法、代码、LaTeX 公式和媒体描述。
- **接续（`continues_prev`）**：后一个 box 在阅读顺序上延续前一个 box 的内容，可以跨栏或跨页。每个 box 各自保存自己的文字；接续是显式关系，不能用"把后一个框清空"来表达。

## 检索与证据

- **SearchUnit（检索单元）**：从每个未隐藏的叶子 box 派生的文本单元，用于全文检索与证据定位。全文检索每命中一个 SearchUnit 返回一条结果。
- **Versioned Evidence URI（证据 URI）**：`patchouli://texts/{document-instance-id}/page-{N}.xml?rev={tree-revision-id}&box={box-id}`。带 `rev` 时读取那份不可变修订，因此适合长期引用；不带 `rev` 时读取当前修订。全文检索结果的默认 `uri` 已经是这种形式，可以直接交给 `fetch`、`view` 或 `cite`。

## 派生与易失数据

- **Page Translation（页面译文）**：一张物理页的逐 box 译文，是当前 committed 源修订的派生数据。源 box 改动后，对应译文变为 `stale`，读取时会自动重新对齐。译文永远不会成为正文的权威来源。URI：`patchouli://translations/{document-instance-id}/page-{N}.xml`。
- **Run（运行）**：OCR 任务、翻译运行或元数据批量补全任务的实时状态投影，位于 `patchouli://runs/`。三个子树各有自己的状态语义，不共享状态机。它们只存在于宿主内存中，不进快照，不可 `put`，也不能被引用。

## 不变量

- MCP 不能编辑 bbox，不能通过 `put` 触发 OCR 或重建索引，也不暴露本机路径、缓存图像或凭据。
- 只有用户显式开启的写入开关和领域授权才允许写入；关闭开关即回到只读。
- 任何写入都经过宿主唯一的写服务，校验失败时不修改书库。
