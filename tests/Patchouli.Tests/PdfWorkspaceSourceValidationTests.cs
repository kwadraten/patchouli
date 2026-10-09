using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Patchouli.UI.Views;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.Host.Composition;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.Tests;

/// <summary>
/// AC16: the PDF workspace exposes a lazy source-validation state that the UI can bind to,
/// stays interactive while validation is in flight, and clearly distinguishes the transient
/// "validating" state from a distinct source warning (source_changed / bbox_basis_stale).
/// </summary>
[Collection("Avalonia")]
public sealed class PdfWorkspaceSourceValidationTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Page_ocr_refreshes_the_open_working_box_tree_and_preview_without_committing(bool logicalPages)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            using MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path,
                modalOperations: new DirectModalRunner())
            {
                RuntimeDatabasePath = CreateDatabasePath("ui-page-ocr-working")
            };
            await OpenImportedItemAsync(main, CreatePdfPath());
            HostServices services = await main.ServicesAsync();
            services.OcrAdapters.RegisterAdapter(new WorkingPageAdapter());
            LibraryItemViewModel item = main.Shell.Items.Single();
            await item.ViewPdfCommand.ExecuteAsync();
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)main.ActiveTab!.Content!;
            await workspace.EnterEditModeCommand.ExecuteAsync();
            PageEditSession edit = (await services.DocumentTrees.GetPageEditAsync(workspace.EditSessionId!.Value))
                .Value;
            DocumentTreeRevision current = (await services.DocumentTrees.GetCurrentRevisionAsync(
                edit.DocumentInstanceId, edit.PageId)).Value;
            if (!logicalPages)
            {
                foreach (DocumentBox root in (await services.DocumentTrees.ListBoxesAsync(edit.DraftRevisionId)).Value
                         .Where(box => box.ParentBoxId is null))
                {
                    await services.DocumentTreeEditor.DeleteBoxAsync(edit.SessionId, root.BoxId);
                }

                await workspace.RefreshBoxesAsync();
            }

            await workspace.RunCurrentPageOcrCommand.ExecuteAsync();

            workspace.Status.Should().Contain("更新页面草稿").And.Contain("保存后生效");
            workspace.EditSessionId.Should().Be(edit.SessionId);
            workspace.BoundingBoxes.Should().Contain(box => box.Text == "new working text");
            workspace.ReadingScene.Should().NotBeNull();
            workspace.PreviewBlocks.Should().Contain(block => block.Markdown.Contains("new working text"));
            (await services.DocumentTrees.GetCurrentRevisionAsync(edit.DocumentInstanceId, edit.PageId)).Value
                .TreeRevisionId.Should().Be(current.TreeRevisionId);
            await workspace.CancelEditModeCommand.ExecuteAsync();
            workspace.BoundingBoxes.Should().NotContain(box => box.Text == "new working text");
            await ReleaseDocumentSessionAsync(main, item);
        }, CancellationToken.None);
    }

    private sealed class DirectModalRunner : IModalOperationRunner
    {
        public async Task<T> RunAsync<T>(ModalOperationOptions options, Func<ModalOperationContext, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            using BlockingOperationDialogViewModel dialog = new();
            return await operation(new ModalOperationContext(dialog, cancellationToken, false));
        }
    }

    private sealed class WorkingPageAdapter : IRealOcrAdapter
    {
        public string EngineId => OcrEngineIds.NdlKoten;
        public string DisplayName => "Working page test";
        public string Kind => OcrAdapterKind.LocalLibrary;

        public OcrEngineCapability GetCapability()
        {
            return new OcrEngineCapability(EngineId, DisplayName, false, false, true, false,
                true, false, false, false, false, [OcrInputKinds.PageImage, OcrInputKinds.RegionImage], "Test adapter");
        }

        public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrEnvironmentCheckResult(
                EngineId, presetVersion.ModelId, null, OcrEnvironmentStatus.Ready, true, "Ready",
                OcrRequiredAction.None, []));
        }

        public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ValidateInputAsync(OcrInputDescriptor input,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrEnginePageResult>.Success(
                new OcrEnginePageResult(input.PageId, true, "new working text",
                    input.RegionBBox ?? new NormalizedBBox(0, 0, 1, 1), null, null)));
        }
    }

    [Fact]
    public async Task Marquee_and_control_selection_delete_boxes_and_parent_subtrees_from_draft()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = CreateMainWindow(CreateDatabasePath("ui-pdf-selection"));
            await OpenImportedItemAsync(main, CreatePdfPath());
            LibraryItemViewModel item = main.Shell.Items.Single();
            await main.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)main.ActiveTab!.Content!;
            await workspace.EnterEditModeCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            PageEditSessionId edit = workspace.EditSessionId!.Value;
            DocumentBox parent = (await services.DocumentTreeEditor.InsertLogicalPageAsync(
                edit, null, new NormalizedBBox(0, 0, 1, 1))).Value;
            DocumentBox first = (await services.DocumentTreeEditor.DrawAndInsertLeafAsync(edit,
                new InsertLeafCommand(parent.BoxId, null, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.1, .1, .2, .2), new TextBoxPayload("First")))).Value;
            DocumentBox second = (await services.DocumentTreeEditor.DrawAndInsertLeafAsync(edit,
                new InsertLeafCommand(parent.BoxId, first.BoxId, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.5, .1, .2, .2), new TextBoxPayload("Second")))).Value;
            await workspace.RefreshBoxesAsync();

            await workspace.SaveAndExitCommand.ExecuteAsync();
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = new() { Content = page, Width = 1280, Height = 900 };
            window.Show();
            MenuFlyout menu = (MenuFlyout)FlyoutBase.GetAttachedFlyout(page)!;

            void RightClickFirstBox()
            {
                window.Measure(new Size(1280, 900));
                window.Arrange(new Rect(0, 0, 1280, 900));
                Border border = page.GetVisualDescendants().OfType<Border>().Single(control =>
                    control.Classes.Contains("BBox") &&
                    control.DataContext is PdfBBoxViewModel box && box.BoxId == first.BoxId);
                Point position =
                    border.TranslatePoint(new Point(border.Bounds.Width / 2, border.Bounds.Height / 2), window)!.Value;
                window.MouseDown(position, MouseButton.Right, RawInputModifiers.None);
                window.MouseUp(position, MouseButton.Right, RawInputModifiers.None);
            }

            RightClickFirstBox();
            menu.IsOpen.Should().BeFalse();
            await workspace.EnterEditModeCommand.ExecuteAsync();
            edit = workspace.EditSessionId!.Value;
            RightClickFirstBox();
            menu.IsOpen.Should().BeTrue();
            menu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, "删除"))
                .Command.Should().BeSameAs(workspace.DeleteSelectedCommand);
            menu.Hide();
            window.Close();

            await workspace.MarqueeToolCommand.ExecuteAsync();
            workspace.OnPointerPressed(.05 * workspace.ActualWidthPixels, .05 * workspace.ActualHeightPixels);
            workspace.OnPointerMoved(.75 * workspace.ActualWidthPixels, .35 * workspace.ActualHeightPixels);
            workspace.OnPointerReleased(false);
            workspace.SelectedBoxes.Select(box => box.BoxId).Should().BeEquivalentTo([first.BoxId, second.BoxId]);
            workspace.IsSelectToolActive.Should().BeTrue();
            PdfBBoxViewModel parentView = workspace.BoundingBoxes.Single(box => box.BoxId == parent.BoxId);
            workspace.SelectedBoxes.Should().OnlyContain(box => box.ZIndex > parentView.ZIndex);
            PdfBBoxViewModel firstView = workspace.SelectedBoxes.Single(box => box.BoxId == first.BoxId);
            workspace.SelectBox(firstView, true);
            workspace.SelectedBoxes.Should().ContainSingle().Which.BoxId.Should().Be(second.BoxId);
            workspace.SelectBox(firstView, true);
            await workspace.DeleteSelectedCommand.ExecuteAsync();
            workspace.BoundingBoxes.Should().ContainSingle().Which.BoxId.Should().Be(parent.BoxId);

            DocumentBox child = (await services.DocumentTreeEditor.DrawAndInsertLeafAsync(edit,
                new InsertLeafCommand(parent.BoxId, null, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.1, .1, .2, .2), new TextBoxPayload("Child")))).Value;
            await workspace.RefreshBoxesAsync();
            workspace.SelectBox(workspace.BoundingBoxes.Single(box => box.BoxId == parent.BoxId), false);
            workspace.SelectBox(workspace.BoundingBoxes.Single(box => box.BoxId == child.BoxId), true);
            await workspace.DeleteSelectedCommand.ExecuteAsync();
            workspace.BoundingBoxes.Should().BeEmpty();
            workspace.Status.Should().NotContain("失败");
            await workspace.CancelEditModeCommand.ExecuteAsync();
            await ReleaseDocumentSessionAsync(main, item);
        }, CancellationToken.None);
    }

    [Fact]
    public void Workspace_starts_unverified_and_not_validating_or_warning()
    {
        MainWindowViewModel vm = new(new FakeClipboard(), settingsPath: _settings.Path);
        PdfWorkspaceViewModel pdf = new(vm, new LibraryItemViewModel(
            ItemId.New().ToString(), "Title", "book", "", "", "", null, null, null, "source.pdf", "", 0, 0, "",
            _ => Task.CompletedTask, _ => Task.CompletedTask));

        pdf.SourceValidationState.Should().Be(SourceValidationStatus.Unverified);
        pdf.IsSourceValidating.Should().BeFalse();
        pdf.HasSourceWarning.Should().BeFalse();
        pdf.SourceWarning.Should().BeNull();
    }

    [Fact]
    public async Task Successful_render_transitions_validating_then_current_and_stays_interactive()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string pdf = CreatePdfPath();
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("ui-pdf-srcval"));
            await OpenImportedItemAsync(vm, pdf);
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            List<string> states = [];
            List<bool> interactiveWhileValidating = [];
            workspace.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PdfWorkspaceViewModel.SourceValidationState))
                {
                    states.Add(workspace.SourceValidationState);
                }

                if (workspace.IsSourceValidating)
                {
                    interactiveWhileValidating.Add(workspace.ZoomInCommand.CanExecute(null));
                    interactiveWhileValidating.Add(workspace.NextPageCommand.CanExecute(null));
                }
            };

            await workspace.LoadAsync();

            workspace.SourceValidationState.Should().Be(SourceValidationStatus.Current);
            workspace.IsSourceValidating.Should().BeFalse();
            workspace.HasSourceWarning.Should().BeFalse();
            workspace.SourceWarning.Should().BeNull();
            states.Should().ContainInOrder(SourceValidationStatus.Validating, SourceValidationStatus.Current);
            interactiveWhileValidating.Should()
                .Contain(true, "the UI must remain interactive while source validation is in flight");
            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Changed_source_shows_a_warning_distinct_from_validating()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            string pdf = CreatePdfPath();
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("ui-pdf-srcval-warn"));
            await OpenImportedItemAsync(vm, pdf);
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;
            await workspace.LoadAsync();
            workspace.SourceValidationState.Should().Be(SourceValidationStatus.Current);

            await File.AppendAllTextAsync(pdf, "mutated");
            List<string> states = [];
            workspace.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PdfWorkspaceViewModel.SourceValidationState))
                {
                    states.Add(workspace.SourceValidationState);
                }
            };

            await workspace.LoadAsync();

            workspace.SourceValidationState.Should().Be(SourceValidationStatus.Changed);
            workspace.IsSourceValidating.Should().BeFalse();
            workspace.HasSourceWarning.Should().BeTrue();
            workspace.SourceWarning.Should().Contain("bbox_basis_stale");
            states.Should().Contain(SourceValidationStatus.Validating)
                .And.Contain(SourceValidationStatus.Changed);
            workspace.IsSourceValidating.Should().NotBe(workspace.HasSourceWarning,
                "validating and warning must be mutually exclusive, distinct states");
            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Rapid_page_flipping_discards_obsolete_renders_and_applies_latest()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("ui-pdf-rapid-flip"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            TaskCompletionSource<bool> slowRenderTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> slowRenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

            workspace.PageRenderPreviewHandler = async (request, ct) =>
            {
                if (workspace.PageIndex == 0)
                {
                    slowRenderStarted.TrySetResult(true);
                    await slowRenderTrigger.Task;
                    return Result<PdfPagePixelBufferLease>.Success(
                        new PdfPagePixelBufferLease(new byte[400], 10, 10, 40, 0, "test", 10, 10, "v1"));
                }
                else
                {
                    return Result<PdfPagePixelBufferLease>.Success(
                        new PdfPagePixelBufferLease(new byte[800], 20, 20, 80, 0, "test", 20, 20, "v1"));
                }
            };

            Task loadTask = workspace.LoadAsync();
            await slowRenderStarted.Task;

            // Quickly navigate to next page while page 0 is still in flight
            Task nextTask = workspace.NextPageCommand.ExecuteAsync();

            // Release page 0 render after page 1 has superseded it
            slowRenderTrigger.TrySetResult(true);

            await Task.WhenAll(loadTask, nextTask);

            workspace.PageIndex.Should().Be(1);
            workspace.WidthPixels.Should().Be(20, "latest page render wins and obsolete page 0 render is discarded");

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Clear_cancels_in_flight_render_and_leaves_workspace_clean()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("ui-pdf-clear-render"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            TaskCompletionSource<bool> slowRenderTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> slowRenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

            workspace.PageRenderPreviewHandler = async (request, ct) =>
            {
                slowRenderStarted.TrySetResult(true);
                await slowRenderTrigger.Task;
                return Result<PdfPagePixelBufferLease>.Success(
                    new PdfPagePixelBufferLease(new byte[400], 10, 10, 40, 0, "test", 10, 10, "v1"));
            };

            Task loadTask = workspace.LoadAsync();
            await slowRenderStarted.Task;
            workspace.IsBusy.Should().BeTrue();

            workspace.Clear();
            workspace.IsBusy.Should().BeFalse();
            workspace.Image.Should().BeNull();

            slowRenderTrigger.TrySetResult(true);
            await loadTask;

            workspace.Image.Should().BeNull("obsolete render after Clear is discarded and never applied");
            workspace.WidthPixels.Should().Be(0);

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void PdfWorkspace_view_binds_distinct_validating_and_warning_indicators_without_a_modal()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));

        xaml.Should().Contain("IsVisible=\"{Binding IsSourceValidating}\"");
        xaml.Should().Contain("Text=\"正在验证源文件...\"");
        xaml.Should().Contain("IsVisible=\"{Binding HasSourceWarning}\"");
        xaml.Should().Contain("Text=\"{Binding SourceWarning}\"");
        xaml.Should().NotContain("IsModal", "source validation must never block the UI with a modal");
        viewModel.Should().Contain("SourceValidationState")
            .And.Contain("SourceValidationStatus.Validating")
            .And.Contain("bbox_basis_stale")
            .And.Contain("ClearSourceValidation");
    }

    private MainWindowViewModel CreateMainWindow(string databasePath)
    {
        return new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path)
        {
            RuntimeDatabasePath = databasePath
        };
    }

    private string CreateDatabasePath(string prefix)
    {
        return _settings.CreateDatabasePath(prefix);
    }

    private string CreatePdfPath()
    {
        string root = Path.GetDirectoryName(_settings.Path)!;
        return Path.Combine(root, $"source-{Guid.NewGuid():N}.pdf");
    }

    private static async Task OpenImportedItemAsync(MainWindowViewModel vm, string pdf)
    {
        File.Copy(TestFixtures.RealThreePagePdf, pdf);
        await vm.OpenDatabaseCommand.ExecuteAsync();
        await vm.Library.CreateCommand.ExecuteAsync();
        HostServices services = await vm.ServicesAsync();
        PdfImportResult imported =
            await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Source validation item", null, 3));
        imported.Success.Should().BeTrue(imported.ErrorMessage);
        await vm.Shell.RefreshItemsAsync();
    }

    private static async Task ReleaseDocumentSessionAsync(MainWindowViewModel vm, LibraryItemViewModel item)
    {
        if (!string.IsNullOrWhiteSpace(item.DocumentInstanceId))
        {
            await (await vm.ServicesAsync()).PageRenders.ReleaseDocumentSessionAsync(
                DocumentInstanceId.Parse(item.DocumentInstanceId));
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

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult(Text);
        }
    }
}
