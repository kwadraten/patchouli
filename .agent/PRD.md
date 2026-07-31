# Patchouli PRD v3

状态：正式版  
版本线：0.3.x（迈向 1.0）  
日期：2026-07-31

仓库只维护这一份 PRD。已完成能力压缩为顶部 walkthrough；长期不变量以 `.agent/CONTEXT.md` 与 `.agent/adr/` 为准。

## Walkthrough（已完成基线）

v1/v2 已交付、可视为 0.2.x 稳定面的能力。细节以代码、测试与 ADR 为准，不在此复述需求正文。

- 桌面栈：.NET + Avalonia；首轮初始化、库页、题录编辑、搜索、OCR 队列、PDF 工作台、设置、About
- 库与文件：路径无关 `library_id`；Item / FileAsset / DocumentInstance；FileSearchRoot 与文件解析冲突
- Document Box Tree：0.2.0 fresh schema；页级 immutable revision；typed leaf；sibling 顺序；Markdig 中央编译
- 搜索与证据：SearchUnit + 可重建本地 FTS；搜索配置文件；`evref:v2`；pinned 默认、current/compare 显示漂移
- OCR：MinerU 为首选生产路径；`OcrDocumentTreeCandidate` 统一 staging/adoption；队列看板；局部 OCR 与工作台编辑
- CSL：type-aware `CslItemTypeProfile`；`general` 不可静默当 CSL `document`；样式管理与复制/导出；渲染失败不空成功
- MCP（当前实现）：可配置端口/bind/CORS/token/工具开关；第一版 MCP 是只读且纯文本的；Bashkit `patchouli_shell` 虚拟 shell（ADR `0022`）
- UI 信息架构：设置五分组；`UiCommandDescriptor`；书库 DataGrid（列宽/顺序/排序/显隐/持久化）；阻塞与冲突模态
- 同步：快照分片；分支检查与显式导入；无自动对象级合并

## 测试锚点与长期边界

下列短语与约束必须继续可被文档/契约测试命中；权威解释见 CONTEXT/ADR，本处仅作产品声明：

- MCP 从不触发 OCR 或索引重建
- 搜索配置文件
- 本地 FTS 索引是可重建的本地缓存
- 提供程序凭据；MCP 无法读取提供程序密钥
- 缓存图像；MCP never returns cached images or image paths；`page_renders`
- 第一版 MCP 是只读且纯文本的
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
| V3-T1 | MCP 路线选择与完善 | 规格已定；先评测再择优落地 |
| V3-T2 | 增强 OCR 文本编辑校注 UI | 占位；正文后续补全 |
| V3-T3 | 集成更多 OCR provider | 方向已定；细则后续补全 |

## 3. V3-T1：MCP 路线选择与完善

### 3.1 问题

当前生产 MCP 表面是 Bashkit 只读虚拟 shell（`patchouli_shell`，ADR `0022`）：对熟悉 shell 的 agent 友好，但存在：

- 探索路径长、组合命令成本高、边界与资源限制复杂
- CLI 与 MCP 工具不是同一套动词/参数/错误码
- 写回题录/样式没有一等、可审计的协议（现状以只读为主）

v3 要决定：**继续深化 Bashkit 路线，还是切换到 CLI/MCP 同构的 `patchouli-cli` 资源协议**，并在胜出方案上重构与优化，作为 1.0 对外 agent 表面的候选。

### 3.2 候选方案

| 方案 | 摘要 |
|---|---|
| A. Bashkit 虚拟 shell | 单工具 `patchouli_shell`；VFS + 域内建命令；现状 ADR `0022` |
| B. CLI/MCP 同构 `patchouli-cli` | 四动词 `find` / `fetch` / `put` / `cite`；URI 资源树；CLI 与 MCP 共享参数、校验、权限、响应与错误码 |

**决策流程（强制）**：

1. 先建立可重复的评测基准（见 3.3），对 A/B 对照跑通
2. 记录分数、失败模式、agent 轨迹长度与安全事件
3. 择优；落败方案可保留为过渡/兼容，但不得双主路径长期分叉
4. 若 B 胜出：以本节资源协议为权威规格重构 MCP；落实 ADR `0023` 有限可写；修订 `0022` 等与表面形态冲突的条款
5. 若 A 胜出：吸收 B 中已验证的 URI/响应/错误模型优点，避免 shell 专有语义膨胀

在基准完成前，**不得**删除 Bashkit 实现或把 B 直接定为唯一生产路径。

### 3.3 评测基准（先于重构）

基准必须可脚本化、可回归，覆盖人类操作员与 agent 轨迹。至少包含：

