using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Performance;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// Issue #31, plan test item 6: preview rendering is render-then-swap. When the
/// next page fails to render — whether the render returns a failure result or
/// throws (after the bounded retries) — the page already on screen stays, and
/// only the status line reports the error. A superseded generation's result is
/// never committed: neither its image nor its failure may overwrite the newer
/// page the user is looking at. (The stale-generation success case is also
/// covered by the pre-existing
/// <see cref="PdfWorkspaceSourceValidationTests.Rapid_page_flipping_discards_obsolete_renders_and_applies_latest"/>.)
/// </summary>
[Collection("RetainedTabUI")]
public sealed class PdfWorkspacePreviewIssue31Tests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private HeadlessUnitTestSession SharedSession => headless.Session;

    private readonly TemporaryAppSettingsFile _settings = new();
    private MainWindowViewModel? _mainWindow;

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Failed_render_result_keeps_the_previous_page_on_screen()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-failure-result"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(SuccessLease(10));
            await workspace.LoadAsync();
            Bitmap? firstImage = workspace.Image;
            firstImage.Should().NotBeNull();
            workspace.WidthPixels.Should().Be(10);

            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(
                Result<PdfPagePixelBufferLease>.Failure("page_render_failed", "injected preview failure"));
            await workspace.NextPageCommand.ExecuteAsync();

            workspace.Image.Should().BeSameAs(firstImage,
                "render-then-swap: a failed render never clears the page on screen");
            workspace.WidthPixels.Should().Be(10);
            workspace.Status.Should().Contain("ERROR").And.Contain("page_render_failed",
                "the failure is visible on the status line instead of a blank page");

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Throwing_render_is_retried_twice_and_still_keeps_the_previous_page()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-failure-throw"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(SuccessLease(10));
            await workspace.LoadAsync();
            Bitmap? firstImage = workspace.Image;
            firstImage.Should().NotBeNull();

            int attempts = 0;
            workspace.PageRenderPreviewHandler = async (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                await Task.CompletedTask;
                throw new InvalidOperationException("render blew up");
            };
            await workspace.NextPageCommand.ExecuteAsync();

            attempts.Should().Be(3, "the initial attempt plus two bounded retries");
            workspace.Image.Should().BeSameAs(firstImage);
            workspace.WidthPixels.Should().Be(10);
            workspace.Status.Should().Contain("PDF 预览失败");

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stale_generation_failure_does_not_replace_the_newer_page()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-stale-failure-image"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            TaskCompletionSource slowStarted = NewSignal();
            TaskCompletionSource slowRelease = NewSignal();
            workspace.PageRenderPreviewHandler = async (_, _) =>
            {
                if (workspace.PageIndex == 0)
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task;
                    return Result<PdfPagePixelBufferLease>.Failure(
                        "page_render_failed", "stale failure");
                }

                return SuccessLease(20);
            };

            Task loadTask = workspace.LoadAsync();
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await workspace.NextPageCommand.ExecuteAsync();
            Bitmap? newerImage = workspace.Image;
            newerImage.Should().NotBeNull();
            workspace.WidthPixels.Should().Be(20);

            slowRelease.TrySetResult();
            await loadTask;

            workspace.Image.Should().BeSameAs(newerImage,
                "the superseded generation's failure must not clear or replace the newer page");
            workspace.WidthPixels.Should().Be(20);

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stale_generation_success_does_not_replace_the_newer_page()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-stale-success-image"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            TaskCompletionSource slowStarted = NewSignal();
            TaskCompletionSource slowRelease = NewSignal();
            workspace.PageRenderPreviewHandler = async (_, _) =>
            {
                if (workspace.PageIndex == 0)
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task;
                    return SuccessLease(10);
                }

                return SuccessLease(20);
            };

            Task loadTask = workspace.LoadAsync();
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await workspace.NextPageCommand.ExecuteAsync();
            Bitmap? newerImage = workspace.Image;
            newerImage.Should().NotBeNull();
            workspace.WidthPixels.Should().Be(20);

            slowRelease.TrySetResult();
            await loadTask;

            workspace.Image.Should().BeSameAs(newerImage,
                "the superseded generation's late success must be discarded, not swapped in");
            workspace.WidthPixels.Should().Be(20);

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stale_generation_failure_does_not_publish_its_error_to_the_status_line()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-stale-failure-status"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            TaskCompletionSource slowStarted = NewSignal();
            TaskCompletionSource slowRelease = NewSignal();
            workspace.PageRenderPreviewHandler = async (_, _) =>
            {
                if (workspace.PageIndex == 0)
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task;
                    return Result<PdfPagePixelBufferLease>.Failure(
                        "page_render_failed", "stale failure");
                }

                return SuccessLease(20);
            };

            Task loadTask = workspace.LoadAsync();
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await workspace.NextPageCommand.ExecuteAsync();
            string statusAfterNewerPage = workspace.Status;

            slowRelease.TrySetResult();
            await loadTask;

            workspace.Status.Should().Be(statusAfterNewerPage,
                "only the current generation may publish its failure; a stale failure is discarded like a stale image");
            workspace.Status.Should().NotContain("ERROR");

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Transient_result_failure_is_retried_and_the_next_attempt_recovers()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-transient-result"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(SuccessLease(10));
            await workspace.LoadAsync();
            Bitmap? firstImage = workspace.Image;
            firstImage.Should().NotBeNull();

            // The production render service reports a renderer timeout as a failed
            // Result (not an exception); that channel must retry just like a throw.
            int attempts = 0;
            workspace.PageRenderPreviewHandler = (_, _) =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                return Task.FromResult(attempt == 1
                    ? Result<PdfPagePixelBufferLease>.Failure(
                        PageRenderStatus.RendererTimeout, "PDF renderer timed out.")
                    : SuccessLease(20));
            };
            await workspace.NextPageCommand.ExecuteAsync();

            attempts.Should().Be(2, "a transient Result failure is retried once and then recovers");
            workspace.Image.Should().NotBeSameAs(firstImage);
            workspace.WidthPixels.Should().Be(20);

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Persistent_transient_result_failure_exhausts_retries_and_keeps_the_previous_page()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-persistent-transient"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(SuccessLease(10));
            await workspace.LoadAsync();
            Bitmap? firstImage = workspace.Image;
            firstImage.Should().NotBeNull();

            int attempts = 0;
            workspace.PageRenderPreviewHandler = (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(Result<PdfPagePixelBufferLease>.Failure(
                    PageRenderStatus.RenderFailed, "PDF preview render failed: injected render failure"));
            };
            await workspace.NextPageCommand.ExecuteAsync();

            attempts.Should().Be(3, "the initial attempt plus two bounded retries, then it gives up");
            workspace.Image.Should().BeSameAs(firstImage,
                "render-then-swap: exhausting the retries still never clears the page on screen");
            workspace.WidthPixels.Should().Be(10);
            workspace.Status.Should().Contain("ERROR").And.Contain(PageRenderStatus.RenderFailed);

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Deterministic_result_failure_is_not_retried()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-deterministic-result"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(SuccessLease(10));
            await workspace.LoadAsync();
            Bitmap? firstImage = workspace.Image;
            firstImage.Should().NotBeNull();

            int attempts = 0;
            workspace.PageRenderPreviewHandler = (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(Result<PdfPagePixelBufferLease>.Failure(
                    AppErrorCodes.NotFound, "Page was not found."));
            };
            await workspace.NextPageCommand.ExecuteAsync();

            attempts.Should().Be(1, "a deterministic failure can never be fixed by rendering again");
            workspace.Image.Should().BeSameAs(firstImage);
            workspace.WidthPixels.Should().Be(10);
            workspace.Status.Should().Contain("ERROR").And.Contain(AppErrorCodes.NotFound);

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stale_generation_boxes_load_never_overwrites_the_newer_page()
    {
        HeadlessUnitTestSession session = SharedSession;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel vm = CreateMainWindow(CreateDatabasePath("preview-stale-boxes"));
            await OpenImportedItemAsync(vm, CreatePdfPath());
            LibraryItemViewModel item = vm.Shell.Items.Single();
            await vm.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)vm.ActiveTab!.Content!;

            // Seed one text box per page so the two generations publish observably
            // different box sets, and capture the real boxes per current revision.
            Host.Composition.HostServices services = await vm.ServicesAsync();
            DocumentInstanceId documentInstanceId = DocumentInstanceId.Parse(item.DocumentInstanceId!);
            IReadOnlyList<Page> pages =
                (await services.Pages.ListPagesAsync(documentInstanceId)).Value;
            await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                documentInstanceId, pages[0].PageId, "page zero box");
            await BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                documentInstanceId, pages[1].PageId, "page one box");
            DocumentTreeRevisionId pageZeroRevision =
                (await services.DocumentTrees.GetCurrentRevisionAsync(documentInstanceId, pages[0].PageId))
                .Value.TreeRevisionId;
            DocumentTreeRevisionId pageOneRevision =
                (await services.DocumentTrees.GetCurrentRevisionAsync(documentInstanceId, pages[1].PageId))
                .Value.TreeRevisionId;
            Dictionary<DocumentTreeRevisionId, IReadOnlyList<DocumentBox>> realBoxes = new()
            {
                [pageZeroRevision] =
                    (await services.DocumentTrees.ListBoxesAsync(pageZeroRevision)).Value,
                [pageOneRevision] =
                    (await services.DocumentTrees.ListBoxesAsync(pageOneRevision)).Value
            };
            IReadOnlyList<DocumentTreeRevisionId> pageOneRevisionIds =
                (await services.DocumentTrees.ListRevisionsAsync(documentInstanceId, pages[1].PageId))
                .Value.Select(revision => revision.TreeRevisionId).ToList();

            // Generation 1 renders instantly but its box load is gated; generation 2
            // (next page) runs to completion while generation 1 is still parked.
            TaskCompletionSource boxesStarted = NewSignal();
            TaskCompletionSource boxesRelease = NewSignal();
            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(
                workspace.PageIndex == 0 ? SuccessLease(10) : SuccessLease(20));
            workspace.DocumentBoxesLoadHandler = async revisionId =>
            {
                if (revisionId == pageZeroRevision)
                {
                    boxesStarted.TrySetResult();
                    await boxesRelease.Task;
                }

                return realBoxes[revisionId];
            };

            Task loadTask = workspace.LoadAsync();
            await boxesStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await workspace.NextPageCommand.ExecuteAsync();

            workspace.PageIndex.Should().Be(1);
            workspace.WidthPixels.Should().Be(20);
            Bitmap? newerImage = workspace.Image;
            newerImage.Should().NotBeNull();
            workspace.BoundingBoxes.Select(box => box.Text).Should().Equal(new[] { "page one box" });
            string statusAfterNewerPage = workspace.Status;
            statusAfterNewerPage.Should().Contain("第 2/3 页");
            workspace.PageRevisions.Select(row => row.RevisionId).Should().Equal(pageOneRevisionIds);

            boxesRelease.TrySetResult();
            await loadTask;

            workspace.Image.Should().BeSameAs(newerImage,
                "the superseded generation's late box load must not touch the newer page");
            workspace.WidthPixels.Should().Be(20);
            workspace.BoundingBoxes.Select(box => box.Text).Should().Equal(new[] { "page one box" },
                "the stale generation's boxes are discarded, never committed over the newer page");
            workspace.Status.Should().Be(statusAfterNewerPage);
            workspace.PageRevisions.Select(row => row.RevisionId).Should().Equal(pageOneRevisionIds,
                "the stale generation must not replace the newer page's revision rows");

            await ReleaseDocumentSessionAsync(vm, item);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Synchronous_tree_loading_keeps_the_shared_heartbeat_within_budget()
    {
        await DispatchAndShutdownAsync(SharedSession, async () =>
        {
            MainWindowViewModel main = CreateMainWindow(CreateDatabasePath("slow-tree-heartbeat"));
            await OpenImportedItemAsync(main, CreatePdfPath());
            LibraryItemViewModel item = main.Shell.Items.Single();
            await main.ShowReadingAsync(item);
            PdfWorkspaceViewModel workspace = (PdfWorkspaceViewModel)main.ActiveTab!.Content!;
            Host.Composition.HostServices services = await main.ServicesAsync();
            DocumentInstanceId documentId = DocumentInstanceId.Parse(item.DocumentInstanceId!);
            IReadOnlyList<Page> pages = (await Task.Run(() => services.Pages.ListPagesAsync(documentId))).Value;
            await Task.Run(() => BoxTreeTestData.CommitTextAsync(services.ConnectionFactory, services.Clock,
                documentId, pages[1].PageId, "A committed page is needed to exercise the tree loader."));
            int treeLoads = 0;
            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(SuccessLease(10));
            workspace.DocumentBoxesLoadHandler = _ =>
            {
                Interlocked.Increment(ref treeLoads);
                Dispatcher.UIThread.CheckAccess().Should().BeFalse();
                Thread.Sleep(700);
                return Task.FromResult<IReadOnlyList<DocumentBox>>([]);
            };

            UiHeartbeatMeasurement heartbeat =
                await UiHeartbeatProbe.MeasureAsync(() => workspace.NextPageCommand.ExecuteAsync());

            treeLoads.Should().Be(1, "the measurement must actually enter the blocking tree substitute");
            heartbeat.Samples.Should().BeGreaterThanOrEqualTo(3);
            heartbeat.MaximumGapMs.Should().BeLessThan(500);
            workspace.PageIndex.Should().Be(1);
            await ReleaseDocumentSessionAsync(main, item);
            return true;
        }, CancellationToken.None);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Result<PdfPagePixelBufferLease> SuccessLease(int size)
    {
        return Result<PdfPagePixelBufferLease>.Success(new PdfPagePixelBufferLease(
            new byte[size * size * 4], size, size, size * 4, 0, "test", size, size, "v1"));
    }

    private MainWindowViewModel CreateMainWindow(string databasePath)
    {
        return _mainWindow = new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path)
        {
            RuntimeDatabasePath = databasePath
        };
    }

    private async Task DispatchAndShutdownAsync(HeadlessUnitTestSession session, Func<Task<bool>> action,
        CancellationToken cancellationToken)
    {
        await session.Dispatch<bool>(async () =>
        {
            try
            {
                return await action();
            }
            finally
            {
                if (_mainWindow is not null)
                {
                    await _mainWindow.ShutdownAsync();
                    _mainWindow = null;
                }
            }
        }, cancellationToken);
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
        Host.Composition.HostServices services = await vm.ServicesAsync();
        PdfImportResult imported =
            await services.PdfImport.ImportPdfAsync(new PdfImportRequest(pdf, "Preview item", null, 3));
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
