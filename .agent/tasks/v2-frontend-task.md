# 任务目标：v2 前端与 UI 重构

来源 PRD: `.agent/PRD.md` (`Patchouli PRD v2 正式版`)

## 核心指导原则

1. **完全中文化 (UI Localization)**：所有面向最终用户的 UI 文本、占位符、提示信息、菜单项与状态文本等必须使用中文。开发者调试专用的内部状态码、错误堆栈除外。
2. **Avalonia 11/12 现代语法规范**：
   - 必须使用**编译绑定 (Compiled Bindings)**：通过配置 `x:DataType`、`x:CompileBindings="True"` 保证类型的静态安全。
   - 弃用陈旧的 `Styles` 强行覆盖模板属性的方法，尽可能使用现代的 `ControlTheme` 和 `ThemeDictionary` 进行控件深层样式定制。
   - 充分利用 Avalonia 11/12 引入的新式布局与原生控件库，确保响应性能和现代质感。
3. **消除 Alpha 开发者痕迹**：用直观的向导、独立模态框和图表替代纯文本的散列 ID 堆砌，让文献管理流程对普通用户真正可用。

## 范围与文件级操作分解

### 1. UI 目录树与 ViewModel 组织重构
**目标**：解决目前所有 ViewModel 缝合在一个 `ViewModels.cs` 文件中的问题，建立可扩展的目录结构。

*   **[MODIFY]** `src/Patchouli.UI/Patchouli.UI.csproj`
    *   确保包含新的 ViewModels 目录层级支持。
*   **[DELETE]** `src/Patchouli.UI/ViewModels.cs`
    *   完全拆除此文件，将内部的巨型类拆分到对应的专属文件中。
*   **[NEW]** `src/Patchouli.UI/ViewModels/MainWindowViewModel.cs`
*   **[NEW]** `src/Patchouli.UI/ViewModels/Core/ViewModelBase.cs`
*   **[NEW]** `src/Patchouli.UI/ViewModels/Core/WorkspaceTabViewModel.cs`
*   **[NEW]** `src/Patchouli.UI/ViewModels/Search/SearchEvidenceViewModel.cs`
*   **[NEW]** `src/Patchouli.UI/ViewModels/About/AboutViewModel.cs`
*   **[NEW]** `tests/Patchouli.Tests/Architecture/ViewModelArchitectureTests.cs`
    *   新增架构保护单元测试，强制要求所有后缀为 `ViewModel` 的类只能放置在 `src/Patchouli.UI/ViewModels/` 目录树下。

### 2. 设置界面 (Settings UI) 与 MCP 控制
**目标**：将杂乱的设置页拆分为模块化的最终用户控制台，并新增“记住上次打开的数据库”和 MCP 安全设置。

*   **[NEW]** `src/Patchouli.UI/ViewModels/Settings/SettingsViewModel.cs` (总调度器)
*   **[NEW]** `src/Patchouli.UI/ViewModels/Settings/LibrarySettingsViewModel.cs` 
    *   控制数据库路径、同步根、文件搜索根，并加入 `RememberLastDatabase` 设置的读写。
*   **[NEW]** `src/Patchouli.UI/ViewModels/Settings/McpSettingsViewModel.cs`
    *   控制端口、`0.0.0.0` 鉴权安全阻塞校验、Token 随机生成与明文回显、单项工具开关 (Toggle)。
*   **[NEW]** `src/Patchouli.UI/ViewModels/Settings/CslSettingsViewModel.cs`
*   **[NEW]** `src/Patchouli.UI/ViewModels/Settings/OcrProviderSettingsViewModel.cs`
*   **[MODIFY]** `src/Patchouli.UI/Views/SettingsPage.axaml`
    *   重构视图以支持滚动分栏或侧边栏切换。
    *   每个分组必须使用中文清晰表达 状态(未保存/保存中/已保存/失败) 的持久化反馈。
    *   移除界面直写底层 SQL 的逻辑，纯依赖 ViewModel 接口。
