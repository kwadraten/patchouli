# MCP / CLI 资源协议

本文件承接原 PRD §3.4 的规范性参数、投影、schema 与错误语义，作为长期契约维护；产品进度见 [PRD](PRD.md)。选择理由见 [ADR 0024](adr/0024-adopt-structured-mcp-resource-protocol.md)，写入边界见 [ADR 0023](adr/0023-allow-limited-writable-mcp.md)，证据身份以 [ADR 0028](adr/0028-versioned-uri-evidence.md) 为准。

实现状态（2026-09-10）：结构化四工具、CLI HTTP 客户端、TOON/JSON 与有限写入已落地。下文的宿主自动发现、自启 headless、互斥与桌面接管仍是目标契约，不能当作现有能力；由 PRD V3-T1 跟踪。回归义务不等于本次已经执行验收。

**定位**：`patchouli-cli` — access an academic literature library powered by patchouli.net

## 1 全局

```text
USAGE
  patchouli-cli [--json] <COMMAND> [ARGUMENTS]

COMMANDS
  find      Discover or search resources
  fetch     Retrieve known resources
  put       Replace one writable resource
  cite      Render citations from citation-capable item/document/page/evidence refs

GLOBAL OPTIONS
  --json       Return JSON instead of the default TOON listing; does not change the unified response schema or entry projection
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

**Help 与初次握手**：CLI `--help` 和 MCP 的初次握手/initialize 响应必须明确说明统一响应外壳及 `message` 的 Unix 哲学：正常且没有需要报告事项的成功响应**不返回 `message` 字段**；调用方看到没有 `message` 即可将该响应视为干净成功，不应期待成功文本。只有有稳定 warning 或 error 需要机器处理时才返回 `message`；warning 不改变成功 exit/error code，error 则按错误码决定失败。帮助、握手、示例与工具 schema 均不得把空字符串、`"OK"`、空 `message` object 或人类成功文案当作成功信号。

**三个操作界面、一个 Library runtime host**：桌面 UI、`patchouli-cli` 和 MCP 是同一 Library 的不同操作界面；Library 数据库、cursor、revision 与领域规则的唯一运行时权威是 .NET **宿主**。桌面宿主承载全功能人类 UI 与本地 MCP HTTP 端点；MCP 是供 agent 在本地或远程协作时使用的网络前端；CLI 同时面向人类和 agent，是该本地 MCP HTTP 端点的瘦客户端。三者因此在构造上共用同一次服务端 URI 解析、投影、校验、revision、错误和写入语义。

CLI 先发现选定 Library 的本地宿主并调用其 MCP HTTP 端点；桌面未运行时，CLI 必须自启同一二进制的无 UI headless 宿主，待其就绪后再执行同一请求。headless 宿主以后台守护程序持续运行。每个 Library 同时只能有一个宿主，由持久化发现记录和互斥锁保证；桌面启动同一 Library 时必须终止 headless 宿主、取得数据库所有权后接管服务。CLI 不得直连 SQLite、另建领域实现或绕过宿主写服务。`--from`/`--stdin` 仍只是 CLI 客户端读取内容的本地适配器，随后必须形成内联 MCP `content` 请求。

所有 UI、CLI 和 MCP 写入均流经宿主的单一写服务；成功提交会发布资源变更通知，桌面 UI 据此刷新。桌面与 headless 宿主共用 MCP 设置；headless 监听 `0.0.0.0` 时同样必须有 token，且 bind、CORS、工具开关和认证策略与桌面宿主一致。宿主生命周期命令（例如显式 `serve-mcp`）可以提供给 CLI，但不属于 `find` / `fetch` / `put` / `cite` 四个资源动词，也不映射为 MCP 工具。

## 2 资源树

```text
RESOURCE TREE
  patchouli://items/
  patchouli://items/{item-id}.bib

  patchouli://texts/
  patchouli://texts/{document-instance-id}/
  patchouli://texts/{document-instance-id}/page-{page-index}.md
  patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree-revision-id}&box={box-id}

  patchouli://csl-styles/
  patchouli://csl-styles/{style-id}.csl
```

无参数 `find` 是 VFS 根目录发现，只返回 `/items`、`/texts`、`/csl-styles` 三个 directory 条目及各自的 canonical URI。根目录不暴露 `/evidence`、`/AGENTS.md`、`/library.yml`、`collections`、`profiles` 或其他虚拟 skill 文件。Evidence 仅通过 text page URI 的 `?rev={tree-revision-id}&box={box-id}` 访问。该 VFS/URI 发现层不恢复 Bashkit 或 `patchouli_shell`。

`page-index` 是指定 DocumentInstance 内与物理 PDF 页对应的**一基**页码。DocumentInstance 的物理页顺序稳定，不因 UI、CLI 或 MCP 的访问而重排；因此 `page-1.md` 是人类报告和程序调用共用的第一页。`?rev=&box=` 是 evidence 的规范消费形式：服务在 `fetch` 或 `cite` 消费它时必须验证 `tree_revision_id`/`box_id` 实际归属所声明的 DocumentInstance 和 page；不存在或不归属时返回 `NOT_FOUND`，不得把其他页面的 evidence 作为成功结果返回。

TOON 使用 MIT `Corvus.Toon.SystemTextJson` NuGet 包作为唯一编码/解码实现，并遵循 TOON specification **v3.0**；不得维护自定义 TOON parser 或 encoder。协议编码固定使用 UTF-8/LF、`ToonWriterOptions` 的字面 TAB delimiter 与 `KeyFolding=Off`；默认 uniform entries 使用 TOON v3 tabular form，声明的 `[N]` 必须与实际行数一致。`text/toon` 是其媒体类型。本资源发现与格式决策已由 ADR `0024` 的 2026-08-01 修订同步；后续改变 URI 根、evidence 消费形式、TOON 库或默认编码时，必须同时更新本契约、ADR 与运行时契约。

## 3 `find`

```text
USAGE
  patchouli-cli find [QUERY] [OPTIONS]

