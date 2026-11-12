using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;

namespace Patchouli.Tests;

public sealed class SearchPerformanceAndHotspotRegressionTests
{
    [Fact]
    public async Task Batch_search_retrieves_over_100_pages_across_cursors_without_drops_or_duplicates()
    {
        await using TestContext context = await TestContext.CreateAsync();
        const int totalPages = 115;
        List<Page> createdPages = new();
        for (int i = 0; i < totalPages; i++)
        {
            Page page = i == 0
                ? context.Page
                : (await context.Pages.CreatePageAsync(
                    context.Document.DocumentInstanceId, i, (i + 1).ToString(), null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            createdPages.Add(page);

            DocumentTreeRevision revision = (await context.Trees.BeginWorkingRevisionAsync(
                context.Document.DocumentInstanceId,
                page.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload($"multipage needle on page {i}"))
                ],
                DocumentTreeRevisionSource.Import)).Value;
            await context.Trees.CommitWorkingRevisionAsync(revision.TreeRevisionId);
        }

        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Index.RebuildFtsForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        // Batch 1: pageSize 100
        SearchResultPage batch1 = (await context.Search.SearchLibraryAsync(
            new SearchRequest("multipage needle", context.Document.DocumentInstanceId, 100))).Value;

        batch1.Results.Should().HaveCount(100);
        batch1.NextCursor.Should().NotBeNullOrWhiteSpace();

        // Batch 2: using NextCursor
        SearchResultPage batch2 = (await context.Search.SearchLibraryAsync(
            new SearchRequest("multipage needle", context.Document.DocumentInstanceId, 100, batch1.NextCursor))).Value;

        batch2.Results.Should().HaveCount(15);
        batch2.NextCursor.Should().BeNull();

        // Verify ordering and distinctness across batches
        List<SearchPageResult> allResults = batch1.Results.Concat(batch2.Results).ToList();
        allResults.Should().HaveCount(totalPages);
        allResults.Select(r => r.PageId).Should().OnlyHaveUniqueItems();
        allResults.Select(r => r.PageIndex).Should().Equal(Enumerable.Range(0, totalPages));

        SearchResultPage wholeBook = (await context.Search.SearchLibraryAsync(
            new SearchRequest("multipage needle", context.Document.DocumentInstanceId, 1,
                IncludeAllMatchedUnits: true, IncludeAllDocumentPages: true))).Value;
        wholeBook.Results.Select(result => result.PageId).Should().Equal(allResults.Select(result => result.PageId));
        wholeBook.NextCursor.Should().BeNull();

        SearchResultPage libraryPage = (await context.Search.SearchLibraryAsync(
            new SearchRequest("multipage needle", PageSize: 1, IncludeAllDocumentPages: true))).Value;
        libraryPage.Results.Should().HaveCount(1);
        libraryPage.NextCursor.Should().NotBeNull();
    }

    [Theory]
    [InlineData("\u0301", "e\u0301")]
    [InlineData("\ue000", "text\ue000symbol")]
    [InlineData("\u200d", "text\u200dmarker")]
    [InlineData("＋", "fullwidth ＋ marker")]
    public async Task Literal_symbol_candidates_preserve_canonical_characters(string query, string text)
    {
        await using TestContext context = await TestContext.CreateAsync();
        DocumentTreeRevision revision = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId, context.Page.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload(text))
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(revision.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Index.RebuildFtsForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        SearchResultPage result = (await context.Search.SearchLibraryAsync(
            new SearchRequest(query, context.Document.DocumentInstanceId))).Value;

        result.Results.Single().MatchedUnits.Single().Text.Should().Be(text);
    }