| 维度 | 度量 |
|---|---|
| 任务完成率 | 固定任务集（发现样式、检索文献页、取证、渲染题录、有条件写回 `.bib`/`.csl`）一次成功率 |
| 轨迹效率 | 达到目标的中位工具调用次数、中位 token/字符吞吐、中位墙钟时间 |
| 可发现性 | 无先验 URI 时，从根浏览到目标资源的步数与失败率 |
| 正确性 | 错误 URI/错误 revision/越权/校验失败时的错误码稳定性；无静默截断 |
| 安全边界 | 不出现本地路径、file URL、提供程序密钥、缓存图像、OCR/索引触发；日志不落查询正文与 secret |
| 权限模型清晰度 | 只读 vs 可写资源是否可被 agent 从响应字段（如 `writable`）推断 |
| 实现与运维成本 | 依赖面、打包物、协议复杂度、故障恢复（sidecar fault 等） |
| 同构性 | CLI 与 MCP 是否可对同一输入得到同一 JSON schema 与同一 exit/error code |

固定任务集示例（可扩展，但变更需版本化）：

1. 列出/搜索 CSL 样式并 `fetch` 一个 style
2. 在 documents 范围搜索关键词，再 `fetch` outline 与单页 range
3. 解析 evidence 并 `cite`
4. 对可写 item `.bib` 做 `fetch` → 本地修改 → `put --base` 成功与 revision conflict 失败
5. 对 `general` 题录与只读 document 页的 `put`/`cite` 拒绝路径
6. 故意越权与超大响应：必须失败而非截断成功

评测产出写入 issue 或 ADR 草案：分数表、样例轨迹、选择理由、迁移计划。

### 3.4 方案 B 规格：`patchouli-cli`（CLI/MCP 同构）

> 本节是候选权威规格。若评测选定 B，则实现必须符合；若选定 A，本节仍作为对照基准与“同构资源协议”灵感来源保留在 PRD 中，直到被 ADR 替代。

**定位**：`patchouli-cli` — access an academic literature library powered by patchouli.net

#### 3.4.1 全局

```text
USAGE
  patchouli-cli [--json] <COMMAND> [ARGUMENTS]

COMMANDS
  find      Discover or search resources
  fetch     Retrieve known resources
  put       Replace one writable resource
  cite      Render citations from items or evidence

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
  适用 scope 可另含 available filters、facet counts、continuation cursor
```

#### 3.4.4 `fetch`

```text
USAGE
  patchouli-cli fetch <URI>... [OPTIONS]

OPTIONS
  --range <RANGE>      Restrict textual content
  --revision <REV>     Fetch a specific resource revision
  --limit-bytes <N>    Maximum total response size

RANGES
  lines:<START>-<END>
  pages:<START>-<END>

CANONICAL REPRESENTATIONS
  item URI             BibLaTeX inspection / editable projection
  document URI         Document outline and page links
  page URI             Canonical Markdown
  style URI            CSL XML
  evidence URI         Evidence record and source mapping

BEHAVIOUR
  fetch never searches.
  fetch never follows links automatically.
  fetch never returns another representation of the same URI.
  Large responses fail instead of being silently truncated.
```

#### 3.4.5 `put`（有限写入）

```text
USAGE
  patchouli-cli put <URI> --from <PATH> --base <REVISION>
  patchouli-cli put <URI> --stdin       --base <REVISION>

WRITABLE
  patchouli://items/*.bib
  patchouli://styles/*.csl

READ-ONLY
  patchouli://documents/**
  patchouli://evidence/**
  bibliography items of type general

BEHAVIOUR
  put replaces exactly one resource.
  put never creates, deletes, or renames resources.
  put validates the complete replacement before writing.
  put commits the replacement atomically.
  put fails if --base does not match the current revision.
  failed validation never modifies the library.
```

**可写 MCP 产品意图**（ADR `0023`）：只读 MCP 只能“访问”库；可写 MCP 才能让 agent **与库交互并辅助人类**——例如样式库缺少合格 CSL 时，由能读全文件的 agent 起草并 `put` 样式；题录字段错误时，agent 修正完整 `.bib` 投影后 `put` 回写。`put` 仍是窄范围、revision-gated 的整资源替换，不得扩展为 OCR 触发、bbox 编辑、索引重建、创建/删除/重命名资源。设置中写入工具可关闭；实现须符合 ADR `0023`，并保留文本-only、无路径/密钥/图像等安全边界（ADR `0010` 中仍有效的条款）。

#### 3.4.6 `cite`

