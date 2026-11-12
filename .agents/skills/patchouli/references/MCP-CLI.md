# MCP 与 CLI

## 宿主与前端

- 一个书库同一时刻只有一个宿主。桌面应用运行时由它服务；否则 `patchouli-cli` 自启同一程序的 headless 宿主并在后台保持运行。桌面应用打开同一书库时会接管 headless 宿主。
- MCP 工具与 CLI 命令一一对应，共用同一份服务端实现：默认值、URI 规则、校验、版本、响应和错误码完全相同。CLI 只负责把位置参数和本地输入文件转换成 MCP 请求。
- 优先使用 MCP；只有当前环境没有 MCP 时才改用 CLI。CLI 失败并报告 `UNAVAILABLE` 时，请用户确认书库能打开、宿主正在运行，并检查 MCP 的端口、防火墙与 token 设置。

| CLI | MCP | 用途 |
|---|---|---|
| `find [QUERY] --in <URI>` | `patchouli.find` | 浏览目录（无查询）或在范围内搜索（有查询） |
| `fetch <URI>...` | `patchouli.fetch` | 读取已知资源，逐 URI 独立返回结果；作用于 plugin `views/{view-id}` 时返回视图描述符而非图像 |
| `add <PATH> [--kind pdf\|csl]` | `patchouli.add { path, kind }` | 导入宿主机本地文件 |
| `fetch patchouli://images/<document-id>/page-<N>.png --output <PNG>` | `patchouli.fetch` | 读取源页面图像（原生 image 内容块，不进文本字段）；`?rev=` 读历史修订、`?box=` 裁剪到该框、`?overlay=boxes` 叠加与页面 XML 顺序一致的带序号边界框、`?max_edge=` 限最长边；单次调用只能含一个 images/ URI，不可用 `--range`/`--limit-bytes`；`--output` 必填 |
| `put <URI> --from/--stdin` 或 `--boxes-from/--boxes-stdin` | `patchouli.put` | 替换一个可写资源；作用于 `patchouli://plugin/{id}/{declared-path}` 时，节点声明并授予 `put` 后写入交给插件进程实现，插件返回的具体结果放在 `result` 字段 |
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
patchouli://images/{document-id}/                      文档的图片目录（逐页 PNG）
patchouli://images/{document-id}/page-{N}.png[?rev={revision}&box={box}&overlay=boxes&max_edge={pixels}]   源页面图像（按需从原始 PDF 渲染，不落盘）
patchouli://translations/{document-id}/page-{N}.xml   页面译文
patchouli://csl-styles/{style-id}.csl
patchouli://runs/{ocr|translation|metadata}/control   领域端点
patchouli://runs/{ocr|translation|metadata}/{id}/status   易失任务状态
patchouli://plugin/{plugin-id}/plugin.yml        插件清单投影（含 views 数组）
patchouli://plugin/{plugin-id}/views/{view-id}   宿主合成的视图描述符
patchouli://plugin/{plugin-id}/{declared-path}   声明的插件资源
```

URI 可以写成完整 `patchouli://…`、相对根的路径或前导 `/`。宿主拒绝本机路径、其他 scheme、`..` 和反斜杠。响应中的链接总是完整的 canonical URI，直接复用即可。

## 响应外壳

所有工具的响应都由 `meta`、`continuation`、可选的 `message` 和 `entries` 组成。默认编码是 TOON；需要交给程序处理时，用 MCP `format=json` 或 CLI `--json` 取得等价 JSON。

- 每次响应都有 `meta.outcome`（`success` / `error`）和 `meta.summary`。先读取它们确认实际结果，不以缺少 `message` 判断成功。error 可以保留成功条目或安全截断的内容，但仍是失败；请按提示缩小范围或续读。warning 不改变成功语义，但要阅读：`[obsoleted]` 给出替代 URI；`RESULT_SET_MAY_HAVE_CHANGED` 表示分页期间书库可能已变化；`LIBRARY_CHANGED_SINCE_LAST_RESPONSE` 表示之前读到的内容可能已过期。
- `fetch` 和 `cite` 保留逐项结果，但任一 URI/REF 失败就使整次调用为 error；每个 entry 仍有自己的 `error`。请求的 bibliography 生成失败也为 error。
- `meta.library_revision` 单调递增，可用来判断本地缓存的内容是否过期。

