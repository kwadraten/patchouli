using System.Data.Common;
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

public sealed class CollectionServiceTests
{
    [Fact]
    public async Task Migration_creates_collection_tables_and_clears_legacy_collections_json()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Legacy");

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "update items set collections_json = '[\"Old\"]' where item_id = @ItemId;",
            new { ItemId = item.Value.ItemId.ToString() });
        await connection.ExecuteAsync("delete from schema_migrations where id = '040';");

        await new MigrationRunner(context.Database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        string? collectionsJson = await connection.ExecuteScalarAsync<string>(
            "select collections_json from items where item_id = @ItemId;",
            new { ItemId = item.Value.ItemId.ToString() });
        collectionsJson.Should().Be("[]");
        long tableCount = await connection.ExecuteScalarAsync<long>(
            "select count(1) from sqlite_master where type = 'table' and name in ('collections', 'item_collections');");
        tableCount.Should().Be(2);
    }

    [Fact]
    public async Task Create_lists_sorted_by_name_and_rejects_case_insensitively_distinct_duplicates()
    {
        await using TestContext context = await CreateContextAsync();
        await context.Collections.CreateCollectionAsync("Zeta");
        await context.Collections.CreateCollectionAsync("Alpha");

        Result<IReadOnlyList<Collection>> list = await context.Collections.ListCollectionsAsync();
        list.Value.Select(collection => collection.Name).Should().Equal("Alpha", "Zeta");

        Result<Collection> duplicate = await context.Collections.CreateCollectionAsync("Alpha");
        duplicate.IsFailure.Should().BeTrue();
        duplicate.ErrorCode.Should().Be(AppErrorCodes.Conflict);
    }

    [Fact]
    public async Task Rename_and_dissolve_never_delete_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Member");
        Result<Collection> collection = await context.Collections.CreateCollectionAsync("Reading");
        await context.Collections.AddItemsAsync(collection.Value.CollectionId, [item.Value.ItemId]);

        Result<Collection> renamed =
            await context.Collections.RenameCollectionAsync(collection.Value.CollectionId, "Assigned");
        renamed.IsSuccess.Should().BeTrue();
        renamed.Value.Name.Should().Be("Assigned");

        Result dissolved = await context.Collections.DissolveCollectionAsync(collection.Value.CollectionId);
        dissolved.IsSuccess.Should().BeTrue();

        Result<IReadOnlyList<Collection>> collections = await context.Collections.ListCollectionsAsync();
        collections.Value.Should().BeEmpty();

        Result<ItemMetadata> stillThere = await context.Items.GetItemAsync(item.Value.ItemId);
        stillThere.IsSuccess.Should().BeTrue();

        Result<IReadOnlyList<LibraryItemRow>> rows = await context.Query.ListRowsAsync();
        rows.Value.Should().ContainSingle(row => row.ItemId == item.Value.ItemId);
    }

    [Fact]
    public async Task Membership_survives_trash_restore_merge_and_is_removed_on_purge()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("book", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("book", "Target");
        Result<Collection> first = await context.Collections.CreateCollectionAsync("First");
        Result<Collection> second = await context.Collections.CreateCollectionAsync("Second");
        await context.Collections.AddItemsAsync(first.Value.CollectionId, [source.Value.ItemId]);
        await context.Collections.AddItemsAsync(second.Value.CollectionId, [target.Value.ItemId]);

        await context.Items.DeleteItemAsync(source.Value.ItemId);
        Result<IReadOnlyList<Collection>> afterTrash = await context.Collections.ListCollectionsAsync();
        afterTrash.Value.Single(collection => collection.CollectionId == first.Value.CollectionId).ItemCount
            .Should().Be(0, "trashed items are excluded from counts");

        await context.Items.RestoreItemAsync(source.Value.ItemId);
        Result<IReadOnlyList<Collection>> afterRestore = await context.Collections.ListCollectionsAsync();
        afterRestore.Value.Single(collection => collection.CollectionId == first.Value.CollectionId).ItemCount
            .Should().Be(1);

        ItemMergeService merge = new(context.Database.ConnectionFactory, context.Clock,
            context.Library, context.Revisions);
        Result merged = await merge.MergeAsync(source.Value.ItemId, target.Value.ItemId, [], _ => false);
        merged.IsSuccess.Should().BeTrue();

        Result<IReadOnlyList<CollectionId>> mergedMembership =
            await context.Collections.GetItemCollectionIdsAsync(target.Value.ItemId);
        mergedMembership.Value.Should().Contain(first.Value.CollectionId)
            .And.Contain(second.Value.CollectionId);
    }

    [Fact]
    public async Task Purge_removes_collection_membership()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Disposable");
        Result<Collection> collection = await context.Collections.CreateCollectionAsync("Reading");
        await context.Collections.AddItemsAsync(collection.Value.CollectionId, [item.Value.ItemId]);
        await context.Items.DeleteItemAsync(item.Value.ItemId);

        ItemPurgeService purge = new(context.Database.ConnectionFactory, context.Clock, context.Library);
        Result purged = await purge.PurgeItemsAsync([item.Value.ItemId]);
        purged.IsSuccess.Should().BeTrue(purged.ErrorMessage);

        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        long memberships = await connection.ExecuteScalarAsync<long>(
            "select count(1) from item_collections where item_id = @ItemId;",
            new { ItemId = item.Value.ItemId.ToString() });
        memberships.Should().Be(0);
    }

    [Fact]
    public async Task Bibliographic_search_filters_by_exact_tag_and_collection_id_with_and()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> book = await context.Items.CreateItemAsync("book", "Book", tagsJson: "[\"Alpha\"]");
        await context.Items.CreateItemAsync("book", "Other", tagsJson: "[\"beta\"]");
        Result<Collection> collection = await context.Collections.CreateCollectionAsync("Reading");
        Result<Collection> emptyCollection = await context.Collections.CreateCollectionAsync("Empty");
        await context.Collections.AddItemsAsync(collection.Value.CollectionId, [book.Value.ItemId]);

        Result<IReadOnlyList<LibraryItemRow>> byTag = await context.Query.SearchRowsAsync(
            new BibliographicItemSearch(null, [new BibliographicSearchFilter("tag", "Alpha")]));
        byTag.Value.Should().ContainSingle(row => row.ItemId == book.Value.ItemId);

        Result<IReadOnlyList<LibraryItemRow>> caseMismatch = await context.Query.SearchRowsAsync(
            new BibliographicItemSearch(null, [new BibliographicSearchFilter("tag", "alpha")]));
        caseMismatch.Value.Should().BeEmpty();

        Result<IReadOnlyList<LibraryItemRow>> intersection = await context.Query.SearchRowsAsync(
            new BibliographicItemSearch(null,
            [
                new BibliographicSearchFilter("tag", "Alpha"),
                new BibliographicSearchFilter("collection_id", collection.Value.CollectionId.ToString())
            ]));
        intersection.Value.Should().ContainSingle(row => row.ItemId == book.Value.ItemId);

        Result<IReadOnlyList<LibraryItemRow>> contradiction = await context.Query.SearchRowsAsync(
            new BibliographicItemSearch(null,
            [
                new BibliographicSearchFilter("tag", "beta"),
                new BibliographicSearchFilter("collection_id", collection.Value.CollectionId.ToString())
            ]));
        contradiction.Value.Should().BeEmpty();

        Result<IReadOnlyList<LibraryItemRow>> emptyCollectionRows = await context.Query.SearchRowsAsync(
            new BibliographicItemSearch(null,
            [
                new BibliographicSearchFilter("collection_id", emptyCollection.Value.CollectionId.ToString())
            ]));
        emptyCollectionRows.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_item_collections_validates_item_and_collection_before_deleting_membership()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Member");
        Result<Collection> collection = await context.Collections.CreateCollectionAsync("Reading");
        await context.Collections.SetItemCollectionsAsync(item.Value.ItemId, [collection.Value.CollectionId]);

        Result missingCollection = await context.Collections.SetItemCollectionsAsync(
            item.Value.ItemId, [CollectionId.New()]);
        missingCollection.IsFailure.Should().BeTrue();
        missingCollection.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        Result<IReadOnlyList<CollectionId>> afterMissing = await context.Collections.GetItemCollectionIdsAsync(
            item.Value.ItemId);
        afterMissing.Value.Should().Equal([collection.Value.CollectionId],
            "a rejected replacement must not delete the existing membership");

        await context.Items.DeleteItemAsync(item.Value.ItemId);
        Result inactive = await context.Collections.SetItemCollectionsAsync(item.Value.ItemId, []);
        inactive.IsFailure.Should().BeTrue();
        inactive.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Item_metadata_update_and_collection_membership_commit_atomically()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Original");
        Result<Collection> first = await context.Collections.CreateCollectionAsync("First");
        Result<Collection> second = await context.Collections.CreateCollectionAsync("Second");

        Result<ItemMetadata> updated = await context.Items.UpdateItemAsync(
            item.Value.ItemId,
            new UpdateItemRequest("book", "Renamed", ExpectedUpdatedAt: item.Value.UpdatedAt,
                Collections: [first.Value.CollectionId]));
        updated.IsSuccess.Should().BeTrue(updated.ErrorMessage);
        (await context.Collections.GetItemCollectionIdsAsync(item.Value.ItemId)).Value
            .Should().Equal(first.Value.CollectionId);

        Result<ItemMetadata> rejected = await context.Items.UpdateItemAsync(
            item.Value.ItemId,
            new UpdateItemRequest("book", "Should not persist", ExpectedUpdatedAt: updated.Value.UpdatedAt,
                Collections: [second.Value.CollectionId, CollectionId.New()]));
        rejected.IsFailure.Should().BeTrue();
        rejected.ErrorCode.Should().Be(AppErrorCodes.NotFound);

        Result<ItemMetadata> afterRejected = await context.Items.GetItemAsync(item.Value.ItemId);
        afterRejected.Value.Title.Should().Be("Renamed", "the metadata write rolls back with the membership write");
        (await context.Collections.GetItemCollectionIdsAsync(item.Value.ItemId)).Value
            .Should().Equal([first.Value.CollectionId], "the prior membership is preserved on failure");

        // The fixed test clock makes legitimate writes share one updated_at, so force a stale row
        // version before exercising the optimistic-concurrency path.
        await using (SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("update items set updated_at = @Now where item_id = @Id;",
                new { Now = "2099-01-01T00:00:00.0000000+00:00", Id = item.Value.ItemId.ToString() });
        }

        Result<ItemMetadata> conflict = await context.Items.UpdateItemAsync(
            item.Value.ItemId,
            new UpdateItemRequest("book", "Stale write", ExpectedUpdatedAt: updated.Value.UpdatedAt,
                Collections: [second.Value.CollectionId]));
        conflict.IsFailure.Should().BeTrue();
        conflict.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        (await context.Collections.GetItemCollectionIdsAsync(item.Value.ItemId)).Value
            .Should().Equal([first.Value.CollectionId], "a concurrency failure keeps the prior membership");
    }

    [Fact]
    public async Task Collection_mutations_publish_affected_collection_ids()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> item = await context.Items.CreateItemAsync("book", "Member");

        Result<Collection> created = await context.Collections.CreateCollectionAsync("Reading");
        context.Revisions.Published.Should().ContainSingle(changeSet =>
            changeSet.CollectionIds.Contains(created.Value.CollectionId));

        await context.Collections.AddItemsAsync(created.Value.CollectionId, [item.Value.ItemId]);
        context.Revisions.Published.Should().Contain(changeSet =>
            changeSet.CollectionIds.Contains(created.Value.CollectionId) &&
            changeSet.ItemIds.Contains(item.Value.ItemId));

        await context.Collections.DissolveCollectionAsync(created.Value.CollectionId);
        context.Revisions.Published.Last().CollectionIds.Should().Equal(created.Value.CollectionId);
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Collection Test");
        CapturingRevisionService revisions = new();
        ItemService items = new(database.ConnectionFactory, library, clock, revisions);
        CollectionService collections = new(database.ConnectionFactory, library, clock, revisions);
        return new TestContext(database, clock, library, items, collections, revisions,
            new LibraryItemQueryService(database.ConnectionFactory));
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(TemporarySqliteDatabase database, FixedClock clock, LibraryIdentityService library,
            ItemService items, CollectionService collections, CapturingRevisionService revisions,
            LibraryItemQueryService query)
        {
            Database = database;
            Clock = clock;
            Library = library;
            Items = items;
            Collections = collections;
            Revisions = revisions;
            Query = query;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public ItemService Items { get; }
        public CollectionService Collections { get; }
        public CapturingRevisionService Revisions { get; }
        public LibraryItemQueryService Query { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class CapturingRevisionService : ILibraryRevisionService
    {
        private long _revision;

        public List<LibraryChangeSet> Published { get; } = [];

        public event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted;

        public Task<Result<long>> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<long>.Success(_revision));
        }

        public Task<Result<long>> CommitAsync(LibraryChangeSet changeSet,
            CancellationToken cancellationToken = default)
        {
            _revision++;
            return Task.FromResult(Result<long>.Success(_revision));
        }

        public Task<Result<LibraryChangeSet>> IncrementInTransactionAsync(DbConnection connection,
            DbTransaction transaction, LibraryChangeSet changeSet, CancellationToken cancellationToken = default)
        {
            _revision++;
            return Task.FromResult(Result<LibraryChangeSet>.Success(changeSet with { NewRevision = _revision }));
        }

        public void PublishCommitted(LibraryChangeSet changeSet)
        {
            Published.Add(changeSet);
            ChangeCommitted?.Invoke(this, new LibraryRevisionCommittedEventArgs(changeSet));
        }
    }
}
