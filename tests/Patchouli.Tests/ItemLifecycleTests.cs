using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ItemLifecycleTests
{
    [Theory]
    [InlineData("newspaper")]
    [InlineData("web")]
    [InlineData("Book")]
    [InlineData("article_newspaper")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task Public_item_writes_reject_nonstandard_types_without_mutating_the_library(string itemType)
    {
        await using TestContext context = await CreateContextAsync();
        ItemMetadata existing = (await context.Items.CreateItemAsync("book", "Original")).Value;
        long revision = await context.RevisionAsync();
        UpdateItemRequest update = new(itemType, "Must not be saved");

        Result<ItemMetadata>[] results =
        [
            await context.Items.CreateItemAsync(itemType, "Invalid"),
            await context.Items.CreateItemAsync(new CreateItemRequest(itemType, "Invalid")),
            await context.Items.CreateItemWithPrimaryDocumentAsync(new CreateItemRequest(itemType, "Invalid"),
                Core.Ids.FileAssetId.New()),
            await context.Items.UpdateItemAsync(existing.ItemId, update),
            await context.Items.ReplaceItemAsync(existing.ItemId, update, []),
            await context.Items.UpdateAndAddIdentifiersAsync(existing.ItemId, update,
                [new ItemIdentifierInput("url", "https://example.test/must-not-be-saved")])
        ];

        foreach (Result<ItemMetadata> result in results)
        {
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
            result.ErrorMessage.Should().Contain("CSL-JSON");
        }

        (await context.Items.GetItemAsync(existing.ItemId)).Value.Should().BeEquivalentTo(existing);
        (await context.Query.ListRowsAsync()).Value.Should().ContainSingle();
        (await context.RevisionAsync()).Should().Be(revision);
    }

    [Fact]
    public async Task Public_item_writes_accept_every_supported_type_including_general()
    {
        await using TestContext context = await CreateContextAsync();
        foreach (string itemType in CslItemTypeDisplayNames.Names.Keys)
        {
            Result<ItemMetadata> created = await context.Items.CreateItemAsync($" {itemType} ", "Supported type");
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            created.Value.ItemType.Should().Be(itemType);
            Result<ItemMetadata> updated = await context.Items.UpdateItemAsync(created.Value.ItemId,
                new UpdateItemRequest(itemType, "Updated"));
            updated.IsSuccess.Should().BeTrue(updated.ErrorMessage);
            updated.Value.ItemType.Should().Be(itemType);
        }
    }

    [Fact]
    public async Task DeleteItem_soft_deletes_and_hides_from_active_lists()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "To Delete");
        item.IsSuccess.Should().BeTrue();

        Result deleted = await context.Items.DeleteItemAsync(item.Value.ItemId);
        deleted.IsSuccess.Should().BeTrue();

        Result<IReadOnlyList<LibraryItemRow>> active = await context.Query.ListRowsAsync();
        active.Value.Should().BeEmpty();

        Result<ItemListPage> trash = await context.Items.ListTrashedItemsAsync();
        trash.IsSuccess.Should().BeTrue();
        trash.Value.Items.Should().ContainSingle(i => i.ItemId == item.Value.ItemId);
    }

    [Fact]
    public async Task RestoreItem_returns_trashed_item_to_active_lists()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "To Restore");
        await context.Items.DeleteItemAsync(item.Value.ItemId);

        Result<ItemMetadata> restored = await context.Items.RestoreItemAsync(item.Value.ItemId);
        restored.IsSuccess.Should().BeTrue();

        Result<IReadOnlyList<LibraryItemRow>> active = await context.Query.ListRowsAsync();
        active.Value.Should().ContainSingle(i => i.ItemId == item.Value.ItemId);

        Result<ItemListPage> trash = await context.Items.ListTrashedItemsAsync();
        trash.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreItem_fails_for_active_or_merged_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> active = await context.Items.CreateItemAsync("book", "Active");
        Result<ItemMetadata> merged = await context.Items.CreateItemAsync("book", "Merged Source");
        await context.Items.DeleteItemAsync(merged.Value.ItemId);

        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            update items
            set deleted_at = null,
                merged_into_item_id = @TargetId,
                updated_at = @Now
            where item_id = @SourceId;
            """,
            new
            {
                SourceId = merged.Value.ItemId.ToString(),
                TargetId = active.Value.ItemId.ToString(),
                Now = context.Clock.UtcNow.ToString("O")
            });

        Result<ItemMetadata> restoreActive = await context.Items.RestoreItemAsync(active.Value.ItemId);
        restoreActive.IsFailure.Should().BeTrue();
        restoreActive.ErrorCode.Should().Be(AppErrorCodes.NotFound);

        Result<ItemMetadata> restoreMerged = await context.Items.RestoreItemAsync(merged.Value.ItemId);
        restoreMerged.IsFailure.Should().BeTrue();
        restoreMerged.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Merged_items_are_excluded_from_active_and_trash_lists()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");

        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """
            update items
            set merged_into_item_id = @TargetId,
                updated_at = @Now
            where item_id = @SourceId;
            """,
            new
            {
                SourceId = source.Value.ItemId.ToString(),
                TargetId = target.Value.ItemId.ToString(),
                Now = context.Clock.UtcNow.ToString("O")
            });

        Result<IReadOnlyList<LibraryItemRow>> active = await context.Query.ListRowsAsync();
        active.Value.Should().ContainSingle(i => i.ItemId == target.Value.ItemId);

        Result<ItemListPage> trash = await context.Items.ListTrashedItemsAsync();
        trash.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleted_items_can_be_queried_via_trash_read_model()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Trash Detail");
        await context.Items.DeleteItemAsync(item.Value.ItemId);

        Result<LibraryItemPage> page = await context.Query.ListTrashedRowsAsync(10, null);
        page.IsSuccess.Should().BeTrue();
        page.Value.Rows.Should().ContainSingle();
        page.Value.Rows.Single().DeletedAt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DeleteItems_and_RestoreItems_increment_revision_once()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync("book", "Batch A");
        Result<ItemMetadata> second = await context.Items.CreateItemAsync("book", "Batch B");
        long beforeDelete = await context.RevisionAsync();
        int deleteEvents = 0;
        context.Revisions.ChangeCommitted += (_, _) => deleteEvents++;

        Result deleted = await context.Items.DeleteItemsAsync([first.Value.ItemId, second.Value.ItemId]);
        deleted.IsSuccess.Should().BeTrue(deleted.ErrorMessage);
        (await context.RevisionAsync()).Should().Be(beforeDelete + 1);
        deleteEvents.Should().Be(1);

        long beforeRestore = await context.RevisionAsync();
        int restoreEvents = 0;
        context.Revisions.ChangeCommitted += (_, _) => restoreEvents++;

        Result restored = await context.Items.RestoreItemsAsync([first.Value.ItemId, second.Value.ItemId]);
        restored.IsSuccess.Should().BeTrue(restored.ErrorMessage);
        (await context.RevisionAsync()).Should().Be(beforeRestore + 1);
        restoreEvents.Should().Be(1);
    }

    [Fact]
    public async Task GetItemLifecycle_returns_purged_state_from_purge_records()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "To Purge");
        await context.Items.DeleteItemAsync(item.Value.ItemId);
        Result purged = await context.Purge.PurgeItemsAsync([item.Value.ItemId]);
        purged.IsSuccess.Should().BeTrue(purged.ErrorMessage);

        Result<ItemLifecycleInfo> lifecycle = await context.Items.GetItemLifecycleAsync(item.Value.ItemId);
        lifecycle.IsSuccess.Should().BeTrue(lifecycle.ErrorMessage);
        lifecycle.Value.State.Should().Be(ItemLifecycleState.Purged);
        lifecycle.Value.PurgedAt.Should().NotBeNull();
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        LibraryRevisionService revisions = new(database.ConnectionFactory);
        await library.CreateLibraryAsync("Lifecycle Test");
        ItemService items = new(database.ConnectionFactory, library, clock, revisions);
        return new TestContext(
            database,
            clock,
            items,
            new LibraryItemQueryService(database.ConnectionFactory),
            revisions,
            new ItemPurgeService(database.ConnectionFactory, clock, library, revisions: revisions));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            TemporarySqliteDatabase database,
            FixedClock clock,
            ItemService items,
            LibraryItemQueryService query,
            LibraryRevisionService revisions,
            ItemPurgeService purge)
        {
            Database = database;
            Clock = clock;
            Items = items;
            Query = query;
            Revisions = revisions;
            Purge = purge;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public ItemService Items { get; }
        public LibraryItemQueryService Query { get; }
        public LibraryRevisionService Revisions { get; }
        public ItemPurgeService Purge { get; }

        public async Task<long> RevisionAsync()
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<long>("select library_revision from library_metadata limit 1;");
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
