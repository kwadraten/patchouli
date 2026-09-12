using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.Reading;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// B1: the PDF workspace's whole-book reading mode. Verifies the view model toggles into and out
/// of reading mode, guards against items without a document instance, clamps and persists the
/// live font settings, and loads pages as a window around the current page: an initial window on
/// entry, on-demand batches near either edge, and a replay for views recreated on a tab switch.
/// The real <see cref="BookReadingStream" /> is covered by <c>BookReadingStreamTests</c>; these
/// tests inject a fake through <see cref="PdfWorkspaceViewModel.BookReadingStreamFactory" /> so
/// they never need a database.
/// </summary>
[Collection("Avalonia")]
public sealed class BookReadingModeTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    // A parseable document-instance id (the view model feeds it to DocumentInstanceId.Parse before
    // loading); the fake stream ignores it, but it must be a valid Guid.
    private const string DocumentInstanceGuid = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Enter_and_exit_toggle_IsBookReadingMode_with_a_streamed_document()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(0);

            workspace.IsBookReadingMode.Should().BeFalse();
            await workspace.EnterBookReadingCommand.ExecuteAsync();
            workspace.IsBookReadingMode.Should().BeTrue();
            workspace.BookReadingProgressText.Should().Be("该文档没有可阅读的页面。",
                "an empty document reports that there is nothing to read");

            workspace.ExitBookReadingCommand.Execute(null);
            workspace.IsBookReadingMode.Should().BeFalse();
            workspace.BookReadingProgressText.Should().BeEmpty("exit clears the progress indicator");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Enter_without_a_DocumentInstanceId_keeps_reading_mode_off_and_reports_status()
    {
        MainWindowViewModel main = new(settingsPath: _settings.Path);
        LibraryItemViewModel item = CreateItem(null);
        PdfWorkspaceViewModel workspace = new(main, item);
        workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(0);

        await workspace.EnterBookReadingCommand.ExecuteAsync();

        workspace.IsBookReadingMode.Should().BeFalse("an item without a document instance cannot be read");
        workspace.Status.Should().Contain("没有可阅读的文档实例");
    }

    [Fact]
    public async Task Enter_loads_only_the_initial_window_around_the_current_page()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            FakeBookReadingStream stream = new(30);
            workspace.BookReadingStreamFactory = _ => stream;
            List<BookReadingPage> delivered = [];
            workspace.BookReadingPageReady += page => delivered.Add(page);

            await workspace.EnterBookReadingCommand.ExecuteAsync();

            // Start page 0, so the initial window is the start page plus eight ahead; nothing
            // behind it exists. The other 21 pages stay unloaded.
            delivered.Select(page => page.PageIndex).Should().Equal(Enumerable.Range(0, 9));
            delivered.Should().OnlyContain(page => !page.IsPrepend);
            stream.RequestedPages.Should().Equal(Enumerable.Range(0, 9));
            workspace.BookReadingProgressText.Should().Be("已加载 9/30 页",
                "progress stays visible while pages remain unloaded");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Enter_preloads_the_pages_behind_the_start_page_after_the_pages_ahead()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            FakeBookReadingStream stream = new(30);
            workspace.BookReadingStreamFactory = _ => stream;
            workspace.BookReadingStartPageOverride = 20;
            List<BookReadingPage> delivered = [];
            workspace.BookReadingPageReady += page => delivered.Add(page);

            await workspace.EnterBookReadingCommand.ExecuteAsync();

            // The pages that follow the start page must arrive before the ones behind it, or the
            // view cannot anchor a prepend against its already-recorded successor.
            int[] expected = [20, 21, 22, 23, 24, 25, 26, 27, 28, 18, 19];
            delivered.Select(page => page.PageIndex).Should().Equal(expected);
            delivered.Select(page => page.IsPrepend).Should().Equal(
                false, false, false, false, false, false, false, false, false, true, true);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Forward_requests_load_the_next_batch_and_stop_at_the_end()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            FakeBookReadingStream stream = new(30);
            workspace.BookReadingStreamFactory = _ => stream;
            List<BookReadingPage> delivered = [];
            workspace.BookReadingPageReady += page => delivered.Add(page);
            await workspace.EnterBookReadingCommand.ExecuteAsync();
            delivered.Clear();

            await workspace.RequestBookReadingForwardAsync();

            delivered.Select(page => page.PageIndex).Should().Equal(Enumerable.Range(9, 8));
            delivered.Should().OnlyContain(page => !page.IsPrepend);
            stream.RequestedPages.Should().OnlyHaveUniqueItems();

            // Walk forward to the end, then a further request must not touch the stream.
            await workspace.RequestBookReadingForwardAsync();
            await workspace.RequestBookReadingForwardAsync();
            delivered.Select(page => page.PageIndex).Should().Equal(Enumerable.Range(9, 21));
            int requested = stream.RequestedPages.Count;
            await workspace.RequestBookReadingForwardAsync();
            stream.RequestedPages.Count.Should().Be(requested, "the last page is already loaded");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Backward_requests_load_the_previous_batch_as_prepends_and_stop_at_the_start()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            FakeBookReadingStream stream = new(30);
            workspace.BookReadingStreamFactory = _ => stream;
            workspace.BookReadingStartPageOverride = 20;
            List<BookReadingPage> delivered = [];
            workspace.BookReadingPageReady += page => delivered.Add(page);
            await workspace.EnterBookReadingCommand.ExecuteAsync();
            delivered.Clear();

            await workspace.RequestBookReadingBackwardAsync();

            delivered.Select(page => page.PageIndex).Should().Equal(Enumerable.Range(10, 8));
            delivered.Should().OnlyContain(page => page.IsPrepend);

            await workspace.RequestBookReadingBackwardAsync();
            await workspace.RequestBookReadingBackwardAsync();
            delivered.Select(page => page.PageIndex).Should().Equal(
                Enumerable.Range(10, 8).Concat(Enumerable.Range(2, 8)).Concat(Enumerable.Range(0, 2)));
            int requested = stream.RequestedPages.Count;
            await workspace.RequestBookReadingBackwardAsync();
            stream.RequestedPages.Count.Should().Be(requested, "page 0 is already loaded");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Requests_are_no_ops_outside_reading_mode()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            FakeBookReadingStream stream = new(30);
            workspace.BookReadingStreamFactory = _ => stream;

            await workspace.RequestBookReadingForwardAsync();
            await workspace.RequestBookReadingBackwardAsync();

            stream.ListCalls.Should().Be(0, "reading mode was never entered");
            stream.RequestedPages.Should().BeEmpty();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Replay_reannounces_the_start_and_every_delivered_page_in_order()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            FakeBookReadingStream stream = new(30);
            workspace.BookReadingStreamFactory = _ => stream;
            workspace.BookReadingStartPageOverride = 20;
            await workspace.EnterBookReadingCommand.ExecuteAsync();
            int started = 0;
            List<BookReadingPage> replayed = [];
            workspace.BookReadingStarted += () => started++;
            workspace.BookReadingPageReady += page => replayed.Add(page);

            workspace.ReplayBookReading();

            started.Should().Be(1);
            replayed.Select(page => page.PageIndex).Should().Equal(Enumerable.Range(18, 11));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Exit_clears_the_session_cache_so_reentering_reloads()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            FakeBookReadingStream stream = new(30);
            workspace.BookReadingStreamFactory = _ => stream;
            List<BookReadingPage> delivered = [];
            workspace.BookReadingPageReady += page => delivered.Add(page);

            await workspace.EnterBookReadingCommand.ExecuteAsync();
            stream.ListCalls.Should().Be(1);
            delivered.Should().HaveCount(9);

            workspace.ExitBookReadingCommand.Execute(null);
            delivered.Clear();
            await workspace.EnterBookReadingCommand.ExecuteAsync();

            stream.ListCalls.Should().Be(2, "a fresh session re-lists the pages");
            delivered.Should().HaveCount(9, "the cleared cache means every page is delivered again");
        }, CancellationToken.None);
    }

    [Fact]
    public void Reading_font_size_is_clamped_to_the_supported_range_and_persisted()
    {
        MainWindowViewModel main = new(settingsPath: _settings.Path);
        LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
        PdfWorkspaceViewModel workspace = new(main, item);

        workspace.BookReadingFontSize = 4;
        workspace.BookReadingFontSize.Should().Be(ReadingFontCatalog.MinimumFontSize);
        main.AppOptions.Ui.ReadingFontSize.Should().Be(ReadingFontCatalog.MinimumFontSize,
            "size changes persist immediately through SaveReadingFont");

        workspace.BookReadingFontSize = 100;
        workspace.BookReadingFontSize.Should().Be(ReadingFontCatalog.MaximumFontSize);
        main.AppOptions.Ui.ReadingFontSize.Should().Be(ReadingFontCatalog.MaximumFontSize);

        workspace.BookReadingFontSize = 16;
        workspace.BookReadingFontSizeText.Should().Be("16pt");
        main.AppOptions.Ui.ReadingFontSize.Should().Be(16);
    }

    [Fact]
    public void Reading_font_family_persists_the_system_default_label_as_an_empty_string()
    {
        MainWindowViewModel main = new(settingsPath: _settings.Path);
        LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
        PdfWorkspaceViewModel workspace = new(main, item);

        workspace.BookReadingFontFamily.Should().Be(PdfWorkspaceViewModel.SystemDefaultReadingFontLabel,
            "the persisted empty family maps to the localized default label");

        workspace.BookReadingFontFamily = PdfWorkspaceViewModel.SystemDefaultReadingFontLabel;
        main.AppOptions.Ui.ReadingFontFamily.Should().BeEmpty(
            "the system-default label is persisted as the empty string");

        workspace.BookReadingFontFamily = "Mock Reading Font";
        main.AppOptions.Ui.ReadingFontFamily.Should().Be("Mock Reading Font");
    }

    [Fact]
    public async Task BookReadingFontFamilies_leads_with_the_system_default_label()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        IReadOnlyList<string> families = await session.Dispatch(() =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            return workspace.BookReadingFontFamilies;
        }, CancellationToken.None);

        families.Should().NotBeEmpty();
        families[0].Should().Be(PdfWorkspaceViewModel.SystemDefaultReadingFontLabel);
        families.Should().OnlyHaveUniqueItems("system font family names must be de-duplicated");
    }

    [Fact]
    public void Reset_font_size_command_restores_the_default_and_persists()
    {
        MainWindowViewModel main = new(settingsPath: _settings.Path);
        LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
        PdfWorkspaceViewModel workspace = new(main, item);

        workspace.BookReadingFontSize = 22;
        workspace.BookReadingResetFontSizeCommand.Execute(null);

        workspace.BookReadingFontSize.Should().Be(ReadingFontCatalog.DefaultFontSize);
        main.AppOptions.Ui.ReadingFontSize.Should().Be(ReadingFontCatalog.DefaultFontSize,
            "the reset persists immediately like any other font change");
    }

    [Fact]
    public async Task ExitBookReadingToPage_leaves_reading_mode()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(3);

            await workspace.EnterBookReadingCommand.ExecuteAsync();
            workspace.IsBookReadingMode.Should().BeTrue();

            await workspace.ExitBookReadingToPageAsync(2);

            workspace.IsBookReadingMode.Should().BeFalse();
            workspace.BookReadingProgressText.Should().BeEmpty();
            // Page navigation itself is guarded by the loaded page count, which stays zero for
            // this documentless fixture; GoToPageAsync's clamping is exercised elsewhere.
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    private static LibraryItemViewModel CreateItem(string? documentInstanceId)
    {
        return new LibraryItemViewModel(
            ItemId.New().ToString(), "阅读模式测试题录", "book", "", "", "", null, documentInstanceId,
            null, "source.pdf", "", 0, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
    }

    // A stand-in for BookReadingStream that serves a fixed page count from memory. It records the
    // order pages were requested in and how many times the page list was read, so a test can
    // assert the windowing and replay contract without a database.
    private sealed class FakeBookReadingStream(int pageCount) : IBookReadingStream
    {
        public List<int> RequestedPages { get; } = [];
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<int>> ListPageIndicesAsync(
            DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult((IReadOnlyList<int>)Enumerable.Range(0, pageCount).ToArray());
        }

        public Task<BookReadingPage> LoadPageAsync(
            DocumentInstanceId documentInstanceId, int pageIndex, int pageCountValue,
            CancellationToken cancellationToken = default)
        {
            RequestedPages.Add(pageIndex);
            return Task.FromResult(new BookReadingPage(
                pageIndex, pageCountValue, false, $"<p>page {pageIndex}</p>"));
        }
    }
}