ARGUMENTS
  QUERY                Optional search query

OPTIONS
  --in <URI>           Scope to search or browse
  --where <KEY=VALUE>  Filter structured metadata; repeatable
  --literal            Disable query rewriting
  --limit <N>          Maximum results; default: 20
  --cursor <CURSOR>    Continue a previous real-time result page
  --long               Return the detailed entry projection

BEHAVIOUR
  Without QUERY: lists resources directly inside --in.
  With QUERY: searches resources inside --in.
  The root scope (`patchouli://`) is discovery-only: it accepts no QUERY or --where;
  choose a returned VFS directory before searching or filtering.
  Text search defaults to ranked SearchUnit search with query rewriting (library-configured search behaviour; not exposed as profile URIs in v3); item and style search use the scope matrix below.
  --literal: direct plain-text matching in the declared scope's supported fields, with no query rewriting.
  --literal requires a non-whitespace QUERY.
  A null, empty, or whitespace-only QUERY is browse, like POSIX `ls`; a non-whitespace
  QUERY is search, like POSIX `find`. A supplied whitespace-only QUERY is normalized
  to browse and returns `WHITESPACE_QUERY_TREATED_AS_BROWSE`.
  Regular-expression search is not part of the CLI/MCP protocol. Agents that need it
  must match the returned text locally after find/fetch.
  Cursors are opaque, stateless, real-time continuation tokens. They bind the
  declared scope, query, filters, ordering, and continuation position, but do not
  retain a materialized result set or server-side handle.
  A cursor resumes its embedded normalized scope, query, filters, and order. If a
  continuation request supplies conflicting values, cursor context wins and the
  response returns `CURSOR_CONTEXT_RESTORED`; an invalid cursor still returns
  INVALID_ARGUMENT. limit is not bound and may change the next page size.
  A text search in `patchouli://texts/` returns one file entry per matching
  SearchUnit. Its default `uri` is the canonical evidence-consumption page URI:
  `patchouli://texts/{document-instance-id}/page-{page-index}.md?rev={tree-revision-id}&box={box-id}`.
  The `rev`/`box` query parameters are therefore present in the default three-field entry projection,
  not only in --long metadata or a separate match field.

DEFAULT SCOPE
  patchouli://

DEFAULT RESULTS
  A root object with meta, continuation, optional message, and entries.
  meta.domain_total: all directly discoverable entries in the current VFS scope,
                       before QUERY/--where, evaluated when this page is read.
  meta.filtered_total: all QUERY/--where matches before pagination, evaluated
                       when this page is read.
  meta.shown_total: entries returned in this page.
  meta.library_revision: the current persistent Library revision.
  continuation: next cursor or null.
  message: omitted for a clean success; when present, contains stable warnings and/or an error.
  entries: exactly uri, title, type; type is file or directory.

DETAILED RESULTS (--long)
  Preserve uri, title, type and add only metadata that is applicable to the
  returned resource. URI already identifies the DocumentInstance, physical
  page and (when present) versioned evidence URI, so long entries never repeat
  document_instance_id, page_index or rev/box. The only public status
  fields are item_status, document_status and source_status; each reflects
  the original status of Item, DocumentInstance or FileAsset respectively.
  OCR/search-index readiness is an independent shared FSM value, not a
  fourth status or an alternate document_status. The stable English values
  are no_primary_document, ocr_failed, ocr_running, no_ocr, ocr_not_indexed
  and indexed; the UI renders the same value as a concise Chinese label plus
  an explanatory detail.
  Because detailed fields differ by resource, detailed entries use TOON list
  form rather than a uniform table; default entries always remain the compact
  table.
```

| `--in` scope | 无 QUERY | 普通 QUERY | `--literal` | 可用 `--where` |
|---|---|---|---|---|
| `patchouli://` | 仅发现三个根目录；`--limit`/`--cursor` 按普通分页处理并返回 `ROOT_DISCOVERY_PAGINATED` | `INVALID_ARGUMENT` | `INVALID_ARGUMENT` | `INVALID_ARGUMENT` |
| `patchouli://items/` | 浏览题录资源 | 题名、作者、identifier 元数据搜索 | 相同字段的直接字面匹配 | `item_type`、`item_status`、`primary_document_ocr_index_status`、`citable` |
| `patchouli://texts/` | 浏览 text document 资源 | 带 query rewrite 的 SearchUnit 全文搜索；每个命中 SearchUnit 一项 | canonical indexed SearchUnit text 的直接字面匹配 | `item_id`、`item_type`、`item_status`、`document_status`、`source_status`、`ocr_index_status`、`citable` |
| `patchouli://csl-styles/` | 浏览 CSL style 资源 | style id、display name 搜索 | 相同字段的直接字面匹配 | `style_enabled` |
| 已知 file URI | 当作单资源 scope 返回该 entry，并返回 `FILE_URI_SINGLETON_SCOPE` | 仅在该资源的矩阵内搜索字段上匹配，并返回同一 warning | 相同的单资源直接字面匹配 | 使用其所属 scope 的 filter 键 |

`--in` file URI 的单资源处理只返回 discovery entry，不自动 fetch 内容或改写为父目录 scope。scope、flag 或 filter 不在矩阵中的组合必须返回 `INVALID_ARGUMENT`，不得回退为成功空列表。`--regex` 是未知选项并返回 `INVALID_ARGUMENT`；它不会被当作 literal query 或由服务端解释。

`--where` 每个 clause 在**第一个** `=` 分割，余下的 `=` 全部属于 value；发生此归一化时返回 `WHERE_VALUE_CONTAINS_EQUALS`。同一 key 重复出现时按 CLI 常见的最后一项覆盖先前项，而不是 AND/OR；发生覆盖时返回 `DUPLICATE_WHERE_KEY_LAST_WINS`。这些 warning 只说明输入被隐式处理，不改变成功响应或 exit/error code。

