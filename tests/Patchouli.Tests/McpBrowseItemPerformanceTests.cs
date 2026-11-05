using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class McpBrowseItemPerformanceTests : IAsyncLifetime
{
    private TemporarySqliteDatabase _database = null!;
    private FixedClock _clock = null!;
    private LibraryIdentityService _libraries = null!;
    private ItemService _items = null!;
    private FileAssetService _files = null!;
    private DocumentInstanceService _documents = null!;
    private McpReadApi _mcpApi = null!;
    private BiblatexImportService _biblatexImport = null!;
    private LibraryId _libraryId;

    public async Task InitializeAsync()
    {
        _database = TemporarySqliteDatabase.Create();
        _clock = new FixedClock(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        MigrationRunner runner = new(_database.ConnectionFactory, TestPaths.MigrationsDirectory);
        await runner.RunAsync();

        _libraries = new LibraryIdentityService(_database.ConnectionFactory, _clock);
        Result<LibraryMetadata> library = await _libraries.CreateLibraryAsync("MCP Browse Perf Test Library");
        _libraryId = library.Value.LibraryId;

        _items = new ItemService(_database.ConnectionFactory, _libraries, _clock);
        _files = new FileAssetService(_database.ConnectionFactory, _libraries, _clock);
        _documents = new DocumentInstanceService(_database.ConnectionFactory, _clock);

        _mcpApi = new McpReadApi(
            _database.ConnectionFactory,
            new SqliteSearchService(_database.ConnectionFactory));

        _biblatexImport = new BiblatexImportService(
            new BiblatexHelperClient(),
            _items,
            _files,
            _documents);
    }

    public Task DisposeAsync()
    {
        return _database.DisposeAsync().AsTask();
    }

    [Fact]
    public async Task BrowseItems_without_filters_returns_equal_totals_and_respects_skip_limit()
    {
        for (int i = 0; i < 5; i++)
        {
            await _items.CreateItemAsync("book", $"Book {i}");
        }

        Result<McpBrowseItemPage> page1 = await _mcpApi.BrowseItemsAsync(0, 2);
        page1.IsSuccess.Should().BeTrue();
        page1.Value.DomainTotal.Should().Be(5);
        page1.Value.FilteredTotal.Should().Be(5);
        page1.Value.Rows.Should().HaveCount(2);
        page1.Value.HasMore.Should().BeTrue();

        Result<McpBrowseItemPage> page2 = await _mcpApi.BrowseItemsAsync(2, 2);
        page2.IsSuccess.Should().BeTrue();
        page2.Value.Rows.Should().HaveCount(2);
        page2.Value.HasMore.Should().BeTrue();

        Result<McpBrowseItemPage> page3 = await _mcpApi.BrowseItemsAsync(4, 2);
        page3.IsSuccess.Should().BeTrue();
        page3.Value.Rows.Should().HaveCount(1);
        page3.Value.HasMore.Should().BeFalse();

        // Ensure distinct items across pages
        List<ItemId> allPagingIds = page1.Value.Rows.Concat(page2.Value.Rows).Concat(page3.Value.Rows)
            .Select(i => i.ItemId).ToList();
        allPagingIds.Distinct().Should().HaveCount(5);
    }

    [Fact]
    public async Task BrowseItems_with_filter_preserves_distinct_domain_and_filtered_totals()
    {
        await _items.CreateItemAsync("book", "Book 1");
        await _items.CreateItemAsync("book", "Book 2");
        await _items.CreateItemAsync("article", "Article 1");

        Result<McpBrowseItemPage> filtered = await _mcpApi.BrowseItemsAsync(
            0,
            10,
            [new McpWhereClause("item_type", "article")]);

        filtered.IsSuccess.Should().BeTrue();
        filtered.Value.DomainTotal.Should().Be(3);
        filtered.Value.FilteredTotal.Should().Be(1);
        filtered.Value.Rows.Should().HaveCount(1);
        filtered.Value.Rows[0].ItemType.Should().Be("article");
        filtered.Value.Rows[0].Title.Should().Be("Article 1");
    }

    [Fact]
    public async Task BrowseItems_out_of_range_short_circuits_to_empty()
    {
        await _items.CreateItemAsync("book", "Book 1");

        Result<McpBrowseItemPage> result = await _mcpApi.BrowseItemsAsync(100, 10);
        result.IsSuccess.Should().BeTrue();
        result.Value.Rows.Should().BeEmpty();
        result.Value.HasMore.Should().BeFalse();
        result.Value.DomainTotal.Should().Be(1);
        result.Value.FilteredTotal.Should().Be(1);
    }

    [Fact]
    public async Task SearchItems_finds_matching_substring_and_projects_ocr_status()
    {
        Result<ItemMetadata> item = await _items.CreateItemAsync("book", "Quantum Mechanics Handbook");
        await _items.CreateItemAsync("book", "Classical Thermodynamics");

        Result<McpBrowseItemPage> searchResult = await _mcpApi.SearchItemsAsync(
            "Mechanics",
            true,
            0,
            10);

        searchResult.IsSuccess.Should().BeTrue();
        searchResult.Value.DomainTotal.Should().Be(2);
        searchResult.Value.FilteredTotal.Should().Be(1);
        searchResult.Value.Rows.Should().ContainSingle();
        searchResult.Value.Rows[0].ItemId.Should().Be(item.Value.ItemId);
        searchResult.Value.Rows[0].PrimaryDocumentOcrIndexStatus.Should().Be("no_primary_document");
    }

    [Fact]
    public async Task ListItems_with_include_total_count_false_returns_null_total_without_fakezero()
    {
        await _items.CreateItemAsync("book", "Alpha");
        await _items.CreateItemAsync("book", "Beta");
        await _items.CreateItemAsync("book", "Gamma");

        Result<ItemListPage> defaultPage = await _items.ListItemsAsync(new ListItemsRequest(PageSize: 2));
        defaultPage.IsSuccess.Should().BeTrue();
        defaultPage.Value.TotalCount.Should().Be(3);
        defaultPage.Value.Items.Should().HaveCount(2);

        Result<ItemListPage> uncountedPage = await _items.ListItemsAsync(
            new ListItemsRequest(PageSize: 2, IncludeTotalCount: false));
        uncountedPage.IsSuccess.Should().BeTrue();
        uncountedPage.Value.TotalCount.Should().BeNull(
            "unknown count semantics must return null instead of a fakezero total");
        uncountedPage.Value.Items.Should().HaveCount(2);
        uncountedPage.Value.NextCursor.Should().NotBeNullOrWhiteSpace();

        // Keyset pagination using NextCursor continues without counting
        Result<ItemListPage> page2 = await _items.ListItemsAsync(
            new ListItemsRequest(PageSize: 2, Cursor: uncountedPage.Value.NextCursor, IncludeTotalCount: false));
        page2.IsSuccess.Should().BeTrue();
        page2.Value.TotalCount.Should().BeNull();
        page2.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Biblatex_load_seeds_walks_entire_library_without_counting_totals()
    {
        const string title = "A uniquely identifiable source beyond the first seed page";
        Result<ItemMetadata> target = await _items.CreateItemAsync(new CreateItemRequest(
            "book", title, Publisher: "Seed Press",
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "SeedAuthor", "Ada")],
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2020]]")]));
        target.IsSuccess.Should().BeTrue(target.ErrorMessage);
        for (int i = 0; i < 200; i++)
        {
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            (await _items.CreateItemAsync("book", $"Seed Item {i}")).IsSuccess.Should().BeTrue();
        }

        BiblatexEntryDto entry = new("seed-entry", "book", false,
            new Dictionary<string, string> { ["title"] = title, ["publisher"] = "Seed Press" },
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>
            {
                ["author"] = [new BiblatexPersonDto("SeedAuthor", "Ada")]
            },
            new Dictionary<string, BiblatexDateDto> { ["date"] = new([2020], [new[] { 2020 }]) },
            [], null, true, new BiblatexVerifyDto([], [], []));
        Result<BiblatexBatchImportPreview> previewResult = await _biblatexImport.PreviewBatchAsync([entry]);

        previewResult.IsSuccess.Should().BeTrue();
        previewResult.Value.Plan.Groups.Single().Candidates.Should()
            .Contain(candidate => candidate.ItemId == target.Value.ItemId.ToString());
    }

    [Fact]
    public async Task Migration057_indexes_are_utilized_by_query_planner()
    {
        await using SqliteConnection connection = _database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();

        // 1. Verify idx_items_active_browse matches MCP browse order by
        string browsePlan = await ExplainPlanAsync(connection,
            """
            with paged_items as (
                select item_id, title, item_type, status, citation_key, updated_at
                from items
                where deleted_at is null and merged_into_item_id is null
                order by updated_at desc, item_id
                limit 20 offset 0
            )
            select * from paged_items;
            """);

        browsePlan.Should().Contain("idx_items_active_browse",
            "active item browse must use the partial ordering index");
        browsePlan.Should().NotContain("USE TEMP B-TREE",
            "matching index must avoid temporary sort b-tree");

        // 2. Verify idx_items_library_active_created matches keyset pagination
        string keysetPlan = await ExplainPlanAsync(connection,
            """
            select item_id, title, created_at
            from items
            where library_id = @LibraryId
              and deleted_at is null
              and merged_into_item_id is null
              and (created_at, item_id) < (@CursorCreatedAt, @CursorItemId)
            order by created_at desc, item_id desc
            limit 51;
            """,
            new { LibraryId = _libraryId.ToString(), CursorCreatedAt = "2026-01-01", CursorItemId = "item1" });

        keysetPlan.Should().Contain("idx_items_library_active_created",
            "active item keyset seek must use the library active created index");
    }

    private static async Task<string> ExplainPlanAsync(SqliteConnection connection, string sql,
        object? parameters = null)
    {
        IEnumerable<dynamic> planRows = await connection.QueryAsync("EXPLAIN QUERY PLAN " + sql, parameters);
        return string.Join("\n", planRows.Select(r => (string)r.detail));
    }
}
