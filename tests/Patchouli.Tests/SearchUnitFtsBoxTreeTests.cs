using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Core.Search;

namespace Patchouli.Tests;

public sealed class SearchUnitFtsBoxTreeTests
{
    [Fact]
    public async Task Fts_indexes_current_non_suppressed_box_leaves_in_sibling_order()
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision working = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Page.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("firstunique indexed phrase")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Header, null, null,
                    new NormalizedBBox(.1, .02, .8, .05), new TextBoxPayload("hiddenunique running head"),
                    Suppressed: true),
                new DocumentBoxSeed(null, null, 2, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.1, .3, .8, .1), new TextBoxPayload("secondunique indexed phrase"))
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(working.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Index.RebuildFtsForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        SearchResultPage first = (await context.Search.SearchLibraryAsync(new SearchRequest("firstunique"))).Value;
        SearchResultPage hidden = (await context.Search.SearchLibraryAsync(new SearchRequest("hiddenunique"))).Value;
        SearchResultPage second = (await context.Search.SearchLibraryAsync(new SearchRequest("secondunique"))).Value;
        SearchUnitId secondUnit = second.Results.Single().MatchedUnits.Single().UnitId;
        IReadOnlyList<SearchMatchedUnit> nearby =
            (await context.Search.GetSearchResultContextAsync(secondUnit, 2, 0)).Value;

        first.Results.Single().MatchedUnits.Single().Text.Should().Be("firstunique indexed phrase");
        hidden.Results.Should().BeEmpty();
        nearby.Select(unit => unit.Text).Should().Equal("firstunique indexed phrase", "secondunique indexed phrase");
        nearby.Last().IsMatch.Should().BeTrue();
    }

    [Fact]
    public async Task Item_filters_narrow_full_text_search_to_matching_items()
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision first = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Page.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("sharedtoken alpha phrase"), null)
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(first.TreeRevisionId);
        DocumentTreeRevision second = (await context.Trees.BeginWorkingRevisionAsync(
            context.SecondDocument.DocumentInstanceId,
            context.SecondPage.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("sharedtoken beta phrase"), null)
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(second.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Units.RebuildForDocumentInstanceAsync(context.SecondDocument.DocumentInstanceId);
        await context.Index.RebuildFtsForLibraryAsync();

        SearchResultPage unfiltered = (await context.Search.SearchLibraryAsync(new SearchRequest("sharedtoken"))).Value;
        unfiltered.Results.Should().HaveCount(2);

        SearchResultPage byTitle = (await context.Search.SearchLibraryAsync(new SearchRequest("sharedtoken")
        {
            ItemFilters = [new BibliographicSearchFilter(BibliographicSearchFilterKeys.Title, "second")]
        })).Value;
        byTitle.Results.Should().ContainSingle().Which.ItemTitle.Should().Be("Second work");

        SearchResultPage byType = (await context.Search.SearchLibraryAsync(new SearchRequest("sharedtoken")
        {
            ItemFilters = [new BibliographicSearchFilter(BibliographicSearchFilterKeys.ItemType, "document")]
        })).Value;
        byType.Results.Should().HaveCount(2);

        SearchResultPage noMatch = (await context.Search.SearchLibraryAsync(new SearchRequest("sharedtoken")
        {
            ItemFilters = [new BibliographicSearchFilter(BibliographicSearchFilterKeys.Title, "absent")]
        })).Value;
        noMatch.Results.Should().BeEmpty();
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstance document,
            Page page,
            DocumentInstance secondDocument,
            Page secondPage,
            IDocumentTreeService trees,
            ISearchUnitBuilder units,
            ISearchIndexRebuilder index,
            ISearchService search)
        {
            _database = database;
            Document = document;
            Page = page;
            SecondDocument = secondDocument;
            SecondPage = secondPage;
            Trees = trees;
            Units = units;
            Index = index;
            Search = search;
        }

        public DocumentInstance Document { get; }
        public Page Page { get; }
        public DocumentInstance SecondDocument { get; }
        public Page SecondPage { get; }
        public IDocumentTreeService Trees { get; }
        public ISearchUnitBuilder Units { get; }
        public ISearchIndexRebuilder Index { get; }
        public ISearchService Search { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("Search units");
            ItemService items = new(database.ConnectionFactory, libraries, clock);
            ItemMetadata item = (await items.CreateItemAsync("document", "Search units")).Value;
            ItemMetadata secondItem = (await items.CreateItemAsync("document", "Second work")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            DocumentInstance secondDocument = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(secondItem.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page page = (await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            Page secondPage = (await pages.CreatePageAsync(secondDocument.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            IDocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            ISearchUnitBuilder units = new SearchUnitBuilder(database.ConnectionFactory, clock,
                new MarkdigMarkdownEngine());
            return new Context(
                database,
                document,
                page,
                secondDocument,
                secondPage,
                trees,
                units,
                new SearchIndexRebuilder(database.ConnectionFactory, clock),
                new SqliteSearchService(database.ConnectionFactory));
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