默认列表是导航视图，不返回 `path`、`kind`、`label`、`revision`、`writable`、匹配片段、关系字段、状态字段或能力字段。`meta` 与 `continuation` 是导航协议，不属于 entry 详细字段，始终返回；`message` 仅在有 warning 或 error 时出现。CLI 普通输出和 MCP 默认文本结果均为 TOON；CLI `--json` 和 MCP `format=json` 可以选择 JSON，但不会隐式返回详细字段。`format=json` 是 agent 批量读取、爬取或交给其他编程语言处理时的等价回退格式：它必须完整表达相同的统一外壳、entry projection、分页、message 与 error 语义，调用方无需解析 TOON。格式选择只改变编码，不改变 query、filter、权限、返回字段或默认/详细投影。MCP 以 `detail=long` 选择与 CLI `--long` 相同的详细投影。

**TOON 确定性 profile**：TOON 和 JSON 先共享同一份严格类型化的 JSON data model，再编码为各自文本。`Corvus.Toon.SystemTextJson` 是唯一的 encoder/strict decoder；它按 TOON v3 词法规则对 string 进行引用和 escape，数值 string、enum 与 bare token 均不得通过自定义字符串后处理改变语义。协议固定 UTF-8/LF、literal TAB 与 `KeyFolding=Off`；`integer`/`number` 保持 JSON number 类型，`boolean` 为无引号小写 `true`/`false`，null 为无引号 `null`。升级依赖或改变 profile 必须提高协议 revision，并以 JSON↔TOON round-trip 与逐字 golden fixture 验证。

**实时分页与计数**：cursor 不创建服务端快照、结果集句柄、TTL 或 agent 专属命名空间。每一页都对当前 Library 状态重新求值；只要响应发出 continuation，或请求消费 cursor，响应必须包含 `RESULT_SET_MAY_HAVE_CHANGED` warning，表示后续页面的 entries、`domain_total`、`filtered_total` 可能因 UI、CLI 或其他 agent 的修改而与此前页面不同，也可能出现遗漏或重复。调用方需要稳定结果时，应在无并发修改的时段自行完成遍历；v3 不提供跨页快照一致性保证。

`citable` 的含义是“该 URI 可以直接作为 `cite.refs` 输入”，不等同于资源可写，也不等同于资源本身就是 Item。只读资源可以是 citable；协议不暴露 `citation_target`。`cite` 在宿主内部按持久化关系解析 document、page 和 evidence 到所属 Item。全文搜索默认条目中的 `?rev=&box=` page URI 本身就是可直接交给 `fetch` 或 `cite.refs` 的 canonical URI，不需要先请求 `--long` 来发现 evidence。

默认根目录响应示例：

```toon
meta:
  library_revision: "lib:42"
  domain_total: 3
  filtered_total: 3
  shown_total: 3
continuation: null
entries[3	]{uri	title	type}:
  "patchouli://items/"	"/items"	"directory"
  "patchouli://texts/"	"/texts"	"directory"
  "patchouli://csl-styles/"	"/csl-styles"	"directory"
```

`--where` 可重复，但只接受上表中当前 scope 的键。`item_status` 保留 `items.status` 的用户自定义值，并将 null 显式映射为 `unset`；`document_status` 原样反映 `document_instances.status`（`active`、`deprecated`、`partial`、`missing_source`）；`source_status` 原样反映关联 `file_assets.status`，无 FileAsset 时映射为 `unavailable`。`primary_document_ocr_index_status` 与 `ocr_index_status` 接受共享 FSM 的 English value；前者检查 Item 的 `is_primary=1` DocumentInstance，后者检查当前 text DocumentInstance。它们是可实时重算的能力，不是 status。所有过滤可在默认视图使用；需要解释状态或能力时再请求详细视图。公共协议中不存在裸 `status` 字段、过滤键、别名、重定向或兼容层。

## 4 `fetch`

```text
USAGE
  patchouli-cli fetch <URI>... [OPTIONS]

OPTIONS
  --range <RANGE>      Restrict textual content
  --limit-bytes <N>    Maximum serialized response size per URI; over-limit responses are returned as explicit partial results

RANGES
  lines:<START>-<END>
  pages:<START>-<END>

CANONICAL REPRESENTATIONS
  item URI             BibLaTeX inspection / editable projection
  text document URI    Document outline, owning item link, and page links
  text page URI        Canonical Markdown and owning item/document link
  text page ?rev=&box= URI  Versioned evidence text, source mapping, and owning item link
  style URI            CSL XML

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
  Fetch always returns the current resource projection unless the URI carries `rev`,
  in which case it returns the immutable revision named by `rev`. Historical selection
  by `rev` is authoritative; there is no separate pinned/current/compare mode.
```

`pages` range 的 START/END 是包含端点的一基物理 PDF 页码，与 `page-{page-index}.md` 使用同一编号；不得在 API 边界使用零基页码。

## 5 `put`（有限写入）

```text
USAGE
  patchouli-cli put <URI> --from <PATH>
  patchouli-cli put <URI> --stdin

MCP INPUT
  patchouli.put { "uri": "<URI>", "content": "<complete replacement>" }

WRITABLE
  patchouli://items/*.bib        (includes general items, mapped to @misc)
  patchouli://csl-styles/*.csl

READ-ONLY
  patchouli://texts/**

BEHAVIOUR
  put replaces exactly one resource.
  put never creates, deletes, or renames resources.
  For an existing item resource, the URI is authoritative: the BibLaTeX entry key in
  replacement content is ignored and the Item's existing citation key is preserved.
  put validates the complete replacement before writing.
  put commits the replacement atomically.
  put has no --base option and does not use optimistic concurrency.
  Concurrent valid replacements are serialized by commit order; the last successfully
  committed complete replacement becomes the current resource.
  put never accepts a partial or truncated fetch result as a complete replacement.
  After every successful put, the host write service emits a resource-changed notification;
  a connected desktop UI refreshes affected library rows, open item editors, and CSL style
  views without requiring a process restart.
  failed validation never modifies the library.
```

