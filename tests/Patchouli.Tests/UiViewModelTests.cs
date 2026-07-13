using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net.Sockets;
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
using Patchouli.Core.Layout;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Editor;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

public sealed class UiViewModelTests
{
    [Fact]
    public void Settings_page_uses_five_editable_groups_and_keeps_csl_about_outside()
    {
        MainWindowViewModel vm = new(new FakeClipboard());
        vm.Settings.Categories.Select(category => category.Title).Should().Equal(
            "库与本机路径", "同步与快照", "MCP 服务与安全", "OCR 引擎", "元数据来源");
        vm.Settings.Categories.Select(category => category.Section is { SupportsEditing: true }).Should()
            .OnlyContain(value => value);
    }

    [Fact]
    public void Sync_settings_expose_a_persisted_device_identity_and_name()
    {
        MainWindowViewModel vm = new(new FakeClipboard());

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
                    false,
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
    public async Task LibraryViewModel_CreateLibrary_updates_current_library()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = new() { RuntimeDatabasePath = path };
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
    public async Task BibliographyViewModel_CreateItem_returns_item()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = new() { RuntimeDatabasePath = path };
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
            MainWindowViewModel vm = new() { RuntimeDatabasePath = path };
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
        MainWindowViewModel vm = new(clipboard);
        vm.SearchEvidence.Markdown = "Pinned source text";
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        clipboard.Text.Should().Be("Pinned source text");
        vm.SearchEvidence.Output.Should().Be("Copied Evidence Markdown");
    }

    [Fact]
    public async Task CopyEvidenceMarkdown_without_markdown_returns_validation_error()
    {
        MainWindowViewModel vm = new(new FakeClipboard());
        await vm.SearchEvidence.CopyMarkdownCommand.ExecuteAsync();
        vm.SearchEvidence.Output.Should().Contain("validation_failed");
    }

    [Fact]
    public async Task CopyEvidenceRef_writes_ref_to_clipboard()
    {
        FakeClipboard clipboard = new();
        MainWindowViewModel vm = new(clipboard);

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
            MainWindowViewModel vm = new(clipboard) { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            AppServices services = await vm.ServicesAsync();
            Result<ItemMetadata> item = await services.Items.CreateItemAsync("book", "UI Evidence Item");
            Result<DocumentInstance> document =
                await services.Documents.AttachDocumentInstanceAsync(item.Value.ItemId, null,
                    DocumentInstanceType.PrimaryScan);
            Result<Page> page = await services.Pages.CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null,
                null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            Result<LayoutRevision> revision =
                await services.Layout.CreateLayoutRevisionAsync(document.Value.DocumentInstanceId,
                    LayoutRevisionSource.Mock, true);
            await services.Layout.AddNodeAsync(revision.Value.LayoutRevisionId, page.Value.PageId, null,
                LayoutNodeType.Paragraph, null, "Pinned clipboard text", TextPolicy.Own, 1, LayoutNodeSource.Mock);
            await services.SearchUnits.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);

            await using SqliteConnection connection = services.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            string? unitId =
                await connection.ExecuteScalarAsync<string>(
                    "select unit_id from search_units where resolved_text = 'Pinned clipboard text';");
            SearchMatchedUnitViewModel unit = new(unitId!, "Pinned clipboard text", LayoutNodeType.Paragraph, 1, true,
                null);

            await vm.SearchEvidence.CopyEvidenceMarkdownForSearchUnitAsync(unit);

            unit.EvidenceRef.Should().StartWith("evref:v1:");
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
        MainWindowViewModel vm = new(new FakeClipboard());
        vm.McpPreview.Output = "{\"bad\":\"/tmp/private.sqlite\"}";
        vm.McpPreview.SpecificPath = "/tmp/private.sqlite";
        await vm.McpPreview.SafetyCommand.ExecuteAsync();
        vm.McpPreview.Safety.Should().Contain("Warning");
    }

    [Fact]
    public async Task McpPreviewViewModel_SafetyCheck_passes_clean_output()
    {
        MainWindowViewModel vm = new(new FakeClipboard());
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
            MainWindow window = new();
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainWindow_new_item_editor_renders_without_recursive_templates()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindow window = new()
            {
                Width = 1280,
                Height = 820
            };
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
            MainWindow window = new() { Width = 1280, Height = 820 };
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
        MainWindowViewModel vm = new(new FakeClipboard());
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
    public async Task MainWindowViewModel_refreshes_sidebar_file_search_roots()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ui-search-root-{Guid.NewGuid():N}"))
            .FullName;
        string database = Path.Combine(root, "ui.sqlite");
        string pdf = Path.Combine(root, "source.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = database };
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
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = database };
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
        settingsXaml.Should().NotContain("SSE（默认）");
        settingsXaml.Should().NotContain("普通 JSON-RPC");
    }

