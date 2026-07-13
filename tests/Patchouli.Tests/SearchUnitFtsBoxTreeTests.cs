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
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class SearchUnitFtsBoxTreeTests
{
    [Fact]
    public async Task Fts_indexes_current_non_suppressed_box_leaves_in_sibling_order()
    {
        await using Context context = await Context.CreateAsync();
        DocumentTreeRevision staging = (await context.Trees.StagePageAsync(
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
            ])).Value;
        await context.Trees.AdoptStagingRevisionAsync(staging.TreeRevisionId);
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

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Context(
            TemporarySqliteDatabase database,
            DocumentInstance document,
            Page page,
            IDocumentTreeService trees,
            ISearchUnitBuilder units,
            ISearchIndexRebuilder index,
            ISearchService search)
        {
            _database = database;
            Document = document;
            Page = page;
            Trees = trees;
            Units = units;
            Index = index;
            Search = search;
        }

        public DocumentInstance Document { get; }
        public Page Page { get; }
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
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "Search units")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Page page = (await new Infrastructure.Layout.PageService(database.ConnectionFactory, clock)
                .CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            IDocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            ISearchUnitBuilder units = new SearchUnitBuilder(database.ConnectionFactory, clock,
                new MarkdigMarkdownEngine());
            return new Context(
                database,
                document,
                page,
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