## find 与 fetch

- 空查询表示浏览，非空查询表示搜索。根目录 `patchouli://` 只用于发现，不接受查询。
- 在 `texts/` 中全文检索时，每个命中的 SearchUnit 返回一条结果，其 `uri` 已是证据 URI。`items/` 按题名、作者、标识符搜索；`--literal` 关闭查询改写。协议不支持正则：先取回文本，再在本地匹配。
- `--where KEY=VALUE` 可以重复使用，与其他条件按 AND 组合，只接受当前范围支持的键（例如 `item_type`、`citable`、`tag`、`collection_id`、`ocr_index_status`）。要筛出可全文检索的文本，用 `ocr_index_status=indexed`。
- 默认结果只有 `uri`、`title`、`type`；`--long`（MCP `detail=long`）才附带状态与关系字段。
- cursor 是无状态的实时续读令牌，翻页之间结果可能变化；需要稳定结果时，在没有并发修改的时段内一次读完。
- `fetch --range pages:A-B|lines:A-B` 限定范围，`--limit-bytes` 设定大小上限。超限时返回显式的部分结果（`complete:false`，带 `next_range` 或 `continuation`）。页面 XML 只会按完整 box 切分，不会截断半个 box。部分结果不能当作完整内容提交。images/ URI 不可用 `--range`/`--limit-bytes`，单次 fetch 只能含一个 images/ URI；图像以原生 image 内容块返回，永不截断，文本包络只带图片元数据。取文本页时条目带可直接取用的 `image_uri`。

## 权限

权限由用户在桌面设置“MCP 服务与权限”中配置，形式是“VFS 域 × 动词”矩阵。另有 `put`、`send`、`add` 三个独立的工具开关，关闭后对应工具返回 `PERMISSION_DENIED`，其余工具不受影响。图像读取受 `images × fetch` 域权限约束，不是独立工具开关；旧配置迁移时，已显式开启 view 授权的转为 images.fetch，未开启的一律拒绝（迁移不扩大授权）。

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
- 发送前先 `find patchouli://runs/<domain>/` 发现端点，再 `fetch` 该端点，取得当前授权下的 `messages`（action、合法 target、输入与结果 Schema、副作用）与 `implementations`（该域可用的内部实现：OCR 引擎 id、翻译扩展 id、元数据来源 id 及其支持的标识符 scheme）。以这里返回的 Schema 与实现列表为准，不要猜测可传入的 id。实现列表中，内置包恒列出；第三方包在首次激活后出现。
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

## 管理集合与标签

先 fetch `patchouli://library.yml`，编辑 tags/collections 后 put 同一 URI。
新增集合写 `{name: 待读}`，不填 collection_id；保存后重新 fetch 取得生成的 ID。
新集合误填的 collection_id（格式无效或不属于当前书库）会被忽略，宿主重新生成 ID。
现有集合保留 ID，改 name 会保留成员；删除条目只解散集合，不删除题录。
新增标签写 `{tag: 待读}`，空标签也会持久保存。标签以名称为身份，改标签名会按
删除旧标签、创建新标签处理，并移除旧归属；不会自动迁移归属。

编辑题录 YAML 的 `extensions.patchouli.tags: [标签名]` 和
`extensions.patchouli.collections: [集合UUID]` 修改归属。引用的分类必须先存在。
目录与归属字段省略时保留，[] 明确清空；隐藏类别始终保留。library_id/display_name
不可修改，item_count 是只读计数。任何校验失败都整笔回滚。
