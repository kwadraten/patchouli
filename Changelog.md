# 更新日志

## 0.3.13

发布：2026-10-19 · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.12...0.3.13)

- 内置 Agent 与 F# 工作流系统移除，改为外部 Agent 集成：在嵌入式终端中直接运行 OpenCode（默认）、Claude Code、Codex、Kimi Code、Antigravity 等 CLI Agent；设置页「Agent 集成」可查看各 Agent 检测状态、选择默认项、指定自定义路径或暂不启用，OpenCode 支持一键安装（官方下载、原子落位、失败回滚）。
- 新增「Agent 集成」工作区页面：终端内运行所选 Agent；四个预置提示词（整理题录、调查主题、梳理文献、反向提问）作为可编辑模板填入输入区，绝不自动发送；Zed 风格的可搜索题录引用面板插入标题、Item ID 与规范 URI（不含本地路径）；内容可复制或注入终端但不自动回车；可为 Agent 生成受限、可撤销的 Patchouli MCP 连接配置。
- 新增不依赖 Agent 的全文直接翻译：按 Box 分段、批量并行请求大模型，支持目标语言、批量大小、并发、请求间隔、超时与重试设置；主窗口菜单、书库右键与阅读工具栏均可启动，进度窗口实时显示各页状态并可取消；冲突自动重读重试，已提交页面在取消或部分失败时保持不变。
- MCP/CLI 新增 `translation.start`/`translation.cancel` 动作与 `runs/translation/` 状态查询，权限矩阵增加相应授权项；移除 `runs/agent` 与 `workflows` 资源树及内置 Agent 设置项（旧快照中的相关载荷仍可导入，将被忽略）。
- 仓库整理：`tools/` 更名 `sidecar/`，调色板生成脚本迁移为 PowerShell；Agent 目录由 `scripts/update-agent-catalog.ps1` 从 ACP registry 生成，支持 `-Check` 漂移检测。

## 0.3.12

发布：2026-10-18 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.12) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.11...0.3.12)

- MCP / CLI 新增 `add` 本地 PDF、CSL 导入和 `view` 页面、边界框原图读取；内置 Agent 支持原生图像结果，可对照原件校勘正文。入库不自动启动 OCR，显式 OCR 工具入口留待后续版本。
- 支持将 PDF 拖入主窗口导入；MCP 与拖拽可分别配置保留原位或移动到首个文件搜索根，完善根目录顺序、去重及移动失败恢复。
- 题录语言统一规范化为语言代码，编辑器提供中文语言选项；新增全局 CSL 本地化规则，支持中日韩文献采用中文分支、其他语言采用英文分支，并保留样式自身语言设置及完整列表排序、编号与消歧。
- 已安装 CSL 样式、在线样式和题录本地化统一进入设置页，改进分组导航、固定页签、滚动和重新加载行为。
- 题录编辑器补齐附注和副标题，附注支持带行号的多行编辑及 BibLaTeX 换行往返；同时改进标签、集合和右键菜单组织，限制回收站中的不可用操作。
- 支持 MinerU v4 中间结果，统一正文、图片、表格、公式等文档框类型，改进阅读预览、页面 XML、翻译和搜索对这些类型的处理。
- 修复上一实例遗留编辑会话阻塞页面重新编辑的问题；启动时清理失效会话，关闭标签页和切换书库时等待对应草稿清理，取消 OCR 后继续保留手动编辑。
- 修复原件位置校验与已知文件位置不一致、文件监听重复导入及书库刷新遗漏，并改善 RapidOCR 模型下载的兼容性。
- 同步检查点和设备绑定采用窄写入，避免覆盖字体、模型等偏好；设置保存后通知现有运行时应用点。修复 MCP 授权回读后丢失，离开相关设置时可显式放弃或保存并重启服务。
- 修复 OCR 引擎下拉框为空、重复加载后丢失选中项的问题，保留已保存配置和未完成草稿。