`--from <PATH>` 与 `--stdin` 是 CLI 的本地输入适配器，二者互斥：CLI 在本地读取完整文本后，向本地宿主 MCP HTTP 端点发送相同的内联 `content`；路径不进入领域命令、响应或 MCP。`patchouli.put` 只接受网络请求中内联的 `uri` 与完整 `content` 字符串；MCP schema 不存在 `from`、`stdin`、`path`、multipart、streaming 或 file-reference 参数。

为限制网络写入请求，MCP host 必须配置 `max_mcp_request_bytes`，默认 1 MiB、硬上限 4 MiB；超过当前上限的 HTTP 请求在调用工具前以 HTTP `413 Payload Too Large` 拒绝，不产生部分写入。宿主写服务还必须以同一当前上限检查完整 UTF-8 replacement content，因此 CLI 从路径/stdin 读取的内容与 MCP 内联 content 有相同的可接受大小、校验、原子提交和响应语义。

**`general` 题录的 agent 可访问性**：为消除 agent 与人类之间的信息不对称，`general` 类型题录对 CLI/MCP 可读可写——`fetch` 以 `@misc` BibLaTeX 投影返回，`put` 以 `@misc` 回写时保留原 `general` 类型。若 agent 明确将完整投影改为受支持的非 `misc` 类型（例如 `@book` 或 `@article`），这是显式的类型修正，应按该 BibLaTeX 类型映射并持久化为新的 Patchouli 类型；未知或仍映射为 `general` 的类型必须失败。`@misc` 投影路径必须是 MCP 专用路径，不得改变 UI 的 `general` 导出/导入限制。`general` 可以在具备最低可渲染字段时通过显式 `@misc` fallback 参与 `cite`，响应必须带有 `general_as_misc` warning；字段不足或 renderer 拒绝时返回 `NOT_CITABLE`，不得静默把它当作 `book`、`article` 或其他类型。`put` 不得绕过该限制把它当作可渲染类型。

**可写 MCP 产品意图**（ADR `0023`）：v1/v2 首发 MCP 只能“访问”库；v3+ 的可写 MCP 才能让 agent **与库交互并辅助人类**——例如样式库缺少合格 CSL 时，由能读全文件的 agent 起草并 `put` 样式；题录字段错误时，agent 修正完整 `.bib` 投影后 `put` 回写。`put` 仍是窄范围、原子性的整资源替换，不得扩展为 OCR 触发、bbox 编辑、索引重建、创建/删除/重命名资源。它不以 resource revision 作为写入前置条件；并发的合法整资源替换按成功提交顺序生效。设置中写入工具可关闭；实现须符合 ADR `0023`，并保留文本-only、无路径/密钥/图像等安全边界（ADR `0010` 中仍有效的条款）。只读 document/page 仍不可 `put`，但不因此失去 `cite` 能力。

## 6 `cite`

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
Successful references should be returned with per-reference results while invalid,
unresolvable, or non-citable references are reported in warnings/errors. The whole
request fails only when the request is invalid, the citation style cannot be loaded,
or no reference can be rendered.

## 7 MCP 映射与共享契约

```text
CLI                                             MCP
patchouli-cli find                              patchouli.find
patchouli-cli fetch                             patchouli.fetch
patchouli-cli put <URI> --from <PATH>|--stdin   patchouli.put { uri, content }
patchouli-cli cite                              patchouli.cite
```

CLI 是本地 MCP HTTP 端点的客户端，四个 CLI 动词分别映射为四个 MCP tool request；因此服务端的默认值、URI 格式、校验规则、资源投影、revision、响应语义与错误码只有一份实现。CLI 契约测试验证参数解析与 MCP request 的映射，而不是维护两套领域实现的一致性。`find --long` 与 `patchouli.find(detail=long)` 是同一详细投影；MCP `format=json` 与 CLI `--json` 是同一 JSON 回退格式。CLI 的 `put` 输入适配器如上表：它读取本地内容后发送内联 content，MCP 从不接受或暴露本地路径。宿主统一执行 bind、CORS、token、工具开关与所有写入策略；本地 CLI 也不得绕过这些服务端规则。

共享响应外壳：CLI 的 JSON、MCP `format=json` 与默认 TOON 使用**同一**逻辑 schema；TOON 仅是该对象的确定性编码，不能省略、提升或重命名字段。干净成功的 JSON 例如：

```json
{
  "meta": { "library_revision": "lib:42" },
  "continuation": null,
  "entries": []
}
```

### 7.1 统一响应 schema（规范性）

以下 schema 是 CLI `--json`、MCP `format=json` 与默认 TOON 的唯一逻辑模型。每个工具响应严格由 `meta`、`continuation`、可选 `message` 与 `entries` 组成；object 不得出现未声明字段。`String` 是 Unicode string，`Uri` 是 canonical `patchouli://` string，`NonNegativeInt` 是非负 JSON integer，`PositiveInt` 是大于零的 JSON integer；它们在 TOON 中分别按 string 与 integer 规则编码。`message.error` 与 `message.warnings` 都是紧凑的终端诊断文本：错误固定为 `NAME [code N]: detail`，其中 `NAME` 与非零 `N` 必须来自同一错误码表；仅内部错误可在方括号追加 `; ref <correlation-id>`。warning 固定为 `NAME: detail`。诊断 detail 必须由宿主白名单模板生成并脱敏，不得直接输出 exception、stack、路径、file URL、secret 或原始 content。

```text
Response<TMeta, TEntry> = {
  meta: TMeta & { library_revision: LibraryRevision },
  continuation: String | null,
  message?: {
    error: String | null,
    warnings: String[],
  },
  entries: TEntry[]
}
```