*   **[MODIFY]** `src/Patchouli.UI/AppRuntimeOptions.cs`
    *   扩展配置模型，支持持久化“记住上次打开的数据库”及各种 UI 偏好。

### 3. 类型感知的题录编辑器 (Type-Aware Item Editor)
**目标**：废弃所有 CSL Item 共用一套硬编码表单的做法，改用 `CslItemTypeProfile` 驱动动态生成。

*   **[NEW]** `src/Patchouli.UI/ViewModels/Editor/ItemEditorViewModel.cs`
*   **[NEW]** `src/Patchouli.UI/ViewModels/Editor/CslItemTypeProfileService.cs` 
    *   为 `general`, `book`, `article-journal`, `thesis` 等类型提供不同的首屏/折叠字段描述。
*   **[NEW]** `src/Patchouli.UI/ViewModels/Editor/ItemFieldDescriptor.cs` 
    *   用于在 UI 中动态渲染编辑框（Literal, Date, Creator 等）。
*   **[MODIFY]** `src/Patchouli.UI/Views/ItemEditorPage.axaml`
    *   用 `ItemsControl` 替代固定的 `Grid`，动态绑定对应类型的字段。
    *   **警告文案处理**：当 `ItemType` 为系统内部的 `general` 时，右侧 CSL 预览区必须显示醒目的中文警告：“当前为通用文献，无法直接生成 CSL 引用，请指定具体类型”。

### 4. CSL 样式管理器 (CSL Style Manager UI)
**目标**：提供独立的 CSL 样式索引、安装和选择界面。

*   **[NEW]** `src/Patchouli.UI/Views/CslStyleManagerWindow.axaml` (独立模态窗口或全屏 Page)
*   **[NEW]** `src/Patchouli.UI/ViewModels/Csl/CslStyleManagerViewModel.cs`
    *   实现刷新索引、搜索本地/远程样式、安装、更新和移除功能。
*   **[MODIFY]** `src/Patchouli.UI/Views/LibraryPage.axaml` 及对应 ViewModel
    *   在题录右键菜单新增“复制 CSL 题录 (默认样式)”和“复制为...(展开最近使用列表)”。

### 5. OCR 版面编辑器 (OCR Editor UI)
**目标**：改造 PDF 工作台，支持区域选择、局部 OCR 及处理 CF-06 冲突。

*   **[NEW]** `src/Patchouli.UI/ViewModels/Ocr/PdfWorkspaceViewModel.cs`
*   **[MODIFY]** `src/Patchouli.UI/Views/PdfWorkspacePage.axaml`
    *   在页面图层上增加鼠标框选区域工具。
    *   使用不同的边框/蒙版样式区分“当前生效层”和“Staging 候选层”。
    *   CF-06 冲突（普通 Bbox 重叠）发生时，不允许静默覆盖，必须高亮冲突区域并弹出交互菜单，要求用户选择“调整边框”、“强制覆盖”或“跳过”。

### 6. 阻塞操作与冲突解决对话框 (Modal Dialogs)
**目标**：摒弃不显眼的 Toast，所有的强制等待（扫描/验证）与冲突处理（CF-01 至 CF-05）全部使用标准的模态对话框。

*   **[NEW]** `src/Patchouli.UI/Views/BlockingOperationDialog.axaml`
    *   **元素要求**：中文标题、阻塞原因、影响范围、进度指示器、**可滚动的终端风细粒度日志区**、失败后的恢复指引。
*   **[NEW]** `src/Patchouli.UI/ViewModels/Dialogs/BlockingOperationDialogViewModel.cs`
*   **[NEW]** `src/Patchouli.UI/Views/ConflictResolutionDialog.axaml`
    *   **元素要求**：必须是左右双栏对比视图（本地 vs 传入），底部必须有互斥的中文化操作按钮（例如：“使用本地版本”、“保留并作为副本文档”、“跳过”）。
