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
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.Host.Composition;

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
