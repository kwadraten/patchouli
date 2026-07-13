<div align=center>

  <h1>Patchouli.Net</h1>

  <img src="/logo/icon.png" width=200></img>

  <p>实验性的桌面端个人文献管理器，面向大型 PDF 文献库的题录管理、数字化、持续校正、有效召回与可复现证据引用，并可与 AI Agent 协同工作。</p>

</div>

## 特性

- 不再有卡到爆的webview和来自前端项目的屎山代码，本项目尽可能使用.net或rust等原生轮子实现功能，保证性能。
- 不会擅自移动、重命名或接管PDF源文件，本项目用文件哈希追踪PDF文件位置，文件爱放哪就放哪。
- OCR识别错了也不浪费，文本、表格、阅读顺序和bbox都进入带版本控制的版面布局模型，识别错了可以继续修。
- 带有良好的全文检索功能的MCP服务器，可以与任何本地的AI Agent配合使用。

## 功能列表 / 路线图

- [x] 桌面应用
- [x] 题录管理
- [x] 文件资产模型
- [x] PDF 导入
- [x] MinerU OCR 支持
- [x] OCR 队列
- [x] 全文检索
- [x] 搜索配置
- [x] 证据引用
- [x] MCP 读取
- [x] 凭据隔离
- [x] 同步快照
- [x] 快照分支
- [x] MCP 配置
- [x] MCP 鉴权
- [x] MCP CSL 输出
- [x] CSL 题录渲染
- [x] 通用题录导入
- [x] 根据标识符拉取元数据
- [x] 元数据来源管理
- [x] 阻塞任务处理及进度 UI
- [x] 现代化的设置管理及 UI
- [ ] 统一的版面布局模型（返工中）
- [ ] 版面布局修订（返工中）
- [ ] MacOS 适配 （施工中）
- [ ] 冲突处理及 UI（施工中）
- [ ] 类型化题录 （施工中）
- [ ] 扩展 CSL 字段 （施工中）
- [ ] 数据库快照发布和导入（施工中）
- [ ] 批量 CSL 复制
- [ ] 更加细分的筛选器
- [ ] 局部 OCR
- [ ] OCR 候选采纳
- [ ] 多模态LLM OCR 支持
- [ ] 基于onnx运行时的本地OCR支持

## 开发指南

- 开发本项目需要安装 .NET SDK 10.0 和 rust cargo 工具链。
- 若希望提交代码，请在提交前运行代码格式化和静态分析。为了运行静态分析，需要安装 [JetBrains ReSharper 命令行工具](https://www.jetbrains.com/zh-cn/help/rider/ReSharper_Command_Line_Tools.html)。

### 编译或运行项目

```pwsh
dotnet restore Patchouli.sln
dotnet build Patchouli.sln --no-restore
dotnet run --project src/Patchouli.UI/Patchouli.UI.csproj
```

`tools/patchouli-hayagriva` 是由应用调用的 Rust 辅助工具；修改它后，在该目录运行 `cargo build --release`。

### 运行单元测试

```pwsh
dotnet test Patchouli.sln
```

### 代码格式化和静态分析

```pwsh
./scripts/cleanup-code.ps1
./scripts/inspect-code.ps1
```

清理和分析脚本要求 JetBrains Command Line Tools `2026.1.4`，并会使用仓库内的 `.editorconfig` 和固定的清理配置。提交非文档改动前，请先执行这两个命令；静态分析报告输出至 `artifacts/inspectcode.sarif`。

## 反馈问题

请通过 [GitHub Issues](https://github.com/kwadraten/patchouli/issues) 反馈问题，并说明操作系统、应用版本、复现步骤、预期行为和实际行为。若问题无法稳定复现，请尽量附上截图或录屏。

运行日志文件为 `patchouli.log`，默认位置如下：Windows 为 `%LOCALAPPDATA%\Patchouli\logs`，macOS 为 `~/Library/Application Support/net.patchouli.app/logs`，Linux 为 `${XDG_DATA_HOME:-~/.local/share}/patchouli/logs`。创建 issue 时请附上相关日志片段；提交前请自行确认其中不含不应公开的内容。

## 免责声明

本项目基于 GPLv3 开源，软件依原样提供，作者不对其适用性、可靠性和准确性提供任何保证。

帕秋莉·诺蕾姬（Patchouli Knowledge）是上海爱丽丝幻乐团原创系列作品 `东方 Project` 的登场人物。根据 [东方 Project 使用规定案](https://thbwiki.cc/%E4%B8%9C%E6%96%B9Project%E4%BD%BF%E7%94%A8%E8%A7%84%E5%AE%9A%E6%A1%88)，本项目的正式中文名称为 `广藿香.Net`。本项目名称捏他自该角色“不动的大图书馆”（The Unmoving Great Library）的称号，也对应本项目保持用户 PDF 源文件不动（Unmove）的设计。