*   **[NEW]** `src/Patchouli.UI/ViewModels/Dialogs/ConflictResolutionDialogViewModel.cs`
*   **[MODIFY]** `src/Patchouli.UI/MainWindow.axaml` (或专门的 DialogService)
    *   构建能够跨宿主弹出的模态框管理机制。

### 7. 主菜单、右键菜单与状态栏
**目标**：清理 Alpha 阶段无序排放的命令，建立严谨的信息架构。

*   **[NEW]** `src/Patchouli.UI/ViewModels/Core/UiCommandDescriptor.cs`
    *   统一管理命令的可执行性、禁用原因（必须是用户可读的中文）、安全级别。
*   **[MODIFY]** `src/Patchouli.UI/MainWindow.axaml`
    *   重新组织主菜单结构，完全抛弃传统的文件/编辑，按任务流重构为：`书库 (Library)`、`同步 (Sync)`、`题录 (Items)`、`OCR`、`搜索 (Search)`、`MCP`、`设置 (Settings)`、`帮助 (Help)`。
    *   移除全部开发调试命令（如 Tick, Enqueue Mock 等）。
*   **[MODIFY]** `src/Patchouli.UI/Views/LibraryPage.axaml` (及其后的 DataGrid)
    *   题录右键菜单必须包含：编辑元数据、打开文档、运行 OCR、复制证据 Markdown、复制 CSL 题录、导出题录、查看同步/冲突状态。
    *   与当前选择无关的命令隐藏或禁用，并通过 `UiCommandDescriptor` 的 `DisabledReason` 提示禁用原因。
*   **[MODIFY]** `src/Patchouli.UI/MainWindowViewModel.cs`
    *   将普通的成功/轻微错误提示绑定到应用底部状态栏，坚决不引入遮挡视线的 Toast 控件。

### 8. OCR 队列看板 (OCR Queue Board)
**目标**：从底层发号施令终端转型为人类可读的后台任务看板。

*   **[NEW]** `src/Patchouli.UI/ViewModels/Ocr/OcrQueueViewModel.cs`
    *   处理数据映射，通过 `DocumentInstanceId` 联表查询并暴露真实的“文献标题”。
*   **[MODIFY]** `src/Patchouli.UI/Views/OcrQueuePage.axaml`
    *   **[DELETE]** 彻底删除原有的手动排队/范围控制的 TextBox 区域。
    *   保留顶部的全局操作：“刷新列表”、“全部暂停”、“全部继续”。
    *   在任务列表的每一行末尾，添加悬浮式的单行操作按钮（暂停 恢复 取消）。

### 9. 书库高效率表格 (Library DataGrid)
**目标**：引入 Avalonia 官方 `DataGrid` 替代原有的 `ListBox` 伪表格，实现 Zotero 级别的管理体验。

*   **[NEW]** `src/Patchouli.UI/ViewModels/Library/LibraryShellViewModel.cs`
    *   添加各列的显示/隐藏配置项，并通过 `PatchouliAppSettings.Save()` 持久化。
*   **[MODIFY]** `src/Patchouli.UI/Patchouli.UI.csproj`
    *   新增依赖项 `<PackageReference Include="Avalonia.Controls.DataGrid" />`。
*   **[MODIFY]** `src/Patchouli.UI/Views/LibraryPage.axaml`
    *   **[DELETE]** 删除旧的 Header `Grid` + `ListBox` 组合。
    *   **[NEW]** 替换为 `<DataGrid>`，配置 `CanUserResizeColumns="True"`, `CanUserSortColumns="True"`, `CanUserReorderColumns="True"`。
    *   在 DataGrid 表头加入右键上下文菜单，供用户勾选显示哪些列（“题录类型”、“年份”、“作者”、“标题”、“来源”、“OCR/索引状态”、“页数”、“关联文件”）。
*   **[MODIFY]** `src/Patchouli.UI/MainWindow.axaml`
    *   在“视图”主菜单下集成“书库列”子菜单，让用户也可以从此处控制列的可见性。
