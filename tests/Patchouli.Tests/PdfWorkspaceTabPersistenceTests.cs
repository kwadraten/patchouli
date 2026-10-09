using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAssertions;
using Patchouli.Core.Layout;
using Patchouli.Core.Ids;
using Patchouli.Host.Composition;
using Patchouli.Reading;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.Reading;
using Patchouli.UI.ViewModels;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <summary>
/// Regression coverage for a PDF workspace page that remains alive while another workspace tab
/// is active. The existing BookReadingModeTests cover initial anchoring and recreated views; this
/// fixture covers the retained-page lifecycle contract.
/// </summary>
[Collection("Avalonia")]
public sealed class PdfWorkspaceTabPersistenceTests : IDisposable
{
    private const string DocumentInstanceGuid = "00000000-0000-0000-0000-000000000001";
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Deactivate_and_reactivate_preserves_both_scroll_offsets_and_reading_scene()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch<int>(async () =>
        {
            using MainWindowViewModel main = CreateMainWindow();
            using PdfWorkspaceViewModel workspace = CreateWorkspace(main);
            FakeBookReadingStream stream = new(30, true);
            workspace.BookReadingStreamFactory = _ => stream;
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = Show(page, 1280, 620);
            using WriteableBitmap pdfBitmap = CreateTallPdfBitmap();
            try
            {
                SetPrivateProperty(workspace, "Image", pdfBitmap);
                SetPrivateProperty(workspace, "WidthPixels", 800);
                SetPrivateProperty(workspace, "HeightPixels", 1800);
                ScrollViewer pdfScroller = page.FindControl<ScrollViewer>("PdfScrollViewer")!;
                pdfScroller.Offset = new Vector(0, 80);
                window.UpdateLayout();
                pdfScroller.Offset.Y.Should().BeGreaterThan(0,
                    "the fake PDF bitmap makes the PDF canvas scrollable");
                double pdfOffset = pdfScroller.Offset.Y;
                IWorkspaceTabPage lifecycle = page;
                lifecycle.OnTabDeactivated();
                lifecycle.OnTabActivated();
                pdfScroller.Offset.Y.Should().Be(pdfOffset,
                    "switching away and back while the PDF surface is visible retains its offset");

                await workspace.EnterBookReadingCommand.ExecuteAsync();
                window.UpdateLayout();
                ScrollViewer bookScroller = page.FindControl<ScrollViewer>("BookReadingScroller")!;
                bookScroller.Offset = new Vector(0, 650);
                window.UpdateLayout();
                bookScroller.Offset.Y.Should().BeGreaterThan(0,
                    "tall fake reading pages make the reader scrollable");
                double bookOffset = bookScroller.Offset.Y;
                ReadingScene? scene = page.FindControl<ReadingView>("BookReadingView")!.SourceScene;

                lifecycle.OnTabDeactivated();
                lifecycle.OnTabActivated();

                page.FindControl<ScrollViewer>("BookReadingScroller")!.Offset.Y.Should().Be(bookOffset);
                page.FindControl<ReadingView>("BookReadingView")!.SourceScene.Should().BeSameAs(scene,
                    "ordinary tab switches retain the page's already-built reading scene");
                window.Content.Should().BeSameAs(page, "the tab host keeps this page instance mounted");
                return 0;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Pages_delivered_while_inactive_are_reconciled_without_resetting_reading_position()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch<int>(async () =>
        {
            using MainWindowViewModel main = CreateMainWindow();
            using PdfWorkspaceViewModel workspace = CreateWorkspace(main);
            FakeBookReadingStream stream = new(50, true, 29);
            workspace.BookReadingStreamFactory = _ => stream;
            workspace.BookReadingStartPageOverride = 20;
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = Show(page, 1280, 620);
            try
            {
                await workspace.EnterBookReadingCommand.ExecuteAsync();
                window.UpdateLayout();

                ScrollViewer scroller = page.FindControl<ScrollViewer>("BookReadingScroller")!;
                scroller.Offset = new Vector(0, 700);
                window.UpdateLayout();
                scroller.Offset.Y.Should().BeGreaterThan(0);
                double offsetBeforeSwitch = scroller.Offset.Y;
                IWorkspaceTabPage lifecycle = page;
                lifecycle.OnTabDeactivated();

                Task forwardLoad = workspace.RequestBookReadingForwardAsync();
                await stream.BlockedPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
                page.FindControl<ReadingView>("BookReadingView")!.SourceScene!.Blocks
                    .Should().NotContain(block => block.Text.Contains("page 29 line", StringComparison.Ordinal),
                        "the requested page has not been delivered while its load is blocked");
                stream.ReleaseBlockedPage();
                await forwardLoad;
                lifecycle.OnTabActivated();
                window.UpdateLayout();

                page.FindControl<ReadingView>("BookReadingView")!.SourceScene!.Blocks
                    .Should().Contain(block => block.Text.Contains("page 29 line", StringComparison.Ordinal),
                        "the retained view catches up with pages delivered while it was unsubscribed");
                scroller.Offset.Y.Should().BeApproximately(offsetBeforeSwitch, 1,
                    "catch-up updates must not reapply the initial reading anchor");
                return 0;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Closing_page_detaches_from_workspace_events_and_disposes_view_resources()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch<int>(async () =>
        {
            using MainWindowViewModel main = CreateMainWindow();
            using PdfWorkspaceViewModel workspace = CreateWorkspace(main);
            workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(12, true);
            PdfWorkspacePage page = new() { DataContext = workspace };
            Window window = Show(page, 1280, 620);
            try
            {
                await workspace.EnterBookReadingCommand.ExecuteAsync();

                ReadingView readingView = page.FindControl<ReadingView>("BookReadingView")!;
                IWorkspaceTabPage lifecycle = page;
                lifecycle.OnTabClosed();

                readingView.SourceScene.Should().BeNull("closing releases the retained page scene");
                window.Content = null;
                workspace.ReplayBookReading();
                readingView.SourceScene.Should().BeNull("a closed page no longer receives workspace events");
                return 0;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Inactive_reading_view_cancels_pending_image_load_and_releases_its_stale_result()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch<int>(async () =>
        {
            using WriteableBitmap firstImage = new(
                new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using WriteableBitmap secondImage = new(
                new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            PendingImageSource imageSource = new(firstImage, secondImage);
            ReadingScene scene = new(
            [
                new ReadingBlock(null, "media", 0, "待加载图像", MediaLabel: "图像",
                    Image: new ReadingImageRegion("page:1", new NormalizedBBox(0, 0, 1, 1)))
            ]);
            ReadingView view = new() { SourceScene = scene, ImageSource = imageSource };
            Window window = Show(view, 640, 480);
            try
            {
                await imageSource.FirstLoadRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
                view.IsActive = false;
                imageSource.FirstCancellationToken.IsCancellationRequested.Should().BeTrue(
                    "deactivation cancels view-specific image work");

                view.Measure(new Size(640, 480));
                view.Arrange(new Rect(0, 0, 640, 480));
                imageSource.LoadCalls.Should().Be(1,
                    "layout while inactive must not start another image request");
                view.SourceScene.Should().BeSameAs(scene,
                    "deactivation preserves the scene and its layout input");

                imageSource.CompleteFirstLoad();
                await imageSource.FirstImageReleased.Task.WaitAsync(TimeSpan.FromSeconds(10));
                imageSource.ReleasedImages.Should().ContainSingle()
                    .Which.Should().BeSameAs(firstImage,
                        "a late result from a cancelled generation is released instead of cached");
                imageSource.LoadCalls.Should().Be(1,
                    "the stale completion does not restart work while the view is inactive");

                view.IsActive = true;
                await imageSource.SecondLoadRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
                imageSource.LoadCalls.Should().Be(2,
                    "reactivation resumes visible media requests");
                view.SourceScene.Should().BeSameAs(scene);
                return 0;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    private MainWindowViewModel CreateMainWindow()
    {
        return new MainWindowViewModel(settingsPath: _settings.Path);
    }

    private static PdfWorkspaceViewModel CreateWorkspace(MainWindowViewModel main)
    {
        LibraryItemViewModel item = new(
            ItemId.New().ToString(), "持久化测试", "book", "", "", "", null, DocumentInstanceGuid,
            null, "source.pdf", "", 0, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
        return new PdfWorkspaceViewModel(main, item);
    }

    private static Window Show(Control content, double width, double height)
    {
        Window window = new() { Content = content, Width = width, Height = height };
        window.Show();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        return window;
    }

    private static WriteableBitmap CreateTallPdfBitmap()
    {
        return new WriteableBitmap(
            new PixelSize(800, 1800), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    }

    private static void SetPrivateProperty<T>(object target, string propertyName, T value)
    {
        target.GetType().GetProperty(propertyName)!.SetValue(target, value);
    }

    private sealed class FakeBookReadingStream(
        int pageCount,
        bool tallPages,
        int? blockedPageIndex = null) : IBookReadingStream
    {
        public List<int> RequestedPages { get; } = [];

        public TaskCompletionSource<bool> BlockedPageRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _releaseBlockedPage =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseBlockedPage()
        {
            _releaseBlockedPage.TrySetResult(true);
        }

        public Task<IReadOnlyList<int>> ListPageIndicesAsync(
            DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((IReadOnlyList<int>)Enumerable.Range(0, pageCount).ToArray());
        }

        public async Task<BookReadingPage> LoadPageAsync(
            DocumentInstanceId documentInstanceId, int pageIndex, int pageCountValue,
            CancellationToken cancellationToken = default)
        {
            RequestedPages.Add(pageIndex);
            if (pageIndex == blockedPageIndex)
            {
                BlockedPageRequested.TrySetResult(true);
                await _releaseBlockedPage.Task.WaitAsync(cancellationToken);
            }

            int lineCount = tallPages ? 80 : 1;
            ReadingBlock[] blocks = Enumerable.Range(0, lineCount)
                .Select(line => new ReadingBlock(null, "paragraph", 0, $"page {pageIndex} line {line}",
                    PageIndex: pageIndex))
                .ToArray();
            return new BookReadingPage(pageIndex, pageCountValue, false, new ReadingScene(blocks));
        }
    }

    private sealed class PendingImageSource(IImage firstImage, IImage secondImage) : IReadingImageSource
    {
        private readonly TaskCompletionSource<IImage?> _firstLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCalls { get; private set; }
        public CancellationToken FirstCancellationToken { get; private set; }

        public TaskCompletionSource<bool> FirstLoadRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SecondLoadRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstImageReleased { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IImage> ReleasedImages { get; } = [];

        public async Task<IImage?> LoadImageAsync(string imageKey, CancellationToken cancellationToken)
        {
            LoadCalls++;
            if (LoadCalls == 1)
            {
                FirstCancellationToken = cancellationToken;
                FirstLoadRequested.TrySetResult(true);
                return await _firstLoad.Task;
            }

            SecondLoadRequested.TrySetResult(true);
            return secondImage;
        }

        public void ReleaseImage(string imageKey, IImage image)
        {
            ReleasedImages.Add(image);
            if (ReferenceEquals(image, firstImage))
            {
                FirstImageReleased.TrySetResult(true);
            }
        }

        public void CompleteFirstLoad()
        {
            _firstLoad.TrySetResult(firstImage);
        }
    }
}
