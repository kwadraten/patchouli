---
name: patchouli
description: 使用 Patchouli 文献库：检索、阅读、引用、导入 PDF/CSL、校勘 OCR、翻译、编辑题录与页面，或编写 FSX 扩展。凡任务涉及 patchouli:// URI、patchouli.* MCP 工具或 patchouli-cli 时使用。
---

# Patchouli

Patchouli 是个人文献管理器。每个书库只有一个运行中的 .NET 宿主（桌面应用或无界面 headless 进程），它是数据、版本与写入规则的唯一权威；MCP 工具与 `patchouli-cli` 都是它的前端，语义完全相同。

## 连接

1. 优先调用 MCP 工具 `patchouli.find`、`fetch`、`add`、`view`、`put`、`cite`、`send`。
2. 没有 MCP 时用 `patchouli-cli`；它会自动发现宿主，桌面未运行时自启 headless 宿主。
3. 参数以运行时为准：`patchouli-cli <command> --help` 给出当前版本的用法，`fetch` 领域端点（如 `patchouli://runs/ocr/control`）给出当前授权下的消息 Schema。本 Skill 讲用法与约束，不复制会随版本变化的帮助和 Schema。

## 每个任务都适用的规则

- **URI 即身份**：资源一律用 `patchouli://` URI 指称。URI 未知用 `find`，已知用 `fetch`；`fetch` 从不搜索，也不自动跟随链接。
- **分类管理**：编辑 `library.yml` 建立/删除集合与标签；编辑题录 YAML 的 tags/collections 修改归属。集合改名保留 ID；解散集合不删除题录。省略字段保留，[] 明确清空，引用未知分类会失败。详见 MCP-CLI。
- **显式结果**：先读 `meta.outcome`（`success` 或 `error`）和 `meta.summary`，了解实际完成了什么。错误时仍可能返回有用内容或截断内容，这不代表部分成功；按错误说明缩小范围或使用续读位置。有 `message.warnings` 时逐条阅读（例如 `[obsoleted]` 指出应改用的 canonical URI）；`message.error` 形如 `NAME [code N]: detail`。
- **写入带版本前提**：先 `fetch` 得到版本，再带同一版本 `put`。`REVISION_CONFLICT` 表示别人先改了：重新 `fetch`，在新内容上重做修改，绝不盲目重试旧内容。
- **权限归用户**：`PERMISSION_DENIED` 是用户的选择。告诉用户需要打开哪个开关或授权，然后停下；不要换一种调用绕过它。
- **受理不等于完成**：`send` 返回 `started` 只表示任务已入队，用返回的 `run_uri` 轮询状态。`runs/` 下的状态只存在于宿主内存中，宿主重启后会消失。

## 按任务阅读

- 对 Item 与 DocumentInstance、物理页与逻辑页、修订、Box、证据 URI 等身份概念不确定时，读 [DOMAIN](references/DOMAIN.md)。
- 需要了解工具参数、响应外壳、分页、权限模型、`send` 端点或错误码时，读 [MCP-CLI](references/MCP-CLI.md)。
- 读写页面正文、对照原图校勘 OCR、按 box 修改、导入 PDF、启动 OCR 或翻译任务、处理译文时，读 [DOCUMENTS](references/DOCUMENTS.md)。
- 生成引用、管理 CSL 样式、修改题录 YAML、维护章节与印刷页码，或在线补全元数据时，读 [CITATIONS](references/CITATIONS.md)。
- 编写或修改 FSX 扩展（翻译引擎、元数据来源等），或需要区分 Core、Extension、Plugin 时，读 [EXTENDING](references/EXTENDING.md)。

## Plugin 范围

已安装并启用的 Plugin 挂载于 `patchouli://plugin/{plugin-id}/`。先用 `find` 和 `fetch plugin.yml` 阅读静态声明，再按声明调用 `find`/`fetch`/`send`。声明了 UI 视图的插件还会列出 `views/`：`fetch` 或 `view` 作用于 `views/{view-id}` 返回宿主合成的视图描述符（不启动插件进程，`view` 对它不返回图像），桌面按声明的 placement 在工作区标签页、非模态弹窗或 PDF 侧栏打开对应的 WebView 容器。仍不要把 `put`/`add`/`cite`、托管 `files/` 或插件书库授权当成现有能力。桌面「设置 → 插件 → 插件商店」使用验签的社区索引安装独立 stdio MCP 插件；安装记录按设备保存，不进入书库快照。
