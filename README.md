<div align=center>

  <h1>Patchouli.Net</h1>

  <img src="/logo/icon.png" width=200></img>

  <p>实验性的桌面端个人文献管理器，面向大型 PDF 文献库的题录管理、数字化、持续校正、有效召回与可复现证据引用，并可与 AI Agent 协同工作。</p>

</div>

## 特性

- 不再有卡到爆的webview和来自前端项目的屎山代码，本项目尽可能使用.net或rust等原生轮子实现功能，保证性能。
- 不会擅自移动、重命名或接管PDF源文件，本项目用文件哈希追踪PDF文件位置，文件爱放哪就放哪。
- OCR 识别错了也不浪费，文本、表格、类型、顺序和 bbox 进入页级不可变边界框树，PDF 工作台可在草稿中持续校正后再提交。
- 带有良好的全文检索功能的MCP服务器，可以与任何本地的AI Agent配合使用。

## 功能列表 / 路线图

- [x] 现代化的桌面应用UI：题录管理、PDF查看和OCR内容原生Markdown预览、页级边界框树与不可变修订、设置管理、阻塞任务处理、冲突处理
- [x] 合理的基础数据模型：贴合CSL规范的题录模型、基于文件哈希的文件资产模型、完整支持MinerU OCR特性的OCR结果模型
- [x] OCR支持：支持文档、页面、逻辑页面、区域等不同粒度的OCR，目前针对MinerU OCR提供一等支持
- [x] 题录支持：目前可以正确输出绝大部分CSL题录的文本和HTML结果
- [x] 外部数据来源支持：支持使用文献标识符快速拉取元数据、支持从zetero官方列表和中文社区样式列表获取CSL样式
- [x] 全文检索支持：基于Sqlite FTS的全文检索，带有唯一证据引用的搜索结果
- [x] 快照同步支持：支持将本机数据库发布为快照，可自行配置通过网盘/同步盘同步快照，支持处理快照间冲突
- [x] MCP支持：支持AI Agent通过MCP只读访问文献库内容、输出CSL题录，MCP高度可配置
- [x] MacOS 适配：对MacOS的TCC权限体系提供支持
- [ ] 完善题录系统：扩展题录类型、扩展 CSL 字段、加入biblatex支持、批量CSL/biblatex导出、实装标签系统、提供基于标签的更加细分的筛选器 （施工中）
- [ ] 渐进式探索MCP：基于虚拟文件系统和虚拟bash环境的MCP（施工中）
- [ ] 支持更多OCR：多模态LLM OCR 支持、基于onnx运行时的本地OCR支持

## 开发指南

- 开发本项目需要安装 .NET SDK 10.0 和 rust cargo 工具链。
- 若希望提交代码，请在提交前运行代码格式化和静态分析。为了运行静态分析，需要安装 [JetBrains ReSharper 命令行工具](https://www.jetbrains.com/zh-cn/help/rider/ReSharper_Command_Line_Tools.html)。

### 编译或运行项目

```pwsh
dotnet restore Patchouli.sln
dotnet build Patchouli.sln --no-restore
dotnet run --project src/Patchouli.UI/Patchouli.UI.csproj
```

CSL 渲染由托管 NuGet 包 `Fsharp.Citeproc` 提供，不需要额外的本机 sidecar。仓库仍保留 Rust/Cargo 工具链约定，用于后续基于 `typst/biblatex` 的 BibLaTeX 到 CSL 转换器；Rust 工具统一放在 `tools/<tool-name>` 下，并在各自目录运行 `cargo build --release`。

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

### macOS 打包与分发

- macOS 版本不使用 App Sandbox，也不上架 Mac App Store；文件访问通过标准 TCC 文件夹选择提示完成。
- 使用 `scripts/package-macos.sh` 在 macOS 上构建 `.app` 和 DMG；签名是可选的（无 `APPLE_CODESIGN_IDENTITY` 时使用 ad-hoc 签名），不需要 entitlements 文件。
- 详见 `.agent/adr/0017-macos-filesystem-no-sandbox-only-tcc.md`。

## 反馈问题

请通过 [GitHub Issues](https://github.com/kwadraten/patchouli/issues) 反馈问题，并说明操作系统、应用版本、复现步骤、预期行为和实际行为。若问题无法稳定复现，请尽量附上截图或录屏。

运行日志文件为 `patchouli.log`，默认位置如下：Windows 为 `%LOCALAPPDATA%\Patchouli\logs`，macOS 为 `~/Library/Application Support/net.patchouli.app/logs`，Linux 为 `${XDG_DATA_HOME:-~/.local/share}/patchouli/logs`。创建 issue 时请附上相关日志片段；提交前请自行确认其中不含不应公开的内容。

## 免责声明

本项目基于 GPLv3 开源，软件依原样提供，作者不对其适用性、可靠性和准确性提供任何保证。

帕秋莉·诺蕾姬（Patchouli Knowledge）是上海爱丽丝幻乐团原创系列作品 `东方 Project` 的登场人物。根据 [东方 Project 使用规定案](https://thbwiki.cc/%E4%B8%9C%E6%96%B9Project%E4%BD%BF%E7%94%A8%E8%A7%84%E5%AE%9A%E6%A1%88)，本项目的正式中文名称为 `广藿香.Net`。本项目名称捏他自该角色“不动的大图书馆”（The Unmoving Great Library）的称号，也对应本项目保持用户 PDF 源文件不动（Unmove）的设计。
