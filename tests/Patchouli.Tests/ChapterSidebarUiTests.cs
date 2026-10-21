using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class ChapterSidebarUiTests
{
    [Fact]
    public async Task Sidebar_headers_expose_translation_and_replace_scan_actions_during_preview()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
            await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0, "# Header chapter"));
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
                { RuntimeDatabasePath = document.Database.Path };
            await main.OpenDatabaseCommand.ExecuteAsync();
            LibraryItemViewModel item = new(ItemId.New().ToString(), "Book", "book", "", "", "", null,
                document.DocumentId.ToString(), null, "book.pdf", "", 3, 0, "",
                _ => Task.CompletedTask, _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item) { IsSidebarOpen = true };
            await workspace.ShowChaptersTabCommand.ExecuteAsync();
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = new() { Width = 1200, Height = 900, Content = page };
            window.Show();
            try
            {
                Pump(window);
                Button add = page.FindControl<Button>("AddChapterButton")!;
                Button scan = page.FindControl<Button>("ScanChaptersButton")!;
                Button apply = page.FindControl<Button>("ApplyChapterScanButton")!;
                Button discard = page.FindControl<Button>("CancelChapterPreviewButton")!;
                Button translate = page.FindControl<Button>("StartDocumentTranslationButton")!;
                StackPanel header = page.FindControl<StackPanel>("ChapterHeaderActions")!;
                header.Children.Should().Contain(apply).And.Contain(discard);
                add.IsEffectivelyVisible.Should().BeTrue();
                scan.IsEffectivelyVisible.Should().BeTrue();
                apply.IsEffectivelyVisible.Should().BeFalse();
                translate.IsEffectivelyVisible.Should().BeFalse();

                await workspace.ChapterEditor.ScanCommand.ExecuteAsync();
                Pump(window);
                workspace.ChapterEditor.HasPreview.Should().BeTrue();
                add.IsEffectivelyVisible.Should().BeFalse();
                scan.IsEffectivelyVisible.Should().BeFalse();
                apply.IsEffectivelyVisible.Should().BeTrue();
                discard.IsEffectivelyVisible.Should().BeTrue();
                apply.Command.Should().BeSameAs(workspace.ChapterEditor.ApplyScanCommand);
                discard.Command.Should().BeSameAs(workspace.ChapterEditor.CancelPreviewCommand);
                page.GetVisualDescendants().OfType<TextBlock>().Should().NotContain(text => text.Text == "扫描预览");
                SavePreview(window, 1200, "chapter-preview-header");
                Click(discard);
                workspace.ChapterEditor.HasPreview.Should().BeFalse();
                (await document.CreateMapService().GetChapterEditingSnapshotAsync(document.DocumentId)).Value
                    .Definition.Chapters.Should().BeEmpty();
                add.IsEffectivelyVisible.Should().BeTrue();
                scan.IsEffectivelyVisible.Should().BeTrue();

                await workspace.ChapterEditor.ScanCommand.ExecuteAsync();
                Pump(window);
                Click(apply);
                await workspace.ChapterEditor.ApplyScanCommand.ExecutionTask!;
                workspace.ChapterEditor.HasPreview.Should().BeFalse();
                (await document.CreateMapService().GetChapterEditingSnapshotAsync(document.DocumentId)).Value
                    .Definition.Chapters.Should().ContainSingle(chapter => chapter.Title == "Header chapter");

                await workspace.ShowTranslationTabCommand.ExecuteAsync();
                Pump(window);
                translate.IsEffectivelyVisible.Should().BeTrue();
                translate.Command.Should().BeSameAs(workspace.StartDocumentTranslationCommand);
                translate.GetVisualAncestors().OfType<Border>().Should()
                    .Contain(border => border.Classes.Contains("SidebarHeader"));
                Avalonia.Platform.AssetLoader.Exists(new Uri("avares://Patchouli.UI/Assets/Lucide/languages.svg"))
                    .Should().BeTrue();
                AsyncCommand exportCommand = workspace.ExportDocumentTranslationCommand;
                IReadOnlyList<string> exportModes = workspace.TranslationExportModes;
                page.GetVisualDescendants().OfType<Button>().Should()
                    .NotContain(button => button.Command == exportCommand);
                page.GetVisualDescendants().OfType<ComboBox>().Should()
                    .NotContain(combo => ReferenceEquals(combo.ItemsSource, exportModes));
                SavePreview(window, 1200, "translation-header");
                workspace.ShowContentTabCommand.Execute(null);
                Pump(window);
                translate.IsEffectivelyVisible.Should().BeFalse();

                void Click(Button button)
                {
                    Point point =
                        button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!
                            .Value;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    Pump(window);
                }
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_drag_moves_whole_subtrees_without_saving_the_map(bool asChild)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
            await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0,
                "# A\n## A1\n# B\n## B1"));
            using ChapterSidebarViewModel sidebar = Sidebar(document);
            await sidebar.ScanCommand.ExecuteAsync();
            ChapterEditorPanel panel = new() { DataContext = sidebar, Margin = new Thickness(12) };
            Window window = new() { Width = 400, Height = 900, Content = panel };
            window.Show();
            try
            {
                Pump(window);
                Border[] handles = panel.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Name == "PreviewDragHandle").ToArray();
                handles.Should().HaveCount(4);

                Point Center(Border handle)
                {
                    return handle.TranslatePoint(
                        new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
                }

                Point source = Center(handles[0]), target = Center(handles[2]);
                RawInputModifiers modifier = asChild ? RawInputModifiers.Control : RawInputModifiers.None;
                window.MouseMove(source);
                window.MouseDown(source, MouseButton.Left, modifier);
                window.MouseMove(target, modifier | RawInputModifiers.LeftMouseButton);
                window.MouseUp(target, MouseButton.Left, modifier);
                Pump(window);
                sidebar.PreviewRows.Select(row => row.Title).Should().Equal("B", "B1", "A", "A1");
                sidebar.PreviewRows.Select(row => row.Depth).Should()
                    .Equal(asChild ? [0, 1, 1, 2] : [0, 1, 0, 1]);
                ChapterScanRowViewModel[] before = sidebar.PreviewRows.ToArray();
                sidebar.DropPreview(before[0], before[1], true, true);
                sidebar.PreviewRows.Should().Equal(before);
                if (asChild)
                {
                    sidebar.DropPreview(before[2], before[0], true, false, true);
                    sidebar.PreviewRows.Select(row => row.Depth).Should().Equal(0, 1, 0, 1);
                }

                (await document.CreateMapService().GetChapterEditingSnapshotAsync(document.DocumentId)).Value
                    .Definition.Chapters.Should().BeEmpty();
                return 0;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Pagination_only_map_keeps_empty_chapter_prompt_and_map_diagnostics()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            DocumentInstanceId documentId = DocumentInstanceId.New();
            LibraryItemViewModel item = new(ItemId.New().ToString(), "Book", "book", "", "", "", null,
                documentId.ToString(), null, "book.pdf", "", 3, 0, "", _ => Task.CompletedTask,
                _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item);
            DocumentPageMap map = new(documentId, [new PhysicalPaginationRule(1, 3, 10)], [], [], []);
            ResolvedDocumentPageMap resolved = new(documentId, true, map, [], [],
                [new MapDiagnostic("test", "映射诊断仍可见")], null);
            System.Reflection.MethodInfo apply = typeof(PdfWorkspaceViewModel).GetMethod("ApplyChapterMap",
                                                     System.Reflection.BindingFlags.Instance |
                                                     System.Reflection.BindingFlags.NonPublic)
                                                 ?? throw new InvalidOperationException(
                                                     "Missing chapter map application method.");
            apply.Invoke(workspace, [resolved]);
            workspace.HasChapterMap.Should().BeTrue();
            workspace.HasNoChapters.Should().BeTrue();
            workspace.ChapterMapStatusText.Should().Contain("映射诊断仍可见");
            workspace.Status.Should().Contain("映射诊断仍可见");
            workspace.ChapterNodes.Should().BeEmpty();
            map.PhysicalRules.Should().ContainSingle();
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(280)]
    [InlineData(400)]
    public async Task Sidebar_forms_and_scan_preview_fit_without_entering_ocr_edit_mode(int width)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
            await document.CommitPageAsync(0, PageMapTestDocument.TextSeed(DocumentBoxId.New(), 0,
                "# 第一章：一段较长的章节标题用于验证窄侧栏的文字换行和扫描修订\n\n### 子章节\n\n## 第二节"));
            using ChapterSidebarViewModel sidebar = Sidebar(document);
            ChapterEditorPanel panel = new() { DataContext = sidebar, Margin = new Thickness(12) };
            Window window = new()
            {
                Width = width, Height = 1400, Background = Brushes.White, Foreground = Brushes.Black,
                RequestedThemeVariant = ThemeVariant.Light, Content = new ScrollViewer { Content = panel }
            };
            window.Show();
            try
            {
                Pump(window);
                foreach (string icon in new[] { "check", "arrow-up-right", "grip-vertical" })
                {
                    Avalonia.Platform.AssetLoader.Exists(new Uri($"avares://Patchouli.UI/Assets/Lucide/{icon}.svg"))
                        .Should().BeTrue("chapter action icons must be bundled rather than rendered as placeholders");
                }

                await sidebar.AddCommand.ExecuteAsync();
                sidebar.Title = "手动章节，无需进入 OCR 文本编辑";
                sidebar.IsFormOpen.Should().BeTrue();
                sidebar.TargetPdfPage.Should().Be("1");
                SavePreview(window, width, "manual");
                sidebar.CancelFormCommand.Execute(null);
                await sidebar.ScanCommand.ExecuteAsync();
                sidebar.HasPreview.Should().BeTrue();
                sidebar.PreviewRows.Should().HaveCount(3);
                sidebar.PreviewRows[0].IsIncluded = false;
                sidebar.SelectedPreviewTree.Should().HaveCount(2);
                sidebar.ReplaceExisting = true;
                SavePreview(window, width, "scan-preview");
                foreach (Button button in panel.GetVisualDescendants().OfType<Button>()
                             .Where(b => b.IsEffectivelyVisible))
                {
                    Point position = button.TranslatePoint(default, panel)!.Value;
                    (position.X + button.Bounds.Width).Should().BeLessThanOrEqualTo(panel.Bounds.Width + 1,
                        $"button '{button.Content}' must stay inside a {width}px sidebar");
                }

                foreach (TextBlock text in panel.GetVisualDescendants().OfType<TextBlock>()
                             .Where(t => t.IsEffectivelyVisible))
                {
                    Point position = text.TranslatePoint(default, panel)!.Value;
                    (position.X + text.Bounds.Width).Should().BeLessThanOrEqualTo(panel.Bounds.Width + 1,
                        $"text '{text.Text}' must stay inside a {width}px sidebar");
                }

                await sidebar.PreviewRows[1].EditCommand.ExecuteAsync();
                SavePreview(window, width, "preview-repair");
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Reset_during_snapshot_load_does_not_open_a_form_for_the_old_document()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        ChapterSidebarServices services = Services(document);
        TaskCompletionSource<ChapterSidebarServices> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StrongBox<DocumentInstanceId?> current = new(document.DocumentId);
        using ChapterSidebarViewModel sidebar = new(() => gate.Task, () => current.Value, () => 1,
            _ => Task.CompletedTask, () => Task.CompletedTask);
        Task pending = sidebar.BeginAddAsync(null, false);
        current.Value = null;
        sidebar.Reset();
        gate.SetResult(services);
        await pending;
        sidebar.IsFormOpen.Should().BeFalse();
        sidebar.HasPreview.Should().BeFalse();
    }

    [Fact]
    public async Task Opening_edit_or_delete_reveals_and_focuses_the_action_in_a_long_outer_scroll()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
            DocumentPageMapService maps = document.CreateMapService();
            ChapterEditingSnapshot initial = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
            (await maps.SaveChaptersAsync(document.DocumentId, new ChapterSaveRequest(initial.VersionToken,
                    [ChapterNode.Leaf("existing", "Existing chapter", new NavigationPosition(1))]))).IsSuccess.Should()
                .BeTrue();
            using ChapterSidebarViewModel sidebar = Sidebar(document);
            ChapterEditorPanel panel = new() { DataContext = sidebar };
            ScrollViewer scroll = new()
            {
                Content = new StackPanel { Children = { panel, new Border { Height = 1800 } } }
            };
            Window window = new() { Width = 280, Height = 420, Content = scroll };
            window.Show();
            try
            {
                Pump(window);
                scroll.ScrollToEnd();
                Pump(window);
                scroll.Offset.Y.Should().BeGreaterThan(1000);
                await sidebar.BeginEditAsync("existing");
                Pump(window);
                Border form = panel.FindControl<Border>("ChapterForm")!;
                Point formTop = form.TranslatePoint(default, scroll)!.Value;
                formTop.Y.Should().BeLessThan(scroll.Viewport.Height);
                (formTop.Y + form.Bounds.Height).Should().BeGreaterThan(0);
                panel.FindControl<TextBox>("ChapterTitleInput")!.IsFocused.Should().BeTrue();
                sidebar.CancelFormCommand.Execute(null);
                scroll.ScrollToEnd();
                Pump(window);
                sidebar.RequestDelete("existing");
                Pump(window);
                Border delete = panel.FindControl<Border>("ChapterDeleteConfirmation")!;
                Point deleteTop = delete.TranslatePoint(default, scroll)!.Value;
                deleteTop.Y.Should().BeLessThan(scroll.Viewport.Height);
                (deleteTop.Y + delete.Bounds.Height).Should().BeGreaterThan(0);
                panel.FindControl<Button>("ChapterConfirmDelete")!.IsFocused.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("indent")]
    [InlineData("outdent")]
    [InlineData("drop")]
    public async Task Pending_old_document_move_cannot_apply_to_a_refreshed_new_document(string operation)
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = document.CreateMapService();
        DocumentInstanceId original = document.DocumentId;
        (DocumentInstanceId next, _) = await document.AddSecondDocumentAsync();
        ChapterNode shared = ChapterNode.Leaf("shared", "Shared ID", new NavigationPosition(1));
        ChapterNode parent = ChapterNode.Leaf("parent", "Parent", new NavigationPosition(1));
        IReadOnlyList<ChapterNode> chapters =
            operation == "outdent" ? [parent with { Children = [shared] }] : [parent, shared];
        foreach (DocumentInstanceId id in new[] { original, next })
        {
            ChapterEditingSnapshot snapshot = (await maps.GetChapterEditingSnapshotAsync(id)).Value;
            (await maps.SaveChaptersAsync(id, new ChapterSaveRequest(snapshot.VersionToken, chapters))).IsSuccess
                .Should().BeTrue();
        }

        ChapterSidebarServices services = Services(document);
        TaskCompletionSource<ChapterSidebarServices> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        StrongBox<DocumentInstanceId?> current = new(original);
        using ChapterSidebarViewModel sidebar = new(() => ++calls == 1 ? gate.Task : Task.FromResult(services),
            () => current.Value, () => 1, _ => Task.CompletedTask, () => Task.CompletedTask);
        Task pending = operation switch
        {
            "move" => sidebar.MoveAsync("shared", -1),
            "indent" => sidebar.IndentAsync("shared"),
            "outdent" => sidebar.OutdentAsync("shared"),
            _ => sidebar.DropAsync("shared", "parent", true)
        };
        current.Value = next;
        sidebar.Reset();
        await sidebar.RefreshAsync();
        ChapterEditingSnapshot baseline = (await maps.GetChapterEditingSnapshotAsync(next)).Value;
        gate.TrySetResult(services);
        await pending;
        ChapterEditingSnapshot after = (await maps.GetChapterEditingSnapshotAsync(next)).Value;
        after.VersionToken.Should().Be(baseline.VersionToken);
        after.Definition.Chapters.Should().BeEquivalentTo(chapters);
        sidebar.IsFormOpen.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_chapter_grip_reorders_or_nests_without_executing_navigation(bool asChild)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
            DocumentPageMapService maps = document.CreateMapService();
            ChapterEditingSnapshot snapshot = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
            ChapterNode first = ChapterNode.Leaf("first", "第一章", new NavigationPosition(1));
            ChapterNode second = ChapterNode.Leaf("second", "第二章", new NavigationPosition(2));
            (await maps.SaveChaptersAsync(document.DocumentId,
                    new ChapterSaveRequest(snapshot.VersionToken, [first, second])))
                .IsSuccess.Should().BeTrue();
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path)
                { RuntimeDatabasePath = document.Database.Path };
            await main.OpenDatabaseCommand.ExecuteAsync();
            LibraryItemViewModel item = new(ItemId.New().ToString(), "Book", "book", "", "", "", null,
                document.DocumentId.ToString(), null, "book.pdf", "", 3, 0, "",
                _ => Task.CompletedTask, _ => Task.CompletedTask);
            using PdfWorkspaceViewModel workspace = new(main, item) { IsSidebarOpen = true };
            await workspace.ShowChaptersTabCommand.ExecuteAsync();
            int navigationCount = 0;
            workspace.ChapterNodes.Clear();
            foreach (ChapterNode node in new[] { first, second })
            {
                workspace.ChapterNodes.Add(new ChapterTreeNodeViewModel(new ResolvedChapterNode(node.ChapterId,
                        node.Title, node.Target, null, null, [], []), 0, false, false,
                    _ =>
                    {
                        navigationCount++;
                        return Task.CompletedTask;
                    }, editor: workspace.ChapterEditor));
            }

            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = new() { Width = 1200, Height = 900, Content = page };
            window.Show();
            try
            {
                Pump(window);
                Border[] handles = page.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Name == "ChapterDragHandle" && b.IsEffectivelyVisible).ToArray();
                handles.Should().HaveCount(2);
                StackPanel header = page.FindControl<StackPanel>("ChapterHeaderActions")!;
                header.IsEffectivelyVisible.Should().BeTrue();
                Button add = page.FindControl<Button>("AddChapterButton")!;
                Button scan = page.FindControl<Button>("ScanChaptersButton")!;
                header.Children.Should().Contain(add).And.Contain(scan);
                add.Command.Should().BeSameAs(workspace.ChapterEditor.AddCommand);
                scan.Command.Should().BeSameAs(workspace.ChapterEditor.ScanCommand);
                add.Content.Should().NotBeOfType<string>();
                scan.Content.Should().NotBeOfType<string>();
                Border[] rows = page.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("ChapterRow")).ToArray();
                rows.Should().HaveCount(2);
                rows.Should().OnlyContain(border => border.CornerRadius == new CornerRadius(6) &&
                                                    border.BorderThickness == new Thickness(1));
                SavePreview(window, 1200, "chapter-header");

                Point Center(Border handle)
                {
                    return handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!
                        .Value;
                }

                Point start = Center(handles[0]), target = Center(handles[1]);
                string baseline = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value.VersionToken;
                RawInputModifiers modifier = asChild ? RawInputModifiers.Control : RawInputModifiers.None;
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left, modifier);
                window.MouseMove(target, modifier | RawInputModifiers.LeftMouseButton);
                window.MouseUp(target, MouseButton.Left, modifier);
                ChapterEditingSnapshot result = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
                for (int attempt = 0; attempt < 100 && result.VersionToken == baseline; attempt++)
                {
                    await Task.Delay(20);
                    Pump(window);
                    result = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
                }

                navigationCount.Should().Be(0);
                if (asChild)
                {
                    result.Definition.Chapters.Should().ContainSingle().Which.ChapterId.Should().Be("second");
                    result.Definition.Chapters[0].Children.Should().ContainSingle().Which.ChapterId.Should()
                        .Be("first");
                    await workspace.ShowChaptersTabCommand.ExecuteAsync();
                    workspace.ChapterNodes.First(node => node.HasChildren).IsExpanded = true;
                    SavePreview(window, 1200, "chapter-expanded");
                }
                else
                {
                    result.Definition.Chapters.Select(n => n.ChapterId).Should().Equal("second", "first");
                }

                workspace.IsEditMode.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Late_scan_completion_after_reset_cannot_restore_old_candidates()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        ChapterSidebarServices services = Services(document);
        GatedScanner scanner = new();
        StrongBox<DocumentInstanceId?> current = new(document.DocumentId);
        using ChapterSidebarViewModel sidebar = new(() => Task.FromResult(services with { Scanner = scanner }),
            () => current.Value, () => 1, _ => Task.CompletedTask, () => Task.CompletedTask);
        Task pending = sidebar.ScanCommand.ExecuteAsync();
        await scanner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        current.Value = null;
        sidebar.Reset();
        ChapterNode old = ChapterNode.Leaf("old", "Old document", new NavigationPosition(1));
        scanner.Completion.SetResult(Result<ChapterScanResult>.Success(new ChapterScanResult(document.DocumentId,
            [], [old], [], [], [])));
        await pending;
        sidebar.PreviewRows.Should().BeEmpty();
        sidebar.HasPreview.Should().BeFalse();
        sidebar.IsBusy.Should().BeFalse();
        sidebar.Status.Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_while_saved_map_reload_is_pending_does_not_restore_old_selection()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        ChapterSidebarServices services = Services(document);
        TaskCompletionSource reloadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StrongBox<DocumentInstanceId?> current = new(document.DocumentId);
        using ChapterSidebarViewModel sidebar = new(() => Task.FromResult(services), () => current.Value, () => 1,
            _ => Task.CompletedTask, async () =>
            {
                reloadEntered.TrySetResult();
                await reloadGate.Task;
            });
        await sidebar.BeginAddAsync(null, false);
        sidebar.Title = "Saved chapter";
        Task saving = sidebar.SaveCommand.ExecuteAsync();
        await reloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        current.Value = null;
        sidebar.Reset();
        reloadGate.TrySetResult();
        await saving;
        sidebar.SelectedChapterId.Should().BeNull();
        sidebar.IsFormOpen.Should().BeFalse();
        sidebar.Status.Should().BeEmpty();
    }

    [Fact]
    public async Task Reloading_a_conflict_does_not_report_success_when_the_edited_chapter_was_deleted()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = document.CreateMapService();
        ChapterEditingSnapshot initial = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        ChapterEditingSnapshot added = (await maps.SaveChaptersAsync(document.DocumentId,
            new ChapterSaveRequest(initial.VersionToken,
                [ChapterNode.Leaf("chapter", "Original", new NavigationPosition(1))]))).Value;
        using ChapterSidebarViewModel sidebar = Sidebar(document);
        await sidebar.BeginEditAsync("chapter");
        sidebar.Title = "Keep this draft";
        (await maps.SaveChaptersAsync(document.DocumentId, new ChapterSaveRequest(added.VersionToken, []))).IsSuccess
            .Should().BeTrue();
        await sidebar.SaveCommand.ExecuteAsync();
        sidebar.HasConflict.Should().BeTrue();
        await sidebar.ReloadBaselineCommand.ExecuteAsync();
        await sidebar.SaveCommand.ExecuteAsync();
        sidebar.IsFormOpen.Should().BeTrue();
        sidebar.Title.Should().Be("Keep this draft");
        sidebar.Status.Should().Contain("已被其他操作删除");
        (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value.Definition.Chapters.Should().BeEmpty();
    }

    private static ChapterSidebarViewModel Sidebar(PageMapTestDocument document)
    {
        return new ChapterSidebarViewModel(
            () => Task.FromResult(Services(document)), () => document.DocumentId, () => 1,
            _ => Task.CompletedTask, () => Task.CompletedTask);
    }

    private static ChapterSidebarServices Services(PageMapTestDocument document)
    {
        MarkdigMarkdownEngine markdown = new();
        return new ChapterSidebarServices(document.CreateMapService(),
            new ChapterScanService(document.Pages, document.Trees,
                new DocumentMarkdownCompiler(document.Trees, markdown), markdown),
            document.Pages, document.Trees);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void SavePreview(Window window, int width, string state)
    {
        Pump(window);
        string directory = TestPaths.FromRepositoryRoot(".tmp", "tests", "chapter-editor-previews");
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        frame!.Save(Path.Combine(directory, $"{state}-{width}.png"));
    }

    private sealed class GatedScanner : IChapterScanService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<Result<ChapterScanResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<ChapterScanResult>> ScanAsync(DocumentInstanceId documentInstanceId,
            ChapterScanOptions? options = null, IProgress<ChapterScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return Completion.Task;
        }
    }
}
