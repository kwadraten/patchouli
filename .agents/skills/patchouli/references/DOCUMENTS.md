# 页面、OCR 与翻译

## 读取页面

- `fetch patchouli://texts/{document-id}/` 返回文献大纲、所属题录和页面链接。
- `fetch …/page-{N}.xml` 返回页面 XML（profile `patchouli-page-2`）：页面身份、`source_revision`、`translation_version`，以及每个 box 的稳定 ID、类型和 payload。被隐藏的 box（例如页脚注）也会出现在结果中。用 `--range boxes:A-B` 或 `?box={box-id}` 只取部分 box；部分视图不能作为整页提交。
- 公式 box 带有不可变的 `<image kind="page-region" …/>` 区域描述，注释作为兄弟节点出现；修改 payload 时这些节点必须原样保留。
- 复杂表格的 payload 是 `format="html-table-v1"` 的受限 HTML，它就是唯一的权威内容；不要把它改写成 Markdown 表格。
- 旧的 `.md` 页面地址仍可读取，但会带 `[obsoleted]` 警告；请改用响应中给出的 `.xml` URI。

## 对照原图校勘

推荐流程：`fetch` 页面或证据 → `view` 同一修订的源图像 → 对照修正 → 带原 `source_revision` 执行 `put`。

- `view` 接受页面 URI（返回整页图像）或 box/证据 URI（返回带小边距的 bbox 裁剪）。译文页会返回对应源页面的图像。每次调用一个目标、一张 PNG；`max_edge` 默认 2048。
- 图像直接从原始 PDF 渲染，不会触发 OCR。未指定修订时，图像锁定在本次调用实际使用的修订；源文件缺失或已变化、目标不存在、坐标过期时都会显式失败，不会悄悄换成其他修订。
- MCP 以原生 image content 返回图像；CLI 用 `--output` 写出 PNG，默认不覆盖已有文件。

## 按 box 修改正文或译文

```json
{ "uri": "patchouli://texts/{document-id}/page-3.xml",
  "source_revision": "<fetch 得到的值>",
  "boxes": [ { "box_id": "<box-id>", "content": "<完整的 <box> 元素，只改 payload 文本>" } ] }
```

- `boxes` 含 1–100 项，每项是 fetch 得到的完整 `<box>` 元素，只能修改 payload 文本（译文还可修改 translated 标记）。ID、类型、结构属性和几何信息不能改。一个批次只作用于一页，并原子提交。
- 正文写入需要 `source_revision`；译文写入还需要 `translation_version`。正文 XML 不能整页替换，只能按 box 修改；译文 XML 可以整页替换，但必须保持与当前 box 身份和结构一致。
- 正文修改会创建新的已提交页面修订和一次文献级提交。若用户正在桌面端编辑同一页（存在未保存草稿），写入会被拒绝。
- 收到 `REVISION_CONFLICT` 时重新 `fetch`，在新版本上重做修改再提交。
- 没有译文的页面会返回 `translation_version` 为 0、以原文填充的可编辑视图。
- CLI 用 `--boxes-from <json>` 提交 `{source_revision, translation_version?, boxes:[…]}`。

## 导入文件

- `add` 只接受宿主机上的本地路径，不接受 URL：先自行下载文件，再把本地路径交给 `add`。支持 PDF 与 CSL；`kind` 可以省略，按内容自动识别。
- PDF 默认留在原位置，登记其 BLAKE3 指纹；是否移动到第一个文件搜索根由用户的“文件处理行为”设置决定，调用方不能在请求中覆盖。
- 成功后返回 `items/{item-id}.yml` 与 `texts/{document-id}/`，并说明新建还是复用、页数和导入问题。**导入不会启动 OCR**。

## OCR：`runs/ocr/control`

- `start`：target 为 `texts/{document-id}/`。`args.pages` 是一基物理页范围，如 `"1,3-5"`，省略表示全文。`args.engine` 省略或设为 `"default"` 时使用宿主当前配置的默认引擎；显式指定的引擎不可用时直接报错，不会换用其他引擎。回执给出实际使用的 `engine_id`、`preset_id` 与 `preset_version_id`。
- `cancel` / `retry`：target 为 `runs/ocr/{task-id}/` 或其 `/status` 地址，`args` 必须为 `{}`。`retry` 只接受状态为 failed、blocked 或 cancelled、且会提交正文的任务，并返回新任务。
- 一次 OCR 分三个阶段：识别、提交、建立索引；状态字段分别报告它们：
  - `recognition_state`：识别状态。
  - `committed` / `committed_page_count`：是否已提交正文、已提交多少页。即使任务最终失败，`committed` 也可能为 true。
  - `index_state`：`indexed`、`stale`、`partial`、`unavailable`、`not_indexed`；提交的修订已被更新的正文取代时为 `superseded`。
- 识别结果若与手工草稿或更新的正文冲突，提交会以 `REVISION_CONFLICT` 失败，并保留手工修改。
- 环境未就绪返回 `UNAVAILABLE`，缺引擎、源文件或凭据返回 `NOT_FOUND`；诊断中不含路径和密钥。

## 翻译

### 直接翻译运行：`runs/translation/control`

- `start`：target 为 `texts/{document-id}/`。`args` 可含 `pages`、`target_language`（只对本次生效）和 `force`。已有同语言当前译文的页面会被跳过，除非 `force` 为 true。
- `cancel`：target 为 `runs/translation/{run-id}/` 或其 `/status`，`args` 必须为 `{}`。取消后已提交的页面保留。
- 状态（`fetch run_uri`）：运行状态为 `running`、`completed`、`cancelled` 或 `failed`，并给出各终态的页数，以及逐页的 `{page_index, state, committed_box_count, error_code?}`。页状态为 `skipped`、`committed`、`partial`、`failed` 或 `cancelled`。个别页面失败不会让整个运行变为 `failed`，必须逐页检查。
- 使用哪个翻译引擎、如何分段与合批，由用户在“文献加工 → 文献翻译”中选择的 FSX 引擎决定。每页译文原子提交，并发冲突时不会覆盖他人的写入。

### 自己翻译

外部 Agent 也可以自行翻译：`fetch translations/{document-id}/page-{N}.xml`，再按上文 box 写入规则 `put` 译文。目录 `translations/{document-id}/` 列出每页的 `translation_status`（`untranslated`、`partial`、`translated`、`stale`），可据此找出仍需翻译的页面。
