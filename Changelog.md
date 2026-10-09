# 更新日志

## 0.3.7

发布：2026-10-09 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.7) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.6...0.3.7)

- 新增内置 AI 聊天与持久化会话，支持模型提供程序配置、订阅连接、上下文压缩与历史检索，以及会话内 F# REPL。
- 新增 F# 工作流定义、脚本编辑、运行检查点与菜单入口；工作流和聊天可通过统一宿主访问书库资源，快照同步包含相关内容。
- MCP / CLI 新增 `send` 与运行资源，使用资源域与动作权限矩阵控制访问，并保持凭据隔离。
- 新增多模态 LLM OCR，可复用模型提供程序配置，在文档与区域识别中选择对应模型。
- 设置按功能重新分组，改进自动保存、无效配置草稿保留、提供程序凭据与模型选择；新增应用内更新日志。
- 改进文件导入去重、快照文件载荷、永久删除确认，以及聊天 Markdown 与资源链接导航。

## 0.3.6

发布：2026-10-01 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.6) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.5...0.3.6)

- PDF 导入整本原子提交，失败时回滚；少量坏页保留页序并显示诊断占位，失败页比例阈值默认 20%，可在设置中调整。
- 全书阅读改用原生阅读组件，按需加载 Markdown 页面，支持图片、表格、页码导航，以及原文与译文并排或段落下方对照；字体、字号与对照布局可配置。
- 修复 OCR 设置草稿被刷新覆盖、引擎选择保存，以及搜索改写规则部分保存失败后重试产生重复规则的问题。
- 优化回收站永久删除、单文档搜索索引、MCP 页级读取和译文目录；资产回收使用引用索引，题录缓存按变更范围刷新，快照分片复用一次一致性备份。

## 0.3.5

发布：2026-09-17 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.5) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.4...0.3.5)

- 新增页级译文存储、编译、显示与编辑，PDF 工作台和阅读模式可查看译文；MCP 增加译文资源目录和页级写入。
- 同一书库由一个桌面或无界面运行时宿主管理，支持宿主发现与桌面接管，并改进 CLI 的连接处理。
- 新增系统托盘图标，将初始化工作延后到首帧显示之后；扫描、导入、OCR 与 MCP 等活动统一显示运行状态。
- 书库列表改用游标分页，OCR 队列改进唤醒机制和分阶段进度显示；设置按字段持久化。
- 修复切换书库后旧异步结果覆盖当前界面、关闭标签页后单例内容被释放，以及进入阅读模式时未定位到当前页的问题。

## 0.3.4

发布：2026-09-12 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.4) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.3...0.3.4)

- 新增 RapidOCR 和 NDLOCR-Lite 本地 ONNX 引擎，并修正 NDL Koten OCR 流程与上游实现的差异。
- 新增全书阅读模式，围绕视口按需加载页面；支持阅读字体、字号设置，以及点击页码返回对应 PDF 工作台页面。
- PDF 工作台侧栏增加阅读视图；切换标签页后可恢复已加载的阅读内容，草稿编辑后及时更新 Markdown 缓存。
- 新增书库的一层集合，完善题录组织；修复列表刷新后丢失选择的问题。
- 新增 OpenCC 简繁体搜索查询改写，放宽 BibTeX 导入格式兼容性，并提高桌面单实例监听的稳定性。

## 0.3.3

发布：2026-09-10 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.3) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.2...0.3.3)

- 新增题录搜索模式和高级筛选条件，改进连续展开搜索结果时的布局稳定性。
- 书库表格切换至 ProDataGrid，桌面框架升级至 Avalonia 12.1.2。
- BibLaTeX 自定义字段支持导入、编辑与导出往返，并显示验证警告。
- 完善 PDF 工作台边界框的选择与删除；删除逻辑页面时同步删除子框。
- OCR 成功但没有识别到文字的页面保留空白占位；优化 NDL Koten 行识别并行度和检测过滤。
- 提取桌面与 MCP 共用的运行时宿主层，并补充 HTTP 传输的 TOON/JSON 编码协商验证。

## 0.3.2

发布：2026-09-04 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.2) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.1...0.3.2)

- 新增桌面单实例启动与激活，再次启动时唤起已有窗口。
- 新增可选配色方案，统一各配色下的选择高亮和状态栏颜色。
- 新增文档修订谱系可视化，便于查看版本之间的关系。
- 改进书库侧栏与详情检查面板的交互。

## 0.3.1

发布：2026-08-19 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.1) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.0...0.3.1)

- 完善题录生命周期，新增标签管理、重复检测、题录合并、回收站与永久清除。
- 统一文档工作稿和已提交修订，增加文档级提交与带版本的证据引用。
- 新增 NDL Koten OCR Lite 本地 ONNX 引擎，完善 OCR 队列及引擎相关界面。
- 新增文件资产垃圾回收和本机文件管理设置，清理不再被引用的资产。

## 0.3.0

发布：2026-08-03 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.0) · [提交记录](https://github.com/kwadraten/patchouli/compare/v0.2.6...0.3.0)

- MCP 生产接口迁移为结构化资源协议，通过虚拟文件系统提供 `find`、`fetch`、`put`、`cite`，支持 TOON/JSON 输出；移除生产路径对虚拟 Shell sidecar 的依赖。
- 新增随桌面应用分发的 `patchouli-cli`，通过 MCP HTTP 访问书库资源。
- 完善题录和 CSL 样式的受限写入能力，修正文档边界框的兄弟顺序输出。
- 优化运行时、OCR、PDF 查看、书库首屏和界面响应，建立性能回归入口。
- 修复 Windows 安装后 PATH 环境变量变更未及时广播的问题。

## 0.2.6

发布：2026-07-31 · Git 标签：`v0.2.6` · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/v0.2.6) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.2.5...v0.2.6)