    [Fact]
    public async Task Batch_search_limits_default_callers_to_five_units_and_sets_has_more()
    {
        await using TestContext context = await TestContext.CreateAsync();
        const int unitCount = 9;
        DocumentTreeRevision revision = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Page.PageId,
            Enumerable.Range(0, unitCount).Select(ordinal => new DocumentBoxSeed(
                null, null, ordinal, DocumentBoxType.Text, null,
                new NormalizedBBox(.1, .05 + ordinal * .08, .8, .05),
                new TextBoxPayload($"unitlimit test phrase {ordinal}"))).ToArray(),
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(revision.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Index.RebuildFtsForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        SearchResultPage defaultPreview = (await context.Search.SearchLibraryAsync(
            new SearchRequest("unitlimit", context.Document.DocumentInstanceId, IncludeAllMatchedUnits: false))).Value;
        SearchResultPage completeWholeBook = (await context.Search.SearchLibraryAsync(
            new SearchRequest("unitlimit", context.Document.DocumentInstanceId, IncludeAllMatchedUnits: true))).Value;

        defaultPreview.Results.Single().MatchedUnits.Should().HaveCount(5);
        defaultPreview.Results.Single().MatchedUnitsHasMore.Should().BeTrue();

        completeWholeBook.Results.Single().MatchedUnits.Should().HaveCount(unitCount);
        completeWholeBook.Results.Single().MatchedUnitsHasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Tokenless_and_symbol_queries_use_indexed_fts_path()
    {
        await using TestContext context = await TestContext.CreateAsync();
        DocumentTreeRevision revision = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Page.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("calculator + operation")),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .2, .8, .1), new TextBoxPayload("subtraction - minus")),
                new DocumentBoxSeed(null, null, 2, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .3, .8, .1), new TextBoxPayload("equality == condition")),
                new DocumentBoxSeed(null, null, 3, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .4, .8, .1), new TextBoxPayload("scope :: resolver")),
                new DocumentBoxSeed(null, null, 4, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .5, .8, .1), new TextBoxPayload("c++ primer guide")),
                new DocumentBoxSeed(null, null, 5, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .6, .8, .1), new TextBoxPayload("pure alpha text only"))
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(revision.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Index.RebuildFtsForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        SearchResultPage plusMatch = (await context.Search.SearchLibraryAsync(
            new SearchRequest("+", context.Document.DocumentInstanceId))).Value;
        plusMatch.Results.SelectMany(p => p.MatchedUnits).Select(u => u.Text)
            .Should().Equal("calculator + operation", "c++ primer guide");

        SearchResultPage scopeMatch = (await context.Search.SearchLibraryAsync(
            new SearchRequest("::", context.Document.DocumentInstanceId))).Value;
        scopeMatch.Results.SelectMany(p => p.MatchedUnits).Select(u => u.Text)
            .Should().Equal("scope :: resolver");

        SearchResultPage eqMatch = (await context.Search.SearchLibraryAsync(
            new SearchRequest("==", context.Document.DocumentInstanceId))).Value;
        eqMatch.Results.SelectMany(p => p.MatchedUnits).Select(u => u.Text)
            .Should().Equal("equality == condition");

        SearchResultPage cppMatch = (await context.Search.SearchLibraryAsync(
            new SearchRequest("c++", context.Document.DocumentInstanceId))).Value;
        cppMatch.Results.SelectMany(p => p.MatchedUnits).Select(u => u.Text)
            .Should().Equal("c++ primer guide");

        // Verify FTS query plan uses the virtual table index for literal tokens
        await using SqliteConnection connection = context.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        string[] plan = (await connection.QueryAsync<PlanRow>(
            """
            explain query plan
            select rowid from search_units_fts where search_units_fts match @Match;
            """,
            new { Match = "\"sym002b\"" })).Select(r => r.Detail).ToArray();
        plan.Should().Contain(detail => detail.Contains("VIRTUAL TABLE INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deleted_deprecated_and_merged_filters_are_respected()
    {
        await using TestContext context = await TestContext.CreateAsync();

        // 1. Regular item
        DocumentTreeRevision firstRev = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Page.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("filtertest item active"))
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(firstRev.TreeRevisionId);

        // 2. Second item
        DocumentTreeRevision secondRev = (await context.Trees.BeginWorkingRevisionAsync(
            context.SecondDocument.DocumentInstanceId,
            context.SecondPage.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("filtertest item second"))
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(secondRev.TreeRevisionId);

        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Units.RebuildForDocumentInstanceAsync(context.SecondDocument.DocumentInstanceId);
        await context.Index.RebuildFtsForLibraryAsync();

        // Base check: both return
        SearchResultPage both = (await context.Search.SearchLibraryAsync(new SearchRequest("filtertest"))).Value;
        both.Results.Should().HaveCount(2);

        // Mark second item as deleted
        await using (SqliteConnection connection = context.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update items set deleted_at = '2026-11-05T00:00:00Z' where item_id = @ItemId;",
                new { ItemId = context.SecondItem.ItemId.ToString() });
        }

        SearchResultPage afterDelete = (await context.Search.SearchLibraryAsync(new SearchRequest("filtertest"))).Value;
        afterDelete.Results.Should().ContainSingle().Which.ItemId.Should().Be(context.Item.ItemId);

        // Un-delete and mark second item as merged
        await using (SqliteConnection connection = context.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update items set deleted_at = null, merged_into_item_id = 'merged-target' where item_id = @ItemId;",
                new { ItemId = context.SecondItem.ItemId.ToString() });
        }

        SearchResultPage afterMerge = (await context.Search.SearchLibraryAsync(new SearchRequest("filtertest"))).Value;
        afterMerge.Results.Should().ContainSingle().Which.ItemId.Should().Be(context.Item.ItemId);

        // Deprecate first document instance
        await using (SqliteConnection connection = context.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update items set merged_into_item_id = null where item_id = @ItemId;",
                new { ItemId = context.SecondItem.ItemId.ToString() });
            await connection.ExecuteAsync(
                "update document_instances set status = 'deprecated' where document_instance_id = @DocId;",
                new { DocId = context.Document.DocumentInstanceId.ToString() });
        }

        SearchResultPage excludeDeprecated = (await context.Search.SearchLibraryAsync(
            new SearchRequest("filtertest", IncludeDeprecatedInstances: false))).Value;
        excludeDeprecated.Results.Should().ContainSingle().Which.ItemId.Should().Be(context.SecondItem.ItemId);

        SearchResultPage includeDeprecated = (await context.Search.SearchLibraryAsync(
            new SearchRequest("filtertest", IncludeDeprecatedInstances: true))).Value;
        includeDeprecated.Results.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(1, "+")]
    [InlineData(0, "needle")]
    public async Task Legacy_or_missing_cache_falls_back_and_upgrades_cleanly(int cacheVersion, string query)
    {
        await using TestContext context = await TestContext.CreateAsync();
        DocumentTreeRevision revision = (await context.Trees.BeginWorkingRevisionAsync(
            context.Document.DocumentInstanceId,
            context.Page.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null,
                    new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("upgrade + test needle"))
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await context.Trees.CommitWorkingRevisionAsync(revision.TreeRevisionId);
        await context.Units.RebuildForDocumentInstanceAsync(context.Document.DocumentInstanceId);
        await context.Index.RebuildFtsForDocumentInstanceAsync(context.Document.DocumentInstanceId);

        // Simulate an old cache or a snapshot without its rebuildable cache.
        await using (SqliteConnection connection = context.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("update fts_cache_state set cache_version = 1 where state_id = 1;");
            if (cacheVersion == 0)
            {
                await connection.ExecuteAsync(
                    "delete from fts_cache_state; delete from search_units_fts; delete from fts_row_map;");
            }
        }

        // Search on cache version 1: tokenless query "+" falls back to scan, still finds match without skipping
        SearchResultPage legacyResult = (await context.Search.SearchLibraryAsync(
            new SearchRequest(query, context.Document.DocumentInstanceId))).Value;
        legacyResult.Results.SelectMany(p => p.MatchedUnits).Select(u => u.Text)
            .Should().Equal("upgrade + test needle");

        // Now run EnsureCacheAsync (triggers upgrade to CurrentCacheVersion = 2)
        Result ensureResult = await context.Index.EnsureCacheAsync();
        ensureResult.IsSuccess.Should().BeTrue();

        // Verify cache_version is now 2
        await using (SqliteConnection connection = context.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            int currentVersion = await connection.ExecuteScalarAsync<int>(
                "select cache_version from fts_cache_state where state_id = 1;");
            currentVersion.Should().Be(SearchTextAnalyzer.CurrentCacheVersion);
        }

        // Search on upgraded cache version 2: succeeds via indexed FTS
        SearchResultPage upgradedResult = (await context.Search.SearchLibraryAsync(
            new SearchRequest("+", context.Document.DocumentInstanceId))).Value;
        upgradedResult.Results.SelectMany(p => p.MatchedUnits).Select(u => u.Text)
            .Should().Equal("upgrade + test needle");
    }

    [Fact]
    public async Task Search_respects_cancellation_token()
    {
        await using TestContext context = await TestContext.CreateAsync();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        OperationCanceledException? cancellation = null;
        try
        {
            await context.Search.SearchLibraryAsync(new SearchRequest("anything"), cts.Token);
        }
        catch (OperationCanceledException error)
        {
            cancellation = error;
        }

        cancellation.Should().NotBeNull();
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private TestContext(
            TemporarySqliteDatabase database,
            ItemMetadata item,
            DocumentInstance document,
            Page page,
            ItemMetadata secondItem,
            DocumentInstance secondDocument,
            Page secondPage,
            IDocumentTreeService trees,
            ISearchUnitBuilder units,
            ISearchIndexRebuilder index,
            ISearchService search,
            IPageService pages)
        {
            _database = database;
            Item = item;
            Document = document;
            Page = page;
            SecondItem = secondItem;
            SecondDocument = secondDocument;
            SecondPage = secondPage;
            Trees = trees;
            Units = units;
            Index = index;
            Search = search;
            Pages = pages;
        }

        public ItemMetadata Item { get; }
        public DocumentInstance Document { get; }
        public Page Page { get; }
        public ItemMetadata SecondItem { get; }
        public DocumentInstance SecondDocument { get; }
        public Page SecondPage { get; }
        public IDocumentTreeService Trees { get; }
        public ISearchUnitBuilder Units { get; }
        public ISearchIndexRebuilder Index { get; }
        public ISearchService Search { get; }
        public IPageService Pages { get; }
        public SqliteConnectionFactory ConnectionFactory => _database.ConnectionFactory;

        public static async Task<TestContext> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-11-05T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("Performance Regression Tests");
            ItemService items = new(database.ConnectionFactory, libraries, clock);
            ItemMetadata item = (await items.CreateItemAsync("document", "First Work")).Value;
            ItemMetadata secondItem = (await items.CreateItemAsync("document", "Second Work")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            DocumentInstance secondDocument = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(secondItem.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            PageService pages = new(database.ConnectionFactory, clock);
            Page page = (await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            Page secondPage = (await pages.CreatePageAsync(secondDocument.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            IDocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            ISearchUnitBuilder units = new SearchUnitBuilder(database.ConnectionFactory, clock,
                new MarkdigMarkdownEngine());
            return new TestContext(
                database,
                item,
                document,
                page,
                secondItem,
                secondDocument,
                secondPage,
                trees,
                units,
                new SearchIndexRebuilder(database.ConnectionFactory, clock),
                new SqliteSearchService(database.ConnectionFactory),
                pages);
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }

    private sealed class PlanRow
    {
        public string Detail { get; set; } = "";
    }
}
