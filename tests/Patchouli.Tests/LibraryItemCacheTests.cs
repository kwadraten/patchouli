using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Host.Caching;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class LibraryItemCacheTests
{
    [Fact]
    public async Task First_load_populates_the_snapshot_and_queries_require_it()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Cached");
        item.IsSuccess.Should().BeTrue(item.ErrorMessage);

        context.Cache.IsLoaded.Should().BeFalse();
        LibraryItemCache cache = context.Cache;
        Action queryBeforeLoad = () => cache.QueryByTags(null);
        queryBeforeLoad.Should().Throw<InvalidOperationException>();
        Action untaggedBeforeLoad = () => cache.QueryUntagged();
        untaggedBeforeLoad.Should().Throw<InvalidOperationException>();
        Action countsBeforeLoad = () => cache.GetTagCounts();
        countsBeforeLoad.Should().Throw<InvalidOperationException>();

        await context.Cache.EnsureLoadedAsync();

        context.Cache.IsLoaded.Should().BeTrue();
        context.Cache.QueryByTags(null).Should().ContainSingle().Which.ItemId.Should().Be(item.Value.ItemId);
        Result<IReadOnlyList<LibraryItemRow>> sqlRows = await context.Query.ListRowsAsync();
        context.Cache.QueryByTags(null).Select(row => row.ItemId.ToString()).Should()
            .BeEquivalentTo(sqlRows.Value.Select(row => row.ItemId.ToString()));
    }

    [Fact]
    public async Task EnsureLoadedAsync_is_a_no_op_once_the_snapshot_exists()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Items.CreateItemAsync("book", "One");
        await context.Cache.EnsureLoadedAsync();
        LibraryItemRow[] firstSnapshot = context.Cache.QueryByTags(null).ToArray();

        Result<ItemMetadata> late = await context.Items.CreateItemAsync("book", "Two");
        late.IsSuccess.Should().BeTrue(late.ErrorMessage);
        await context.Cache.EnsureLoadedAsync();

        context.Cache.QueryByTags(null).Select(row => row.ItemId.ToString()).Should()
            .BeEquivalentTo(firstSnapshot.Select(row => row.ItemId.ToString()),
                "EnsureLoadedAsync must not reload an already-loaded snapshot");
    }

    [Fact]
    public async Task QueryByTags_mirrors_the_SQL_tag_filter_semantics()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> both = await context.Items.CreateItemAsync("book", "Both Tags");
        Result<ItemMetadata> onlyX = await context.Items.CreateItemAsync("book", "Only X");
        await context.Items.CreateItemAsync("book", "Untagged");
        Result tagResult = await context.Tags.AddTagsToItemsAsync([both.Value.ItemId], ["x", "y"]);
        tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);
        tagResult = await context.Tags.AddTagsToItemsAsync([onlyX.Value.ItemId], ["x"]);
        tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);
        await context.Cache.EnsureLoadedAsync();

        foreach ((IReadOnlyList<string>? requiredTags, string because) in new (IReadOnlyList<string>?, string)[]
                 {
                     (null, "a null filter returns every row"),
                     (Array.Empty<string>(), "an empty filter returns every row"),
                     (new[] { "x" }, "a single required tag matches items carrying it"),
                     (new[] { "x", "y" }, "multiple required tags combine with AND semantics"),
                     (new[] { "x", "x" }, "duplicate required tags match the SQL count(distinct) filter"),
                     (new[] { "X" }, "tag matching is ordinal like SQLite's binary IN collation"),
                     (new[] { "missing" }, "an unknown tag matches nothing")
                 })
        {
            IReadOnlyList<LibraryItemRow> cached = context.Cache.QueryByTags(requiredTags);
            Result<IReadOnlyList<LibraryItemRow>> sql = await context.Query.ListRowsAsync(requiredTags);
            cached.Select(row => row.ItemId.ToString()).Should()
                .BeEquivalentTo(sql.Value.Select(row => row.ItemId.ToString()), because);
        }

        context.Cache.QueryByTags(["x", "y"]).Should().ContainSingle().Which.ItemId.Should().Be(both.Value.ItemId);
        context.Cache.QueryByTags(["x"]).Select(row => row.ItemId).Should()
            .BeEquivalentTo([both.Value.ItemId, onlyX.Value.ItemId]);
        context.Cache.QueryByTags(["x", "x"]).Should().BeEmpty(
            "the SQL path compares count(distinct value) against the raw required-tag count, " +
            "so a duplicated required tag never matches");
        context.Cache.QueryByTags(["X"]).Should().BeEmpty();
    }

    [Fact]
    public async Task QueryUntagged_returns_only_rows_without_tags()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> tagged = await context.Items.CreateItemAsync("book", "Tagged");
        Result<ItemMetadata> untagged = await context.Items.CreateItemAsync("book", "Untagged");
        Result tagResult = await context.Tags.AddTagsToItemsAsync([tagged.Value.ItemId], ["x"]);
        tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);
        await context.Cache.EnsureLoadedAsync();

        context.Cache.QueryUntagged().Should().ContainSingle().Which.ItemId.Should().Be(untagged.Value.ItemId);

        Result<IReadOnlyList<LibraryItemRow>> sqlRows = await context.Query.ListRowsAsync();
        context.Cache.QueryUntagged().Select(row => row.ItemId.ToString()).Should().BeEquivalentTo(
            sqlRows.Value.Where(row => row.Tags is null || row.Tags.Count == 0).Select(row => row.ItemId.ToString()));
    }

    [Fact]
    public async Task GetTagCounts_returns_sidebar_counts_including_the_untagged_count()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> tagged = await context.Items.CreateItemAsync("book", "Tagged");
        await context.Items.CreateItemAsync("book", "Untagged");
        Result tagResult = await context.Tags.AddTagsToItemsAsync([tagged.Value.ItemId], ["x", "y"]);
        tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);
        await context.Cache.EnsureLoadedAsync();

        TagCountsSnapshot counts = context.Cache.GetTagCounts();

        Result<IReadOnlyList<TagInfo>> sqlTags = await context.Tags.ListTagsAsync();
        counts.Tags.Should().BeEquivalentTo(sqlTags.Value);
        counts.Tags.Should().BeEquivalentTo([new TagInfo("x", 1), new TagInfo("y", 1)]);
        Result<int> sqlUntagged = await context.Query.CountUntaggedItemsAsync();
        counts.UntaggedCount.Should().Be(sqlUntagged.Value).And.Be(1);
    }

    [Fact]
    public async Task Refresh_reflects_writes_made_through_the_item_and_tag_services()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync("book", "First");
        await context.Cache.EnsureLoadedAsync();

        Result<ItemMetadata> late = await context.Items.CreateItemAsync("book", "Late");
        late.IsSuccess.Should().BeTrue(late.ErrorMessage);
        Result tagResult = await context.Tags.AddTagsToItemsAsync([late.Value.ItemId], ["fresh"]);
        tagResult.IsSuccess.Should().BeTrue(tagResult.ErrorMessage);

        context.Cache.QueryByTags(null).Should().ContainSingle("the snapshot is stale until refreshed");
        context.Cache.QueryByTags(["fresh"]).Should().BeEmpty();
        context.Cache.GetTagCounts().UntaggedCount.Should().Be(1);

        Result refreshed = await context.Cache.RefreshAsync();
        refreshed.IsSuccess.Should().BeTrue(refreshed.ErrorMessage);

        context.Cache.QueryByTags(null).Select(row => row.ItemId).Should()
            .BeEquivalentTo([first.Value.ItemId, late.Value.ItemId]);
        context.Cache.QueryByTags(["fresh"]).Should().ContainSingle().Which.ItemId.Should().Be(late.Value.ItemId);
        context.Cache.QueryUntagged().Should().ContainSingle().Which.ItemId.Should().Be(first.Value.ItemId);
        context.Cache.GetTagCounts().UntaggedCount.Should().Be(1);
        context.Cache.GetTagCounts().Tags.Should().Contain(tag => tag.Name == "fresh" && tag.Count == 1);
    }

    [Fact]
    public async Task Scoped_refresh_updates_tags_and_trash_without_reloading_unrelated_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync("book", "First");
        Result<ItemMetadata> second = await context.Items.CreateItemAsync("book", "Second");
        await context.Cache.EnsureLoadedAsync();
        await context.Tags.AddTagsToItemsAsync([first.Value.ItemId], ["fresh"]);
        await context.Tags.AddTagsToItemsAsync([second.Value.ItemId], ["outside"]);
        LibraryChangeSet changes = LibraryChangeSet.Empty with { ItemIds = [first.Value.ItemId] };

        Result refreshed = await context.Cache.ApplyChangesAsync(changes);

        refreshed.IsSuccess.Should().BeTrue(refreshed.ErrorMessage);
        context.Cache.QueryByTags(["fresh"]).Should().ContainSingle();
        context.Cache.QueryByTags(["outside"]).Should().BeEmpty("only the published scope is read");
        context.Cache.GetTagCounts().UntaggedCount.Should().Be(1);
        await context.Items.DeleteItemsAsync([first.Value.ItemId]);
        await context.Cache.ApplyChangesAsync(changes);
        context.Cache.QueryByTags(null).Should().ContainSingle().Which.ItemId.Should().Be(second.Value.ItemId);
        context.Cache.GetTagCounts().Tags.Should().BeEmpty();
        await context.Items.RestoreItemsAsync([first.Value.ItemId]);
        await context.Cache.ApplyChangesAsync(changes);
        context.Cache.QueryByTags(["fresh"]).Should().ContainSingle();
        context.Cache.GetTagCounts().UntaggedCount.Should().Be(1);
    }

    [Fact]
    public async Task Scoped_refresh_preserves_SQLite_tag_order_for_non_ASCII_case()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Tags");
        await context.Tags.AddTagsToItemsAsync([item.Value.ItemId], ["É", "ä", "a", "A", "𐀀", ""]);
        await context.Cache.EnsureLoadedAsync();
        await context.Tags.AddTagsToItemsAsync([item.Value.ItemId], ["new"]);
        await context.Cache.ApplyChangesAsync(LibraryChangeSet.Empty with { ItemIds = [item.Value.ItemId] });
        Result<IReadOnlyList<TagInfo>> sqlTags = await context.Tags.ListTagsAsync();
        context.Cache.GetTagCounts().Tags.Select(tag => tag.Name)
            .Should().Equal(sqlTags.Value.Select(tag => tag.Name));
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Cache Test");
        LibraryRevisionService revisions = new(database.ConnectionFactory);
        ItemService items = new(database.ConnectionFactory, library, clock, revisions);
        ItemTagService tags = new(database.ConnectionFactory, revisions);
        LibraryItemQueryService query = new(database.ConnectionFactory);
        return new TestContext(database, items, tags, query, new LibraryItemCache(query, tags));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, ItemService items, ItemTagService tags,
            LibraryItemQueryService query, LibraryItemCache cache)
        {
            Database = database;
            Items = items;
            Tags = tags;
            Query = query;
            Cache = cache;
        }

        public TemporarySqliteDatabase Database { get; }
        public ItemService Items { get; }
        public ItemTagService Tags { get; }
        public LibraryItemQueryService Query { get; }
        public LibraryItemCache Cache { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