## 0.3.11

发布：2026-10-16 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.11) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.10...0.3.11)

- MCP 改用 YAML 文献与书库元数据、XML 页面边界框树，支持按 box 原子局部写入；CLI 同步支持新格式与细粒度编辑。
- 保留旧 bib、Markdown、TOON 的兼容解析，统一输出新资源链接，并支持省略 patchouli 协议前缀的 VFS 寻址；旧接口将在 0.4.0 弃用。
- 修复 CSL 日期及带类型元数据在 YAML 往返转换中的丢失，保留文献的精确类型与年份信息。
- 在线多模态 OCR 默认使用本地版面识别，可选择 PP-DocLayoutV3、DocLayout-YOLO 或直接识别整页；版面模型在本地文件页统一下载、校验与管理。
- 用户框选区域直接识别；已有边界框的识别仅生成文本，避免重复生成坐标。支持由大语言模型确定区域阅读顺序，并发识别后保持顺序。
- 多模态 OCR 新增独立推理强度与并发数设置，推理强度通过 LLMTornado 通用接口传递；聊天与工作流也支持配置推理强度。
- 修复多模态 OCR 默认预设与队列路由，将 NDL Koten OCR Lite 显示名称统一为 NDL古典籍OCR-Lite，并改善无界面测试的线程隔离与运行时序。

## 0.3.10

发布：2026-10-14 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.10) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.9...0.3.10)

- 产品名称统一为 Patchouli.NET，同步界面、文档、程序集元数据及各平台安装包名称。
- Agent 工作流新增显式上下文选择，主上下文保持连续，独立 worker 隔离历史与 FSI 状态；每次执行使用独立 activation，允许阶段切换工具与指令而不破坏确定性重放。
- 工作流协议升级到 `/8`：旧脚本需显式选择上下文，旧自动化快照需新建会话，原始历史仍可读取。
- 新增 Linux deb、rpm、AppImage、Flatpak 打包，GitHub Release 同时发布 Windows、macOS 和四种 Linux 安装包。
- 改进 Linux CLI 的宿主查找与 CSL 样式标识校验，统一三个平台的打包默认版本。
- Avalonia 升级至 12.1.3，修复单实例激活测试使用的 headless 会话启动与释放竞态，并保留 Windows、Linux、macOS CI 测试日志。
- LLMTornado 升级至 3.8.74，修正 Anthropic 工具结果缓存标记的序列化。

## 0.3.9

发布：2026-10-11 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.9) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.8...0.3.9)

- Agent 工具调用、FSI 和工作流输出纠错不再消耗请求重试额度；临时模型请求失败使用可取消的指数退避，成功响应立即重置计数。
- 模型设置新增自动重试次数，默认 3 次，可调整为 0–20 次；0 表示关闭自动重试。
- 工作流编辑器根据 F# 参数声明生成独立配置表单，启动前统一校验，并保存参数与模型选择；无效配置会定位到对应字段并保留草稿。
- 改进 Agent 上下文预算、工具结果折叠和历史分页检索，保留原始记录与稳定的模型输入，增强恢复和重放的一致性。
- 工作流重试与重放协议升级：旧会话历史保留，自动运行需从现有脚本新建会话，无需重写脚本。

## 0.3.8

发布：2026-10-09 · [GitHub Release](https://github.com/kwadraten/patchouli/releases/tag/0.3.8) · [提交记录](https://github.com/kwadraten/patchouli/compare/0.3.7...0.3.8)

- OCR 菜单新增引擎快速切换，可分别为文档级、页面级和区域级选择引擎，选择后立即保存。
- 搜索方式与 OCR 引擎统一使用单选菜单样式，明确显示当前选择；OCR 级别菜单仅用于展开子菜单。
- 修复工作区标签页共享页面导致的显示与状态问题，各标签页保留独立页面。
- 默认隐藏书库中的可选列，并修复首次设置后书库侧栏状态未及时更新的问题。

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
