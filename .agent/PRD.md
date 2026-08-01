# Patchouli PRD v3

状态：正式版  
版本线：0.3.x（迈向 1.0）  
日期：2026-08-01

仓库只维护这一份 PRD。已完成能力压缩为顶部 walkthrough；长期不变量以 `.agent/CONTEXT.md` 与 `.agent/adr/` 为准。

## Walkthrough（已完成基线）

v1/v2 已交付、可视为 0.2.x 稳定面的能力。细节以代码、测试与 ADR 为准，不在此复述需求正文。

- 桌面栈：.NET + Avalonia；首轮初始化、库页、题录编辑、搜索、OCR 队列、PDF 工作台、设置、About
- 库与文件：路径无关 `library_id`；Item / FileAsset / DocumentInstance；FileSearchRoot 与文件解析冲突
- Document Box Tree：0.2.0 fresh schema；页级 immutable revision；typed leaf；sibling 顺序；Markdig 中央编译
- 搜索与证据：SearchUnit + 可重建本地 FTS；搜索配置文件；`evref:v2`；pinned 默认、current/compare 显示漂移
- OCR：MinerU 为首选生产路径；`OcrDocumentTreeCandidate` 统一 staging/adoption；队列看板；局部 OCR 与工作台编辑
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
| V3-T1 | MCP 路线选择与完善 | B 已择优；结构化生产迁移进行中 |
| V3-T2 | 增强 OCR 文本编辑校注 UI | 占位；正文后续补全 |
| V3-T3 | 集成更多 OCR provider | 方向已定；细则后续补全 |
| V3-T4 | Linux 桌面适配与发行打包 | 新增；未开始 |
| V3-T5 | 桌面 UI 体验提升（书库页、搜索框、PDF 工作台 Markdown 预览） | 新增；方案评估中 |

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
| 正确性 | 错误 URI/错误 revision/越权/校验失败时的错误码稳定性；不把截断内容静默呈现为完整内容 |
| 安全边界 | 不出现本地路径、file URL、提供程序密钥、缓存图像、OCR/索引触发；日志不落查询正文与 secret |
| 权限模型清晰度 | 只读 vs 可写资源是否可被 agent 从响应字段（如 `writable`）推断 |
| 实现与运维成本 | 依赖面、打包物、协议复杂度、故障恢复（sidecar fault 等） |
| 同构性 | CLI 与 MCP 是否可对同一输入得到同一 JSON schema 与同一 exit/error code |

固定任务集示例（可扩展，但变更需版本化）：

1. 列出/搜索 CSL 样式并 `fetch` 一个 style
2. 在 documents 范围搜索关键词，再 `fetch` outline 与单页 range
3. 解析 evidence 并 `cite`
4. 对可写 item `.bib` 做 `fetch` → 本地修改 → `put --base` 成功与 revision conflict 失败
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
  --json       Return the shared CLI/MCP JSON response
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

#### 3.4.2 资源树

```text
RESOURCE TREE
  patchouli://items/
  patchouli://items/{item-id}.bib

  patchouli://documents/
  patchouli://documents/{document-id}/
  patchouli://documents/{document-id}/pages/{page}.md

  patchouli://styles/
  patchouli://styles/{style-id}.csl

  patchouli://evidence/{evidence-id}
```

v3 资源树不暴露 `collections` 与 `profiles`：库内尚未形成可对外承诺的完整实现。日后若产品面就绪，再以 PRD/ADR 修订增补 URI，不得在实现前 silently 占位。

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
  --regex              Interpret QUERY as a regular expression
  --limit <N>          Maximum results; default: 20
  --cursor <CURSOR>    Continue a previous result page

BEHAVIOUR
  Without QUERY: lists resources directly inside --in.
  With QUERY: searches resources inside --in.
  Default search: ranked search with query rewriting (library-configured search behaviour; not exposed as profile URIs in v3).
  --literal: canonical indexed text, no rewriting.
  --regex: canonical indexed text as regular expression.
  --literal and --regex are mutually exclusive.

DEFAULT SCOPE
  patchouli://

RESULTS（每条）
  uri, kind, label, revision, preview, writable, citable
  document/page/evidence 资源可另含 item_uri 或 parent_uri、citation_target
  适用 scope 可另含 available filters、facet counts、continuation cursor
```

`citable` 的含义是“该 URI 可以直接作为 `cite.refs` 输入”，不等同于资源可写，也不等同于资源本身就是 Item。只读资源可以是 citable；如果资源需要解析到所属 Item，响应应提供 `citation_target` 或 `item_uri`。

#### 3.4.4 `fetch`

```text
USAGE
  patchouli-cli fetch <URI>... [OPTIONS]