- 完善当时的 MCP 只读虚拟 Shell，限制执行范围与输出规模。
- OCR Markdown 增加列表项输出和 GFM 表格验证，允许安全的 HTML 行内标签。
- 增加界面多选支持并调整布局。
- 修复旧文件搜索根绑定结构的迁移兼容性问题，移除已弃用的 Gitee CSL 样式目录来源。

## 0.2.5

发布：2026-07-30 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.2.5) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.2.4...0.2.5)

- 新增 MCP 只读虚拟 Shell sidecar，并接入界面的资源导航与导入流程。
- 完善题录编辑和 OCR 工作流，修复导入题录元数据在编辑时未被保留的问题。
- 完善设备设置同步生命周期和迁移边界。
- 导入时可实体化云端占位文件，复杂表格保留 HTML 供 MCP 读取。
- 改进 BibLaTeX 冲突解决对话框，阻塞任务对话框高度随内容调整。

## 0.2.4

发布：2026-07-28 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.2.4) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.2.3...0.2.4)

- 新增基于 Rust 辅助程序的 BibLaTeX 解析、字段映射、导入预览与应用，以及导出界面。
- 修复 PDF 工作台未正确将文档 OCR 加入队列的问题，改进任务取消处理。
- 修复 macOS 文件搜索根扫描挂起，允许导入部分成功的扫描结果。
- Windows 安装时清理旧布局迁移文件，避免残留文件干扰新版数据库迁移。

## 0.2.3

发布：2026-07-28 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.2.3) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.2.2...0.2.3)

- 重构 OCR 队列协调流程，拆分 MinerU 上传与结果解析，连续普通页面可在逻辑文档 OCR 中批量处理。
- PDF 工作台支持边界框多选、批量移动与抑制。
- 增加布局重叠警告，并保留 MinerU 跨段续接关系。

## 0.2.2

发布：2026-07-27 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.2.2) · [发布比较](https://github.com/kwadraten/patchouli/compare/0.1.2...0.2.2)

- CSL 渲染切换至托管的 `Fsharp.Citeproc`，修正引用类型的兼容性问题。
- 完善 macOS 文件系统适配，PDF 渲染与 OCR 前实体化文件，默认路径采用平台应用目录。
- 建立标签触发的 Windows 安装包和 macOS 磁盘映像自动发布流程。
- 本次 Release 也包含下述 0.2.0、0.2.1 阶段的改动。

## 0.2.1

版本准备：2026-07-15 · [版本提交](https://github.com/kwadraten/patchouli/commit/cbcd032)

Git 历史中有 0.2.1 版本准备提交，未找到独立版本标签或 GitHub Release；这些改动随后包含在 0.2.2 中。

- 将旧布局树替换为页级文档边界框树，恢复 OCR 导入、证据引用和工作台编辑流程。
- 保留 MinerU 辅助载荷文本，完善文档边界框树的 OCR 工作台。
- 新增同步中心和完整快照同步生命周期，统一冲突解决流程及可执行的冲突处理动作。
- 完善快照设置生命周期，并在状态栏报告不兼容数据库结构版本被拒绝的原因。

## 0.2.0

本地标签日期：2026-07-13 · [版本提交](https://github.com/kwadraten/patchouli/commit/2dcf40b)

该版本有本地 Git 标签，未找到对应的 GitHub Release；改动随后包含在 0.2.2 中。

- PDF 预览改用 PDFium 像素数据，优化预览显示路径。
- 设置 JSON 统一管理提供程序凭据，MCP 服务设置移出 SQLite。
- 快照排除设备本地凭据，统一设置保存控件。

## 0.1.2

标签日期：2026-07-12 · [版本提交](https://github.com/kwadraten/patchouli/commit/9d1cd22) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.1.1...0.1.2)

- PDF 渲染和元数据读取切换至 PDFium，替换原 MuPDF.NET 路径。
- 加固 macOS 存储、文件搜索根访问、首轮导入与 OCR 文件处理。
- 调整平台应用路径和桌面打包配置，提高跨平台运行兼容性。

## 0.1.1

标签日期：2026-07-11 · [版本提交](https://github.com/kwadraten/patchouli/commit/83f2425) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.1.0...0.1.1)

- 阻塞工作流改用模态对话框，快照操作支持取消，冲突处理由统一描述驱动。
- 应用数据采用平台目录，文件搜索根支持授权遍历和部分扫描结果报告。
- 完善应用生命周期、命令、后台任务、OCR 和 MCP 的意外异常捕获与诊断记录。
- 补充桌面打包、开发、问题反馈与代码质量检查指南。

## 0.1.0

标签日期：2026-07-10 · [版本提交](https://github.com/kwadraten/patchouli/commit/064e1cc)

- 建立 Avalonia 桌面应用，提供书库、题录编辑、PDF 工作台、搜索结果、OCR 队列、设置与关于页面。
- 支持扫描与导入用户管理的 PDF，使用 Blake3 哈希追踪文件身份，提供首轮初始化向导。
- 接入 MinerU OCR、结果导入与布局编辑，支持文档和区域 OCR，以及大文件分段上传。
- 新增 SQLite 持久化、混合中日韩文本搜索、证据复制与导出，以及快照分片与复用。
- 支持 CSL 题录渲染、可选样式目录和通过文献标识符获取元数据。
- 提供 MCP HTTP 服务和书目读取接口，加入 Windows/macOS 桌面打包脚本、许可证及第三方组件鸣谢。
