using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class BibliographicSearchServiceTests : IAsyncLifetime
{
    private TemporarySqliteDatabase _database = null!;
    private ItemService _items = null!;
    private LibraryItemQueryService _queries = null!;
    private ItemMetadata _book = null!;
    private ItemMetadata _journal = null!;
    private ItemMetadata _emptyGeneral = null!;

    public async Task InitializeAsync()
    {
        _database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
        await new MigrationRunner(_database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService libraries = new(_database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("Bibliographic search");
        _items = new ItemService(_database.ConnectionFactory, libraries, clock);
        _queries = new LibraryItemQueryService(_database.ConnectionFactory);

        _book = (await _items.CreateItemAsync("book", "机器学习基础",
            creatorsJson: """[{"name":"周志华"}]""")).Value;
        _journal = (await _items.CreateItemAsync("article-journal", "深度学习综述",
            creatorsJson: """[{"name":"Yann LeCun"}]""", status: "in-press")).Value;
        _emptyGeneral = (await _items.CreateItemAsync("general", "通用条目")).Value;
        await _items.AddIdentifierAsync(_book.ItemId, "doi", "10.1000/abc", null);

        ItemMetadata trashed = (await _items.CreateItemAsync("book", "被删除的条目")).Value;
        await _items.DeleteItemAsync(trashed.ItemId);
        ItemMetadata merged = (await _items.CreateItemAsync("book", "被合并的条目")).Value;
        await using (SqliteConnection connection = _database.ConnectionFactory.CreateWriteConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update items set merged_into_item_id = @Target where item_id = @Id;",
                new { Target = _book.ItemId.ToString(), Id = merged.ItemId.ToString() });
        }
    }

    public async Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Search_matches_title_creators_citation_key_and_identifiers_with_contains()
    {
        (await Search("机器学习")).Should().Equal(_book.ItemId);
        (await Search("10.1000")).Should().Equal(_book.ItemId);
        (await Search("leCun")).Should().Equal(_journal.ItemId);

        Result<IReadOnlyList<LibraryItemRow>> empty = await _queries.SearchRowsAsync(
            new BibliographicItemSearch(null, []));
        empty.IsSuccess.Should().BeTrue();
        empty.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_combines_structured_filters_with_and()
    {
        IReadOnlyList<LibraryItemRow> rows = (await _queries.SearchRowsAsync(new BibliographicItemSearch(null,
        [
            new BibliographicSearchFilter(BibliographicSearchFilterKeys.ItemType, "book"),
            new BibliographicSearchFilter(BibliographicSearchFilterKeys.Author, "周志华"),
        ]))).Value;
        rows.Select(row => row.ItemId).Should().Equal(_book.ItemId);

        IReadOnlyList<LibraryItemRow> noHit = (await _queries.SearchRowsAsync(new BibliographicItemSearch(null,
        [
            new BibliographicSearchFilter(BibliographicSearchFilterKeys.ItemType, "article-journal"),
            new BibliographicSearchFilter(BibliographicSearchFilterKeys.Title, "机器学习"),
        ]))).Value;
        noHit.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_filters_item_status_citable_and_identifier_fields()
    {
        (await Filter(BibliographicSearchFilterKeys.ItemStatus, "in-press")).Should().Equal(_journal.ItemId);
        (await Filter(BibliographicSearchFilterKeys.ItemStatus, "unset"))
            .Should().BeEquivalentTo([_book.ItemId, _emptyGeneral.ItemId]);
        (await Filter(BibliographicSearchFilterKeys.Citable, "true"))
            .Should().BeEquivalentTo([_book.ItemId, _journal.ItemId, _emptyGeneral.ItemId]);
        (await Filter(BibliographicSearchFilterKeys.Identifier, "10.1000")).Should().Equal(_book.ItemId);
    }

    [Fact]
    public async Task Search_excludes_trash_and_merged_items_from_query_and_filters()
    {
        (await Search("被删")).Should().BeEmpty();
        (await Search("被合")).Should().BeEmpty();
        (await Filter(BibliographicSearchFilterKeys.ItemType, "book")).Should().Equal(_book.ItemId);
    }

    [Fact]
    public async Task Search_filter_options_list_real_persisted_values_with_display_labels()
    {
        BibliographicSearchFilterOptions options =
            (await _queries.GetSearchFilterOptionsAsync()).Value;

        options.ItemTypes.Select(option => option.Value)
            .Should().BeEquivalentTo(["book", "article-journal", "general"]);
        options.ItemTypes.First(option => option.Value == "book").Label.Should().Be("图书");
        options.ItemStatuses.Select(option => option.Value).Should().BeEquivalentTo(["unset", "in-press"]);
        options.ItemStatuses.First(option => option.Value == "unset").Label.Should().Be("未设置");
        options.OcrIndexStatuses.Select(option => option.Value).Should().Equal("no_primary_document");
        options.OcrIndexStatuses.First().Label.Should().Be("无主文档");
    }

    [Fact]
    public async Task Search_rows_carry_library_grid_projection_fields()
    {
        LibraryItemRow row = (await _queries.SearchRowsAsync(new BibliographicItemSearch("机器学习", []))).Value
            .Single();
        row.Title.Should().Be("机器学习基础");
        row.ItemType.Should().Be("book");
        row.Authors.Should().Contain("周志华");
        row.PrimaryDocumentOcrIndexState.Value.Should().Be(PrimaryDocumentOcrIndexState.NoPrimaryDocument);
    }

    private async Task<IReadOnlyList<ItemId>> Search(string query)
    {
        Result<IReadOnlyList<LibraryItemRow>> result =
            await _queries.SearchRowsAsync(new BibliographicItemSearch(query, []));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Value.Select(row => row.ItemId).ToArray();
    }

    private async Task<IReadOnlyList<ItemId>> Filter(string key, string value)
    {
        Result<IReadOnlyList<LibraryItemRow>> result =
            await _queries.SearchRowsAsync(new BibliographicItemSearch(null,
                [new BibliographicSearchFilter(key, value)]));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Value.Select(row => row.ItemId).ToArray();
    }
}