OPTIONS
  --range <RANGE>      Restrict textual content
  --revision <REV>     Fetch a specific resource revision
  --limit-bytes <N>    Maximum serialized response size per URI; over-limit responses are returned as explicit partial results

RANGES
  lines:<START>-<END>
  pages:<START>-<END>

CANONICAL REPRESENTATIONS
  item URI             BibLaTeX inspection / editable projection
  document URI         Document outline, owning item link, and page links
  page URI             Canonical Markdown and owning item/document link
  style URI            CSL XML
  evidence URI         Evidence record, source mapping, and owning item link

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
  Page resources may be fetched at a committed page revision. If no revision is supplied,
  current content is returned; pinned evidence remains the preferred reproducible path.
```

#### 3.4.5 `put`（有限写入）

```text
USAGE
  patchouli-cli put <URI> --from <PATH> --base <REVISION>
  patchouli-cli put <URI> --stdin       --base <REVISION>

WRITABLE
  patchouli://items/*.bib        (includes general items, mapped to @misc)
  patchouli://styles/*.csl

READ-ONLY
  patchouli://documents/**
  patchouli://evidence/**

BEHAVIOUR
  put replaces exactly one resource.
  put never creates, deletes, or renames resources.
  put validates the complete replacement before writing.
  put commits the replacement atomically.
  put fails if --base does not match the current revision.
  put never accepts a partial or truncated fetch result as a complete replacement.
  After a successful put in the desktop host, the write service emits an app-local resource
  changed notification; the UI refreshes affected library rows, open item editors, and CSL
  style views without requiring a process restart.
  failed validation never modifies the library.
