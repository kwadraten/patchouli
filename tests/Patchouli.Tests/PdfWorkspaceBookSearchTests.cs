using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Documents;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// Exercises the whole-book search sidebar (全书搜索) of the PDF workspace: document-scoped
/// matched units flatten into rows, clicking a row navigates to its page and highlights its
/// source box, and a superseded or cleared search can never write stale results back.
/// </summary>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class PdfWorkspaceBookSearchTests(AvaloniaSharedSessionFixture headless) : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();
    private MainWindowViewModel? _mainWindow;
    private PdfWorkspaceViewModel? _workspace;

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Book_search_flattens_matched_units_and_jumps_to_page_and_box()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel main = _mainWindow =
                new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path)
                {
                    RuntimeDatabasePath = _settings.CreateDatabasePath("ui-book-search")
                };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();

            (ItemMetadata item, DocumentInstance document, Page page1, Page page2) =
                await CreateTwoPageDocumentAsync(services, "Search workspace");
            await BoxTreeTestData.CommitTextAsync(
                services.ConnectionFactory, services.Clock, document.DocumentInstanceId, page1.PageId,
                "first page text");
            DocumentTreeRevision revision2 = await BoxTreeTestData.CommitTextAsync(
                services.ConnectionFactory, services.Clock, document.DocumentInstanceId, page2.PageId,
                "needle on second page");
            DocumentTreeService trees = BoxTreeTestData.CreateService(services.ConnectionFactory, services.Clock);
            DocumentBoxId targetBox = (await trees.ListBoxesAsync(revision2.TreeRevisionId)).Value[0].BoxId;

            PdfWorkspaceViewModel workspace = _workspace = CreateWorkspace(main, document);
            await workspace.LoadAsync();
            workspace.BoundingBoxes.Should().HaveCount(1);

            workspace.BookSearchHandler = (request, _) =>
            {
                request.DocumentInstanceId.Should().Be(document.DocumentInstanceId,
                    "the sidebar search must stay scoped to the loaded document");
                request.IncludeAllMatchedUnits.Should().BeTrue();
                return Task.FromResult(Result<SearchResultPage>.Success(new SearchResultPage(
                    [
                        new SearchPageResult(
                            item.ItemId, "Search workspace", document.DocumentInstanceId, page2.PageId,
                            "2", 1,
                            [
                                new SearchMatchedUnit(SearchUnitId.New(), page2.PageId, targetBox,
                                    "needle on second page", "text", 0, revision2.TreeRevisionId, true)
                            ],
                            false, "current")
                    ],
                    null, 1, "current", null)));
            };

            workspace.BookSearchQuery = "needle";
            await workspace.EnterBookReadingCommand.ExecuteAsync();
            workspace.IsBookReadingMode.Should().BeTrue();
            await workspace.ShowSearchTabCommand.ExecuteAsync();
            workspace.IsBookReadingMode.Should().BeFalse();
            workspace.ActiveSidebarTab.Should().Be(SidebarTab.Search);
            workspace.IsSearchTabActive.Should().BeTrue();
            workspace.SidebarTabTitle.Should().Be("全书搜索");

            await workspace.RunBookSearchCommand.ExecuteAsync();

            workspace.BookSearchResults.Should().HaveCount(1);
            workspace.HasNoBookSearchResults.Should().BeFalse();
            workspace.BookSearchStatusText.Should().Contain("1");
            BookSearchResultViewModel hit = workspace.BookSearchResults[0];
            hit.Hit.Should().Be("needle");
            hit.Suffix.Should().Contain("second page");
            hit.PageNumberText.Should().Be("第 2 页");
            hit.BadgeText.Should().Contain("第 2 页");

            await workspace.JumpToBookSearchResultAsync(hit);
            workspace.PageIndex.Should().Be(1);
            workspace.SelectedBox.Should().NotBeNull();
            workspace.SelectedBox!.BoxId.Should().Be(targetBox);
            workspace.SelectedBookSearchResult.Should().Be(hit);
            hit.IsSelected.Should().BeTrue();
            workspace.BookSearchMatchPositionText.Should().Be("1 / 1");

            await workspace.NextBookSearchMatchCommand.ExecuteAsync();
            workspace.PageIndex.Should().Be(1);
            workspace.SelectedBookSearchResult.Should().Be(hit);

            await services.PageRenders.ReleaseDocumentSessionAsync(document.DocumentInstanceId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_superseded_book_search_cannot_overwrite_newer_results()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel main = _mainWindow =
                new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path)
                {
                    RuntimeDatabasePath = _settings.CreateDatabasePath("ui-book-search-stale")
                };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();

            (ItemMetadata item, DocumentInstance document, Page page1, _) =
                await CreateTwoPageDocumentAsync(services, "Stale search workspace");
            DocumentTreeRevision revision = await BoxTreeTestData.CommitTextAsync(
                services.ConnectionFactory, services.Clock, document.DocumentInstanceId, page1.PageId,
                "first query text");
            DocumentTreeService trees = BoxTreeTestData.CreateService(services.ConnectionFactory, services.Clock);
            DocumentBoxId box = (await trees.ListBoxesAsync(revision.TreeRevisionId)).Value[0].BoxId;

            PdfWorkspaceViewModel workspace = _workspace = CreateWorkspace(main, document);
            await workspace.LoadAsync();

            TaskCompletionSource<Result<SearchResultPage>> first =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            workspace.BookSearchHandler = (_, _) =>
            {
                calls++;
                return calls == 1
                    ? first.Task
                    : Task.FromResult(Result<SearchResultPage>.Success(
                        new SearchResultPage([], null, 0, "current", null)));
            };

            workspace.BookSearchQuery = "first";
            Task superseded = workspace.RunBookSearchCommand.ExecuteAsync();
            workspace.BookSearchQuery = "second";
            workspace.IsBookSearchRunning.Should().BeFalse();
            await workspace.RunBookSearchCommand.ExecuteAsync();
            workspace.BookSearchResults.Should().BeEmpty();
            workspace.BookSearchStatusText.Should().Be("没有命中结果。");

            // The late first response must be dropped instead of replacing the newer result set.
            first.SetResult(Result<SearchResultPage>.Success(new SearchResultPage(
                [
                    new SearchPageResult(
                        item.ItemId, "Stale search workspace", document.DocumentInstanceId, page1.PageId,
                        "1", 0,
                        [
                            new SearchMatchedUnit(SearchUnitId.New(), page1.PageId, box,
                                "first query text", "text", 0, revision.TreeRevisionId, true)
                        ],
                        false, "current")
                ],
                null, 1, "current", null)));
            await superseded;
            workspace.BookSearchResults.Should().BeEmpty();
            workspace.BookSearchStatusText.Should().Be("没有命中结果。");
            workspace.IsBookSearchRunning.Should().BeFalse();

            await services.PageRenders.ReleaseDocumentSessionAsync(document.DocumentInstanceId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Clearing_the_workspace_drops_book_search_results()
    {
        HeadlessUnitTestSession session = headless.Session;
        await DispatchAndShutdownAsync(session, async () =>
        {
            MainWindowViewModel main = _mainWindow =
                new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path)
                {
                    RuntimeDatabasePath = _settings.CreateDatabasePath("ui-book-search-clear")
                };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();

            (ItemMetadata item, DocumentInstance document, Page page1, _) =
                await CreateTwoPageDocumentAsync(services, "Clearing search workspace");
            DocumentTreeRevision revision = await BoxTreeTestData.CommitTextAsync(
                services.ConnectionFactory, services.Clock, document.DocumentInstanceId, page1.PageId,
                "clear me text");
            DocumentTreeService trees = BoxTreeTestData.CreateService(services.ConnectionFactory, services.Clock);
            DocumentBoxId box = (await trees.ListBoxesAsync(revision.TreeRevisionId)).Value[0].BoxId;

            PdfWorkspaceViewModel workspace = _workspace = CreateWorkspace(main, document);
            await workspace.LoadAsync();
            workspace.BookSearchHandler = (_, _) => Task.FromResult(
                Result<SearchResultPage>.Success(new SearchResultPage(
                    [
                        new SearchPageResult(
                            item.ItemId, "Clearing search workspace", document.DocumentInstanceId,
                            page1.PageId, "1", 0,
                            [
                                new SearchMatchedUnit(SearchUnitId.New(), page1.PageId, box,
                                    "clear me text", "text", 0, revision.TreeRevisionId, true)
                            ],
                            false, "current")
                    ],
                    null, 1, "current", null)));

            workspace.BookSearchQuery = "clear";
            await workspace.RunBookSearchCommand.ExecuteAsync();
            workspace.BookSearchResults.Should().HaveCount(1);

            (await workspace.ClearAsync()).Should().BeTrue();
            workspace.BookSearchResults.Should().BeEmpty();
            workspace.HasNoBookSearchResults.Should().BeTrue();
            workspace.IsBookSearchRunning.Should().BeFalse();
            workspace.SelectedBookSearchResult.Should().BeNull();

            await services.PageRenders.ReleaseDocumentSessionAsync(document.DocumentInstanceId);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Whole_book_search_reads_every_result_page_beyond_five_hundred_matches()
    {
        await DispatchAndShutdownAsync(headless.Session, async () =>
        {
            MainWindowViewModel main = _mainWindow =
                new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path)
                {
                    RuntimeDatabasePath = _settings.CreateDatabasePath("ui-book-search-pagination")
                };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            (ItemMetadata item, DocumentInstance document, _, _) =
                await CreateTwoPageDocumentAsync(services, "Pagination");
            PdfWorkspaceViewModel workspace = _workspace = CreateWorkspace(main, document);
            await workspace.LoadAsync();
            int calls = 0;
            workspace.BookSearchHandler = (request, _) =>
            {
                request.IncludeAllMatchedUnits.Should().BeTrue();
                int offset = calls++ * 100;
                SearchPageResult[] pages = Enumerable.Range(offset, Math.Min(100, 501 - offset))
                    .Select(index => new SearchPageResult(item.ItemId, item.Title, document.DocumentInstanceId,
                        PageId.New(), null, index,
                        [
                            new SearchMatchedUnit(SearchUnitId.New(), PageId.New(), DocumentBoxId.New(),
                                "needle", "text", 0, DocumentTreeRevisionId.New(), true)
                        ], false, "current"))
                    .ToArray();
                return Task.FromResult(Result<SearchResultPage>.Success(
                    new SearchResultPage(pages, calls < 6 ? calls.ToString() : null, null, "current", null)));
            };

            workspace.BookSearchQuery = "needle";
            await workspace.RunBookSearchCommand.ExecuteAsync();

            calls.Should().Be(6);
            workspace.BookSearchResults.Should().HaveCount(501);
            workspace.BookSearchResults[^1].PageIndex.Should().Be(500);
        }, CancellationToken.None);
    }

    private static async Task<(ItemMetadata, DocumentInstance, Page, Page)> CreateTwoPageDocumentAsync(
        HostServices services, string title)
    {
        ItemMetadata item = (await services.Items.CreateItemAsync("book", title)).Value;
        DocumentInstance document = (await services.Documents.AttachDocumentInstanceAsync(
            item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Page page1 = (await services.Pages.CreatePageAsync(
            document.DocumentInstanceId, 0, "1", null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
        Page page2 = (await services.Pages.CreatePageAsync(
            document.DocumentInstanceId, 1, "2", null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
        return (item, document, page1, page2);
    }

    private static PdfWorkspaceViewModel CreateWorkspace(MainWindowViewModel main, DocumentInstance document)
    {
        LibraryItemViewModel libraryItem = new(
            ItemId.New().ToString(), "Search workspace", "book", "", "", "", null,
            document.DocumentInstanceId.ToString(),
            FileAssetId.New().ToString(),
            "source.pdf", "", 2, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
        PdfWorkspaceViewModel workspace = new(main, libraryItem)
        {
            PageRenderPreviewHandler = (_, _) => Task.FromResult(
                Result<PdfPagePixelBufferLease>.Success(
                    new PdfPagePixelBufferLease(new byte[400], 10, 10, 40, 0, "test", 10, 10, "v1")))
        };
        return workspace;
    }

    private async Task DispatchAndShutdownAsync(HeadlessUnitTestSession session, Func<Task> action,
        CancellationToken cancellationToken)
    {
        await session.Dispatch<bool>(async () =>
        {
            try
            {
                await action();
                return true;
            }
            finally
            {
                try
                {
                    if (_workspace is not null)
                    {
                        await _workspace.ClearAsync();
                        _workspace.Dispose();
                        _workspace = null;
                    }
                }
                finally
                {
                    if (_mainWindow is not null)
                    {
                        await _mainWindow.ShutdownAsync();
                        _mainWindow = null;
                    }
                }
            }
        }, cancellationToken);
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

        public Task ClearAsync()
        {
            Text = null;
            return Task.CompletedTask;
        }
    }
}