```text
USAGE
  patchouli-cli cite <REF>... --style <URI> [OPTIONS]

OPTIONS
  --style <URI>        required CSL style URI
  --locale <LOCALE>
  --bibliography
  --html

RESTRICTIONS
  Items of type general cannot be cited.
  Inspection-only .bib projections are not formal exports.
```

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
  "continuation": null
}
```

Exit / error codes：

| Code | Name | 含义 |
|---|---|---|
| 0 | OK | 成功（含空 find） |
| 2 | INVALID_ARGUMENT | 非法或冲突参数 |
| 3 | NOT_FOUND | URI 或 revision 不存在 |
| 4 | PERMISSION_DENIED | 不可读/写/cite |
| 5 | REVISION_CONFLICT | `--base` 不匹配 |
| 6 | INVALID_CONTENT | BibLaTeX 或 CSL 校验失败 |
| 8 | UNAVAILABLE | Patchouli 服务不可用 |

推荐探索顺序：无 query 的 find 发现 scope → 带 query 的 find → fetch outline → fetch 所需 page/range → 本地处理 → 仅对最终合法 `.bib`/`.csl` 执行 put。

### 3.5 实现约束（无论 A/B）

- .NET 仍是唯一领域权威（库、SQLite、搜索、证据、CSL、权限）
- 不暴露本地路径、file URL、提供程序凭据/配置、缓存图像或 image path
- 不因 agent 调用而触发 OCR 或索引重建
- Token/鉴权、bind、CORS、工具开关继续由本机 MCP 设置拥有；secret 不进快照与日志
- 打包与版本：CLI 若胜出，应作为一等发布物（或与桌面/MCP 同源构建），版本与库协议 revision 可观测

### 3.6 V3-T1 验收

| 编号 | 标准 |
|---|---|
| V3-AC1 | 存在可运行的 A/B 评测基准与任务集，结果可重复 |
| V3-AC2 | 形成书面择优结论（ADR 或 PRD 修订），含迁移/兼容策略 |
| V3-AC3 | 胜出方案完成重构：生产默认 MCP 表面单一，无双主路径 |
| V3-AC4 | 若 B 胜出：四工具与 CLI 同构；共享 JSON 与错误码有契约测试 |
| V3-AC5 | 安全锚点测试仍通过；`put` 若启用则仅限规定 URI 且 revision-gated |
| V3-AC6 | `general` 不可 cite；只读资源 put 返回 PERMISSION_DENIED |

## 4. V3-T2：增强 OCR 文本编辑校注 UI

**状态**：占位。  

目标方向（非正式需求）：在现有 PDF/Box Tree 工作台之上，增强面向校对与校注的文本编辑体验——选区、修订、批注、与 bbox/证据身份的稳定关联等。具体信息架构、命令集、数据模型与验收标准在后续 PRD 修订中补全。在补全前不实现范围外的校注持久化格式。

## 5. V3-T3：集成更多 OCR

**状态**：方向已定，细则待补。

- 使用 **LLMTornado** 集成多模态大语言模型 OCR/理解路径，输出仍必须进入既有 `OcrDocumentTreeCandidate` → 统一 import/adoption，禁止 provider 直写 `document_boxes`
- 同时探索接入：**onnxOCR**、**ultimateOCR**、**ndlocr-lite**、**ndlkotenocr-lite**
- MinerU 仍为已交付的生产参考路径；新 provider 的打包、模型分发、许可、preset UX、失败分类与密钥边界在后续修订中规定
- 继续遵守：Mock/历史占位不进生产默认；secret 仅 local-only credentials；不规则表等 canonical 规则不因新 provider 回退

## 6. 明确不做（v3 默认）

- 不把向量化、混合搜索、语义搜索作为 v3 完成标准
- 不做程序托管的原文件同步
- 不做账号注册、配额购买、云端计费管理
- 不做自动对象级同步合并或静默 last-writer-wins
- 不做库级加密/主密码方案
- 不让 MCP/CLI 获得 OCR 触发、索引重建、任意删除/重命名资源、或读取提供程序密钥的能力
- macOS 不上架 Mac App Store / 不启用 App Sandbox 作为前提（既有 ADR）

## 7. 版本理念

- **v1**：alpha 可验证基线——保护证据，暴露歧义，拒绝不安全自动化  
- **v2（0.2.x）**：最终用户可用面——UI、CSL、生产 OCR、可配置 MCP、冲突/阻塞  
- **v3（0.3.x）**：迈向 1.0——用评测选择长期 agent 表面，打磨 OCR 编辑校注，扩展可替换 OCR 组合，只留下经得起稳定承诺的能力  
- **1.0**：在 v3 验证通过的能力组合上冻结对外契约与升级策略  

## 8. 长期约束索引

| 约束 | 权威位置 |
|---|---|
| 领域词汇与产品边界 | `.agent/CONTEXT.md` |
| 运行库与快照分离、分片、library_id、三层模型、OCR/证据/MCP 等 | `.agent/adr/`（`0001`–`0011`、`0014`、`0015`、`0022`、`0023` 等） |
| 当前 Bashkit MCP 实现 | ADR `0022`（v3 评测后可能修订或取代） |
| 有限可写 MCP（item `.bib` / style `.csl` put） | ADR `0023`（修订 `0010` 的“绝对只读”后果） |