```

**`general` 题录的 agent 可访问性**：为消除 agent 与人类之间的信息不对称，`general` 类型题录对 CLI/MCP 可读可写——`fetch` 以 `@misc` BibLaTeX 投影返回，`put` 以 `@misc` 回写时保留原 `general` 类型。若 agent 明确将完整投影改为受支持的非 `misc` 类型（例如 `@book` 或 `@article`），这是显式的类型修正，应按该 BibLaTeX 类型映射并持久化为新的 Patchouli 类型；未知或仍映射为 `general` 的类型必须失败。`@misc` 投影路径必须是 MCP 专用路径，不得改变 UI 的 `general` 导出/导入限制。`general` 可以在具备最低可渲染字段时通过显式 `@misc` fallback 参与 `cite`，响应必须带有 `general_as_misc` warning；字段不足或 renderer 拒绝时返回 `NOT_CITABLE`，不得静默把它当作 `book`、`article` 或其他类型。`put` 不得绕过该限制把它当作可渲染类型。

**可写 MCP 产品意图**（ADR `0023`）：v1/v2 首发 MCP 只能“访问”库；v3+ 的可写 MCP 才能让 agent **与库交互并辅助人类**——例如样式库缺少合格 CSL 时，由能读全文件的 agent 起草并 `put` 样式；题录字段错误时，agent 修正完整 `.bib` 投影后 `put` 回写。`put` 仍是窄范围、revision-gated 的整资源替换，不得扩展为 OCR 触发、bbox 编辑、索引重建、创建/删除/重命名资源。设置中写入工具可关闭；实现须符合 ADR `0023`，并保留文本-only、无路径/密钥/图像等安全边界（ADR `0010` 中仍有效的条款）。只读 document/page 仍不可 `put`，但不因此失去 `cite` 能力。

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
Successful references should be returned with per-reference status while invalid,
unresolvable, or non-citable references are reported in warnings/errors. The whole
request fails only when the request is invalid, the citation style cannot be loaded,
or no reference can be rendered.

#### 3.4.7 MCP 映射与共享契约

```text
CLI                         MCP
patchouli-cli find          patchouli.find
patchouli-cli fetch         patchouli.fetch
patchouli-cli put           patchouli.put
patchouli-cli cite          patchouli.cite
```

CLI 与 MCP 必须共享：参数名、默认值、URI 格式、校验规则、权限、响应 schema、错误码。

共享 JSON 响应：

```json
{
  "data": {},
  "revision": "lib:42",
  "warnings": [],
  "continuation": null,
  "error": null
}
```

`error` 为非空时，`data` 仍可以包含受限的 partial 结果。对 `fetch` 超限响应，
resource data 至少应能表达 `complete=false`、`truncated=true`、`returned_bytes`、
`limit_bytes` 和继续读取所需的 `continuation` 或 `next_range`。MCP tool response
必须同时标记 `isError=true` 并保留结构化 partial data；CLI 将 partial JSON 写入
stdout，并以非零退出码报告错误。只有明确带有 `complete=true` 的内容才可以作为
完整资源交给 `put`。

Exit / error codes：

| Code | Name | 含义 |
|---|---|---|
| 0 | OK | 成功（含空 find） |
| 2 | INVALID_ARGUMENT | 非法或冲突参数 |
| 3 | NOT_FOUND | URI 或 revision 不存在 |
| 4 | PERMISSION_DENIED | 资源权限或策略禁止当前操作 |
| 5 | REVISION_CONFLICT | `--base` 不匹配 |
| 6 | INVALID_CONTENT | BibLaTeX 或 CSL 校验失败 |
| 7 | RESPONSE_TRUNCATED | 响应超过安全大小上限，已返回显式 partial 内容 |
| 8 | UNAVAILABLE | Patchouli 服务不可用 |
| 9 | NOT_CITABLE | 资源存在，但无法解析为可渲染的引用目标 |

推荐探索顺序：无 query 的 find 发现 scope → 带 query 的 find → fetch outline → fetch 所需 page/range → 本地处理 → 仅对最终合法 `.bib`/`.csl` 执行 put。

#### 3.4.8 Agent 可用性与关系解析

- `citable=true` 必须与 `cite` 实际接受的 URI 类型一致。Document、Page 和 Evidence 是只读资源，但可以是 citable；`writable` 与 `citable` 是两个独立维度。
- Document、Page 和 Evidence 的 `find`/`fetch` 结果应返回 `item_uri` 或等价的 `parent_uri`。这是关系元数据，不构成自动 link following。
- `cite` 对 document/page 的解析只使用持久化的 `document_instances.item_id` 关系，不通过标题、文件名或全文搜索猜测 Item。Page URI 必须先验证 page 属于 URI 中声明的 Document。
- 如果多个 REF 解析到同一个 Item，bibliography 默认去重，但响应应保留每个 REF 的解析状态。
- `find` 带 query 时应搜索所声明的 `--in` scope。Item 至少支持题名/作者/identifier 等 metadata search，Style 至少支持 id/display name search；`item_type` 与 `status` 过滤用于 document/page/evidence 时应沿所属 Item 解析。尚未支持的 scope/filter 必须返回明确的 `INVALID_ARGUMENT`，不得以成功的空数组代替“不支持”。
- `fetch <URI>...` 和 `cite <REF>...` 均采用逐项结果语义。单个资源的 `NOT_FOUND`、`NOT_CITABLE` 或 `RESPONSE_TRUNCATED` 不得丢弃同一请求中其他成功结果。
- `limit_bytes` 的默认值可以由服务配置，但必须有服务端硬上限；调用者请求超过硬上限时可以钳制并返回 warning，不得因此取消已可安全返回的 partial 内容。
- `general` 的 `@misc` cite fallback 与无默认 style 时的 deterministic style fallback 已同步记录到 ADR `0023`/`0024`；后续实现变更必须同时维护 PRD、ADR 与运行时契约，避免三者分叉。

### 3.5 实现约束（无论 A/B）

- .NET 仍是唯一领域权威（库、SQLite、搜索、证据、CSL、权限）
- 不暴露本地路径、file URL、提供程序凭据/配置、缓存图像或 image path
- 不因 agent 调用而触发 OCR 或索引重建
- 响应必须受服务端硬大小上限保护；允许显式 partial response，但不得将 partial 内容呈现为完整资源
- Token/鉴权、bind、CORS、工具开关继续由本机 MCP 设置拥有；secret 不进快照与日志
- 打包与版本：CLI 若胜出，应作为一等发布物（或与桌面/MCP 同源构建），版本与库协议 revision 可观测

### 3.6 V3-T1 验收

| 编号 | 标准 |
|---|---|
| V3-AC1 | 存在可运行的 A/B 评测基准与任务集，结果可重复；外部 UUID-chain 证据已完成 |
| V3-AC2 | ADR `0024` 形成 B 择优结论，含迁移策略 |
| V3-AC3 | B 迁移完成：生产默认 MCP 表面单一；shell/sidecar 已从 main 彻底移除（不再启动、不再打包、不保留实现），仅历史分支 `feature/mcp-ab-benchmark` 存有评测证据 |
| V3-AC4 | 若 B 胜出：四工具与 CLI 同构；共享 JSON 与错误码有契约测试 |
| V3-AC5 | 安全锚点测试仍通过；`put` 若启用则仅限规定 URI 且 revision-gated |
| V3-AC6 | `general` 可在满足字段要求时通过显式 `@misc` fallback cite，否则返回 `NOT_CITABLE`；只读资源 put 返回 `PERMISSION_DENIED`，但 document/page/evidence cite 可解析到所属 Item |
| V3-AC7 | 超过 `limit_bytes` 的 fetch 返回安全边界内的 partial 内容、`complete=false`/`truncated=true`、continuation 或 next range，以及 `RESPONSE_TRUNCATED`；不得静默呈现为完整内容 |
| V3-AC8 | Document、Page、Evidence 的资源响应暴露所属 Item 关系；`citable` 与 cite 实际接受的 URI 类型一致；document/page cite 验证关系后成功解析 |
| V3-AC9 | 多 URI fetch 与多 REF cite 采用逐项结果语义；单项失败不丢弃同一请求中的成功结果，并对实际使用的 CSL style 返回 effective style |
| V3-AC10 | `find` 对声明支持的 scope/filter 执行搜索或过滤；不支持的 scope/filter 返回明确错误，不以成功空数组代替 |
| V3-AC11 | 桌面进程中的 MCP put 成功后发出资源变更通知；书库列表、打开的题录编辑器和 CSL 样式视图无需重启即可显示最新数据 |

## 4. V3-T2：增强 OCR 文本编辑校注 UI

**状态**：占位。  

目标方向（非正式需求）：在现有 PDF/Box Tree 工作台之上，增强面向校对与校注的文本编辑体验——选区、修订、批注、与 bbox/证据身份的稳定关联等。具体信息架构、命令集、数据模型与验收标准在后续 PRD 修订中补全。在补全前不实现范围外的校注持久化格式。

## 5. V3-T3：集成更多 OCR

**状态**：方向已定，细则待补。

- 使用 **LLMTornado** 集成多模态大语言模型 OCR/理解路径，输出仍必须进入既有 `OcrDocumentTreeCandidate` → 统一 import/adoption，禁止 provider 直写 `document_boxes`
- 同时探索接入：**onnxOCR**、**ultimateOCR**、**ndlocr-lite**、**ndlkotenocr-lite**
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

## 7. V3-T5：桌面 UI 体验提升

**状态**：新增；方案评估中。

本任务把若干互相关联的桌面 UI 改进集中在一起，避免零散占位条目分散注意。当前包含三个子项：书库页改进、搜索框改进、PDF 工作台 Markdown 预览。子项可以分批落地，但共享同一份 UI 一致性、可测试性与安全边界要求。

### 7.1 更好的书库页

- **来源列**：当前“来源”列只使用期刊名/出处文献（`publicationTitle`）。改进后按题录类型选择更合适的来源字段：
  - 专著（`book` 等）使用出版社（`publisher`）
  - 期刊文章等继续使用期刊名/出处文献
  - 其他类型使用与该类型对应的来源字段（如会议名、学位授予机构等）
  - 缺失字段回退到现有行为，不得显示空占位误导用户
- **详情面板**：当前右侧详情面板以固定键值网格展示少量字段。改进后使用 TableView 展示更多信息，支持折叠分组、长文本换行与可扩展的字段集，避免一次塞满有限网格列。
- 列宽、列顺序、列显隐等已有持久化行为必须继续生效；新增来源字段策略不得破坏现有列持久化测试。
- 详情面板扩展不得修改 Item 元数据、Document Box Tree 或证据身份；它只是展示投影。

### 7.2 更好的搜索框

- 在顶部搜索框旁加入快捷下拉菜单，可切换两种模式：
  - **元数据筛选**：在书库题录元数据范围内筛选
  - **全文搜索**：现有的全文检索行为
- 无论处于哪种模式，搜索框都必须继续解析 `patchouli://` URI 并导航到对应资源；URI 解析路径不因模式切换而失效。
- 搜索由回车键触发，不再依赖或要求用户点击“搜索”按钮；按钮仍可保留作为备选触发方式，但回车是主交互路径。
- 模式切换是纯 UI 状态，不得改变 SearchUnit、FTS 索引、证据或 MCP 表面；两种模式复用既有搜索服务与搜索配置文件。
- 切换模式时必须保持当前输入文本，不得清空用户已输入内容；空查询行为按模式各自定义。
- 下拉菜单必须有明确的当前模式标识和可访问性提示，不得依赖仅靠图标无法区分的控件。