`LibraryRevision` 是 `"lib:<positive decimal integer>"`。`message` 遵循 Unix 哲学：当且仅当没有 warning 且没有 request-level error 时必须省略；没有 `message` 即表示干净成功。存在 warning 时 `message.error` 为 null，仍可为成功；存在 request-level error 时 `message.error` 非 null。逐项操作的失败保留在相应 entry 的同格式 `error` 字符串；只有所有逐项操作均失败、请求本身无效，或必须以非零状态报告的 partial/truncated 情况，才同时在 `message.error` 给出对应错误。warning 与 error 都是供人和 agent 直接读取的终端式诊断，不承载成功文案或本地实现细节。`message.error` 非 null 时，MCP tool response 必须标记 `isError=true` 并保留可用的 partial `entries`；CLI 将结构化响应写入 stdout，并从错误文本中的 `[code N]` 使用相应非零退出码报告错误。

**旧 envelope 清理**：实现必须删除 `McpEnvelope<T>.Revision` 及其序列化顶层 `revision` 字段，并将 CLI、MCP transport、契约测试和所有调用方迁移为读取 `meta.library_revision`。依照 ADR `0027`/`0028`，不得以旧字段替代或重新引入 `fetch --revision`、`resource_revision` 或其他资源级历史版本选择字段。

只有 `find` 可以在顶层 `continuation` 中返回 cursor；`fetch` 的继续读取信息只属于其单个 entry，`put`/`cite` 的顶层 continuation 必须为 null。

| Tool | `meta` 与 `entries` schema | 成功与逐项失败规则 |
|---|---|---|
| `find` | `meta` 为 `{ library_revision: LibraryRevision, domain_total: NonNegativeInt, filtered_total: NonNegativeInt, shown_total: NonNegativeInt }`；`entries` 为 `FindEntry[]` | 默认 `FindEntry` **严格只有** `{ "uri": Uri, "title": String, "type": "file" \| "directory" }`。`detail=long` 使用按资源种类判别的精确投影：`ItemLongEntry = { uri, title, type: "file", item_status, primary_document_ocr_index_status, citable }`；`TextLongEntry = { uri, title, type: "file"\|"directory", item_uri, item_status, document_status, source_status, ocr_index_status, citable }`；`StyleLongEntry = { uri, title, type: "file", style_enabled }`。除 `item_status` 可为 `"unset"` 外，三个 `*_status` 均为对应实体的原始持久化值；没有关联 FileAsset 的 text 资源使用 `source_status: "unavailable"`。字段只在其适用 variant 中出现，严禁用 null 填充其他 variant，也不得重复 URI 已表达的 document/page/evidence locator。`meta.shown_total` 必须等于 entries 长度。 |
| `fetch` | `meta` 为 `{ library_revision: LibraryRevision }`；`entries` 为 `FetchResult[]` | 每个输入 URI 产生一个同序 `FetchResult`，不得因其他 URI 失败而丢失。见下方 discriminated variant。 |
| `put` | `meta` 为 `{ library_revision: LibraryRevision }`；成功时 `entries` 为单元素 `PutResult[]`，失败、取消或超限写入时为 `[]` | `PutResult = { uri: Uri, resource_type: "item_bib" \| "csl_style", committed: true, content_bytes: NonNegativeInt }`。失败详情由 `message.error` 表达。`content_bytes` 是已提交 UTF-8 content 的实际字节数。 |
| `cite` | `meta` 为 `{ library_revision: LibraryRevision, effective_style_uri: Uri, effective_locale: String, render_format: "text" \| "html", bibliography: String\|null }`；`entries` 为 `CitationResult[]` | 每个输入 REF 产生一个同序 result；可渲染 result 与不可解析/不可引用 result 可以共存。`bibliography` 仅在请求时为 String，否则为 null；其去重不影响 entries 长度与顺序。 |

上述 long entry 由固定宽表改为资源专属 variant 是协议 schema 变更：实现该条款时必须提升 MCP protocol revision，并在同一变更中更新 MCP tool schema、CLI help、DTO、TOON/JSON fixture 与所有调用方；不得保留输出旧 null 填充字段的兼容分支。

```text
FetchResult = CompleteFetch | TruncatedFetch | FailedFetch

CompleteFetch = {
  uri: Uri, resource_type: ResourceType,
  item_uri: Uri | null, content: String,
  complete: true, truncated: false,
  returned_bytes: NonNegativeInt, limit_bytes: PositiveInt,
  continuation: null, next_range: null, error: null
}

TruncatedFetch = {
  uri: Uri, resource_type: ResourceType,
  item_uri: Uri | null, content: String,
  complete: false, truncated: true,
  returned_bytes: NonNegativeInt, limit_bytes: PositiveInt,
  continuation: String | null, next_range: String | null,
  error: "RESPONSE_TRUNCATED [code 7]: detail"
}

FailedFetch = {
  uri: Uri, resource_type: null, item_uri: null,
  content: null, complete: false, truncated: false,
  returned_bytes: 0, limit_bytes: PositiveInt,
  continuation: null, next_range: null, error: "NAME [code N]: detail"
}

CitationResult = {
  ref: String, item_uri: Uri | null, citation: String | null, error: String | null
}
```

`ResourceType` 是 `item_bib`、`text_document`、`text_page`、`evidence` 或 `csl_style`。`CompleteFetch.returned_bytes` 必须等于 content 的 UTF-8 字节数且不超过 `limit_bytes`；`TruncatedFetch` 有相同字节约束，且 `continuation` 与 `next_range` 至少一个为非 null。任一 `TruncatedFetch` 使顶层 `message.error` 为 `RESPONSE_TRUNCATED [code 7]: …`，但仍保留完整 `entries`；`FailedFetch.error` 不得包含 `[code 7]`。`CitationResult` 成功时 `item_uri`、`citation` 非 null 且 `error` 为 null，失败时前两者为 null 且 `error` 为终端错误行。若所有 citation result 均失败，`message.error` 为相应错误；否则 `message.error` 为 null（若也没有 warning，省略 `message`）。错误码、schema 或类型新增/变更均为协议 revision 变更。历史文本只由 URI 的 `rev` 选择；独立 `fetch --revision` 不属于协议（ADR `0028`）。

