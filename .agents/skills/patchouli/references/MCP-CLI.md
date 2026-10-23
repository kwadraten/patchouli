# MCP 与 CLI

## 宿主与前端

- 一个书库同一时刻只有一个宿主。桌面应用运行时由它服务；否则 `patchouli-cli` 自启同一程序的 headless 宿主并在后台保持运行。桌面应用打开同一书库时会接管 headless 宿主。
- MCP 工具与 CLI 命令一一对应，共用同一份服务端实现：默认值、URI 规则、校验、版本、响应和错误码完全相同。CLI 只负责把位置参数和本地输入文件转换成 MCP 请求。
- 优先使用 MCP；只有当前环境没有 MCP 时才改用 CLI。CLI 失败并报告 `UNAVAILABLE` 时，请用户确认书库能打开、宿主正在运行，并检查 MCP 的端口、防火墙与 token 设置。

| CLI | MCP | 用途 |
|---|---|---|
| `find [QUERY] --in <URI>` | `patchouli.find` | 浏览目录（无查询）或在范围内搜索（有查询） |
| `fetch <URI>...` | `patchouli.fetch` | 读取已知资源，逐 URI 独立返回结果 |
| `add <PATH> [--kind pdf\|csl]` | `patchouli.add { path, kind }` | 导入宿主机本地文件 |
| `view <URI> --output <PNG>` | `patchouli.view { uri, context?, max_edge? }` | 读取页面或 box 的源图像 |
| `put <URI> --from/--stdin` 或 `--boxes-from/--boxes-stdin` | `patchouli.put` | 替换一个可写资源 |
| `cite <REF>...` | `patchouli.cite` | 渲染引用 |
| `send <ENDPOINT> <ACTION> <TARGET> [选项]` | `patchouli.send` | 向领域端点发送类型化消息 |

## 资源树

```text
patchouli://library.yml
patchouli://items/{item-id}.yml
patchouli://texts/{document-id}/                      文献大纲与页面链接
patchouli://texts/{document-id}/page-{N}.xml          当前页面 XML
patchouli://texts/{document-id}/page-{N}.xml?rev={revision}&box={box}   证据
patchouli://texts/{document-id}/map.yml               章节与印刷页码映射
patchouli://translations/{document-id}/page-{N}.xml   页面译文
patchouli://csl-styles/{style-id}.csl
patchouli://runs/{ocr|translation|metadata}/control   领域端点
patchouli://runs/{ocr|translation|metadata}/{id}/status   易失任务状态
```

URI 可以写成完整 `patchouli://…`、相对根的路径或前导 `/`。宿主拒绝本机路径、其他 scheme、`..` 和反斜杠。响应中的链接总是完整的 canonical URI，直接复用即可。

## 响应外壳

所有工具的响应都由 `meta`、`continuation`、可选的 `message` 和 `entries` 组成。默认编码是 TOON；需要交给程序处理时，用 MCP `format=json` 或 CLI `--json` 取得等价 JSON。

- 没有 `message` 表示干净成功。warning 不改变成功语义，但要阅读：`[obsoleted]` 给出替代 URI；`RESULT_SET_MAY_HAVE_CHANGED` 表示分页期间书库可能已变化；`LIBRARY_CHANGED_SINCE_LAST_RESPONSE` 表示之前读到的内容可能已过期。
- `fetch` 和 `cite` 逐项返回：一个 URI 失败不影响其他结果，每个 entry 有自己的 `error`。
- `meta.library_revision` 单调递增，可用来判断本地缓存的内容是否过期。

## find 与 fetch

- 空查询表示浏览，非空查询表示搜索。根目录 `patchouli://` 只用于发现，不接受查询。
- 在 `texts/` 中全文检索时，每个命中的 SearchUnit 返回一条结果，其 `uri` 已是证据 URI。`items/` 按题名、作者、标识符搜索；`--literal` 关闭查询改写。协议不支持正则：先取回文本，再在本地匹配。
- `--where KEY=VALUE` 可以重复使用，与其他条件按 AND 组合，只接受当前范围支持的键（例如 `item_type`、`citable`、`tag`、`collection_id`、`ocr_index_status`）。要筛出可全文检索的文本，用 `ocr_index_status=indexed`。
- 默认结果只有 `uri`、`title`、`type`；`--long`（MCP `detail=long`）才附带状态与关系字段。
- cursor 是无状态的实时续读令牌，翻页之间结果可能变化；需要稳定结果时，在没有并发修改的时段内一次读完。
- `fetch --range pages:A-B|lines:A-B` 限定范围，`--limit-bytes` 设定大小上限。超限时返回显式的部分结果（`complete:false`，带 `next_range` 或 `continuation`）。页面 XML 只会按完整 box 切分，不会截断半个 box。部分结果不能当作完整内容提交。

