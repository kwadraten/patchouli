using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Shell;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Editor;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

public sealed class UiViewModelTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    private MainWindowViewModel CreateMainWindow(IClipboardService? clipboard = null, IAppLogger? logger = null,
        bool autoStartMcpServer = false, int mcpPort = 4536)
    {
        return new MainWindowViewModel(clipboard, logger, autoStartMcpServer: autoStartMcpServer, mcpPort: mcpPort,
            settingsPath: _settings.Path);
    }

    private MainWindow CreateMainWindowShell(IClipboardService? clipboard = null, bool autoStartMcpServer = false,
        int mcpPort = 4536)
    {
        return new MainWindow(CreateMainWindow(clipboard, autoStartMcpServer: autoStartMcpServer, mcpPort: mcpPort));
    }

    private static MainWindowViewModel WithRuntimeDatabasePath(MainWindowViewModel viewModel, string path)
    {
        viewModel.RuntimeDatabasePath = path;
        return viewModel;
    }

    [Fact]
    public void Toolbar_uri_parser_recognizes_protocol_case_and_markdown_links()
    {
        string itemId = ItemId.New().ToString();
        PatchouliNavigationParseResult parsed =
            PatchouliUriNavigationParser.ParseInput($"引用：[题录](PATCHOULI://ITEMS/{itemId}.bib)");

        parsed.HasProtocolPrefix.Should().BeTrue();
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        parsed.Target!.Kind.Should().Be(PatchouliNavigationKind.Item);
        parsed.Target.ResourceId.Should().Be(itemId);

        PatchouliUriNavigationParser.ParseInput("patchouli knowledge")
            .HasProtocolPrefix.Should().BeFalse();
        PatchouliUriNavigationParser.ParseInput(
                $"patchouli://texts/{DocumentInstanceId.New()}/page-0.md?evref=one&evref=two")
            .IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Biblatex_conflict_dialog_is_localized_and_formats_structured_values()
    {
        const string creators =
            """[{"role":"author","family":"司门","given":"西门","literal":null,"suffix":null,"particles":null}]""";
        const string dates =
            """[{"role":"issued","date_parts_json":"[[2019]]","circa":false,"season":null,"literal":null}]""";
        ConflictResolutionDialogViewModel vm = new(
            Infrastructure.Conflicts.ConflictDescriptorMapper.BiblatexItemFieldConflict(
                ItemId.New().ToString(),
                "source",
                [
                    ("item_type", "题录类型", "general", "book"),
                    ("creators", "责任者", null, creators),
                    ("dates", "日期", null, dates)
                ]));

        vm.Title.Should().Be("处理题录字段冲突");
        vm.Title.Should().NotContain("CF-");
        vm.ConflictDescription.Should().Be("导入的 BibLaTeX 字段与目标题录不同，请逐项选择处理方式。");
        vm.Severity.Should().Be("必须处理");
        vm.Actions.Select(static action => action.Label).Should().Equal("应用字段选择", "暂不处理");
        vm.FieldChoices.Single(choice => choice.FieldKey == "item_type").LocalValue.Should().Be("通用");
        vm.FieldChoices.Single(choice => choice.FieldKey == "item_type").IncomingValue.Should().Be("图书");
        vm.FieldChoices.Single(choice => choice.FieldKey == "creators").IncomingValue.Should().Be("作者：西门 司门");
        vm.FieldChoices.Single(choice => choice.FieldKey == "dates").IncomingValue.Should().Be("出版日期：2019");
        vm.FieldChoices.SelectMany(static choice => new[] { choice.LocalValue, choice.IncomingValue })
            .Should().NotContain(value => value.Contains("\"role\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Biblatex_conflict_dialog_uses_non_overlapping_choice_cards()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "ConflictResolutionDialog.axaml"));

        xaml.Should().Contain("Content=\"保留本地值\"");
        xaml.Should().Contain("Content=\"采用导入值\"");
        xaml.Should().Contain("<Grid Grid.Row=\"1\" ColumnDefinitions=\"*,*\"");
        xaml.Should().Contain("<RadioButton Grid.Row=\"0\" Content=\"保留本地值\"");
        xaml.Should().Contain("<TextBlock Grid.Row=\"1\" Text=\"{Binding LocalValue}\"");
        xaml.Should().NotContain("Background=\"{DynamicResource PrimaryContainerBrush}\"");
        xaml.Should().NotContain("Foreground=\"{DynamicResource OnPrimaryBrush}\"");
        xaml.Should().NotContain("Content=\"Choose fields\"");
    }

    [Fact]
    public async Task Single_biblatex_entry_skips_selection_and_opens_conflict_handling()
    {
        if (!File.Exists(BiblatexHelperClient.ResolveDefaultHelperPath()))
        {
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), $"ui-biblatex-single-{Guid.NewGuid():N}.sqlite");
        CapturingDialogService dialogs = new();
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), dialogs: dialogs, settingsPath: _settings.Path)
                { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            Result<ItemMetadata> created =
                await (await vm.ServicesAsync()).Items.CreateItemAsync("book", "本地标题");
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await vm.ImportBiblatexTextIntoEditorAsync(
                """
                @book{only-entry,
                  author = {Doe, Jane},
                  title = {导入标题},
                  publisher = {Test Press},
                  date = {2020}
                }
                """,
                null,
                created.Value.ItemId);

            dialogs.ViewModels.Should().ContainSingle("当前状态：{0}", vm.Status);
            dialogs.ViewModels.Single().Should().BeOfType<ConflictResolutionDialogViewModel>();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Toolbar_patchouli_uri_opens_item_and_zero_based_text_page()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-uri-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-uri-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            PdfImportResult imported =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "URI navigation", null, 3));
            imported.Success.Should().BeTrue(imported.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();

            vm.SearchEvidence.Query =
                $"PATCHOULI://TEXTS/{imported.CreatedDocumentInstanceId}/page-1.md";
            await vm.RunToolbarSearchCommand.ExecuteAsync();

            vm.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.PdfWorkspace);
            ((PdfWorkspaceViewModel)vm.ActiveTab.Content).PageNumberText.Should().Be("2");

            vm.SearchEvidence.Query =
                $"[URI navigation](patchouli://items/{imported.CreatedItemId}.bib)";
            await vm.RunToolbarSearchCommand.ExecuteAsync();

            vm.ActiveTab.Kind.Should().Be(WorkspaceTabKind.ItemEditor);
            vm.ActiveTab.TabId.Should().Be($"ItemEditor_{imported.CreatedItemId}");
        }
        finally
        {
            if (File.Exists(pdf))
            {
                File.Delete(pdf);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Settings_page_uses_five_editable_groups_and_keeps_csl_about_outside()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());
        vm.Settings.Categories.Select(category => category.Title).Should().Equal(
            "库与本机路径", "同步与快照", "MCP 服务与安全", "OCR 引擎", "元数据来源");
        vm.Settings.Categories.Select(category => category.Section is { SupportsEditing: true }).Should()
            .OnlyContain(value => value);
    }

    [Fact]
    public void Sync_settings_expose_a_persisted_device_identity_and_name()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());

        vm.Settings.Categories.Single(category => category.Title == "同步与快照").Section
            .Should().BeOfType<SyncSettingsViewModel>();
        SyncSettingsViewModel sync = (SyncSettingsViewModel)vm.Settings.Categories
            .Single(category => category.Title == "同步与快照").Section!;

        sync.DeviceId.Should().NotBeNullOrWhiteSpace();
        sync.DeviceName.Should().NotBeNullOrWhiteSpace();
        sync.DeviceId.Should().Be(vm.AppOptions.Sync.DeviceId);
    }

    [Fact]
    public async Task Sync_menu_command_opens_the_sync_center_without_development_path_inputs()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-sync-ui-{Guid.NewGuid():N}");
        string settingsPath = Path.Combine(root, "settings.json");
        try
        {
            AppRuntimeOptions runtime = PatchouliAppSettings.Default().Runtime with
            {
                RuntimeDatabasePath = Path.Combine(root, "runtime.sqlite"),
                DefaultSyncRoot = Path.Combine(root, "sync"),
                DefaultStagingRoot = Path.Combine(root, "staging"),
                LogDirectory = Path.Combine(root, "logs")
            };
            PatchouliAppSettings settings = PatchouliAppSettings.Default() with
            {
                Runtime = runtime,
                Sync = new SyncAppSettings(
                    "device-a",
                    "Test device",
                    runtime.DefaultSyncRoot,
                    false,
                    "sync-root-a")
            };
            settings.Save(settingsPath).IsSuccess.Should().BeTrue();
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath);

            await vm.CheckSyncStateCommand.ExecuteAsync();

            vm.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.SyncCenter);
            vm.ActiveTab.Content.Should().BeSameAs(vm.Snapshot);
            typeof(SnapshotViewModel).GetProperty("SyncRoot").Should().BeNull();
            typeof(SnapshotViewModel).GetProperty("StagingRoot").Should().BeNull();
            typeof(SnapshotViewModel).GetProperty("DeviceId").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void Sync_center_page_shows_library_device_branch_details_and_path_pickers()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SyncCenterPage.axaml"));

        xaml.Should().Contain("Text=\"{Binding LibrarySummary}\"");
        xaml.Should().Contain("Text=\"{Binding DeviceSummary}\"");
        xaml.Should().Contain("Text=\"{Binding BranchDetailSummary}\"");
        xaml.Should().Contain("Text=\"{Binding LastErrorText}\"");
        xaml.Split("<controls:PathPickerTextBox").Length.Should().Be(4);
        xaml.Should().NotContain("<TextBox Text=\"{Binding ExportDestinationDirectory}\"");
        xaml.Should().NotContain("<TextBox Text=\"{Binding PackageManifestPath}\"");
        xaml.Should().NotContain("<TextBox Text=\"{Binding IncomingCopyDestinationPath}\"");
    }

    [Fact]
    public void Sync_settings_page_offers_export_snapshot_package_button()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml"));

        xaml.Should().Contain("Command=\"{Binding ExportSnapshotPackageCommand}\"");
    }

    [Fact]
    public async Task Sync_center_presents_device_and_branch_details_and_enabled_descriptors()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-sync-ui-{Guid.NewGuid():N}");
        string settingsPath = Path.Combine(root, "settings.json");
        try
        {
            AppRuntimeOptions runtime = PatchouliAppSettings.Default().Runtime with
            {
                RuntimeDatabasePath = Path.Combine(root, "runtime.sqlite"),
                DefaultSyncRoot = Path.Combine(root, "sync"),
                DefaultStagingRoot = Path.Combine(root, "staging"),
                LogDirectory = Path.Combine(root, "logs")
            };
            PatchouliAppSettings settings = PatchouliAppSettings.Default() with
            {
                Runtime = runtime,
                Sync = new SyncAppSettings(
                    "device-a",
                    "Test device",
                    runtime.DefaultSyncRoot,
                    false,
                    "sync-root-a")
            };
            settings.Save(settingsPath).IsSuccess.Should().BeTrue();
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath);

            await vm.OpenSyncCenterAsync();

            vm.Snapshot.DeviceSummary.Should().Contain("Test device");
            vm.Snapshot.DeviceSummary.Should().Contain("device-a");
            vm.Snapshot.LibrarySummary.Should().NotBeNullOrWhiteSpace();
            vm.Snapshot.BranchDetailSummary.Should().NotBeNullOrWhiteSpace();
            vm.Snapshot.HasLastError.Should().BeFalse();
            vm.PublishSnapshotDescriptor.Enabled.Should().BeTrue();
            vm.PublishSnapshotDescriptor.DisabledReason.Should().BeEmpty();
            vm.ReceiveSnapshotDescriptor.Enabled.Should().BeTrue();
            vm.ExportSnapshotPackageDescriptor.Enabled.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Publish_snapshot_menu_descriptor_opens_sync_center_first()
    {
        string root = Path.Combine(Path.GetTempPath(), $"patchouli-sync-ui-{Guid.NewGuid():N}");
        string settingsPath = Path.Combine(root, "settings.json");
        try
        {
            AppRuntimeOptions runtime = PatchouliAppSettings.Default().Runtime with
            {
                RuntimeDatabasePath = Path.Combine(root, "runtime.sqlite"),
                DefaultSyncRoot = Path.Combine(root, "sync"),
                DefaultStagingRoot = Path.Combine(root, "staging"),
                LogDirectory = Path.Combine(root, "logs")
            };
            PatchouliAppSettings settings = PatchouliAppSettings.Default() with
            {
                Runtime = runtime,
                Sync = new SyncAppSettings(
                    "device-a",
                    "Test device",
                    runtime.DefaultSyncRoot,
                    false,
                    "sync-root-a")
            };
            settings.Save(settingsPath).IsSuccess.Should().BeTrue();
            MainWindowViewModel vm = new(new FakeClipboard(), dialogs: new FakeDialogService(),
                settingsPath: settingsPath);

            await vm.PublishSnapshotDescriptor.ExecuteAsync();

            vm.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.SyncCenter);
            vm.ActiveTab.Content.Should().BeSameAs(vm.Snapshot);
            vm.Snapshot.OperationMessage.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task LibraryViewModel_CreateLibrary_updates_current_library()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(), path);
            vm.SettingsFilePath.Should().Be(_settings.Path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            vm.Library.DisplayName = "UI Library";
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.Library.Details.Should().Contain("UI Library");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task OpenDatabaseCommand_reports_unsupported_schema_in_status_bar()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-legacy-{Guid.NewGuid():N}.sqlite");
        try
        {
            await using (SqliteConnection connection = new($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    """
                    create table library_metadata (
                        library_id text primary key,
                        display_name text not null,
                        schema_version integer not null,
                        created_at text not null,
                        updated_at text not null
                    );
                    insert into library_metadata values ('legacy', 'Legacy', 1, 'now', 'now');
                    create table layout_nodes (node_id text primary key);
                    """);
            }

            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);

            await vm.OpenDatabaseCommand.ExecuteAsync();

            vm.StatusIsError.Should().BeTrue();
            vm.Status.Should().Contain("不受 Patchouli 0.2.4 支持");
            vm.Status.Should().Contain("schema epoch（1）");
            vm.Status.Should().Contain("请新建资料库并重新导入源文档");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task BibliographyViewModel_CreateItem_returns_item()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.Bibliography.Title = "UI Item";
            await vm.Bibliography.CreateItemCommand.ExecuteAsync();
            vm.Bibliography.Output.Should().Contain("UI Item");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task FileDocumentViewModel_RegisterMissingFile_creates_missing_asset()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.FileDocument.FilePath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pdf");
            await vm.FileDocument.RegisterCommand.ExecuteAsync();
            vm.FileDocument.Output.Should().Contain("missing");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task CopyEvidenceMarkdown_writes_pinned_markdown_to_clipboard()
    {
        FakeClipboard clipboard = new();
        MainWindowViewModel vm = CreateMainWindow(clipboard);
        vm.SearchEvidence.Markdown = "Pinned source text";
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        clipboard.Text.Should().Be("Pinned source text");
        vm.SearchEvidence.Output.Should().Be("Copied Evidence Markdown");
    }

    [Fact]
    public async Task CopyEvidenceMarkdown_without_markdown_returns_validation_error()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        vm.SearchEvidence.Output.Should().Contain("validation_failed");
    }

    [Fact]
    public async Task CopyEvidenceRef_writes_ref_to_clipboard()
    {
        FakeClipboard clipboard = new();
        MainWindowViewModel vm = CreateMainWindow(clipboard);

        await vm.SearchEvidence.CopyEvidenceRefAsync("evref:v1:test");

        clipboard.Text.Should().Be("evref:v1:test");
        vm.SearchEvidence.EvidenceRef.Should().Be("evref:v1:test");
        vm.SearchEvidence.Output.Should().Be("Copied EvidenceRef");
    }

    [Fact]
    public async Task CopySearchResultEvidenceMarkdown_creates_search_unit_evidence_lazily()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-evidence-copy-{Guid.NewGuid():N}.sqlite");
        FakeClipboard clipboard = new();
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(clipboard), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            Result<ItemMetadata> item = await services.Items.CreateItemAsync("book", "UI Evidence Item");
            Result<DocumentInstance> document =
                await services.Documents.AttachDocumentInstanceAsync(item.Value.ItemId, null,
                    DocumentInstanceType.PrimaryScan);
            Result<Page> page = await services.Pages.CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null,
                null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                document.Value.DocumentInstanceId, page.Value.PageId, "Pinned clipboard text");
            await services.SearchUnits.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);

            await using SqliteConnection connection = services.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            string? unitId =
                await connection.ExecuteScalarAsync<string>(
                    "select unit_id from search_units where resolved_text = 'Pinned clipboard text';");
            SearchMatchedUnitViewModel unit = new(unitId!, "Pinned clipboard text", DocumentBoxType.Text, 1, true,
                null);

            await vm.SearchEvidence.CopyEvidenceMarkdownForSearchUnitAsync(unit);

            unit.EvidenceRef.Should().StartWith("evref:v2:");
            clipboard.Text.Should().Contain("Pinned clipboard text").And.Contain("UI Evidence Item").And
                .Contain(unit.EvidenceRef);
            vm.SearchEvidence.Markdown.Should().Be(clipboard.Text);
            vm.SearchEvidence.Output.Should().Be("Copied Evidence Markdown");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task McpPreviewViewModel_SafetyCheck_flags_specific_local_path()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());
        vm.McpPreview.Output = "{\"bad\":\"/tmp/private.sqlite\"}";
        vm.McpPreview.SpecificPath = "/tmp/private.sqlite";
        await vm.McpPreview.SafetyCommand.ExecuteAsync();
        vm.McpPreview.Safety.Should().Contain("Warning");
    }

    [Fact]
    public async Task McpPreviewViewModel_SafetyCheck_passes_clean_output()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());
        vm.McpPreview.Output = "{\"title\":\"A historical source\"}";
        await vm.McpPreview.SafetyCommand.ExecuteAsync();
        vm.McpPreview.Safety.Should().Be("No obvious local path or secret exposure detected.");
    }

    [Fact]
    public void MainWindow_xaml_does_not_use_invalid_none_brush()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        xaml.Should().NotContain("Fill=\"None\"");
        xaml.Should().NotContain("Stroke=\"None\"");
        xaml.Should().NotContain("Background=\"None\"");
        xaml.Should().NotContain("BorderBrush=\"None\"");
        xaml.Should().NotContain("Foreground=\"None\"");
    }

    [Fact]
    public void LucideIcon_renders_svg_resource_without_external_package()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        session.Dispatch(() =>
        {
            Lucide.Avalonia.Lucide icon = new()
            {
                Icon = "Search",
                Width = 24,
                Height = 24,
                StrokeBrush = Brushes.Black
            };

            icon.Measure(new Size(24, 24));
            icon.Arrange(new Rect(0, 0, 24, 24));

            RenderTargetBitmap bitmap = new(new PixelSize(24, 24), new Vector(96, 96));
            bitmap.Render(icon);
        }, CancellationToken.None);
    }

    [Fact]
    public void MainWindow_constructs_with_local_lucide_icons()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        session.Dispatch(() =>
        {
            MainWindow window = CreateMainWindowShell();
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_new_item_editor_renders_without_recursive_templates()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindow window = CreateMainWindowShell();
            window.Width = 1280;
            window.Height = 820;
            window.Show();
            try
            {
                MainWindowViewModel vm = (MainWindowViewModel)window.DataContext!;

                await vm.CreateItemMenuCommand.ExecuteAsync();

                window.Measure(new Size(1280, 820));
                window.Arrange(new Rect(0, 0, 1280, 820));

                RenderTargetBitmap bitmap = new(new PixelSize(1280, 820), new Vector(96, 96));
                bitmap.Render(window);

                vm.IsItemEditorVisible.Should().BeTrue();
                await vm.StopMcpServerAsync();
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_returns_to_rendered_library_after_switching_tabs()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindow window = CreateMainWindowShell();
            window.Width = 1280;
            window.Height = 820;
            window.Show();
            try
            {
                MainWindowViewModel vm = (MainWindowViewModel)window.DataContext!;
                vm.Shell.IsReadingMode = true;
                await vm.OpenAboutAsync();

                await vm.ShowLibraryCommand.ExecuteAsync();
                window.Measure(new Size(1280, 820));
                window.Arrange(new Rect(0, 0, 1280, 820));
                using RenderTargetBitmap bitmap = new(new PixelSize(1280, 820), new Vector(96, 96));
                bitmap.Render(window);

                vm.ActiveTab!.Kind.Should().Be(WorkspaceTabKind.Library);
                vm.Shell.IsReadingMode.Should().BeFalse();
                window.GetVisualDescendants().OfType<UI.Views.LibraryPage>().Should().ContainSingle();
                await vm.StopMcpServerAsync();
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
        string editorXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "ItemEditorPage.axaml"));

        editorXaml.Should().NotContain("ContentControl Content=\"{Binding}\"");
    }

    [Fact]
    public void Metadata_lookup_ui_wires_identifier_rows_batch_selection_and_progress()
    {
        string editorXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "ItemEditorPage.axaml"));
        string mainXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        string libraryXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        string libraryCode =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml.cs"));

        editorXaml.Should().Contain("editor:IdentifierItemViewModel").And.Contain("LookupCommand").And
            .Contain("IsBusy");
        editorXaml.Should().Contain("RemoveCommand").And.Contain("Content=\"移除\"");
        libraryXaml.Should().Contain("SelectionMode=\"Extended\"").And.Contain("OnDataGridSelectionChanged");
        libraryXaml.Should().NotContain("<Button Content=\"获取所选元数据\"");
        mainXaml.Should().Contain("获取所选题录元数据").And.Contain("Shell.LookupMetadataBatchCommand").And
            .Contain("Shell.CancelMetadataBatchCommand");
        libraryXaml.Should().Contain("获取所选题录元数据").And.Contain("LookupMetadataBatchCommand").And
            .Contain("CancelMetadataBatchCommand");
        libraryCode.Should().Contain("grid.SelectedItems").And.Contain("SetSelectedItems").And
            .Contain("SyncSelectionFromViewModel");
    }

    [Fact]
    public void MainWindow_xaml_avoids_recursive_theme_and_local_self_styles()
    {
        string shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));

        shellXaml.Should().NotContain("Theme=\"{StaticResource {x:Type TabControl}}\"");
        shellXaml.Should().NotContain("<TextBlock.Styles>");
    }

    [Fact]
    public void MainWindow_xaml_uses_local_lucide_svg_control()
    {
        string project = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Patchouli.UI.csproj"));
        string packages = File.ReadAllText(TestPaths.FromRepositoryRoot("Directory.Packages.props"));
        string mainXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        string libraryXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        string pdfXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));

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
        string assetsPath = TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Assets", "Lucide");
        HashSet<string> assets = Directory.EnumerateFiles(assetsPath, "*.svg")
            .Select(path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant())
            .ToHashSet();

        HashSet<string> iconNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(TestPaths.FromRepositoryRoot("src", "Patchouli.UI"), "*.axaml",
                     SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text,
                         "lucide:Lucide[^>]*\\bIcon=\"([^\"]+)\""))
            {
                string icon = match.Groups[1].Value;
                if (!icon.StartsWith("{Binding", StringComparison.Ordinal))
                {
                    iconNames.Add(icon);
                }
            }
        }

        string settingsText = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "ViewModels",
            "Settings", "SettingsViewModel.cs"));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     settingsText, "new\\(\"[^\"]+\",\\s*\"([^\"]+)\""))
        {
            iconNames.Add(match.Groups[1].Value);
        }

        iconNames.Select(ToKebab).Should().OnlyContain(icon => assets.Contains(icon));

        static string ToKebab(string value)
        {
            StringBuilder builder = new(value.Length + 4);
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (current is '_' or '-' or ' ')
                {
                    AppendDash(builder);
                    continue;
                }

                if (i > 0 && (char.IsUpper(current) || char.IsDigit(current)) && builder.Length > 0 &&
                    builder[^1] != '-')
                {
                    AppendDash(builder);
                }

                builder.Append(char.ToLowerInvariant(current));
            }

            return builder.ToString();
        }

        static void AppendDash(StringBuilder builder)
        {
            if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
    }

    [Fact]
    public void MainWindow_xaml_uses_menu_shell_without_legacy_developer_tools_or_token_prompt()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        xaml.Should().Contain("<Menu");
        xaml.Should().NotContain("Developer Tools");
        xaml.Should().NotContain("ShowMinerUTokenPrompt");
    }

    [Fact]
    public void MainWindow_xaml_sidebar_uses_real_path_bindings()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        xaml.Should().Contain("DefaultSyncRootPath");
        xaml.Should().Contain("FileSearchRoots");
        xaml.Should().NotContain("/Documents/Papers");
        xaml.Should().NotContain("/Downloads/Scan");
        xaml.Should().NotContain("WPS Drive");
        xaml.Should().NotContain("最近更改");
        xaml.Should().NotContain("回收站");
    }

    [Fact]
    public void Library_shell_exposes_sidebar_menu_and_single_empty_inspector_state()
    {
        string mainXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        string libraryXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        string settingsXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml"));
        mainXaml.Should().Contain("Header=\"侧边栏\"");
        mainXaml.Should().Contain("Header=\"左侧边栏\"");
        mainXaml.Should().Contain("Header=\"右侧边栏\"");
        mainXaml.Should().Contain("ShowLibraryLeftSidebarPreference");
        mainXaml.Should().Contain("ShowLibraryRightSidebarPreference");
        mainXaml.Should().NotContain("切换详情面板");
        mainXaml.Should().NotContain("显示左侧边栏");
        libraryXaml.Split("未选择题录").Length.Should().Be(2);
        libraryXaml.Should().Contain("RescanFileSearchRootsCommand");
        libraryXaml.Should().Contain("重新扫描");
        libraryXaml.Should().NotContain("重新搜索");
        settingsXaml.Should().Contain("重新扫描");
        settingsXaml.Should().Contain("DeleteCommand");
        settingsXaml.Should().Contain("settings:FileSearchRootSettingsRowViewModel");
        settingsXaml.Should().NotContain("重新搜索");
    }

    [Fact]
    public async Task MainWindowViewModel_keeps_sidebar_menu_preferences_when_settings_tab_is_active()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"patchouli-sidebar-settings-{Guid.NewGuid():N}.json");
        try
        {
            PatchouliAppSettings.Default().Save(settingsPath);
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath);
            await vm.OpenSettingsCommand.ExecuteAsync();

            vm.ShowLibraryLeftSidebarPreference.Should().BeTrue();
            vm.ShowLibraryRightSidebarPreference.Should().BeTrue();

            vm.ShowLibraryLeftSidebarPreference = false;
            vm.ShowLibraryRightSidebarPreference = false;

            vm.ShowLibraryLeftSidebarPreference.Should().BeFalse();
            vm.ShowLibraryRightSidebarPreference.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task Library_sidebars_reopen_after_switching_tabs()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());
        List<string?> shellChanges = new();
        vm.Shell.PropertyChanged += (_, args) => shellChanges.Add(args.PropertyName);

        vm.Shell.IsLibraryLeftSidebarVisible.Should().BeTrue();
        vm.Shell.IsLibraryRightSidebarVisible.Should().BeTrue();

        await vm.OpenAboutAsync();
        vm.Shell.IsLibraryLeftSidebarVisible.Should().BeFalse();
        vm.Shell.IsLibraryRightSidebarVisible.Should().BeFalse();

        vm.Workspace.ActivateKind(WorkspaceTabKind.Library).Should().BeTrue();
        vm.Shell.IsLibraryLeftSidebarVisible.Should().BeTrue();
        vm.Shell.IsLibraryRightSidebarVisible.Should().BeTrue();
        shellChanges.Should().Contain(nameof(LibraryShellViewModel.IsLibraryLeftSidebarVisible));
        shellChanges.Should().Contain(nameof(LibraryShellViewModel.IsLibraryRightSidebarVisible));

        shellChanges.Clear();
        vm.ShowLibraryLeftSidebarPreference = false;
        vm.ShowLibraryRightSidebarPreference = false;
        vm.Shell.IsLibraryLeftSidebarVisible.Should().BeFalse();
        vm.Shell.IsLibraryRightSidebarVisible.Should().BeFalse();

        vm.ShowLibraryLeftSidebarPreference = true;
        vm.ShowLibraryRightSidebarPreference = true;
        vm.Shell.IsLibraryLeftSidebarVisible.Should().BeTrue();
        vm.Shell.IsLibraryRightSidebarVisible.Should().BeTrue();
        shellChanges.Should().Contain(nameof(LibraryShellViewModel.IsLibraryLeftSidebarVisible));
        shellChanges.Should().Contain(nameof(LibraryShellViewModel.IsLibraryRightSidebarVisible));
    }

    [Fact]
    public async Task BlockingOperationDialog_only_closes_after_operation_reaches_terminal_state()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views",
                "BlockingOperationDialog.axaml"));
        BlockingOperationDialogViewModel vm = new();
        object? closeResult = new();
        vm.RequestClose = result => closeResult = result;

        await vm.ConfirmCommand.ExecuteAsync();
        closeResult.Should().NotBeNull();

        vm.MarkCompleted();
        await vm.ConfirmCommand.ExecuteAsync();

        xaml.Should().Contain("Content=\"关闭\"");
        xaml.Should().Contain("CancelCommand");
        xaml.Should().Contain("Content=\"取消\"");
        xaml.Should().Contain("IsVisible=\"{Binding CanCancel}\"");
        xaml.Should().Contain("ConfirmCommand");
        xaml.Should().Contain("ToggleDetailsCommand");
        xaml.Should().Contain("Height=\"150\"");
        xaml.Should().Contain("Text=\"{Binding DetailedResult}\"");
        xaml.Should().NotContain("RecoveryGuidance");
        vm.DetailsToggleText.Should().Be("显示详细信息");
        await vm.ToggleDetailsCommand.ExecuteAsync();
        vm.IsDetailsVisible.Should().BeTrue();
        vm.DetailsToggleText.Should().Be("隐藏详细信息");
        vm.OperationState.Should().Be("已成功");
        vm.StatusMessage.Should().Be("操作已成功完成。");
        closeResult.Should().BeNull();
    }

    [Fact]
    public async Task BlockingOperationDialog_cancel_invokes_action_when_enabled()
    {
        int cancelCalls = 0;
        BlockingOperationDialogViewModel vm = new(() => cancelCalls++)
        {
            CanCancel = true
        };

        await vm.CancelCommand.ExecuteAsync();

        cancelCalls.Should().Be(1);
        vm.CanCancel.Should().BeFalse();
        vm.StatusMessage.Should().Be("正在取消操作...");
        vm.IsRunning.Should().BeTrue();

        vm.MarkCancelled();
        vm.IsRunning.Should().BeFalse();
        vm.OperationState.Should().Be("已取消");
        vm.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task ModalOperationRunner_cancel_marks_dialog_cancelled_and_rethrows()
    {
        CapturingDialogService dialogs = new();
        ModalOperationRunner runner = new(dialogs);

        Func<Task> action = () => runner.RunAsync(
            new ModalOperationOptions("t", "s", true),
            async context =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                BlockingOperationDialogViewModel dialog =
                    dialogs.LastViewModel.Should().BeOfType<BlockingOperationDialogViewModel>().Subject;
                await dialog.CancelCommand.ExecuteAsync();
                context.CancellationToken.ThrowIfCancellationRequested();
                return true;
            });

        await action.Should().ThrowAsync<OperationCanceledException>();
        BlockingOperationDialogViewModel closed =
            dialogs.LastViewModel.Should().BeOfType<BlockingOperationDialogViewModel>().Subject;
        closed.OperationState.Should().Be("已取消");
        closed.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task ModalOperationRunner_cancelled_outcome_marks_dialog_cancelled()
    {
        CapturingDialogService dialogs = new();
        ModalOperationRunner runner = new(dialogs);

        IOperationOutcome result = await runner.RunAsync(
            new ModalOperationOptions("t", "s", true),
            _ => Task.FromResult<IOperationOutcome>(new CancelledOutcome()));

        result.IsCancelled.Should().BeTrue();
        BlockingOperationDialogViewModel closed =
            dialogs.LastViewModel.Should().BeOfType<BlockingOperationDialogViewModel>().Subject;
        closed.OperationState.Should().Be("已取消");
        closed.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task MainWindowViewModel_refreshes_sidebar_file_search_roots()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-search-root-{Guid.NewGuid():N}"))
            .FullName;
        string database = Path.Combine(root, "ui.sqlite");
        string pdf = Path.Combine(root, "source.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), database);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            await services.FileResolution.AddSearchRootAsync(SelectedRoot(root));
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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task MainWindowViewModel_rescans_file_search_roots_and_imports_new_pdfs_once()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-rescan-root-{Guid.NewGuid():N}"))
            .FullName;
        string database = Path.Combine(root, "ui.sqlite");
        string pdf = Path.Combine(root, "new-source.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), database);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            await services.FileResolution.AddSearchRootAsync(SelectedRoot(root));

            Result<FileSearchRootRescanSummary> first = await vm.RescanFileSearchRootsAsync();
            Result<FileSearchRootRescanSummary> second = await vm.RescanFileSearchRootsAsync();

            first.IsSuccess.Should().BeTrue();
            first.Value.ImportedPdfCount.Should().Be(1);
            second.IsSuccess.Should().BeTrue();
            second.Value.ImportedPdfCount.Should().Be(0);
            second.Value.SkippedKnownPdfCount.Should().BeGreaterThanOrEqualTo(1);
            vm.Shell.Items.Should().ContainSingle(item => item.FileName == "new-source.pdf");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task MainWindowViewModel_logs_file_scan_and_file_watcher_events()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-scan-log-{Guid.NewGuid():N}"))
            .FullName;
        string database = Path.Combine(root, "ui.sqlite");
        string pdf = Path.Combine(root, "logged-source.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            CapturingLogger logger = new();
            MainWindowViewModel vm =
                WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard(), logger), database);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            await services.FileResolution.AddSearchRootAsync(SelectedRoot(root));

            Result<FileSearchRootRescanSummary> result = await vm.RescanFileSearchRootsAsync();

            result.IsSuccess.Should().BeTrue();
            logger.Messages.Should().Contain(message =>
                message.Operation == "file-scan" && message.Message.Contains("Rescan started") &&
                message.Message.Contains("trigger=manual"));
            logger.Messages.Should().Contain(message =>
                message.Operation == "file-scan" && message.Message.Contains("Root scan finished") &&
                message.Message.Contains(Path.GetFullPath(root)));
            logger.Messages.Should().Contain(message =>
                message.Operation == "file-scan" && message.Message.Contains("Rescan finished") &&
                message.Message.Contains("imported=1"));

            File.Copy(TestFixtures.RealThreePagePdf, Path.Combine(root, "watched.pdf"));
            bool sawWatcherLog = false;
            for (int attempt = 0; attempt < 40 && !sawWatcherLog; attempt++)
            {
                await Task.Delay(250);
                sawWatcherLog = logger.Messages.Any(message => message.Operation == "file-watcher");
            }

            sawWatcherLog.Should().BeTrue("the file watcher should log changes under the search root");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void MainWindow_xaml_wires_toolbar_search_and_results_workspace()
    {
        string shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        string searchXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SearchResultsPage.axaml"));
        shellXaml.Should().Contain("RunToolbarSearchCommand");
        shellXaml.Should().Contain("SearchEvidence.Query");
        searchXaml.Should().Contain("搜索结果");
    }

    [Fact]
    public void MainWindow_xaml_wires_ocr_queue_workspace()
    {
        string shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        string queueXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "OcrQueuePage.axaml"));
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
        string shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        string libraryXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "LibraryPage.axaml"));
        string searchXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SearchResultsPage.axaml"));
        string codeBehind =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml.cs"));
        string searchCodeBehind =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views",
                "SearchResultsPage.axaml.cs"));
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
        string shellXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        string editorXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "ItemEditorPage.axaml"));
        string settingsXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml"));
        shellXaml.Should().Contain("EditSelectedItemCommand");
        shellXaml.Should().Contain("DataType=\"{x:Type local:ItemEditorViewModel}\"");
        shellXaml.Should().Contain("ItemEditorPage");
        editorXaml.Should().Contain("AddIdentifierCommand");
        editorXaml.Should().Contain("RegisterFileCommand");
        settingsXaml.Should().Contain("FileSearchRoot");
        settingsXaml.Should().Contain("OCR 预设");
        settingsXaml.Should().Contain("搜索配置");
        settingsXaml.Should().Contain("MCP");
        settingsXaml.Should().Contain("Streamable HTTP");
        settingsXaml.Should().Contain("强制重启 Shell 沙箱");
        settingsXaml.Should().Contain("ForceRestartShellSandboxCommand");
        settingsXaml.Should().Contain("ShellSandboxStatusText");
        settingsXaml.Should().NotContain("SSE（默认）");
        settingsXaml.Should().NotContain("普通 JSON-RPC");
    }

    [Fact]
    public async Task ExportEvidenceMarkdownToFile_without_evidence_ref_reports_validation_error()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());

        await vm.ExportEvidenceMarkdownToFileAsync("",
            Path.Combine(Path.GetTempPath(), $"evidence-{Guid.NewGuid():N}.md"));

        vm.SearchEvidence.Output.Should().Contain("validation_failed");
        vm.Status.Should().Contain("EvidenceRef");
    }

    [Fact]
    public async Task MainWindowViewModel_auto_starts_mcp_http_server_and_reports_status()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-mcp-{Guid.NewGuid():N}.sqlite");
        int port = GetFreeTcpPort();
        MainWindowViewModel vm = WithRuntimeDatabasePath(
            CreateMainWindow(new FakeClipboard(), autoStartMcpServer: true, mcpPort: port), path);
        try
        {
            AppServices services = await vm.ServicesAsync();
            Result<McpServerSettings> settings = await services.McpSettings.GetSettingsAsync();
            settings.IsSuccess.Should().BeTrue();
            await vm.StopMcpServerAsync();
            await services.McpSettings.SaveSettingsAsync(settings.Value with { Port = port });
            await vm.StartMcpServerAsync();

            vm.McpEndpoint.Should().Be($"http://localhost:{port}/mcp");
            vm.McpStatusText.Should().Be("MCP: 运行中");
            vm.McpStatusDetail.Should().StartWith("连接数: 0 / 0").And.Contain("shell:");
            vm.ShellSandboxStatusText.Should().BeOneOf(ShellSandboxStatus.Ready, ShellSandboxStatus.Faulted,
                ShellSandboxStatus.Starting);
            using HttpClient http = new();
            string health = await http.GetStringAsync($"http://localhost:{port}/health");
            health.Should().Contain("ok");
        }
        finally
        {
            await vm.StopMcpServerAsync();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task QueueViewModel_enqueue_mock_adds_task_and_displays_runtime_only_warning()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            IOcrQueueScheduler queue = (await (await vm.ServicesAsync()).GetOcrQueueAsync()).Value;
            await queue.PauseAsync(OcrPauseScope.Global);
            vm.OcrQueue.DocumentInstanceId = DocumentInstanceId.New().ToString();
            vm.OcrQueue.PresetId = OcrPresetId.New().ToString();
            vm.OcrQueue.PageIds = PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();
            vm.OcrQueue.Output.Should().Contain("Queued mock OCR task");
            vm.OcrQueue.Tasks.Should().ContainSingle();
            vm.OcrQueue.Output.ToLowerInvariant().Should().NotContain("secret");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task QueueViewModel_refresh_shows_multiple_tasks_as_rows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            IOcrQueueScheduler queue = (await (await vm.ServicesAsync()).GetOcrQueueAsync()).Value;
            await queue.PauseAsync(OcrPauseScope.Global);
            vm.OcrQueue.DocumentInstanceId = DocumentInstanceId.New().ToString();
            vm.OcrQueue.PresetId = OcrPresetId.New().ToString();
            vm.OcrQueue.PageIds = PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();
            vm.OcrQueue.PageIds = PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();

            vm.OcrQueue.TaskRows.Should().HaveCount(2);
            vm.OcrQueue.HasTasks.Should().BeTrue();
            vm.OcrQueue.NoTasks.Should().BeFalse();
            vm.OcrQueue.TaskRows.Should().OnlyContain(row => row.State == OcrQueueTaskState.Queued);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task QueueViewModel_start_stop_pause_and_validation_are_visible()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-queue-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
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
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Logical_page_ocr_service_runs_region_ocr_through_the_queue()
    {
        DocumentInstanceId documentInstanceId = DocumentInstanceId.New();
        PageId pageId = PageId.New();
        StubLogicalPageTrees trees = new(documentInstanceId, pageId);
        RecordingRegionEngine engine = new(documentInstanceId, pageId, trees.RegionRevisionId);
        OcrQueueTaskExecutor executor = new(engine);
        OcrQueueScheduler scheduler = new(LibraryId.New(), new FixedClock(DateTimeOffset.UtcNow), executor,
            loopInterval: TimeSpan.FromMilliseconds(10));
        QueuedOcrRunCoordinator facade = new(scheduler, engine);
        LogicalPageOcrService service = new(facade, trees);
        LogicalPageOcrTarget target = new(trees.LogicalPageBoxId, new NormalizedBBox(.1, .2, .3, .4));

        Result<LogicalPageOcrResult> result = await service.RunAsync(
            documentInstanceId, engine.Run.PresetId, pageId, [target]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.RegionRunIds.Should().Equal(engine.Run.OcrRunId);
        engine.Calls.Should().Equal("region");
        OcrQueueTask task = (await scheduler.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
        task.TaskKind.Should().Be(OcrQueueTaskKind.Region);
        task.State.Should().Be(OcrQueueTaskState.Succeeded);
        task.RunId.Should().Be(engine.Run.OcrRunId);
        task.RegionBBox.Should().Be(target.BBox);
    }

    [Fact]
    public async Task Library_run_ocr_enqueues_document_task_visible_on_queue_board()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-queue-real-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-queue-real-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            PdfImportResult import =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Queued OCR Item", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();
            vm.Shell.MinerUToken = "token";
            await vm.OcrQueue.PauseGlobalCommand.ExecuteAsync();

            await vm.Shell.Items.Single().RunOcrCommand.ExecuteAsync();
            await vm.OcrQueue.RefreshAsync();

            vm.Status.Should().Contain("OCR 已加入后台队列");
            vm.OcrQueue.StatusSummary.Should().Contain("running");
            vm.OcrQueue.TaskRows.Should().ContainSingle();
            OcrQueueTaskViewModel row = vm.OcrQueue.TaskRows.Single();
            row.DocumentTitle.Should().Be("Queued OCR Item");
            row.Kind.Should().Be(OcrQueueTaskKind.Document);
            row.PageCount.Should().Be(1);

            await vm.OcrQueue.StopCommand.ExecuteAsync();
        }
        finally
        {
            if (File.Exists(pdf))
            {
                File.Delete(pdf);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Queue_terminal_failure_updates_status_bar_and_library_item_immediately()
    {
        string root = Directory
            .CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-queue-failure-{Guid.NewGuid():N}"))
            .FullName;
        string path = Path.Combine(root, "failure.sqlite");
        string pdf = Path.Combine(root, "failure.pdf");
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            await session.Dispatch(async () =>
            {
                File.Copy(TestFixtures.RealThreePagePdf, pdf);
                MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
                await vm.OpenDatabaseCommand.ExecuteAsync();
                await vm.Library.CreateCommand.ExecuteAsync();
                AppServices services = await vm.ServicesAsync();
                PdfImportResult import =
                    await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Failed queue item", null, 1));
                await vm.Shell.RefreshItemsAsync();
                LibraryItemViewModel item = vm.Shell.Items.Single();
                item.OcrStatus = "OCR 已加入后台队列";
                LibraryMetadata library = (await services.Library.GetCurrentLibraryAsync()).Value;
                OcrQueueTask task = new(
                    OcrQueueTaskId.New(), library.LibraryId, DocumentInstanceId.Parse(item.DocumentInstanceId!),
                    OcrPresetId.New(), [PageId.New()], OcrQueueTaskKind.Document, OcrEngineIds.MinerU,
                    OcrAdapterKind.CloudApi, ProviderIds.MinerU, OcrQueuePriority.UserStartedDocument,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, OcrQueueTaskState.Failed, 1, 1, null,
                    "upload_url_failed", "MinerU rejected model_version", null, null, null);

                typeof(OcrQueueViewModel).GetMethod("OnQueueChanged",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm.OcrQueue, [null, new OcrQueueChangedEventArgs(task, OcrQueueChangeKind.Updated)]);

                vm.StatusIsError.Should().BeTrue();
                vm.Status.Should().Contain("MinerU rejected model_version");
                item.OcrStatus.Should().Be("OCR 失败：MinerU rejected model_version");
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task SearchProfileViewModel_creates_rule_and_previews_plan()
    {
        string path = _settings.CreateDatabasePath("ui-profile");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.SearchProfiles.Name = "UI variants";
            await vm.SearchProfiles.CreateProfileCommand.ExecuteAsync();
            vm.SearchProfiles.Pattern = "臺灣";
            vm.SearchProfiles.Replacement = "台湾";
            await vm.SearchProfiles.AddRuleCommand.ExecuteAsync();
            vm.SearchProfiles.Query = "臺灣";
            await vm.SearchProfiles.PreviewCommand.ExecuteAsync();
            vm.SearchProfiles.Output.Should().Contain("台湾").And.Contain("OriginalQuery");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Shell_refresh_lists_imported_items()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            PdfImportResult import =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Shell Item", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);

            await vm.Shell.RefreshItemsAsync();

            vm.Shell.Items.Should().ContainSingle(item => item.Title == "Shell Item");
            vm.Shell.Items.Single().RunOcrCommand.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(pdf))
            {
                File.Delete(pdf);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Shell_run_ocr_without_token_reports_recoverable_error()
    {
        string settingsPath = WriteSettingsFile("");
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath);
            LibraryItemViewModel item = new(
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
            vm.Settings.OcrProviderSettings.MinerUCredentialStatus.Should().Contain("未配置");
            vm.Status.Should().Contain("MinerU API token");
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public async Task OpenDatabase_prefers_provider_credential_over_appsettings_token()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        string settingsPath = WriteSettingsFile("fallback-token");
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath)
                { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            Result<ProviderCredentialMetadata> saved =
                await services.Credentials.SaveAsync(ProviderIds.MinerU, "MinerU API token",
                    "provider-token");
            saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);

            MainWindowViewModel reloaded = new(new FakeClipboard(), settingsPath: settingsPath)
                { RuntimeDatabasePath = path };
            await reloaded.OpenDatabaseCommand.ExecuteAsync();

            reloaded.Shell.MinerUToken.Should().Be("provider-token");
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Invalid_settings_are_reported_in_the_status_bar()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"ui-invalid-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(settingsPath, "{ invalid json");
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath);

            vm.StatusIsError.Should().BeTrue();
            vm.Status.Should().Contain("设置文件格式无效");
            vm.RuntimeDatabasePath.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public async Task Settings_save_mineru_token_updates_provider_credential_and_appsettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        string settingsPath = WriteSettingsFile("");
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath)
                { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            await vm.OpenSettingsAsync("mineru");
            object section = vm.Settings.OcrProviderSettings;
            PropertyInfo? modelOptions = section.GetType().GetProperty("MinerUModelVersionOptions");
            PropertyInfo? selectedModel = section.GetType().GetProperty("MinerUModelVersion");
            modelOptions.Should().NotBeNull();
            selectedModel.Should().NotBeNull();
            ((IEnumerable<string>)modelOptions!.GetValue(section)!).Should().Equal("vlm", "pipeline");
            selectedModel!.SetValue(section, "pipeline");
            vm.Settings.OcrProviderSettings.MinerUTokenInput = "saved-token";

            await vm.Settings.SaveCommand.ExecuteAsync();

            AppServices services = await vm.ServicesAsync();
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU)).Value.Should()
                .Be("saved-token");
            PatchouliAppSettings.Load(settingsPath).Credentials.Providers.Should()
                .ContainSingle(value => value.ProviderId == ProviderIds.MinerU && value.SecretValue == "saved-token");
            PatchouliAppSettings.Load(settingsPath).MinerU.ModelVersion.Should().Be("pipeline");
            vm.Shell.MinerUToken.Should().Be("saved-token");
            vm.Status.Should().Contain("已保存");
            string xaml =
                File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml"));
            xaml.Should().Contain("ItemsSource=\"{Binding MinerUModelVersionOptions}\"");
            xaml.Should().Contain("SelectedItem=\"{Binding MinerUModelVersion}\"");
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task OpenDatabase_remembers_custom_runtime_database_when_enabled()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-remember-db-{Guid.NewGuid():N}.sqlite");
        string settingsPath = WriteSettingsFile("", true);
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath)
                { RuntimeDatabasePath = path };

            await vm.OpenDatabaseCommand.ExecuteAsync();

            PatchouliAppSettings saved = PatchouliAppSettings.Load(settingsPath);
            Path.GetFullPath(saved.Runtime.RuntimeDatabasePath).Should().Be(Path.GetFullPath(path));

            MainWindowViewModel reloaded = new(new FakeClipboard(), settingsPath: settingsPath);
            Path.GetFullPath(reloaded.RuntimeDatabasePath).Should().Be(Path.GetFullPath(path));
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task OpenDatabase_does_not_remember_custom_runtime_database_when_disabled()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-do-not-remember-db-{Guid.NewGuid():N}.sqlite");
        string settingsPath = WriteSettingsFile("", false);
        PatchouliAppSettings originalSettings = PatchouliAppSettings.Load(settingsPath);
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath)
                { RuntimeDatabasePath = path };

            await vm.OpenDatabaseCommand.ExecuteAsync();

            PatchouliAppSettings saved = PatchouliAppSettings.Load(settingsPath);
            Path.GetFullPath(saved.Runtime.RuntimeDatabasePath).Should()
                .Be(Path.GetFullPath(originalSettings.Runtime.RuntimeDatabasePath));

            MainWindowViewModel reloaded = new(new FakeClipboard(), settingsPath: settingsPath);
            Path.GetFullPath(reloaded.RuntimeDatabasePath).Should()
                .Be(Path.GetFullPath(AppRuntimeOptions.Default().RuntimeDatabasePath));
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task FirstRun_scan_imports_all_pdfs_as_items_without_manual_metadata()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-first-run-{Guid.NewGuid():N}.sqlite");
        string scanRoot = Directory
            .CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-first-run-scan-{Guid.NewGuid():N}")).FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(scanRoot, "alpha.pdf");
            TestFixtures.CopyRealThreePagePdfTo(scanRoot, "beta.pdf");
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.ShowInlineFirstRunAsync();
            await vm.FirstRun.OpenDatabaseCommand.ExecuteAsync();
            await vm.FirstRun.CreateLibraryCommand.ExecuteAsync();
            vm.FirstRun.ScanRoot = scanRoot;
            vm.FirstRun.SelectedScanRoot = new SelectedFileSearchRoot(scanRoot, "test",
                FileSearchRootAuthorizationKinds.None, null, null, DateTimeOffset.UtcNow);

            await vm.FirstRun.ScanCommand.ExecuteAsync();

            vm.FirstRun.CurrentStep.Should().Be("mineru_config");
            vm.FirstRun.ImportedPdfCount.Should().Be(2);
            await vm.Shell.RefreshItemsAsync();
            vm.Shell.Items.Should().HaveCount(2);
            vm.Shell.Items.Select(item => item.Title).Should().BeEquivalentTo("alpha", "beta");
        }
        finally
        {
            if (Directory.Exists(scanRoot))
            {
                Directory.Delete(scanRoot, true);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task FirstRun_import_then_selected_item_ocr_makes_text_readable_through_mcp()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-loop-{Guid.NewGuid():N}.sqlite");
        string settingsPath = Path.Combine(Path.GetTempPath(), $"ui-loop-{Guid.NewGuid():N}.json");
        string scanRoot = Directory
            .CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-loop-scan-{Guid.NewGuid():N}")).FullName;
        string zipPath = CreateMinerUZip("""
                                         [
                                           { "type": "text", "page_idx": 0, "text": "ui selected item mineru searchable text", "bbox": [0, 0, 1000, 100] },
                                           { "type": "text", "page_idx": 1, "text": "second physical page", "bbox": [0, 0, 1000, 100] },
                                           { "type": "text", "page_idx": 2, "text": "third physical page", "bbox": [0, 0, 1000, 100] }
                                         ]
                                         """);

        try
        {
            TestFixtures.CopyRealThreePagePdfTo(scanRoot, "selected.pdf");
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath)
                { RuntimeDatabasePath = path };
            await vm.ShowInlineFirstRunAsync();
            await vm.FirstRun.OpenDatabaseCommand.ExecuteAsync();
            await vm.FirstRun.CreateLibraryCommand.ExecuteAsync();
            vm.FirstRun.ScanRoot = scanRoot;
            vm.FirstRun.SelectedScanRoot = new SelectedFileSearchRoot(scanRoot, "test",
                FileSearchRootAuthorizationKinds.None, null, null, DateTimeOffset.UtcNow);

            await vm.FirstRun.ScanCommand.ExecuteAsync();
            vm.FirstRun.MinerUToken = "token";
            await vm.CompleteFirstRunCommand.ExecuteAsync();

            vm.IsLibraryVisible.Should().BeTrue();
            vm.Shell.Items.Should().ContainSingle();
            AppServices services = await vm.ServicesAsync();
            File.ReadAllText(settingsPath).Should().Contain("token");

            byte[] zipBytes = await File.ReadAllBytesAsync(zipPath);
            string? tokenUsed = null;
            string? modelVersionUsed = "not-called";
            services.MinerUClientFactoryOverride = config =>
            {
                tokenUsed = config.Token;
                modelVersionUsed = config.ModelVersion;
                return CreateProtocolMinerUClient(config, zipBytes);
            };
            await vm.Shell.Items.Single().RunOcrCommand.ExecuteAsync();

            IOcrQueueScheduler queue = (await services.GetOcrQueueAsync()).Value;
            await queue.WaitForIdleAsync();
            await vm.Shell.RefreshItemsAsync();

            File.ReadAllText(PatchouliAppSettings.ResolvePath()).Should().Contain("token");
            modelVersionUsed.Should().BeNull("the app settings own the MinerU API model version fallback");
            tokenUsed.Should().Be("token");
            OcrQueueTask ocrTask = (await queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Single();
            ocrTask.State.Should().Be(OcrQueueTaskState.Succeeded);
            vm.Shell.Items.Single().OcrStatus.Should().Contain("已索引");
            Result<McpSearchLibraryResponse> search =
                await services.Mcp.SearchLibraryAsync(new McpSearchLibraryRequest("searchable"));
            search.IsSuccess.Should().BeTrue(search.ErrorMessage);
            search.Value.Results.SelectMany(r => r.MatchedUnits).Should()
                .Contain(u => u.Text.Contains("ui selected item mineru searchable text"));
        }
        finally
        {
            if (Directory.Exists(scanRoot))
            {
                Directory.Delete(scanRoot, true);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Document_mineru_ocr_uses_page_and_size_aware_pdf_splitting()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"ui-mineru-split-{Guid.NewGuid():N}")).FullName;
        string databasePath = Path.Combine(root, "library.sqlite");
        string settingsPath = Path.Combine(root, "appsettings.json");
        string pdfPath = Path.Combine(root, "three-pages.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);

        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: settingsPath)
                { RuntimeDatabasePath = databasePath };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            PdfImportResult imported =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdfPath, "Split PDF", null, 3));
            imported.Success.Should().BeTrue(imported.ErrorMessage);
            Result<OcrPreset> preset = await services.OcrPresets.CreatePresetAsync(
                "MinerU", null, OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true);
            preset.IsSuccess.Should().BeTrue(preset.ErrorMessage);
            await services.Credentials.SaveAsync(ProviderIds.MinerU, "MinerU API token", "token");
            PageLimitedMinerUClient client = new(1);
            OcrRunEngine engine = new(
                services.ConnectionFactory,
                services.Clock,
                services.Credentials.GetActiveSecretForProviderAsync,
                services.Settings.Runtime.UseMockOcrOnly
                    ? (IOcrEngine)new MockOcrEngine()
                    : new UnavailableOcrEngine(),
                new SearchUnitBuilder(services.ConnectionFactory, services.Clock, services.Markdown),
                new OcrDocumentTreeImporter(services.DocumentTrees),
                services.OcrAdapters,
                services.PageRenders,
                services.PageCoordinates,
                services.MinerUImporter,
                _ => client,
                minerUUploadLimits: new MinerUUploadLimits(1, MinerUUploadLimits.OfficialMaxBytesPerFile),
                fileResolution: services.FileResolution);
            LibraryId libraryId = (await services.Library.GetCurrentLibraryAsync()).Value.LibraryId;
            OcrQueueTaskExecutor executor = new(engine, services.SearchUnits, services.SearchIndex);
            OcrQueueScheduler scheduler = new(libraryId, services.Clock, executor,
                loopInterval: TimeSpan.FromMilliseconds(10));
            IOcrRunCoordinator coordinator = new QueuedOcrRunCoordinator(scheduler, engine);

            Result<OcrRun> result = await coordinator.RunPresetOnDocumentAsync(
                DocumentInstanceId.Parse(imported.CreatedDocumentInstanceId!), preset.Value.PresetId);

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            client.AcceptedRequests.Should().HaveCount(3);
            client.AcceptedRequests.Should().OnlyContain(request => request.LocalPath != pdfPath);
            foreach (MinerUUploadRequest request in client.AcceptedRequests)
            {
                (await new PdfiumDocumentEngine().GetPageCountAsync(request.LocalPath)).Should().Be(1);
                request.FileSize.Should().BeLessThanOrEqualTo(MinerUUploadLimits.OfficialMaxBytesPerFile);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Shell_edit_metadata_context_action_opens_item_editor_tab()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            PdfImportResult import =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Editable Item", null, 1));
            import.Success.Should().BeTrue(import.ErrorMessage);
            await vm.Shell.RefreshItemsAsync();

            await vm.Shell.Items.Single().EditMetadataCommand.ExecuteAsync();
            vm.ShowItemEditorTab.Should().BeTrue();
            vm.IsItemEditorVisible.Should().BeTrue();
            vm.ItemEditor.Title.Should().Be("Editable Item");
            vm.ItemEditor.HasItem.Should().BeTrue();

            vm.ItemEditor.Title = "Edited Title";
            CreatorItemViewModel creator = vm.ItemEditor.Creators.Single();
            creator.Literal = "Chen, Li";
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
            if (File.Exists(pdf))
            {
                File.Delete(pdf);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Item_editor_removes_loaded_creator_after_field_rebuild_and_save()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-creator-remove-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            Result<ItemMetadata> created = await services.Items.CreateItemAsync(new CreateItemRequest(
                "book",
                "Creator removal",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, Literal: "Fetched Author")]));
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);

            await vm.Shell.RefreshItemsAsync();
            await vm.Shell.Items.Single(item => item.ItemId == created.Value.ItemId.ToString()).EditMetadataCommand
                .ExecuteAsync();
            ObservableCollection<CreatorItemViewModel> originalCreators = vm.ItemEditor.Creators;
            vm.ItemEditor.ItemType = "report";
            for (int attempt = 0; attempt < 100 && ReferenceEquals(originalCreators, vm.ItemEditor.Creators); attempt++)
            {
                await Task.Delay(20);
            }

            vm.ItemEditor.Creators.Should().ContainSingle(creator => creator.Name == "Fetched Author");
            await vm.ItemEditor.Creators.Single().RemoveCommand.ExecuteAsync();
            vm.ItemEditor.Creators.Should().BeEmpty();

            await vm.ItemEditor.SaveCommand.ExecuteAsync();
            Result<ItemMetadata> saved = await services.Items.GetItemAsync(created.Value.ItemId);
            saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
            saved.Value.Creators.Should().BeEmpty();

            await vm.ItemEditor.LoadAsync(created.Value.ItemId.ToString());
            vm.ItemEditor.Creators.Should().ContainSingle(creator =>
                string.IsNullOrWhiteSpace(creator.Name) &&
                string.IsNullOrWhiteSpace(creator.Literal));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task General_item_metadata_editor_renders_from_context_action()
    {
        string root = Directory
            .CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-general-editor-{Guid.NewGuid():N}")).FullName;
        string path = Path.Combine(root, "runtime.sqlite");
        string pdf = Path.Combine(root, "general.pdf");
        string userSettingsPath = new PlatformAppPaths().Resolve().UserSettingsPath;
        string? userSettingsBefore = File.Exists(userSettingsPath) ? File.ReadAllText(userSettingsPath) : null;
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            await session.Dispatch(async () =>
            {
                File.Copy(TestFixtures.RealThreePagePdf, pdf);
                MainWindow window = CreateMainWindowShell();
                window.Width = 1280;
                window.Height = 820;
                window.Show();
                try
                {
                    MainWindowViewModel vm = (MainWindowViewModel)window.DataContext!;
                    vm.SettingsFilePath.Should().Be(_settings.Path);
                    vm.RuntimeDatabasePath = path;
                    await vm.OpenDatabaseCommand.ExecuteAsync();
                    await vm.Library.CreateCommand.ExecuteAsync();
                    AppServices services = await vm.ServicesAsync();
                    PdfImportResult import =
                        await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "General Item", null, 1));
                    import.Success.Should().BeTrue(import.ErrorMessage);
                    await vm.Shell.RefreshItemsAsync();
                    vm.Shell.Items.Single().ItemType.Should().Be("general");

                    await vm.Shell.Items.Single().EditMetadataCommand.ExecuteAsync();

                    window.Measure(new Size(1280, 820));
                    window.Arrange(new Rect(0, 0, 1280, 820));
                    RenderTargetBitmap bitmap = new(new PixelSize(1280, 820), new Vector(96, 96));
                    bitmap.Render(window);

                    vm.ItemEditor.ItemType.Should().Be("general");
                    vm.ItemEditor.IsGeneralTypeWarningVisible.Should().BeTrue();
                    vm.ItemEditor.Fields.Should().Contain(field => field.Key == "Publisher");
                    vm.ItemEditor.Fields.Should().Contain(field => field.Key == "Pages");
                    await vm.StopMcpServerAsync();
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
            string? userSettingsAfter = File.Exists(userSettingsPath) ? File.ReadAllText(userSettingsPath) : null;
            userSettingsAfter.Should().Be(userSettingsBefore);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Headless_MainWindow_open_database_does_not_mutate_user_settings()
    {
        string userSettingsPath = new PlatformAppPaths().Resolve().UserSettingsPath;
        string? userSettingsBefore = File.Exists(userSettingsPath) ? File.ReadAllText(userSettingsPath) : null;
        string path = _settings.CreateDatabasePath("ui-isolation");
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            await session.Dispatch(async () =>
            {
                MainWindow window = CreateMainWindowShell();
                try
                {
                    MainWindowViewModel vm = (MainWindowViewModel)window.DataContext!;
                    vm.SettingsFilePath.Should().Be(_settings.Path);
                    vm.RuntimeDatabasePath = path;
                    await vm.OpenDatabaseCommand.ExecuteAsync();
                    Path.GetFullPath(PatchouliAppSettings.Load(_settings.Path).Runtime.RuntimeDatabasePath)
                        .Should().Be(Path.GetFullPath(path));
                    await vm.StopMcpServerAsync();
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
            string? userSettingsAfter = File.Exists(userSettingsPath) ? File.ReadAllText(userSettingsPath) : null;
            userSettingsAfter.Should().Be(userSettingsBefore);
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task FirstRun_tab_stays_open_and_search_disabled_until_setup_completes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-first-run-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.ShowInlineFirstRunAsync();

            vm.IsFirstRunVisible.Should().BeTrue();
            vm.IsSearchEnabled.Should().BeFalse();

            await vm.CompleteFirstRunCommand.ExecuteAsync();

            vm.IsFirstRunVisible.Should().BeTrue();
            vm.IsSearchEnabled.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Reader_mode_hides_library_sidebars()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());

        vm.Shell.IsReadingMode = true;
        vm.RaiseShellSelectionChanged();

        vm.Shell.ShowLibraryList.Should().BeFalse();
        vm.ShowSidebar.Should().BeFalse();
        vm.IsInspectorVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Workspace_singleton_tabs_reuse_existing_instances()
    {
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());

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
        MainWindowViewModel vm = CreateMainWindow(new FakeClipboard());

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
        string path = Path.Combine(Path.GetTempPath(), $"ui-tab-title-{Guid.NewGuid():N}.sqlite");
        string firstPdf = Path.Combine(Path.GetTempPath(), $"ui-tab-title-{Guid.NewGuid():N}-first.pdf");
        string secondPdf = Path.Combine(Path.GetTempPath(), $"ui-tab-title-{Guid.NewGuid():N}-second.pdf");
        string longTitle = "非常长的题录标题用于验证标签页会在合理长度之后被截断而不是撑爆标签栏";
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, firstPdf);
            File.Copy(TestFixtures.RealThreePagePdf, secondPdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            (await services.PdfImport.ImportPdfAsync(new PdfImportRequest(firstPdf, "短标题", null, 1))).Success.Should()
                .BeTrue();
            (await services.PdfImport.ImportPdfAsync(new PdfImportRequest(secondPdf, longTitle, null, 1))).Success
                .Should().BeTrue();
            await vm.Shell.RefreshItemsAsync();

            vm.Shell.SelectedItem = vm.Shell.Items.Single(item => item.Title == "短标题");
            await vm.ShowReadingCommand.ExecuteAsync();
            WorkspaceTabViewModel firstTab = vm.ActiveTab!;

            vm.Shell.SelectedItem = vm.Shell.Items.Single(item => item.Title == longTitle);
            await vm.ShowReadingCommand.ExecuteAsync();
            WorkspaceTabViewModel secondTab = vm.ActiveTab!;

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
            if (File.Exists(firstPdf))
            {
                File.Delete(firstPdf);
            }

            if (File.Exists(secondPdf))
            {
                File.Delete(secondPdf);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task MainWindow_close_pdf_tab_keeps_library_selection()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            PdfImportResult import =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Closable Tab", null, 1));
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
            if (File.Exists(pdf))
            {
                File.Delete(pdf);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task MainWindow_tabs_persist_across_switches_until_closed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            PdfImportResult import =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Persistent Tab", null, 1));
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
            if (File.Exists(pdf))
            {
                File.Delete(pdf);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Shell_reading_mode_renders_pdf_preview_in_memory()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-preview-{Guid.NewGuid():N}"))
            .FullName;
        string path = Path.Combine(root, "preview.sqlite");
        string pdf = Path.Combine(root, "preview.pdf");
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            await session.Dispatch(async () =>
            {
                File.Copy(TestFixtures.RealThreePagePdf, pdf);
                MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
                await vm.OpenDatabaseCommand.ExecuteAsync();
                await vm.Library.CreateCommand.ExecuteAsync();
                AppServices services = await vm.ServicesAsync();
                PdfImportResult import =
                    await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Previewable", null, 1));
                import.Success.Should().BeTrue(import.ErrorMessage);
                await vm.Shell.RefreshItemsAsync();

                await vm.Shell.SwitchToReadingModeCommand.ExecuteAsync();
                vm.RaiseShellSelectionChanged();

                vm.Shell.ShowPdfWorkspace.Should().BeTrue();
                vm.ShowSidebar.Should().BeFalse();
                vm.IsInspectorVisible.Should().BeFalse();
                vm.PdfWorkspace.Image.Should().NotBeNull(vm.PdfWorkspace.Status);
                vm.PdfWorkspace.Status.Should().Contain($"pdfium-{PdfiumDocumentEngine.Version}-dpi120");
                Directory.EnumerateFiles(root, "*.png").Should().BeEmpty();
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task Dirty_settings_block_section_switch_and_tab_close_until_current_section_is_discarded()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-settings-lifecycle-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            await vm.OpenSettingsAsync("library");
            SettingsCategoryViewModel libraryCategory = vm.Settings.ActiveCategory;
            SettingsCategoryViewModel syncCategory =
                vm.Settings.Categories.Single(category => category.Icon == "Cloud");
            vm.Settings.LibrarySettings.RememberLastDatabase =
                !vm.Settings.LibrarySettings.RememberLastDatabase;

            vm.Settings.ActiveCategory = syncCategory;
            await vm.CloseSettingsTabCommand.ExecuteAsync();

            vm.Settings.ActiveCategory.Should().BeSameAs(libraryCategory);
            vm.ShowSettingsTab.Should().BeTrue();
            vm.Settings.HasDirtySections.Should().BeTrue();

            await vm.Settings.DiscardCommand.ExecuteAsync();
            vm.Settings.ActiveCategory = syncCategory;

            vm.Settings.HasDirtySections.Should().BeFalse();
            vm.Settings.ActiveCategory.Should().BeSameAs(syncCategory);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Settings_page_header_omits_duplicate_status_and_sync_scope_is_shown_in_sync_section()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml"));

        xaml.Should().NotContain("Text=\"{Binding ActiveSaveStateText}\"");
        xaml.Should().NotContain("Text=\"{Binding ActiveScopeText}\"");
        xaml.Should().NotContain("ActiveEffectiveSourceText");
        xaml.Should().NotContain("ActiveRequiresReload");
        xaml.Should().NotContain("ActiveHasLastError");
        xaml.Should().Contain("SettingScopeRows");
        xaml.Should().Contain("AllowedSyncText");
        xaml.Should().Contain("OwnerText");
        xaml.Should().Contain("SchemaText");
        xaml.Should().Contain("MCP 配置");
        xaml.Should().Contain("凭据");
        xaml.Should().Contain("运行状态");
        xaml.Should().Contain("Command=\"{Binding SaveAndRestartCommand}\"");
        xaml.Should().Contain("Command=\"{Binding RemoveMinerUCredentialCommand}\"");
        xaml.Should().Contain("添加并扫描");
        xaml.Should().Contain("立即重新扫描");
    }

    [Fact]
    public async Task Mcp_settings_save_marks_saved_state_without_reload_when_server_stopped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-mcp-state-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = WithRuntimeDatabasePath(CreateMainWindow(new FakeClipboard()), path);
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            McpSettingsViewModel mcp = vm.Settings.McpSettings;
            await mcp.LoadAsync();
            mcp.Port = 4567;
            mcp.IsDirty.Should().BeTrue();
            mcp.SaveState.Should().Be(SettingsSaveState.Dirty);

            await mcp.SaveAsync();

            mcp.SaveState.Should().Be(SettingsSaveState.Saved);
            mcp.SaveStateText.Should().Be("已保存");
            mcp.IsDirty.Should().BeFalse();
            mcp.RequiresReload.Should().BeFalse();
            mcp.LastError.Should().BeNull();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Remove_mineru_credential_with_confirmation_clears_persisted_token()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-cred-{Guid.NewGuid():N}.sqlite");
        string settingsPath = WriteSettingsFile("");
        FakeDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), dialogs: dialogs, settingsPath: settingsPath)
                { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            (await vm.SaveMinerUSettingsAsync("token-to-remove", "vlm")).Should().BeTrue();
            vm.Settings.OcrProviderSettings.HasPersistedCredential.Should().BeTrue();

            (await vm.RemoveMinerUCredentialAsync()).Should().BeTrue();

            AppServices services = await vm.ServicesAsync();
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU)).IsFailure.Should()
                .BeTrue();
            vm.Shell.MinerUToken.Should().Be("");
            vm.Settings.OcrProviderSettings.HasPersistedCredential.Should().BeFalse();
            vm.Settings.OcrProviderSettings.MinerUTokenInput.Should().Be("");
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Remove_mineru_credential_cancel_keeps_persisted_token()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-cred-{Guid.NewGuid():N}.sqlite");
        string settingsPath = WriteSettingsFile("");
        FakeDialogService dialogs = new() { Result = ConfirmDialogResult.Cancel };
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard(), dialogs: dialogs, settingsPath: settingsPath)
                { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            (await vm.SaveMinerUSettingsAsync("token-to-keep", "vlm")).Should().BeTrue();

            (await vm.RemoveMinerUCredentialAsync()).Should().BeTrue();

            AppServices services = await vm.ServicesAsync();
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU)).Value.Should()
                .Be("token-to-keep");
            vm.Shell.MinerUToken.Should().Be("token-to-keep");
            vm.Settings.OcrProviderSettings.HasPersistedCredential.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public object? Result { get; init; }

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            return Task.FromResult((TResult?)Result);
        }
    }

    private sealed class CapturingDialogService : IDialogService
    {
        public object? LastViewModel { get; private set; }
        public List<object> ViewModels { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            LastViewModel = viewModel;
            ViewModels.Add(viewModel);
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            LastViewModel = viewModel;
            ViewModels.Add(viewModel);
            return Task.FromResult(default(TResult?));
        }
    }

    private sealed record CancelledOutcome : IOperationOutcome
    {
        public bool IsSuccess => false;
        public bool IsCancelled => true;
        public string ErrorMessage => "cancelled";
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult(Text);
        }
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public ConcurrentQueue<(string Operation, string Message)> Messages { get; } = new();

        public Task LogAsync(string operation, string message)
        {
            Messages.Enqueue((operation, message));
            return Task.CompletedTask;
        }
    }

    private static SelectedFileSearchRoot SelectedRoot(string path)
    {
        return new SelectedFileSearchRoot(
            path,
            "test",
            FileSearchRootAuthorizationKinds.None,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string CreateMinerUZip(string contentListJson)
    {
        string zipPath = Path.Combine(Path.GetTempPath(), $"ui-mineru-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("sample_content_list.json");
        using StreamWriter writer = new(entry.Open());
        writer.Write(contentListJson);
        return zipPath;
    }

    private static string WriteSettingsFile(string token, bool rememberLastDatabase = true)
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-appsettings-{Guid.NewGuid():N}.json");
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
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"code":0,"data":{"batch_id":"batch-ui","file_urls":["https://upload.example.test/file"]},"msg":"ok"}""")
                    };
                }

                if (request.Method == HttpMethod.Put && request.RequestUri!.Host == "upload.example.test")
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                }

                if (request.Method == HttpMethod.Get &&
                    request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/batch-ui")
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                                                    {"code":0,"data":{"batch_id":"batch-ui","extract_result":[{"file_name":"selected.pdf","state":"done","err_msg":"","full_zip_url":"https://cdn.example.test/result.zip"}]},"msg":"ok"}
                                                    """)
                    };
                }

                if (request.Method == HttpMethod.Get && request.RequestUri!.Host == "cdn.example.test")
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        { Content = new ByteArrayContent(zipBytes) };
                }

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

        public MinerUProtocolHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class PageLimitedMinerUClient : IMinerUClient
    {
        private readonly int _maxPages;

        public PageLimitedMinerUClient(int maxPages)
        {
            _maxPages = maxPages;
        }

        public bool IsConfigured => true;
        public List<MinerUUploadRequest> AcceptedRequests { get; } = new();

        public async Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
            IReadOnlyList<MinerUUploadRequest> files,
            CancellationToken cancellationToken = default)
        {
            MinerUUploadRequest request = files.Single();
            int pageCount = await new PdfiumDocumentEngine().GetPageCountAsync(request.LocalPath, cancellationToken);
            if (pageCount > _maxPages)
            {
                return Result<MinerUUploadBatch>.Failure(MinerUProviderStatus.UploadUrlFailed,
                    $"number of pages exceeds limit ({_maxPages} pages), please split the file and try again");
            }

            AcceptedRequests.Add(request);
            string batchId = $"batch-{AcceptedRequests.Count}";
            return Result<MinerUUploadBatch>.Success(new MinerUUploadBatch(batchId,
            [
                new MinerUFileUploadUrl(request.FileName, "https://upload.example.test/file", request.DataId)
            ]));
        }

        public Task<Result> UploadFileAsync(string uploadUrl, string localPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MinerUPollResult>.Success(
                new MinerUPollResult(batchId, MinerUProviderStatus.Done, null, null)));
        }

        public Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(
            string batchId,
            string downloadDirectory,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(downloadDirectory);
            string zipPath = Path.Combine(downloadDirectory, $"{batchId}.zip");
            using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            ZipArchiveEntry entry = archive.CreateEntry("sample_content_list_v2.json");
            using StreamWriter writer = new(entry.Open());
            writer.Write($$"""
                           [[{"type":"paragraph","content":{"paragraph_content":[{"type":"text","content":"{{batchId}}"}]},"bbox":[0,0,100,100]}]]
                           """);
            return Task.FromResult(Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done)));
        }
    }

    private sealed class RecordingRegionEngine : IOcrRunEngine
    {
        private readonly PageId _pageId;
        private readonly DocumentTreeRevisionId _regionRevisionId;

        public RecordingRegionEngine(DocumentInstanceId documentInstanceId, PageId pageId,
            DocumentTreeRevisionId regionRevisionId)
        {
            _pageId = pageId;
            _regionRevisionId = regionRevisionId;
            OcrPresetVersion version = new(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.Mock, "model",
                null, "{}", false, DateTimeOffset.UtcNow);
            Version = version;
            Run = new OcrRun(OcrRunId.New(), documentInstanceId, version.PresetId, version.PresetVersionId,
                version.EngineId, version.ModelId, "{}", null, null, null, OcrRunState.Completed,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }

        public List<string> Calls { get; } = [];
        public OcrPresetVersion Version { get; }
        public OcrRun Run { get; }

        public Task<Result<OcrPresetVersion>> ResolvePresetVersionAsync(OcrPresetId presetId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrPresetVersion>.Success(Version));
        }

        public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("region");
            return Task.FromResult(Result<OcrRun>.Success(Run));
        }

        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<OcrPageResult>>.Success([
                new OcrPageResult(OcrPageResultId.New(), runId, _pageId, OcrPageResultState.Succeeded,
                    _regionRevisionId, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]));
        }

        public Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrRun>.Success(Run));
        }

        public Task<Result<IReadOnlyList<PageId>>> ListPageIdsAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> ReconcileInterruptedRunsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, IReadOnlyList<PageId> pageIds, string engineId, string adapterKind,
            string? providerId, string priority, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId, OcrPresetId presetId,
            IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, string imagePath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, int dpi = 200, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId runId,
            IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubLogicalPageTrees : IDocumentTreeService
    {
        private readonly DocumentInstanceId _documentInstanceId;
        private readonly PageId _pageId;
        private readonly DocumentTreeRevision _currentRevision;

        public StubLogicalPageTrees(DocumentInstanceId documentInstanceId, PageId pageId)
        {
            _documentInstanceId = documentInstanceId;
            _pageId = pageId;
            _currentRevision = new DocumentTreeRevision(DocumentTreeRevisionId.New(), documentInstanceId, pageId, null,
                DocumentTreeRevisionSource.Import, "current", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }

        public DocumentBoxId LogicalPageBoxId { get; } = DocumentBoxId.New();
        public DocumentTreeRevisionId RegionRevisionId { get; } = DocumentTreeRevisionId.New();

        public Task<Result<DocumentTreeRevision>> GetCurrentRevisionAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<DocumentTreeRevision>.Success(_currentRevision));
        }

        public Task<Result<IReadOnlyList<DocumentBox>>> ListBoxesAsync(DocumentTreeRevisionId treeRevisionId,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DocumentBox> boxes;
            if (treeRevisionId == _currentRevision.TreeRevisionId)
            {
                boxes =
                [
                    new DocumentBox(_currentRevision.TreeRevisionId, LogicalPageBoxId, _documentInstanceId, _pageId,
                        null, null, DocumentBoxType.LogicalPage, null, null, new NormalizedBBox(0, 0, 1, 1), null,
                        null, null, null, false)
                ];
            }
            else if (treeRevisionId == RegionRevisionId)
            {
                boxes =
                [
                    new DocumentBox(RegionRevisionId, DocumentBoxId.New(), _documentInstanceId, _pageId,
                        LogicalPageBoxId, null, DocumentBoxType.Text, null, null, new NormalizedBBox(.1, .2, .3, .4),
                        new TextBoxPayload("queued region text"), null, null, null, false)
                ];
            }
            else
            {
                boxes = [];
            }

            return Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(boxes));
        }

        public Task<Result<DocumentTreeRevision>> StagePageAsync(DocumentInstanceId documentInstanceId, PageId pageId,
            IReadOnlyList<DocumentBoxSeed> boxes, string source = DocumentTreeRevisionSource.Import,
            DocumentTreeRevisionId? parentTreeRevisionId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<DocumentTreeRevision>.Success(new DocumentTreeRevision(
                DocumentTreeRevisionId.New(), documentInstanceId, pageId, parentTreeRevisionId, source, "staging",
                false, DateTimeOffset.UtcNow, null)));
        }

        public Task<Result> ValidateStoredTreesAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<DocumentTreeRevision>> CreateStagingRevisionAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, string source, DocumentTreeRevisionId? parentTreeRevisionId = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<PageEditSession>> BeginPageEditAsync(DocumentInstanceId documentInstanceId, PageId pageId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<DocumentTreeRevision>> AdoptStagingRevisionAsync(DocumentTreeRevisionId stagingRevisionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<IReadOnlyList<DocumentTreeRevision>>> AdoptStagingRevisionsAsync(
            IReadOnlyList<DocumentTreeRevisionId> stagingRevisionIds, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<DocumentTreeRevision>> CommitPageEditAsync(PageEditSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> DiscardPageEditAsync(PageEditSessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
