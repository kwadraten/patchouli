using System.Runtime.CompilerServices;
using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.Reading;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// B1: the PDF workspace's whole-book reading mode. Verifies the view model toggles into and
/// out of reading mode, guards against items without a document instance, clamps and persists
/// the live font settings, and streams pages to the view in reading order with progress text.
/// The real <see cref="BookReadingStream" /> ordering is covered by <c>BookReadingStreamTests</c>;
/// these tests inject a fake stream through <see cref="PdfWorkspaceViewModel.BookReadingStreamFactory" />
/// so they never need a database.
/// </summary>
[Collection("Avalonia")]
public sealed class BookReadingModeTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    // A parseable document-instance id (the view model feeds it to DocumentInstanceId.Parse before
    // streaming); the fake stream ignores it, but it must be a valid Guid.
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
            workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(0, 0);

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
        workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(0, 0);

        await workspace.EnterBookReadingCommand.ExecuteAsync();

        workspace.IsBookReadingMode.Should().BeFalse("an item without a document instance cannot be read");
        workspace.Status.Should().Contain("没有可阅读的文档实例");
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
    public async Task Stream_delivers_pages_in_reading_order_and_reports_load_progress()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path);
            LibraryItemViewModel item = CreateItem(DocumentInstanceGuid);
            PdfWorkspaceViewModel workspace = new(main, item);
            // Five pages, start at page 2: the fake yields 2,3,4 (append) then 0,1 (prepend),
            // mirroring the real stream's ordering so the test exercises both delivery paths.
            workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(5, 2);

            List<BookReadingPage> delivered = [];
            string? lastNonEmptyProgress = null;
            workspace.BookReadingPageReady += page => delivered.Add(page);
            workspace.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PdfWorkspaceViewModel.BookReadingProgressText))
                {
                    string current = workspace.BookReadingProgressText;
                    if (!string.IsNullOrEmpty(current))
                    {
                        lastNonEmptyProgress = current;
                    }
                }
            };

            await workspace.EnterBookReadingCommand.ExecuteAsync();

            delivered.Select(page => page.PageIndex).Should().Equal(2, 3, 4, 0, 1);
            delivered.Select(page => page.IsPrepend).Should().Equal(false, false, false, true, true);
            workspace.BookReadingProgressText.Should().BeEmpty("progress is cleared once streaming completes");
            lastNonEmptyProgress.Should().Be("已加载 5/5 页");
        }, CancellationToken.None);
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
            workspace.BookReadingStreamFactory = _ => new FakeBookReadingStream(3, 0);

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

    // A stand-in for BookReadingStream that yields a fixed page set in reading order (start..last
    // appended, then 0..start prepended) without touching a database. The start page is taken from
    // the fake's own startIndex because the view model's _pageIndex is private; what matters here
    // is the delivery/progress contract, not which start page the VM picks.
    private sealed class FakeBookReadingStream(int pageCount, int startIndex) : IBookReadingStream
    {
        public async IAsyncEnumerable<BookReadingPage> StreamPagesAsync(
            DocumentInstanceId documentInstanceId, int startPageIndex,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = startIndex; i < pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new BookReadingPage(i, pageCount, false, $"<p>page {i}</p>");
            }

            for (int i = 0; i < startIndex; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new BookReadingPage(i, pageCount, true, $"<p>page {i}</p>");
            }
        }
    }
}