`meta.library_revision` 是当前 Library 的宿主权威 revision，格式固定为 `lib:<十进制正整数>`；它持久化于该 Library，且每次成功、会改变协议可见资源或关系的 Library 写入后严格单调递增，即使桌面/headless 宿主交接也不得回退或复用。它不是默认 `find` entry 的资源 revision，也不是 `put` 的写前置条件。客户端保存的 fetch 内容只是该 revision 时的本地快照；v3 不推送或撤回其已交付内容。cursor 继续按实时语义读取，且其创建 revision 与当前 revision 不同时继续在 `message.warnings` 返回 `RESULT_SET_MAY_HAVE_CHANGED`。MCP 会话中宿主发现上一次已观察 revision 已落后于当前 revision 时，也必须在 `message.warnings` 追加 `LIBRARY_CHANGED_SINCE_LAST_RESPONSE`；无会话或断线客户端可通过 `meta.library_revision` 自行检测陈旧性并按需重新 fetch。

`patchouli://` 始终解析到处理请求的宿主当前 Library。当前 UI 尚不支持切换 Library，但宿主的生命周期内仍必须固定一个 `library_id`。cursor、versioned evidence URI（按当前 Library 校验 revision/box 归属）或未来显式 Library 上下文若与该 `library_id` 不匹配，解析器必须丢弃已经准备的内容，以 `NOT_FOUND` 返回，不得混入旧 Library 的 entries、partial entries 或 citation 结果。

`find` 的 warning 使用稳定名称：`RESULT_SET_MAY_HAVE_CHANGED`（实时分页可能漂移）、`WHITESPACE_QUERY_TREATED_AS_BROWSE`、`CURSOR_CONTEXT_RESTORED`、`ROOT_DISCOVERY_PAGINATED`、`FILE_URI_SINGLETON_SCOPE`、`WHERE_VALUE_CONTAINS_EQUALS` 与 `DUPLICATE_WHERE_KEY_LAST_WINS`。所有工具还可在 `message.warnings` 返回 `LIBRARY_CHANGED_SINCE_LAST_RESPONSE`。每一项按 `NAME: detail` 输出，使 agent 可立即知道宿主如何解释或调整了请求；warning 是成功响应的一部分，不改变 exit/error code；没有 warning/error 时不返回 `message`。

Exit / error codes：

| Code | Name | 含义 |
|---|---|---|
| 0 | OK | 成功（含空 find） |
| 1 | INTERNAL | 未预期的宿主、数据库或内部 helper 异常；只返回稳定错误码及可选 correlation id，绝不泄露异常详情、堆栈、本地路径或 secret |
| 2 | INVALID_ARGUMENT | 非法或冲突参数 |
| 3 | NOT_FOUND | URI 不存在 |
| 4 | PERMISSION_DENIED | 资源权限或策略禁止当前操作 |
| 5 | RESERVED | v3 `put` 不使用 base revision，也不返回 revision conflict |
| 6 | INVALID_CONTENT | BibLaTeX 或 CSL 校验失败 |
| 7 | RESPONSE_TRUNCATED | 响应超过安全大小上限，已返回显式 partial 内容 |
| 8 | UNAVAILABLE | Patchouli 服务不可用 |
| 9 | NOT_CITABLE | 资源存在，但无法解析为可渲染的引用目标 |
| 10 | DEADLINE_EXCEEDED | 宿主在请求 deadline 前未能完成；不返回 timeout 导致的 partial data |
| 11 | CANCELLED | 调用方取消且宿主在原子提交前停止了操作；不产生写入 |
| 12 | ITEM_IN_TRASH | 题录在回收站，拒绝 fetch/put |
| 13 | ITEM_MERGED | 题录已合并，拒绝 fetch/put；诊断说明重定向目标 |

推荐探索顺序：裸 `find` 发现 `/items`、`/texts`、`/csl-styles` → 进入一个返回的 URI，以小 `limit`、query、`--where` 和 `continuation` 缩小范围 → 对 Item 以 `primary_document_ocr_index_status=indexed` 筛选可全文检索的主文档，对 text 以 `ocr_index_status=indexed` 筛选可全文检索文本 → 仅 `fetch` 已返回的 URI → 按需以 `--long`/`detail=long` 检查状态、关系与引用能力 → 本地处理 → 仅对最终合法 `.bib`/`.csl` 执行 `put`。全文搜索返回的 `?rev=&box=` page URI 可直接 `fetch` evidence 或作为 `cite.refs` 输入；其他资源引用前使用 `where citable=true`。

## 8 Agent 可用性与关系解析