### 7.3 PDF 工作台的正确 Markdown UI 预览

目标是为 PDF 工作台页面中的 OCR/文档 Markdown 内容提供正确且稳定的 UI 预览，替代不完整或与 Avalonia 控件行为不一致的临时渲染方式。

- 预计评估并优先采用 `MarkView.Avalonia` 作为 PDF 工作台中的 Avalonia Markdown UI 预览组件；当前仅记录为候选方案，不代表依赖已经加入项目。
- PDF 工作台预览必须正确处理当前 OCR/文档内容实际使用的 Markdown/GFM，包括标题、段落、列表、引用、代码、表格、链接和安全的内联 HTML。
- 预览只是 PDF 工作台的 UI 展示投影，不得修改 canonical Markdown、Document Box Tree、SearchUnit、EvidenceRef 或 revision 身份。
- PDF 工作台中的预览不得暴露本地路径、`file:` URL、提供程序密钥或缓存图像路径；外部链接和 HTML 处理必须有明确的安全策略。
- 组件选型必须验证 Avalonia/.NET 版本兼容性、中文字体与布局、PDF 工作台长文档性能、主题适配、测试可控性和发布包体积，再决定是否正式引入依赖。

### 7.4 V3-T5 验收

| 编号 | 标准 |
|---|---|
| V3-T5-AC1 | 书库页“来源”列按题录类型展示出版社/期刊名/对应来源字段；缺失字段有明确回退，不破坏现有列持久化测试 |
| V3-T5-AC2 | 书库页详情面板使用 TableView 展示更多字段，支持折叠分组与长文本换行；展示投影不修改领域数据 |
| V3-T5-AC3 | 搜索框下拉菜单可在元数据筛选与全文搜索间切换；两种模式均能解析 `patchouli://` URI 并导航；切换不清空输入；回车键触发搜索 |
| V3-T5-AC4 | 模式切换不改变 SearchUnit、FTS 索引、证据或 MCP 表面；复用既有搜索服务与搜索配置文件 |
| V3-T5-AC5 | 代表性 OCR/文档 Markdown/GFM fixture 在 PDF 工作台页面中正确呈现标题、列表、引用、代码、表格、链接和安全内联 HTML |
| V3-T5-AC6 | PDF 工作台预览失败或不支持的语法可解释，不静默丢失正文；canonical Markdown 与领域数据不被修改 |
| V3-T5-AC7 | PDF 工作台预览不会暴露本地路径、file URL、提供程序密钥、缓存图像路径或未允许的 HTML/脚本内容 |
| V3-T5-AC8 | MarkView.Avalonia（或其他候选组件）通过 Avalonia 兼容性、主题、中文文本、PDF 工作台长文档性能、包体积和发布构建验证后才进入生产依赖 |

