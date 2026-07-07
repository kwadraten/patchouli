using System.IO.Compression;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Mcp;
using Patchouli.Ocr.MinerU;
using Patchouli.UI;

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
        queueXaml.Should().NotContain("OCR 队列页面将在后续任务中接入");
    }

    [Fact]
    public void MainWindow_xaml_wires_evidence_markdown_export_picker()
    {
        var shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var searchXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SearchResultsPage.axaml"));
        var codeBehind = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml.cs"));
        var searchCodeBehind = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SearchResultsPage.axaml.cs"));
        shellXaml.Should().Contain("OnExportEvidenceMarkdownClick");
        searchXaml.Should().Contain("OnExportSearchUnitEvidenceMarkdownClick");
        searchXaml.Should().Contain("<ContextMenu>");
        codeBehind.Should().Contain("SaveFilePickerAsync");
        codeBehind.Should().Contain("ExportEvidenceMarkdownToFileAsync");
        searchCodeBehind.Should().Contain("SaveFilePickerAsync");
        searchCodeBehind.Should().Contain("ExportEvidenceMarkdownToFileAsync");
    }

    [Fact]
    public void MainWindow_xaml_wires_item_editor_and_settings_sections()
    {
        var shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var editorXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "ItemEditorPage.axaml"));
        var settingsXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml"));
        shellXaml.Should().Contain("ItemEditor.Header");
        shellXaml.Should().Contain("ItemEditorPage");
        editorXaml.Should().Contain("AddIdentifierCommand");
        editorXaml.Should().Contain("RegisterFileCommand");
        settingsXaml.Should().Contain("FileSearchRoot");
        settingsXaml.Should().Contain("OCR 预设");
        settingsXaml.Should().Contain("搜索配置");
        settingsXaml.Should().Contain("MCP");
    }

    [Fact]
    public async Task ExportEvidenceMarkdownCommand_without_evidence_ref_reports_validation_error()
    {
        var vm = new MainWindowViewModel(new FakeClipboard());

        await vm.ExportEvidenceMarkdownCommand.ExecuteAsync();

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
            await vm.ServicesAsync();

            vm.McpEndpoint.Should().Be($"http://localhost:{port}");
            vm.McpStatusText.Should().Be("MCP: 运行中");
            vm.McpStatusDetail.Should().Contain(vm.McpEndpoint);
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
        }
        finally
        {
            if (File.Exists(pdf)) File.Delete(pdf);
            if (File.Exists(path)) File.Delete(path);
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

    private static string WriteSettingsFile(string token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"patchouli-appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
        {
          "Patchouli": {
            "RuntimeDatabasePath": "{{Path.Combine(Path.GetTempPath(), $"runtime-{Guid.NewGuid():N}.sqlite").Replace("\\", "/")}}",
            "DefaultSyncRoot": "{{Path.Combine(Path.GetTempPath(), $"sync-{Guid.NewGuid():N}").Replace("\\", "/")}}",
            "DefaultStagingRoot": "{{Path.Combine(Path.GetTempPath(), $"staging-{Guid.NewGuid():N}").Replace("\\", "/")}}",
            "LogDirectory": "{{Path.Combine(Path.GetTempPath(), $"logs-{Guid.NewGuid():N}").Replace("\\", "/")}}",
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