- `citable=true` 必须与 `cite.refs` 实际接受的 URI 类型一致。Item、Document、Page 和 Evidence 可以是 citable；CSL style 仅可作为 `cite.style` 使用，绝不是 citable，因此 StyleLongEntry 不包含 `citable`。`writable` 与 `citable` 是两个独立维度。可引用资格由单一确定性规则计算；协议不返回 `citation_target`，宿主在 `cite` 内部解析到所属 Item。
- Document、Page 和 Evidence 的详细 `find`/`fetch` 结果应返回 `item_uri` 或等价的 `parent_uri`。这是关系元数据，不构成自动 link following。
- `cite` 对 text document/page 的解析只使用持久化的 `document_instances.item_id` 关系，不通过标题、文件名或全文搜索猜测 Item。Page URI 必须先验证 page 属于 URI 中声明的 text document。
- 如果多个 REF 解析到同一个 Item，bibliography 默认去重，但响应应保留每个 REF 的解析结果。
- `find` 带 query 时应搜索所声明的 `--in` scope。Item 至少支持题名/作者/identifier 等 metadata search，CSL style 至少支持 id/display name search；`item_status`、`document_status`、`source_status` 与 `style_enabled` 分别按其 Item、DocumentInstance、FileAsset 或 style 配置的权威记录读取，不能以派生索引/布局状态替代原始实体 status。`primary_document_ocr_index_status` 与 `ocr_index_status` 是共享 FSM 的独立能力：前者按 Item 的 primary DocumentInstance 关系解析，后者按 text document/page/evidence URI 所属 DocumentInstance 解析。Evidence 不是可浏览根资源；它只在 text 搜索中通过已含 `?rev=&box=` 的 page URI 表示，long 投影不得再次输出 `rev`/`box`、页码或 document ID。尚未支持的 scope/filter 必须返回明确的 `INVALID_ARGUMENT`，不得以成功的空数组代替“不支持”。
- `find` 在 `patchouli://texts/` 中带 query 时按 SearchUnit 命中逐项返回，不聚合为缺少 evidence 身份的 document 条目；每个默认 entry 的 `uri` 都必须内嵌该命中的 canonical `?rev=&box=`，因此 agent 仅凭 `uri`、`title`、`type` 就能继续 `fetch` 或 `cite`。
- `fetch <URI>...` 和 `cite <REF>...` 均采用逐项结果语义。单个资源的 `NOT_FOUND`、`NOT_CITABLE` 或 `RESPONSE_TRUNCATED` 不得丢弃同一请求中其他成功结果。
- `limit_bytes` 的默认值可以由服务配置，但必须有服务端硬上限；调用者请求超过硬上限时可以钳制并返回 warning，不得因此取消已可安全返回的 partial 内容。
- `general` 的 `@misc` cite fallback 与无默认 style 时的 deterministic style fallback 已同步记录到 ADR `0023`/`0024`；后续实现变更必须同时维护本契约、ADR 与运行时契约，避免三者分叉。

## 9 超时与取消

宿主必须实施较宽松的服务端 deadline：`find`、`fetch`、`cite` 默认最多 60 秒，`put`（含完整内容校验）默认最多 120 秒；部署可配置更严格的上限，但不得让客户端无限等待。deadline 到期返回 `DEADLINE_EXCEEDED`，不把因超时而中断的资源伪装为 `RESPONSE_TRUNCATED` partial 成功。

MCP cancellation、HTTP 断连和 CLI 中断必须传播到宿主的取消令牌。`find`、`fetch`、`cite` 取消后停止工作；`put` 在进入原子提交点之前被取消时返回 `CANCELLED` 且 Library 不变。提交点之后宿主必须完成该原子提交或回滚，绝不留下部分资源；已断连的调用方不得推断结果，必须重新 `fetch` 确认当前内容与 `meta.library_revision`。取消不停止后台 headless 宿主。

## 回归义务（保留原验收编号）


