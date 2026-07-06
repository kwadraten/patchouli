using System.IO.Compression;
using Avalonia;
using Avalonia.Headless;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.Ocr.MinerU;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class UiViewModelTests
{
    static UiViewModelTests()
    {
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
    }
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
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
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
            await File.WriteAllTextAsync(pdf, "%PDF-1.4");
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
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        xaml.Should().Contain("RunToolbarSearchCommand");
        xaml.Should().Contain("搜索结果");
        xaml.Should().Contain("SearchEvidence.Query");
    }

    [Fact]
    public void MainWindow_xaml_wires_ocr_queue_workspace()
    {
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        xaml.Should().Contain("ShowOcrQueueWorkspace");
        xaml.Should().Contain("OpenOcrQueueCommand");
        xaml.Should().Contain("StartCommand");
        xaml.Should().Contain("PauseGlobalCommand");
        xaml.Should().Contain("CancelCommand");
        xaml.Should().NotContain("OCR 队列页面将在后续任务中接入");
    }

    [Fact]
    public void MainWindow_xaml_wires_evidence_markdown_export_picker()
    {
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        var codeBehind = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml.cs"));
        xaml.Should().Contain("OnExportEvidenceMarkdownClick");
        xaml.Should().Contain("OnExportSearchUnitEvidenceMarkdownClick");
        xaml.Should().Contain("<ContextMenu>");
        codeBehind.Should().Contain("SaveFilePickerAsync");
        codeBehind.Should().Contain("ExportEvidenceMarkdownToFileAsync");
    }

    [Fact]
    public void MainWindow_xaml_wires_item_editor_and_settings_sections()
    {
        var xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        xaml.Should().Contain("ShowItemEditorWorkspace");
        xaml.Should().Contain("ItemEditor.Header");
        xaml.Should().Contain("AddIdentifierCommand");
        xaml.Should().Contain("RegisterFileCommand");
        xaml.Should().Contain("FileSearchRoot");
        xaml.Should().Contain("OCR 预设");
        xaml.Should().Contain("搜索配置");
        xaml.Should().Contain("MCP");
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
            await File.WriteAllTextAsync(pdf, "%PDF-1.4");
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
            vm.Settings.SelectedSection.Should().Be("mineru");
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
            await File.WriteAllTextAsync(Path.Combine(scanRoot, "alpha.pdf"), "%PDF-1.4");
            await File.WriteAllTextAsync(Path.Combine(scanRoot, "beta.pdf"), "%PDF-1.4");
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
            await File.WriteAllTextAsync(Path.Combine(scanRoot, "selected.pdf"), "%PDF-1.4");
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
            string? tokenUsed = null;
            vm.Shell.MinerUClientFactory = config => { tokenUsed = config.Token; return new FakeMinerUClient(zipPath); };
            vm.Shell.MinerUToken = "";

            await vm.Shell.Items.Single().RunOcrCommand.ExecuteAsync();

            tokenUsed.Should().Be("token");
            vm.Status.Should().Contain("MCP verification passed");
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
    public async Task Shell_edit_metadata_updates_selected_item_after_context_action()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllTextAsync(pdf, "%PDF-1.4");
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, null, null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();

            await vm.Shell.Items.Single().EditMetadataCommand.ExecuteAsync();
            vm.Shell.ShowMetadataEditor.Should().BeTrue();
            vm.Shell.EditTitle = "Edited Title";
            vm.Shell.EditAuthors = "Chen, Li";
            vm.Shell.EditYear = "2026";
            vm.Shell.EditPublicationTitle = "Journal of Patchouli";
            await vm.Shell.SaveMetadataCommand.ExecuteAsync();

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
    public async Task Shell_close_document_tab_clears_selected_item()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        var pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllTextAsync(pdf, "%PDF-1.4");
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Closable Tab", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();

            vm.ShowSelectedDocumentTab.Should().BeTrue();
            await vm.Shell.CloseDocumentTabCommand.ExecuteAsync();

            vm.Shell.SelectedItem.Should().BeNull();
            vm.ShowSelectedDocumentTab.Should().BeFalse();
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
        try
        {
            await File.WriteAllTextAsync(pdf, CreateMinimalPdf());
            var vm = new MainWindowViewModel(new FakeClipboard()) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            var services = await vm.ServicesAsync();
            var import = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Previewable", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();

            await vm.Shell.SwitchToReadingModeCommand.ExecuteAsync();

            vm.Shell.ShowPdfReader.Should().BeTrue();
            vm.PdfPreview.Image.Should().NotBeNull(vm.PdfPreview.Status);
            vm.PdfPreview.Status.Should().Contain("mupdf-net-dpi120");
            Directory.EnumerateFiles(root, "*.png").Should().BeEmpty();
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

    private static string CreateMinimalPdf()
    {
        var objects = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << >> >>\nendobj\n"
        };
        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var item in objects)
        {
            offsets.Add(System.Text.Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(item);
        }

        var xref = System.Text.Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return builder.ToString();
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

    private sealed class FakeMinerUClient : IMinerUClient
    {
        private readonly string _zipPath;
        public FakeMinerUClient(string zipPath) => _zipPath = zipPath;
        public bool IsConfigured => true;
        public Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(IReadOnlyList<MinerUUploadRequest> files, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinerUUploadBatch>.Success(new MinerUUploadBatch("batch-ui", [new MinerUFileUploadUrl(files[0].FileName, "https://upload.example.test/file", "file-ui")])));
        public Task<Result> UploadFileAsync(string uploadUrl, string localPath, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinerUPollResult>.Success(new MinerUPollResult(batchId, MinerUProviderStatus.Done, "https://download.example.test/result.zip", null)));
        public Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(string batchId, string downloadDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinerUDownloadedResult>.Success(new MinerUDownloadedResult(batchId, _zipPath, MinerUProviderStatus.Done)));
    }
}