## 权限

权限由用户在桌面设置“MCP 服务与权限”中配置，形式是“VFS 域 × 动词”矩阵。另有 `put`、`send`、`add`、`view` 四个独立的工具开关，关闭后对应工具返回 `PERMISSION_DENIED`，其余工具不受影响。

- `send` 需要同时具备两项：`send` 工具开关，以及 `runs/ocr`、`runs/translation` 或 `runs/metadata` 子域的 send 授权。新子域默认关闭。
- 没有授权的端点在 `find` 中不可见，直接 `fetch` 也会被拒绝。
- 遇到 `PERMISSION_DENIED` 时，向用户说明缺少的开关或授权，然后停下。

## send：领域端点

```text
patchouli-cli --json send runs/ocr/control start texts/{document-id}/ --pages 1,3-5 --request-id ocr-001
patchouli.send { "endpoint": "runs/ocr/control", "action": "start",
                 "target": "texts/{document-id}/", "args": { "pages": "1,3-5" }, "request_id": "ocr-001" }
```

- 端点是类似 Unix socket 的虚拟接收地址，target 是被操作的资源，领域参数都放在 `args` 对象中。CLI 的平铺选项（如 `--pages`、`--engine`、`--target-language`、`--force`）按端点 Schema 转换为 `args`；复杂值用 `--args '<JSON>'`。未知字段、重复字段、错误类型以及平铺选项与 `--args` 冲突时都会被拒绝。
- 发送前先 `find patchouli://runs/<domain>/` 发现端点，再 `fetch` 该端点，取得当前授权下的 `messages`（action、合法 target、输入与结果 Schema、副作用）。以这里返回的 Schema 为准。
- 结果是 `completed + result`（同步完成）或 `started + result + run_uri`（后台受理）。`fetch run_uri` 查询进度。HTTP 断连不会取消任务，取消必须显式发送 `cancel`。
- 外层 `request_id` 用于幂等：同一 ID、同一消息返回原受理回执，并标记 `reused: true`；同一 ID、不同消息返回冲突。需要有意重跑时使用新 ID。
- 当前端点为 `runs/ocr/control`、`runs/translation/control`（见 [DOCUMENTS](DOCUMENTS.md)）和 `runs/metadata/control`（见 [CITATIONS](CITATIONS.md)）。

## 错误码

| Code | Name | 处理方式 |
|---|---|---|
| 2 | `INVALID_ARGUMENT` | 修正参数；不要猜测其他写法反复重试 |
| 3 | `NOT_FOUND` | URI 不存在，或宿主重启后易失状态已消失 |
| 4 | `PERMISSION_DENIED` | 请用户授权，然后停下 |
| 5 | `REVISION_CONFLICT` | 重新 `fetch`，在新版本上重做修改 |
| 6 | `INVALID_CONTENT` | 提交内容未通过校验，未写入任何内容 |
| 7 | `RESPONSE_TRUNCATED` | 按 `next_range` 或 `continuation` 继续读取 |
| 8 | `UNAVAILABLE` | 宿主或依赖服务不可用 |
| 9 | `NOT_CITABLE` | 资源存在，但不能作为引用目标 |
| 10 / 11 | `DEADLINE_EXCEEDED` / `CANCELLED` | 写入在提交前停止，未写入任何内容；重新 `fetch` 确认现状 |
| 12 / 13 | `ITEM_IN_TRASH` / `ITEM_MERGED` | 题录在回收站或已合并；后者的诊断会给出合并目标 |
| 1 | `INTERNAL` | 宿主内部错误；记录可选的 correlation id，告知用户 |

单个 MCP 请求默认上限为 1 MiB（硬上限 4 MiB），超限时在调用工具前就被拒绝。