| 编号 | 标准 |
|---|---|
| V3-AC1 | 存在可运行的 A/B 评测基准与任务集，结果可重复；外部 UUID-chain 证据已完成 |
| V3-AC2 | ADR `0024` 形成 B 择优结论，含迁移策略 |
| V3-AC3 | B 迁移完成：生产默认 MCP 表面单一；shell/sidecar 已从 main 彻底移除（不再启动、不再打包、不保留实现），仅历史分支 `feature/mcp-ab-benchmark` 存有评测证据 |
| V3-AC4 | 若 B 胜出：CLI 是本地 MCP HTTP 客户端，四个 CLI 命令到四个 MCP tool request 的参数映射、共享 JSON 与错误码有契约测试；未预期宿主/数据库/helper 异常统一映射为 `INTERNAL`，不泄露内部详情；不存在第二套 CLI 领域实现 |
| V3-AC5 | 安全锚点测试仍通过；`put` 若启用则仅限规定 URI、完整内容校验与原子提交，不接受 partial/truncated 内容 |
| V3-AC6 | `general` 可在满足字段要求时通过显式 `@misc` fallback cite，否则返回 `NOT_CITABLE`；只读资源 put 返回 `PERMISSION_DENIED`，但 document/page/evidence cite 可解析到所属 Item |
| V3-AC7 | 超过 `limit_bytes` 的 fetch 返回安全边界内的 partial 内容、`complete=false`/`truncated=true`、continuation 或 next range，以及 `RESPONSE_TRUNCATED`；不得静默呈现为完整内容 |
| V3-AC8 | Document、Page、Evidence 的资源响应暴露所属 Item 关系；`citable` 与 cite 实际接受的 URI 类型一致；document/page cite 验证关系后成功解析 |
| V3-AC9 | 多 URI fetch 与多 REF cite 采用逐项结果语义；单项失败不丢弃同一请求中的成功结果，并对实际使用的 CSL style 返回 effective style |
| V3-AC10 | 裸 `find` 仅返回 `/items`、`/texts`、`/csl-styles` 三个 VFS 根 directory；旧 `AGENTS.md`、`library.yml`、evidence 根和 shell 入口均不可发现或访问 |
| V3-AC11 | 经 UI、CLI 或 MCP 成功写入的宿主写服务均发出资源变更通知；连接到该宿主的桌面书库列表、打开的题录编辑器和 CSL 样式视图无需重启即可显示最新数据 |
| V3-AC12 | 默认 `find` 的 TOON/JSON 条目严格只有 `uri`、`title`、`type`；所有工具的 TOON/JSON 响应均严格使用 `meta`、`continuation`、可选 `message`、`entries` 外壳，干净成功不返回 `message`。`meta` 三项计数反映各页读取时的当前 Library 状态、`shown_total` 与 entries 行数一致、continuation 可继续读取。实时 cursor 的跨页 entries 或计数可能漂移，必须在 `message.warnings` 有 `RESULT_SET_MAY_HAVE_CHANGED`，不承诺 `filtered_total` 跨页稳定 |
| V3-AC13 | `--long` / `detail=long` 才返回状态、能力与必要关系元数据；默认和详细的 CLI/MCP/JSON 输出、schema、help 与示例均不存在 `citation_target`、`preview` 或裸 `status`。Long 投影按 Item、Text、Style 资源种类精确省略不适用字段，绝不重复 URI 已表达的 DocumentInstance、页码或 `rev`/`box`；Style 不包含 `citable` |
| V3-AC14 | `find` 对声明支持的 scope/query/`--literal`/filter 执行搜索或过滤，严格遵循 scope × flag 合法矩阵；不支持的组合返回 `INVALID_ARGUMENT`，不以成功空数组代替 |
| V3-AC15 | `patchouli-cli` 先连接同 Library 的本地 MCP HTTP 宿主；桌面未运行时自动启动后台 headless 宿主后执行四个资源命令。UI、CLI 与 agent MCP 都经同一宿主服务；CLI 不直连 SQLite。每个 Library 同时只有一个宿主，桌面启动时接管并终止该 Library 的 headless 宿主；headless 的 `0.0.0.0` 监听同样要求 token |
| V3-AC16 | MCP `format=json` 与 CLI `--json` 为批量机器处理返回等价 JSON；无需解析 TOON，且三种编码均使用相同的 `meta`、`continuation`、可选 `message`、`entries` schema。格式切换不改变默认/详细投影、字段、分页、warning 或 error 语义 |
| V3-AC17 | `patchouli://texts/{document-instance-id}/page-{page-index}.md` 和 `pages:` range 均以一基、稳定的物理 PDF 页码寻址；带 `?rev=&box=` 的 fetch/cite 必须校验 `tree_revision_id`/`box_id` 与所声明 document/page 的归属，不归属时返回 `NOT_FOUND` |
| V3-AC18 | cursor 不持有服务端快照、结果集句柄、TTL 或 agent 命名空间，并绑定原 scope/query/filter/order；消费或发出 continuation 的响应在 `message.warnings` 包含 `RESULT_SET_MAY_HAVE_CHANGED` |
| V3-AC19 | `find QUERY --in patchouli://texts/` 的每个全文命中在默认 `uri` 中内嵌 canonical `?rev=&box=` page URI，不依赖 `--long` 或独立 evidence 字段；该 URI 可直接 fetch evidence，并可作为 `cite.refs` 输入 |
| V3-AC20 | CLI/MCP 的 TOON 输出仅使用 `Corvus.Toon.SystemTextJson` 产生，符合 TOON v3.0；契约 fixture 验证 UTF-8/LF、literal TAB、`KeyFolding=Off`、tabular `[N]` 计数、`text/toon`、TOON v3 词法引用/escape、number/boolean/null 的严格类型与 JSON↔TOON round-trip。不得存在自定义 TOON encoder/parser 或字符串后处理 |
| V3-AC21 | `--regex` 不出现在 CLI help、MCP schema 或协议示例；传入时返回 `INVALID_ARGUMENT`，服务端不执行正则搜索，agent 可在 find/fetch 的返回内容上自行匹配 |
| V3-AC22 | MCP `patchouli.put` schema 仅接受内联 `uri` 与 `content`，不含 `from`、`stdin`、`path`、streaming、multipart 或 file reference；CLI `--from`/`--stdin` 读取后与 MCP content 进入同一写服务，拥有相同的大小检查、校验、原子提交和响应。MCP 超过 `max_mcp_request_bytes`（默认 1 MiB、硬上限 4 MiB）的请求在工具调用前返回 HTTP 413，且不写入 |
| V3-AC23 | find 的边界输入按契约归一化并带稳定 warning：whitespace QUERY 等同 browse；root `--limit`/`--cursor` 可分页；file URI 是单资源 scope；cursor 冲突时恢复其绑定上下文；where 在第一个 `=` 分割且重复 key 最后一项覆盖。无效 cursor 或矩阵外组合仍返回 `INVALID_ARGUMENT` |
| V3-AC24 | `meta.library_revision` 是持久化、严格单调的 `lib:<十进制正整数>` Library revision；每次成功的协议可见 Library 写入及桌面/headless 交接均不重置它。已 fetch 内容仅为客户端快照；cursor 或 MCP 会话观察到 Library 变化时继续执行并在 `message.warnings` 给出相应 warning，不提供服务端推送式内容撤回 |
| V3-AC25 | `patchouli://` 只解析到宿主固定的当前 `library_id`；含有不匹配 Library 绑定的 cursor、versioned evidence URI 或显式上下文必须丢弃已准备 entries 并以 `NOT_FOUND` 失败，不得返回跨库内容。当前 UI 不能切库不构成省略该校验的理由 |
| V3-AC26 | 宿主对 `find`/`fetch`/`cite` 默认执行 60 秒、`put` 默认执行 120 秒的 deadline；超时为 `DEADLINE_EXCEEDED`，取消为 `CANCELLED`。取消或断连能停止校验/查询，且 `put` 要么在提交前不写、要么完整原子完成，绝无部分写入 |
| V3-AC27 | 四个工具均有封闭、逐字段类型化的统一响应 schema fixture；默认与 long find、complete/truncated/failed fetch、put 成功、cite 部分成功/全部失败均验证 `meta`、`continuation`、可选 `message`、`entries` 的 required/null/省略规则、无额外字段、同序逐项结果、UTF-8 byte 计数及 message/error 对应关系；help 和 MCP 初次握手 fixture 还必须验证“无 `message` 即干净成功”的 Unix 语义。迁移 fixture 还必须验证输出中不存在顶层 `revision` 或 `resource_revision`，且 CLI help/MCP schema 不含 `fetch --revision`；Library revision 只在 `meta.library_revision` |
| V3-AC28 | Long `find` fixture 分别验证 Item、Text 与 Style 的精确 variant：仅 `item_status`、`document_status`、`source_status` 是公共 status，且分别等于 Item、DocumentInstance、FileAsset 的原始持久化值；OCR 索引为共享 English FSM 能力，UI 使用同一 FSM 的中文标签与说明。`primary_document_ocr_index_status` 与 `ocr_index_status` 由数据库侧本页批量投影过滤，且不存在逐项 metadata/status 查询 |