## 8. 明确不做（v3 默认）

- 不把向量化、混合搜索、语义搜索作为 v3 完成标准
- 不做程序托管的原文件同步
- 不做账号注册、配额购买、云端计费管理
- 不做自动对象级同步合并或静默 last-writer-wins
- 不做库级加密/主密码方案
- 不让 MCP/CLI 获得 OCR 触发、索引重建、任意删除/重命名资源、或读取提供程序密钥的能力
- macOS 不上架 Mac App Store / 不启用 App Sandbox 作为前提（既有 ADR）

## 9. 版本理念

- **v1**：alpha 可验证基线——保护证据，暴露歧义，拒绝不安全自动化  
- **v2（0.2.x）**：最终用户可用面——UI、CSL、生产 OCR、可配置 MCP、冲突/阻塞  
- **v3（0.3.x）**：迈向 1.0——用评测选择长期 agent 表面，打磨 OCR 编辑校注，扩展可替换 OCR 组合，只留下经得起稳定承诺的能力  
- **1.0**：在 v3 验证通过的能力组合上冻结对外契约与升级策略  

## 10. 长期约束索引

| 约束 | 权威位置 |
|---|---|
| 领域词汇与产品边界 | `.agent/CONTEXT.md` |
| 运行库与快照分离、分片、library_id、三层模型、OCR/证据/MCP 等 | `.agent/adr/`（`0001`–`0011`、`0014`、`0015`、`0022`、`0023` 等） |
| 已移除的 Bashkit MCP 实现 | ADR `0022`（已由 ADR `0024` 取代；实现已从 main 删除，仅存于 `feature/mcp-ab-benchmark` 分支） |
| 有限可写 MCP（item `.bib` / style `.csl` put） | ADR `0023`（修订 `0010` 的“绝对只读”后果） |
