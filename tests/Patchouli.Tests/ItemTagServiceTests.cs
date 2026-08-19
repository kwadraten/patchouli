using System.Data.Common;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ItemTagServiceTests
{
    [Fact]
    public async Task TagNormalizer_trims_and_removes_empty_and_duplicates_preserving_order()
    {
        IReadOnlyList<string> result = TagNormalizer.NormalizeMany(["  A  ", "", "B", " A ", "", "C", "B"]);
        result.Should().Equal("A", "B", "C");
    }

    [Fact]
    public void TagNormalizer_is_case_sensitive()
    {
        IReadOnlyList<string> result = TagNormalizer.NormalizeMany(["Tag", "tag", "TAG"]);
        result.Should().Equal("Tag", "tag", "TAG");
    }

    [Fact]
    public async Task AddTagsToItemsAsync_appends_new_tags_and_skips_duplicates()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Tagged", tagsJson: "[\"alpha\"]");

        Result result = await context.Tags.AddTagsToItemsAsync(
            [item.Value.ItemId], ["beta", " alpha ", ""]);
        result.IsSuccess.Should().BeTrue();

        Result<ItemMetadata> updated = await context.Items.GetItemAsync(item.Value.ItemId);
        updated.Value.TagsJson.Should().Be("[\"alpha\",\"beta\"]");
    }

    [Fact]
    public async Task AddTagsToItemsAsync_skips_trashed_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Trashed", tagsJson: "[\"alpha\"]");
        await context.Items.DeleteItemAsync(item.Value.ItemId);

        Result result = await context.Tags.AddTagsToItemsAsync([item.Value.ItemId], ["beta"]);
        result.IsSuccess.Should().BeTrue();

        string? tagsJson = await GetTagsJsonAsync(context, item.Value.ItemId);
        tagsJson.Should().Be("[\"alpha\"]");
    }

    [Fact]
    public async Task RemoveTagFromItemsAsync_removes_only_specified_tag()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync(
            "book", "Tagged", tagsJson: "[\"alpha\",\"beta\"]");

        Result result = await context.Tags.RemoveTagFromItemsAsync([item.Value.ItemId], "alpha");
        result.IsSuccess.Should().BeTrue();

        Result<ItemMetadata> updated = await context.Items.GetItemAsync(item.Value.ItemId);
        updated.Value.TagsJson.Should().Be("[\"beta\"]");
    }

    [Fact]
    public async Task SetTagsAsync_replaces_tags_and_skips_trashed_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> active = await context.Items.CreateItemAsync("book", "Active", tagsJson: "[\"alpha\"]");
        Result<ItemMetadata> trashed = await context.Items.CreateItemAsync("book", "Trashed", tagsJson: "[\"beta\"]");
        await context.Items.DeleteItemAsync(trashed.Value.ItemId);

        Result result = await context.Tags.SetTagsAsync(
            [active.Value.ItemId, trashed.Value.ItemId], ["gamma"]);
        result.IsSuccess.Should().BeTrue();

        Result<ItemMetadata> activeUpdated = await context.Items.GetItemAsync(active.Value.ItemId);
        activeUpdated.Value.TagsJson.Should().Be("[\"gamma\"]");

        string? trashedTagsJson = await GetTagsJsonAsync(context, trashed.Value.ItemId);
        trashedTagsJson.Should().Be("[\"beta\"]");
    }

    [Fact]
    public async Task RenameTagAsync_renames_tag_across_active_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> a = await context.Items.CreateItemAsync("book", "A", tagsJson: "[\"old\"]");
        Result<ItemMetadata> b = await context.Items.CreateItemAsync("book", "B", tagsJson: "[\"old\",\"other\"]");

        Result result = await context.Tags.RenameTagAsync("old", "new");
        result.IsSuccess.Should().BeTrue();

        Result<ItemMetadata> aUpdated = await context.Items.GetItemAsync(a.Value.ItemId);
        aUpdated.Value.TagsJson.Should().Be("[\"new\"]");
        Result<ItemMetadata> bUpdated = await context.Items.GetItemAsync(b.Value.ItemId);
        bUpdated.Value.TagsJson.Should().Be("[\"new\",\"other\"]");
    }

    [Fact]
    public async Task RenameTagAsync_to_existing_tag_merges_tags()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync(
            "book", "Merge", tagsJson: "[\"source\",\"target\"]");

        Result result = await context.Tags.RenameTagAsync("source", "target");
        result.IsSuccess.Should().BeTrue();

        Result<ItemMetadata> updated = await context.Items.GetItemAsync(item.Value.ItemId);
        updated.Value.TagsJson.Should().Be("[\"target\"]");
    }

    [Fact]
    public async Task MergeTagsAsync_replaces_source_with_target()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync(
            "book", "Merge", tagsJson: "[\"source\",\"other\"]");

        Result result = await context.Tags.MergeTagsAsync("source", "target");
        result.IsSuccess.Should().BeTrue();

        Result<ItemMetadata> updated = await context.Items.GetItemAsync(item.Value.ItemId);
        updated.Value.TagsJson.Should().Be("[\"target\",\"other\"]");
    }

    [Fact]
    public async Task ListTagsAsync_counts_only_active_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> active = await context.Items.CreateItemAsync("book", "Active", tagsJson: "[\"shared\"]");
        Result<ItemMetadata> trashed = await context.Items.CreateItemAsync("book", "Trashed", tagsJson: "[\"shared\"]");
        await context.Items.DeleteItemAsync(trashed.Value.ItemId);

        Result<IReadOnlyList<TagInfo>> tags = await context.Tags.ListTagsAsync();
        tags.Value.Should().ContainSingle(t => t.Name == "shared").Which.Count.Should().Be(1);
    }

    [Fact]
    public async Task ListTagsAsync_orders_by_name_ordinal_case_sensitive()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Items.CreateItemAsync("book", "One", tagsJson: "[\"Zebra\"]");
        await context.Items.CreateItemAsync("book", "Two", tagsJson: "[\"apple\"]");
        await context.Items.CreateItemAsync("book", "Three", tagsJson: "[\"Mango\"]");

        Result<IReadOnlyList<TagInfo>> tags = await context.Tags.ListTagsAsync();
        tags.Value.Select(t => t.Name).Should().Equal("apple", "Mango", "Zebra");
    }

    [Fact]
    public async Task Bulk_write_emits_single_revision()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> a = await context.Items.CreateItemAsync("book", "A", tagsJson: "[\"old\"]");
        Result<ItemMetadata> b = await context.Items.CreateItemAsync("book", "B", tagsJson: "[\"old\"]");
        List<ItemId> captured = new();
        context.Revisions.ChangeCommitted += (_, args) => captured.AddRange(args.ChangeSet.ItemIds);

        Result result = await context.Tags.RenameTagAsync("old", "new");
        result.IsSuccess.Should().BeTrue();

        captured.Should().Contain(a.Value.ItemId);
        captured.Should().Contain(b.Value.ItemId);
    }

    private static async Task<string?> GetTagsJsonAsync(TestContext context, ItemId itemId)
    {
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        return await connection.QueryFirstOrDefaultAsync<string>(
            "select tags_json from items where item_id = @ItemId",
            new { ItemId = itemId.ToString() });
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Tag Test");
        CapturingRevisionService revisions = new();
        ItemService items = new(database.ConnectionFactory, library, clock, revisions);
        ItemTagService tags = new(database.ConnectionFactory, revisions);
        return new TestContext(database, clock, items, tags, revisions,
            new LibraryItemQueryService(database.ConnectionFactory));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, FixedClock clock, ItemService items,
            ItemTagService tags, CapturingRevisionService revisions, LibraryItemQueryService query)
        {
            Database = database;
            Clock = clock;
            Items = items;
            Tags = tags;
            Revisions = revisions;
            Query = query;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public ItemService Items { get; }
        public ItemTagService Tags { get; }
        public CapturingRevisionService Revisions { get; }
        public LibraryItemQueryService Query { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class CapturingRevisionService : ILibraryRevisionService
    {
        public event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted;

        public Task<Result<long>> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<long>.Success(0));
        }

        public Task<Result<long>> CommitAsync(LibraryChangeSet changeSet,
            CancellationToken cancellationToken = default)
        {
            PublishCommitted(changeSet);
            return Task.FromResult(Result<long>.Success(1));
        }

        public Task<Result<LibraryChangeSet>> IncrementInTransactionAsync(
            DbConnection connection,
            DbTransaction transaction,
            LibraryChangeSet changeSet,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryChangeSet>.Success(changeSet));
        }

        public void PublishCommitted(LibraryChangeSet changeSet)
        {
            ChangeCommitted?.Invoke(this, new LibraryRevisionCommittedEventArgs(changeSet));
        }
    }
}
