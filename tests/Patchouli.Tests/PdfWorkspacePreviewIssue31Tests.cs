using Avalonia.Headless;
using Avalonia.Media.Imaging;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Ocr;
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
[Collection("Avalonia")]
public sealed class PdfWorkspacePreviewIssue31Tests : IDisposable
{
    // One process-wide headless session, never disposed: in this environment
    // HeadlessUnitTestSession.Dispose() can deadlock on session shutdown (verified
    // with a minimal repro: StartNew + Dispatch + any post-await work + Dispose
    // hangs, while the identical flow without Dispose completes). This mirrors the
    // repository's own AvaloniaSharedSessionFixture pattern; per-test isolation is
    // retained because every test drives its own MainWindowViewModel via Dispatch.
    private static readonly HeadlessUnitTestSession SharedSession =
        HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Failed_render_result_keeps_the_previous_page_on_screen()
    {
        HeadlessUnitTestSession session = SharedSession;
        await session.Dispatch(async () =>
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
        await session.Dispatch(async () =>
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
        await session.Dispatch(async () =>
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
        await session.Dispatch(async () =>
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
        await session.Dispatch(async () =>
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

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Result<PdfPagePixelBufferLease> SuccessLease(int size)
    {
        return Result<PdfPagePixelBufferLease>.Success(new PdfPagePixelBufferLease(
            new byte[size * size * 4], size, size, size * 4, 0, "test", size, size, "v1"));
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
        Patchouli.Host.Composition.HostServices services = await vm.ServicesAsync();
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