    [Fact]
    public async Task ExportEvidenceMarkdownToFile_without_evidence_ref_reports_validation_error()
    {
        MainWindowViewModel vm = new(new FakeClipboard());

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
        MainWindowViewModel vm = new(new FakeClipboard(), autoStartMcpServer: true, mcpPort: port)
            { RuntimeDatabasePath = path };
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
            vm.McpStatusDetail.Should().Be("连接数: 0 / 0");
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
            MainWindowViewModel vm = new() { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.OcrQueue.DocumentInstanceId = Core.Ids.DocumentInstanceId.New().ToString();
            vm.OcrQueue.PresetId = Core.Ids.OcrPresetId.New().ToString();
            vm.OcrQueue.PageIds = Core.Ids.PageId.New().ToString();
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
            MainWindowViewModel vm = new() { RuntimeDatabasePath = path };
            await vm.OpenDatabaseCommand.ExecuteAsync();
            await vm.Library.CreateCommand.ExecuteAsync();
            vm.OcrQueue.DocumentInstanceId = Core.Ids.DocumentInstanceId.New().ToString();
            vm.OcrQueue.PresetId = Core.Ids.OcrPresetId.New().ToString();
            vm.OcrQueue.PageIds = Core.Ids.PageId.New().ToString();
            await vm.OcrQueue.EnqueueMockCommand.ExecuteAsync();
            vm.OcrQueue.PageIds = Core.Ids.PageId.New().ToString();
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
            MainWindowViewModel vm = new() { RuntimeDatabasePath = path };
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
    public async Task Library_run_ocr_enqueues_document_task_visible_on_queue_board()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-queue-real-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-queue-real-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
    public async Task SearchProfileViewModel_creates_rule_and_previews_plan()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-profile-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = new() { RuntimeDatabasePath = path };
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
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
            vm.Settings.MinerUCredentialStatus.Should().Contain("未配置");
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
            vm.Settings.MinerUTokenInput = "saved-token";

            await vm.Settings.SaveMinerUSettingsCommand.ExecuteAsync();

            AppServices services = await vm.ServicesAsync();
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU)).Value.Should()
                .Be("saved-token");
            PatchouliAppSettings.Load(settingsPath).Credentials.Providers.Should()
                .ContainSingle(value => value.ProviderId == ProviderIds.MinerU && value.SecretValue == "saved-token");
            vm.Shell.MinerUToken.Should().Be("saved-token");
            vm.Status.Should().Contain("已保存");
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
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
                                           { "type": "text", "page_idx": 0, "text": "ui selected item mineru searchable text", "bbox": [0, 0, 1000, 100] }
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
            vm.Shell.MinerUClientFactory = config =>
            {
                tokenUsed = config.Token;
                return CreateProtocolMinerUClient(config, zipBytes);
            };
            await vm.Shell.Items.Single().RunOcrCommand.ExecuteAsync();

            File.ReadAllText(PatchouliAppSettings.ResolvePath()).Should().Contain("token");
            vm.Status.Should().Contain("OCR 完成");
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
    public async Task Shell_edit_metadata_context_action_opens_item_editor_tab()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.sqlite");
        string pdf = Path.Combine(Path.GetTempPath(), $"ui-shell-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
            vm.ItemEditor.Creators.Should().BeEmpty();
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        try
        {
            await session.Dispatch(async () =>
            {
                File.Copy(TestFixtures.RealThreePagePdf, pdf);
                MainWindow window = new()
                {
                    Width = 1280,
                    Height = 820
                };
                window.Show();
                try
                {
                    MainWindowViewModel vm = (MainWindowViewModel)window.DataContext!;
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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task FirstRun_tab_stays_open_and_search_disabled_until_setup_completes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ui-first-run-{Guid.NewGuid():N}.sqlite");
        try
        {
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
        MainWindowViewModel vm = new(new FakeClipboard());

        vm.Shell.IsReadingMode = true;
        vm.RaiseShellSelectionChanged();

        vm.Shell.ShowLibraryList.Should().BeFalse();
        vm.ShowSidebar.Should().BeFalse();
        vm.IsInspectorVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Workspace_singleton_tabs_reuse_existing_instances()
    {
        MainWindowViewModel vm = new(new FakeClipboard());

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
        MainWindowViewModel vm = new(new FakeClipboard());

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
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
            MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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
                MainWindowViewModel vm = new(new FakeClipboard()) { RuntimeDatabasePath = path };
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

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
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
}
