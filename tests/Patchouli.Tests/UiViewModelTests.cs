using System.IO.Compression;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Mcp;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class UiViewModelTests
{
    [Fact]
    public async Task LibraryViewModel_CreateLibrary_updates_current_library()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try { var vm = new MainWindowViewModel { RuntimeDatabasePath = path }; await vm.OpenDatabaseCommand.ExecuteAsync(); vm.Library.DisplayName = "UI Library"; await vm.Library.CreateCommand.ExecuteAsync(); vm.Library.Details.Should().Contain("UI Library"); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BibliographyViewModel_CreateItem_returns_item()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try { var vm = new MainWindowViewModel { RuntimeDatabasePath = path }; await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync(); vm.Bibliography.Title = "UI Item"; await vm.Bibliography.CreateItemCommand.ExecuteAsync(); vm.Bibliography.Output.Should().Contain("UI Item"); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task FileDocumentViewModel_RegisterMissingFile_creates_missing_asset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try { var vm = new MainWindowViewModel { RuntimeDatabasePath = path }; await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync(); vm.FileDocument.FilePath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pdf"); await vm.FileDocument.RegisterCommand.ExecuteAsync(); vm.FileDocument.Output.Should().Contain("missing"); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task CopyEvidenceMarkdown_writes_pinned_markdown_to_clipboard()
    {
        var clipboard = new FakeClipboard(); var vm = new MainWindowViewModel(clipboard);
        vm.SearchEvidence.Markdown = "Pinned source text";
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        clipboard.Text.Should().Be("Pinned source text");
        vm.SearchEvidence.Output.Should().Be("Copied Evidence Markdown");
    }

    [Fact]
    public async Task CopyEvidenceMarkdown_without_markdown_returns_validation_error()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        vm.SearchEvidence.Output.Should().Contain("validation_failed");
    }

    [Fact]
    public async Task CopyEvidenceRef_writes_ref_to_clipboard()
    {
        var clipboard = new FakeClipboard(); var vm = new MainWindowViewModel(clipboard);

        await vm.SearchEvidence.CopyEvidenceRefAsync("evref:v1:test");

        clipboard.Text.Should().Be("evref:v1:test");
        vm.SearchEvidence.EvidenceRef.Should().Be("evref:v1:test");
        vm.SearchEvidence.Output.Should().Be("Copied EvidenceRef");
    }

    [Fact]
    public async Task CopySearchResultEvidenceMarkdown_creates_search_unit_evidence_lazily()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-evidence-copy-{Guid.NewGuid():N}.sqlite");
        var clipboard = new FakeClipboard();
        try
        {
            var vm = new MainWindowViewModel(clipboard) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var item = await services.Items.CreateItemAsync("book", "UI Evidence Item");
            var document = await services.Documents.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var page = await services.Pages.CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            var revision = await services.Layout.CreateLayoutRevisionAsync(document.Value.DocumentInstanceId, LayoutRevisionSource.Mock, makeCurrent: true);
            await services.Layout.AddNodeAsync(revision.Value.LayoutRevisionId, page.Value.PageId, null, LayoutNodeType.Paragraph, null, "Pinned clipboard text", TextPolicy.Own, 1, LayoutNodeSource.Mock);
            await services.SearchUnits.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);

            await using var connection = services.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            var unitId = await connection.ExecuteScalarAsync<string>("select unit_id from search_units where resolved_text = 'Pinned clipboard text';");
            var unit = new SearchMatchedUnitViewModel(unitId!, "Pinned clipboard text", LayoutNodeType.Paragraph, 1, true, null);

            await vm.SearchEvidence.CopyEvidenceMarkdownForSearchUnitAsync(unit);

            unit.EvidenceRef.Should().StartWith("evref:v1:");
            clipboard.Text.Should().Contain("Pinned clipboard text").And.Contain("UI Evidence Item").And.Contain(unit.EvidenceRef);
            vm.SearchEvidence.Markdown.Should().Be(clipboard.Text);
            vm.SearchEvidence.Output.Should().Be("Copied Evidence Markdown");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task McpPreviewViewModel_SafetyCheck_flags_specific_local_path()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());
        vm.McpPreview.Output = "{\"bad\":\"/tmp/private.sqlite\"}";
        vm.McpPreview.SpecificPath = "/tmp/private.sqlite";
        await vm.McpPreview.SafetyCommand.ExecuteAsync();
        vm.McpPreview.Safety.Should().Contain("Warning");
    }

    [Fact]
    public async Task McpPreviewViewModel_SafetyCheck_passes_clean_output()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());
        vm.McpPreview.Output = "{\"title\":\"A historical source\"}";
        await vm.McpPreview.SafetyCommand.ExecuteAsync();
        vm.McpPreview.Safety.Should().Be("No obvious local path or secret exposure detected.");
    }

    [Fact]
    public void MainWindow_xaml_does_not_use_invalid_none_brush()
    {
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        xaml.Should().NotContain("Fill=\"None\"");
        xaml.Should().NotContain("Stroke=\"None\"");
        xaml.Should().NotContain("Background=\"None\"");
        xaml.Should().NotContain("BorderBrush=\"None\"");
        xaml.Should().NotContain("Foreground=\"None\"");
    }

    [Fact]
    public void LucideIcon_renders_svg_resource_without_external_package()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        session.Dispatch(() =>
        {
            var icon = new Patchouli.Lucide.Avalonia.Lucide
            {
                Icon = "Search",
                Width = 24,
                Height = 24,
                StrokeBrush = Brushes.Black
            };

            icon.Measure(new Size(24, 24));
            icon.Arrange(new Rect(0, 0, 24, 24));

            var bitmap = new RenderTargetBitmap(new PixelSize(24, 24), new Vector(96, 96));
            bitmap.Render(icon);
        }, CancellationToken.None);
    }

    [Fact]
    public void MainWindow_constructs_with_local_lucide_icons()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        session.Dispatch(() =>
        {
            var window = new MainWindow();
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_new_item_editor_renders_without_recursive_templates()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            var window = new MainWindow
            {
                Width = 1280,
                Height = 820
            };
            window.Show();
            try
            {
                var vm = (MainWindowViewModel)window.DataContext!;

                await vm.CreateItemMenuCommand.ExecuteAsync();

                window.Measure(new Size(1280, 820));
                window.Arrange(new Rect(0, 0, 1280, 820));

                var bitmap = new RenderTargetBitmap(new PixelSize(1280, 820), new Vector(96, 96));
                bitmap.Render(window);

                vm.IsItemEditorVisible.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void ItemEditorPage_does_not_template_field_descriptor_with_self_content()
    {
        var editorXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "ItemEditorPage.axaml"));

        editorXaml.Should().NotContain("ContentControl Content=\"{Binding}\"");
    }

    [Fact]
    public void MainWindow_xaml_avoids_recursive_theme_and_local_self_styles()
    {
        var shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));

        shellXaml.Should().NotContain("Theme=\"{StaticResource {x:Type TabControl}}\"");
        shellXaml.Should().NotContain("<TextBlock.Styles>");
    }

    [Fact]
    public void MainWindow_xaml_uses_local_lucide_svg_control()
    {
        var project = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Patchouli.UI.csproj"));
        var packages = File.ReadAllText(TestPaths.FromRepositoryRoot("Directory.Packages.props"));
        var mainXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var libraryXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        var pdfXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));

        project.Should().NotContain("LucideAvalonia").And.NotContain("Lucide.Avalonia");
        packages.Should().NotContain("LucideAvalonia").And.NotContain("Lucide.Avalonia");
        project.Should().Contain("Assets\\Lucide\\*.svg");
        mainXaml.Should().Contain("using:Patchouli.Lucide.Avalonia").And.NotContain("assembly=LucideAvalonia");
        libraryXaml.Should().Contain("using:Patchouli.Lucide.Avalonia").And.NotContain("assembly=LucideAvalonia");
        pdfXaml.Should().Contain("using:Patchouli.Lucide.Avalonia").And.NotContain("assembly=LucideAvalonia");
    }

    [Fact]
    public void All_bound_lucide_icon_names_have_svg_assets()
    {
        var assetsPath = TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Assets", "Lucide");
        var assets = Directory.EnumerateFiles(assetsPath, "*.svg")
            .Select(path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant())
            .ToHashSet();

        var iconNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(TestPaths.FromRepositoryRoot("src", "Patchouli.UI"), "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, "lucide:Lucide[^>]*\\bIcon=\"([^\"]+)\""))
            {
                var icon = match.Groups[1].Value;
                if (!icon.StartsWith("{Binding", StringComparison.Ordinal))
                    iconNames.Add(icon);
            }
        }

        var settingsText = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "ViewModels", "Settings", "SettingsViewModel.cs"));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(settingsText, "new\\(\"[^\"]+\",\\s*\"([^\"]+)\""))
            iconNames.Add(match.Groups[1].Value);

        iconNames.Select(ToKebab).Should().OnlyContain(icon => assets.Contains(icon));

        static string ToKebab(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length + 4);
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                if (current is '_' or '-' or ' ')
                {
                    AppendDash(builder);
                    continue;
                }

                if (i > 0 && (char.IsUpper(current) || char.IsDigit(current)) && builder.Length > 0 && builder[^1] != '-')
                    AppendDash(builder);

                builder.Append(char.ToLowerInvariant(current));
            }

            return builder.ToString();
        }

        static void AppendDash(System.Text.StringBuilder builder)
        {
            if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
    }

    [Fact]
    public void MainWindow_xaml_uses_menu_shell_without_legacy_developer_tools_or_token_prompt()
    {
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        xaml.Should().Contain("<Menu");
        xaml.Should().NotContain("Developer Tools");
        xaml.Should().NotContain("ShowMinerUTokenPrompt");
    }

    [Fact]
    public void MainWindow_xaml_sidebar_uses_real_path_bindings()
    {
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        xaml.Should().Contain("DefaultSyncRootPath");
        xaml.Should().Contain("FileSearchRoots");
        xaml.Should().NotContain("/Documents/Papers");
        xaml.Should().NotContain("/Downloads/Scan");
        xaml.Should().NotContain("WPS Drive");
        xaml.Should().NotContain("最近更改");
        xaml.Should().NotContain("回收站");
    }

    [Fact]
    public async Task MainWindowViewModel_refreshes_sidebar_file_search_roots()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-search-root-{Guid.NewGuid():N}")).FullName;
        var database = Path.Combine(root, "ui.sqlite");
        var pdf = Path.Combine(root, "source.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = database };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            await services.FileResolution.AddSearchRootAsync(root);
            await services.Files.RegisterFileAsync(pdf);

            await vm.RefreshSidebarPathsAsync();

            vm.FileSearchRoots.Should().ContainSingle();
            vm.FileSearchRoots.Single().RootPath.Should().Be(Path.GetFullPath(root));
            vm.FileSearchRoots.Single().FileCount.Should().Be(1);
            vm.FileSearchRoots.Single().AvailabilityText.Should().Be("可用");
            vm.HasFileSearchRoots.Should().BeTrue();
            vm.NoFileSearchRoots.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MainWindow_xaml_wires_toolbar_search_and_results_workspace()
    {
        var shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var searchXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SearchResultsPage.axaml"));
        shellXaml.Should().Contain("RunToolbarSearchCommand");
        shellXaml.Should().Contain("SearchEvidence.Query");
        searchXaml.Should().Contain("搜索结果");
    }

    [Fact]
    public void MainWindow_xaml_wires_ocr_queue_workspace()
    {
        var shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var queueXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "OcrQueuePage.axaml"));
        shellXaml.Should().Contain("OpenOcrQueueCommand");
        shellXaml.Should().Contain("OcrQueuePage");
        queueXaml.Should().Contain("StartCommand");
        queueXaml.Should().Contain("PauseGlobalCommand");
        queueXaml.Should().Contain("CancelCommand");
        queueXaml.Should().Contain("TaskRows");
        queueXaml.Should().Contain("HasTasks");
        queueXaml.Should().NotContain("OCR 队列页面将在后续任务中接入");
    }

    [Fact]
    public void SearchResults_xaml_wires_search_unit_evidence_actions_only()
    {
        var shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var libraryXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        var searchXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SearchResultsPage.axaml"));
        var codeBehind = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml.cs"));
        var searchCodeBehind = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SearchResultsPage.axaml.cs"));
        shellXaml.Should().NotContain("复制证据 Markdown");
        shellXaml.Should().NotContain("导出证据 Markdown");
        libraryXaml.Should().NotContain("复制证据 Markdown");
        searchXaml.Should().Contain("OnCopySearchUnitEvidenceRefClick");
        searchXaml.Should().Contain("OnCopySearchUnitEvidenceMarkdownClick");
        searchXaml.Should().Contain("OnExportSearchUnitEvidenceMarkdownClick");
        searchXaml.Should().Contain("<ContextMenu>");
        codeBehind.Should().NotContain("SaveFilePickerAsync");
        codeBehind.Should().NotContain("OnExportEvidenceMarkdownClick");
        searchCodeBehind.Should().Contain("CopyEvidenceRefForSearchUnitAsync");
        searchCodeBehind.Should().Contain("CopyEvidenceMarkdownForSearchUnitAsync");
        searchCodeBehind.Should().Contain("SaveFilePickerAsync");
        searchCodeBehind.Should().Contain("ExportEvidenceMarkdownToFileAsync");
    }

    [Fact]
    public void MainWindow_xaml_wires_item_editor_and_settings_sections()
    {
        var shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var editorXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "ItemEditorPage.axaml"));
        var settingsXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml"));
        shellXaml.Should().Contain("EditSelectedItemCommand");
        shellXaml.Should().Contain("DataType=\"{x:Type local:ItemEditorViewModel}\"");
        shellXaml.Should().Contain("ItemEditorPage");
        editorXaml.Should().Contain("AddIdentifierCommand");
        editorXaml.Should().Contain("RegisterFileCommand");
        settingsXaml.Should().Contain("FileSearchRoot");
        settingsXaml.Should().Contain("OCR 预设");
        settingsXaml.Should().Contain("搜索配置");
        settingsXaml.Should().Contain("MCP");
    }

    [Fact]
    public async Task ExportEvidenceMarkdownToFile_without_evidence_ref_reports_validation_error()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());

        await vm.ExportEvidenceMarkdownToFileAsync("", Path.Combine(Path.GetTempPath(), $"evidence-{Guid.NewGuid():N}.md"));

        vm.SearchEvidence.Output.Should().Contain("validation_failed");
        vm.Status.Should().Contain("EvidenceRef");
    }

    [Fact]
    public async Task MainWindowViewModel_auto_starts_mcp_http_server_and_reports_status()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-mcp-{Guid.NewGuid():N}.sqlite");
        var port = GetFreeTcpPort();
        var vm = new MainWindowViewModel(new FakeClipboard(), autoStartMcpServer: true, mcpPort: port) { RuntimeDatabasePath = path };
        try
        {
            var services = await vm.ServicesAsync();
            var settings = await services.McpSettings.GetSettingsAsync();
            settings.IsSuccess.Should().BeTrue();
            await vm.StopMcpServerAsync();
            await services.McpSettings.SaveSettingsAsync(settings.Value with { Port = port });
            await vm.StartMcpServerAsync();

            vm.McpEndpoint.Should().Be($"http://localhost:{port}");
            vm.McpStatusText.Should().Be("MCP: 运行中");
            vm.McpStatusDetail.Should().Be("连接数: 0 / 0");
            using var http = new HttpClient();
            var health = await http.GetStringAsync($"{vm.McpEndpoint}/health");
            health.Should().Contain("ok");
        }
        finally
        {
            await vm.StopMcpServerAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task QueueViewModel_enqueue_mock_adds_task_and_displays_runtime_only_warning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync();
            vm.OcrQueue.DocumentInstanceId = Patchouli.Core.Ids.DocumentInstanceId.New().ToString();
            vm.OcrQueue.PresetId = Patchouli.Core.Ids.OcrPresetId.New().ToString();
            vm.OcrQueue.PageIds = Patchouli.Core.Ids.PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();
            vm.OcrQueue.Output.Should().Contain("Queued mock OCR task");
            vm.OcrQueue.Tasks.Should().ContainSingle();
            vm.OcrQueue.Output.ToLowerInvariant().Should().NotContain("secret");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task QueueViewModel_refresh_shows_multiple_tasks_as_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync();
            vm.OcrQueue.DocumentInstanceId = Patchouli.Core.Ids.DocumentInstanceId.New().ToString();
            vm.OcrQueue.PresetId = Patchouli.Core.Ids.OcrPresetId.New().ToString();
            vm.OcrQueue.PageIds = Patchouli.Core.Ids.PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();
            vm.OcrQueue.PageIds = Patchouli.Core.Ids.PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();

            vm.OcrQueue.TaskRows.Should().HaveCount(2);
            vm.OcrQueue.HasTasks.Should().BeTrue();
            vm.OcrQueue.NoTasks.Should().BeFalse();
            vm.OcrQueue.TaskRows.Should().OnlyContain(row => row.State == Patchouli.Ocr.OcrQueueTaskState.Queued);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task QueueViewModel_start_stop_pause_and_validation_are_visible()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync();
            await vm.OcrQueue.StartCommand.ExecuteAsync();
            vm.OcrQueue.StatusSummary.Should().Contain("running");
            await vm.OcrQueue.PauseGlobalCommand.ExecuteAsync();
            vm.OcrQueue.StatusSummary.Should().Contain("global:");
            vm.OcrQueue.TaskId = "not-a-guid";
            await vm.OcrQueue.CancelCommand.ExecuteAsync();
            vm.OcrQueue.Output.Should().Contain("validation_failed");
            await vm.OcrQueue.StopCommand.ExecuteAsync();
            vm.OcrQueue.StatusSummary.Should().Contain("stopped");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Library_run_ocr_enqueues_document_task_visible_on_queue_board()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-queue-real-{Guid.NewGuid():N}.sqlite");
        var pdf = Path.Combine(Path.GetTempPath(), $"ui-queue-real-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Queued OCR Item", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();
            vm.Shell.MinerUToken = "token";
            await vm.OcrQueue.PauseGlobalCommand.ExecuteAsync();

            await vm.Shell.Items.Single().RunOcrCommand.ExecuteAsync();
            await vm.OcrQueue.RefreshAsync();

            vm.Status.Should().Contain("OCR 已加入后台队列");
            vm.OcrQueue.StatusSummary.Should().Contain("running");
            vm.OcrQueue.TaskRows.Should().ContainSingle();
            var row = vm.OcrQueue.TaskRows.Single();
            row.DocumentTitle.Should().Be("Queued OCR Item");
            row.Kind.Should().Be(OcrQueueTaskKind.Document);
            row.PageCount.Should().Be(1);

            await vm.OcrQueue.StopCommand.ExecuteAsync();
        }
        finally
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SearchProfileViewModel_creates_rule_and_previews_plan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-profile-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync(); await vm.Library.CreateCommand.ExecuteAsync();
            vm.SearchProfiles.Name = "UI variants"; await vm.SearchProfiles.CreateProfileCommand.ExecuteAsync();
            vm.SearchProfiles.Pattern = "臺灣"; vm.SearchProfiles.Replacement = "台湾";
            await vm.SearchProfiles.AddRuleCommand.ExecuteAsync();
            vm.SearchProfiles.Query = "臺灣"; await vm.SearchProfiles.PreviewCommand.ExecuteAsync();
            vm.SearchProfiles.Output.Should().Contain("台湾").And.Contain("OriginalQuery");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Shell_refresh_lists_imported_items()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Shell Item", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);

            await vm.Shell.RefreshItemsAsync();

            vm.Shell.Items.Should().ContainSingle(item => item.Title == "Shell Item");
            vm.Shell.Items.Single().RunOcrCommand.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Shell_run_ocr_without_token_reports_recoverable_error()
    {
        var settingsPath = WriteSettingsFile("");
        try
        {
            var vm = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath);
            var item = new LibraryItemViewModel(
                "item-1",
                "Needs OCR",
                "book",
                "",
                "",
                "",
                "doc-1",
                null,
                "needs-ocr.pdf",
                "D:/tmp/needs-ocr.pdf",
                0,
                0,
                "not_indexed",
                _ => Task.CompletedTask,
                _ => Task.CompletedTask);

            await vm.Shell.RunOcrForItemAsync(item);

            vm.Shell.SelectedItem.Should().Be(item);
            vm.Shell.SelectedItem!.OcrStatus.Should().Contain("token");
            vm.Shell.SelectedItem.OcrStatus.Should().Contain("设置");
            vm.IsSettingsVisible.Should().BeTrue();
            vm.ShowSettingsTab.Should().BeTrue();
            vm.Settings.MinerUCredentialStatus.Should().Contain("未配置");
            vm.Status.Should().Contain("MinerU API token");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public async Task OpenDatabase_prefers_provider_credential_over_appsettings_token()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var settingsPath = WriteSettingsFile("fallback-token");
        try
        {
            var vm = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var saved = await services.Credentials.SaveOrUpdateProviderCredentialAsync(ProviderIds.MinerU, "MinerU API token", "provider-token");
            saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);

            var reloaded = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath) { RuntimeDatabasePath = path };
            await reloaded.OpenDatabaseCommand.ExecuteAsync();

            reloaded.Shell.MinerUToken.Should().Be("provider-token");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Settings_save_mineru_token_updates_provider_credential_and_appsettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var settingsPath = WriteSettingsFile("");
        try
        {
            var vm = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            await vm.OpenSettingsAsync("mineru");
            vm.Settings.MinerUTokenInput = "saved-token";

            await vm.Settings.SaveMinerUSettingsCommand.ExecuteAsync();

            var services = await vm.ServicesAsync();
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU)).Value.Should().Be("saved-token");
            PatchouliAppSettings.Load(settingsPath).MinerU.Token.Should().Be("saved-token");
            vm.Shell.MinerUToken.Should().Be("saved-token");
            vm.Status.Should().Contain("已保存");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenDatabase_remembers_custom_runtime_database_when_enabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-remember-db-{Guid.NewGuid():N}.sqlite");
        var settingsPath = WriteSettingsFile("", rememberLastDatabase: true);
        try
        {
            var vm = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath) { RuntimeDatabasePath = path };

            await vm.OpenDatabaseCommand.ExecuteAsync();

            var saved = PatchouliAppSettings.Load(settingsPath);
            Path.GetFullPath(saved.Runtime.RuntimeDatabasePath).Should().Be(Path.GetFullPath(path));

            var reloaded = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath);
            Path.GetFullPath(reloaded.RuntimeDatabasePath).Should().Be(Path.GetFullPath(path));
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenDatabase_does_not_remember_custom_runtime_database_when_disabled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-do-not-remember-db-{Guid.NewGuid():N}.sqlite");
        var settingsPath = WriteSettingsFile("", rememberLastDatabase: false);
        var originalSettings = PatchouliAppSettings.Load(settingsPath);
        try
        {
            var vm = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath) { RuntimeDatabasePath = path };

            await vm.OpenDatabaseCommand.ExecuteAsync();

            var saved = PatchouliAppSettings.Load(settingsPath);
            Path.GetFullPath(saved.Runtime.RuntimeDatabasePath).Should().Be(Path.GetFullPath(originalSettings.Runtime.RuntimeDatabasePath));

            var reloaded = new MainWindowViewModel(new FakeClipboard(), settingsPath: settingsPath);
            Path.GetFullPath(reloaded.RuntimeDatabasePath).Should().Be(Path.GetFullPath(AppRuntimeOptions.Default().RuntimeDatabasePath));
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FirstRun_scan_imports_all_pdfs_as_items_without_manual_metadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-first-run-{Guid.NewGuid():N}.sqlite");
        var scanRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-first-run-scan-{Guid.NewGuid():N}")).FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(scanRoot, "alpha.pdf");
            TestFixtures.CopyRealThreePagePdfTo(scanRoot, "beta.pdf");
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.ShowInlineFirstRunAsync();
            await vm.FirstRun.OpenDatabaseCommand.ExecuteAsync();
            await vm.FirstRun.CreateLibraryCommand.ExecuteAsync();
            vm.FirstRun.ScanRoot = scanRoot;

            await vm.FirstRun.ScanCommand.ExecuteAsync();

            vm.FirstRun.CurrentStep.Should().Be("mineru_config");
            vm.FirstRun.ImportedPdfCount.Should().Be(2);
            await vm.Shell.RefreshItemsAsync();
            vm.Shell.Items.Should().HaveCount(2);
            vm.Shell.Items.Select(item => item.Title).Should().BeEquivalentTo("alpha", "beta");
        }
        finally
        {
            if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FirstRun_import_then_selected_item_ocr_makes_text_readable_through_mcp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-loop-{Guid.NewGuid():N}.sqlite");
        var scanRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-loop-scan-{Guid.NewGuid():N}")).FullName;
        var zipPath = CreateMinerUZip("""
        [
          { "type": "text", "page_idx": 0, "text": "ui selected item mineru searchable text", "bbox": [0, 0, 1000, 100] }
        ]
        """);

        try
        {
            TestFixtures.CopyRealThreePagePdfTo(scanRoot, "selected.pdf");
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.ShowInlineFirstRunAsync();
            await vm.FirstRun.OpenDatabaseCommand.ExecuteAsync();
            await vm.FirstRun.CreateLibraryCommand.ExecuteAsync();
            vm.FirstRun.ScanRoot = scanRoot;

            await vm.FirstRun.ScanCommand.ExecuteAsync();
            vm.FirstRun.MinerUToken = "token";
            await vm.CompleteFirstRunCommand.ExecuteAsync();

            vm.IsLibraryVisible.Should().BeTrue();
            vm.Shell.Items.Should().ContainSingle();
            var services = await vm.ServicesAsync();
            await using (var connection = services.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                (await connection.ExecuteScalarAsync<string>("select secret_value from provider_credentials where provider_id=@Provider;", new { Provider = ProviderIds.MinerU })).Should().Be("token");
            }
            var zipBytes = await File.ReadAllBytesAsync(zipPath);
            string? tokenUsed = null;
            vm.Shell.MinerUClientFactory = config =>
            {
                tokenUsed = config.Token;
                return CreateProtocolMinerUClient(config, zipBytes);
            };
            vm.Shell.MinerUToken = "";

            await vm.Shell.Items.Single().RunOcrCommand.ExecuteAsync();

            tokenUsed.Should().Be("token");
            vm.Status.Should().Contain("OCR 完成");
            vm.Shell.Items.Single().OcrStatus.Should().Contain("已索引");
            var search = await services.Mcp.SearchLibraryAsync(new McpSearchLibraryRequest("searchable"));
            search.IsSuccess.Should().BeTrue(search.ErrorMessage);
            search.Value.Results.SelectMany(r => r.MatchedUnits).Should().Contain(u => u.Text.Contains("ui selected item mineru searchable text"));
        }
        finally
        {
            if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Shell_edit_metadata_context_action_opens_item_editor_tab()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Editable Item", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();

            await vm.Shell.Items.Single().EditMetadataCommand.ExecuteAsync();
            vm.ShowItemEditorTab.Should().BeTrue();
            vm.IsItemEditorVisible.Should().BeTrue();
            vm.ItemEditor.Title.Should().Be("Editable Item");
            vm.ItemEditor.HasItem.Should().BeTrue();

            vm.ItemEditor.Title = "Edited Title";
            await vm.ItemEditor.AddCreatorCommand.ExecuteAsync();
            vm.ItemEditor.Creators.Single().Literal = "Chen, Li";
            vm.ItemEditor.IssuedDate = "2026";
            vm.ItemEditor.PublicationTitle = "Journal of Patchouli";
            await vm.ItemEditor.SaveCommand.ExecuteAsync();

            vm.Shell.Items.Single().Title.Should().Be("Edited Title");
            vm.Shell.Items.Single().Authors.Should().Be("Chen, Li");
            vm.Shell.Items.Single().Year.Should().Be("2026");
            vm.Shell.Items.Single().PublicationTitle.Should().Be("Journal of Patchouli");
            vm.ActiveTab!.Title.Should().Be("编辑题录：Edited Title");
        }
        finally
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task General_item_metadata_editor_renders_from_context_action()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-general-editor-{Guid.NewGuid():N}")).FullName;
        var path = Path.Combine(root, "runtime.sqlite");
        var pdf = Path.Combine(root, "general.pdf");
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            await session.Dispatch(async () =>
            {
                File.Copy(TestFixtures.RealThreePagePdf, pdf);
                var window = new MainWindow
                {
                    Width = 1280,
                    Height = 820
                };
                window.Show();
                try
                {
                    var vm = (MainWindowViewModel)window.DataContext!;
                    vm.RuntimeDatabasePath = path;
                    await vm.OpenDatabaseCommand.ExecuteAsync();
                    await vm.Library.CreateCommand.ExecuteAsync();
                    var services = await vm.ServicesAsync();
                    var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "General Item", null, 1));
                    import.Success.Should().BeTrue(import.ErrorMessage);
                    await vm.Shell.RefreshItemsAsync();
                    vm.Shell.Items.Single().ItemType.Should().Be("general");

                    await vm.Shell.Items.Single().EditMetadataCommand.ExecuteAsync();

                    window.Measure(new Size(1280, 820));
                    window.Arrange(new Rect(0, 0, 1280, 820));
                    var bitmap = new RenderTargetBitmap(new PixelSize(1280, 820), new Vector(96, 96));
                    bitmap.Render(window);

                    vm.ItemEditor.ItemType.Should().Be("general");
                    vm.ItemEditor.IsGeneralTypeWarningVisible.Should().BeTrue();
                    vm.ItemEditor.Fields.Should().Contain(field => field.Key == "Publisher");
                    vm.ItemEditor.Fields.Should().Contain(field => field.Key == "Pages");
                }
                finally
                {
                    window.Close();
                }

                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FirstRun_tab_stays_open_and_search_disabled_until_setup_completes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-first-run-{Guid.NewGuid():N}.sqlite");
        try
        {
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.ShowInlineFirstRunAsync();

            vm.IsFirstRunVisible.Should().BeTrue();
            vm.IsSearchEnabled.Should().BeFalse();

            await vm.CompleteFirstRunCommand.ExecuteAsync();

            vm.IsFirstRunVisible.Should().BeTrue();
            vm.IsSearchEnabled.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Reader_mode_hides_library_sidebars()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());

        vm.Shell.IsReadingMode = true;
        vm.RaiseShellSelectionChanged();

        vm.Shell.ShowLibraryList.Should().BeFalse();
        vm.ShowSidebar.Should().BeFalse();
        vm.IsInspectorVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Workspace_singleton_tabs_reuse_existing_instances()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());

        await vm.OpenSettingsAsync("mineru");
        await vm.OpenSettingsAsync("mineru");
        await vm.OpenAboutAsync();
        await vm.OpenAboutAsync();

        vm.Layout.Tabs.Count(tab => tab.Kind == WorkspaceTabKind.Settings).Should().Be(1);
        vm.Layout.Tabs.Count(tab => tab.Kind == WorkspaceTabKind.About).Should().Be(1);
        vm.ShowSettingsTab.Should().BeTrue();
        vm.ActiveTab?.Kind.Should().Be(WorkspaceTabKind.About);
    }

    [Fact]
    public async Task Workspace_closing_active_tab_falls_back_to_library_and_keeps_library_open()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());

        await vm.OpenAboutAsync();
        vm.IsLibraryTabActive.Should().BeFalse();

        await vm.CloseAboutTabCommand.ExecuteAsync();
        vm.IsLibraryTabActive.Should().BeTrue();
        vm.Layout.Tabs.Count(tab => tab.Kind == WorkspaceTabKind.Library).Should().Be(1);

        vm.Workspace.Close("Library").Should().BeFalse();
        vm.Layout.Tabs.Count(tab => tab.Kind == WorkspaceTabKind.Library).Should().Be(1);
        vm.IsLibraryTabActive.Should().BeTrue();
    }

    [Fact]
    public async Task Item_workspace_tabs_use_page_name_item_title_and_truncate_long_titles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-tab-title-{Guid.NewGuid():N}.sqlite");
        var firstPdf = Path.Combine(Path.GetTempPath(), $"ui-tab-title-{Guid.NewGuid():N}-first.pdf");
        var secondPdf = Path.Combine(Path.GetTempPath(), $"ui-tab-title-{Guid.NewGuid():N}-second.pdf");
        var longTitle = "非常长的题录标题用于验证标签页会在合理长度之后被截断而不是撑爆标签栏";
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, firstPdf);
            File.Copy(TestFixtures.RealThreePagePdf, secondPdf);
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            (await services.PdfImport.ImportPdfAsync(new PdfImportRequest(firstPdf, "短标题", null, 1))).Success.Should().BeTrue();
            (await services.PdfImport.ImportPdfAsync(new PdfImportRequest(secondPdf, longTitle, null, 1))).Success.Should().BeTrue();
            await vm.Shell.RefreshItemsAsync();

            vm.Shell.SelectedItem = vm.Shell.Items.Single(item => item.Title == "短标题");
            await vm.ShowReadingCommand.ExecuteAsync();
            var firstTab = vm.ActiveTab!;

            vm.Shell.SelectedItem = vm.Shell.Items.Single(item => item.Title == longTitle);
            await vm.ShowReadingCommand.ExecuteAsync();
            var secondTab = vm.ActiveTab!;

            firstTab.Title.Should().Be("PDF 工作台：短标题");
            secondTab.Title.Should().StartWith("PDF 工作台：");
            secondTab.Title.Should().EndWith("...");
            secondTab.Title.Length.Should().BeLessThanOrEqualTo(32);

            await vm.EditSelectedItemCommand.ExecuteAsync();
            vm.ActiveTab!.Title.Should().StartWith("编辑题录：");
            vm.ActiveTab.Title.Should().EndWith("...");
            vm.ActiveTab.Title.Length.Should().BeLessThanOrEqualTo(32);
        }
        finally
        {
            if (File.Exists(firstPdf)) File.Delete(firstPdf);
            if (File.Exists(secondPdf)) File.Delete(secondPdf);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MainWindow_close_pdf_tab_keeps_library_selection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Closable Tab", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();
            await vm.ShowReadingCommand.ExecuteAsync();
            vm.ShowSelectedDocumentTab.Should().BeTrue();
            await vm.ClosePdfWorkspaceTabCommand.ExecuteAsync();

            vm.Shell.SelectedItem.Should().NotBeNull();
            vm.ShowSelectedDocumentTab.Should().BeFalse();
            vm.IsLibraryTabActive.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MainWindow_tabs_persist_across_switches_until_closed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Persistent Tab", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();

            vm.IsLibraryTabActive.Should().BeTrue();
            vm.ShowSelectedDocumentTab.Should().BeFalse();
            vm.ShowSettingsTab.Should().BeFalse();

            await vm.ShowReadingCommand.ExecuteAsync();
            vm.ShowSelectedDocumentTab.Should().BeTrue();
            vm.IsReaderTabActive.Should().BeTrue();

            await vm.OpenSettingsAsync("mineru");
            vm.ShowSelectedDocumentTab.Should().BeTrue();
            vm.ShowSettingsTab.Should().BeTrue();
            vm.IsSettingsVisible.Should().BeTrue();

            await vm.ShowReadingCommand.ExecuteAsync();
            vm.IsReaderTabActive.Should().BeTrue();
            vm.ShowSettingsTab.Should().BeTrue();

            await vm.CloseSettingsTabCommand.ExecuteAsync();
            vm.ShowSettingsTab.Should().BeFalse();
            vm.ShowSelectedDocumentTab.Should().BeTrue();
            vm.IsReaderTabActive.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Shell_reading_mode_renders_pdf_preview_in_memory()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-preview-{Guid.NewGuid():N}")).FullName;
        var path = Path.Combine(root, "preview.sqlite");
        var pdf = Path.Combine(root, "preview.pdf");
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            await session.Dispatch(async () =>
            {
                File.Copy(TestFixtures.RealThreePagePdf, pdf);
                var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
                await vm.OpenDatabaseCommand.ExecuteAsync();
                await vm.Library.CreateCommand.ExecuteAsync();
                var services = await vm.ServicesAsync();
                var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Previewable", null, 1));
                import.Success.Should().BeTrue(import.ErrorMessage);
                await vm.Shell.RefreshItemsAsync();

                await vm.Shell.SwitchToReadingModeCommand.ExecuteAsync();
                vm.RaiseShellSelectionChanged();

                vm.Shell.ShowPdfWorkspace.Should().BeTrue();
                vm.ShowSidebar.Should().BeFalse();
                vm.IsInspectorVisible.Should().BeFalse();
                vm.PdfWorkspace.Image.Should().NotBeNull(vm.PdfWorkspace.Status);
                vm.PdfWorkspace.Status.Should().Contain("mupdf-net-dpi120");
                Directory.EnumerateFiles(root, "*.png").Should().BeEmpty();
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string CreateMinerUZip(string contentListJson)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"ui-mineru-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("sample_content_list.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contentListJson);
        return zipPath;
    }

    private static string WriteSettingsFile(string token, bool rememberLastDatabase = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"patchouli-appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
        {
          "Patchouli": {
            "RuntimeDatabasePath": "{{Path.Combine(Path.GetTempPath(), $"runtime-{Guid.NewGuid():N}.sqlite").Replace("\\", "/")}}",
            "DefaultSyncRoot": "{{Path.Combine(Path.GetTempPath(), $"sync-{Guid.NewGuid():N}").Replace("\\", "/")}}",
            "DefaultStagingRoot": "{{Path.Combine(Path.GetTempPath(), $"staging-{Guid.NewGuid():N}").Replace("\\", "/")}}",
            "LogDirectory": "{{Path.Combine(Path.GetTempPath(), $"logs-{Guid.NewGuid():N}").Replace("\\", "/")}}",
            "RememberLastDatabase": {{rememberLastDatabase.ToString().ToLowerInvariant()}},
            "UseMockOcrOnly": true
          },
          "MinerU": {
            "BaseUrl": "https://mineru.example.test",
            "ModelVersion": "vlm",
            "IsOcr": true,
            "EnableTable": true,
            "EnableFormula": true,
            "Token": "{{token}}"
          }
        }
        """);
        return path;
    }

    private static IMinerUClient CreateProtocolMinerUClient(MinerUConfiguration config, byte[] zipBytes)
    {
        return new MinerUClient(
            new HttpClient(new MinerUProtocolHandler(request =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v4/file-urls/batch")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"code":0,"data":{"batch_id":"batch-ui","file_urls":["https://upload.example.test/file"]},"msg":"ok"}""")
                    };

                if (request.Method == HttpMethod.Put && request.RequestUri!.Host == "upload.example.test")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);

                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/batch-ui")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"code":0,"data":{"batch_id":"batch-ui","extract_result":[{"file_name":"selected.pdf","state":"done","err_msg":"","full_zip_url":"https://cdn.example.test/result.zip"}]},"msg":"ok"}
                        """)
                    };

                if (request.Method == HttpMethod.Get && request.RequestUri!.Host == "cdn.example.test")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            })),
            new MinerUOptions
            {
                Token = config.Token,
                BaseUrl = "https://mineru.example.test",
                ModelVersion = config.ModelVersion ?? "vlm",
                IsOcr = config.IsOcr,
                EnableTable = config.EnableTable,
                EnableFormula = config.EnableFormula,
                PollingIntervalMs = 1,
                PollingTimeoutSeconds = 5
            });
    }

    private sealed class MinerUProtocolHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MinerUProtocolHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
