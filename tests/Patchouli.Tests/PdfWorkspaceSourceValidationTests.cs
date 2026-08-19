using Avalonia;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

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
        AppServices services = await vm.ServicesAsync();
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
