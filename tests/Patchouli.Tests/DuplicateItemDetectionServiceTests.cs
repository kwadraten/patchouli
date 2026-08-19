using System.Data.Common;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class DuplicateItemDetectionServiceTests
{
    [Fact]
    public async Task FindDuplicatesAsync_detects_identifier_match()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync("article", "First Article");
        context.Clock.UtcNow = context.Clock.UtcNow.AddMinutes(1);
        Result<ItemMetadata> second = await context.Items.CreateItemAsync("article", "Second Article");

        await context.Items.AddIdentifierAsync(first.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/example",
            null);
        await context.Items.AddIdentifierAsync(second.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/example",
            null);

        IReadOnlyList<DuplicateItemPair> pairs = await context.DuplicateDetection.FindDuplicatesAsync();

        pairs.Should().ContainSingle();
        pairs[0].Reasons.Should().Contain(DuplicateItemReason.IdentifierMatch);
        pairs[0].DefaultTargetItemId.Should().Be(first.Value.ItemId);
    }

    [Fact]
    public async Task FindDuplicatesAsync_detects_similar_metadata_match()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync(
            "article",
            "Shared Title",
            creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Lovelace", "Ada")],
            dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1843]]")]);
        Result<ItemMetadata> second = await context.Items.CreateItemAsync(
            "article",
            "Shared Title",
            creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Lovelace", "Ada")],
            dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1843]]")]);

        IReadOnlyList<DuplicateItemPair> pairs = await context.DuplicateDetection.FindDuplicatesAsync();

        pairs.Should().ContainSingle();
        pairs[0].Reasons.Should().Contain(DuplicateItemReason.SimilarMetadata);
    }

    [Fact]
    public async Task FindDuplicatesAsync_detects_file_hash_match()
    {
        await using TestContext context = await CreateContextAsync();
        string firstPath = await TemporaryFile.WriteAsync("duplicate scanned pdf bytes");
        string secondPath = await TemporaryFile.WriteAsync("duplicate scanned pdf bytes");

        try
        {
            Result<FileAsset> firstAsset = await context.FileAssets.RegisterFileAsync(firstPath);
            Result<FileAsset> secondAsset = await context.FileAssets.RegisterFileAsync(secondPath);
            firstAsset.Value.FullBlake3.Should().Be(secondAsset.Value.FullBlake3);

            Result<ItemMetadata> firstItem = await context.Items.CreateItemAsync("book", "First Scan");
            Result<ItemMetadata> secondItem = await context.Items.CreateItemAsync("book", "Second Scan");

            await context.Documents.AttachDocumentInstanceAsync(
                firstItem.Value.ItemId,
                firstAsset.Value.FileAssetId,
                DocumentInstanceType.PrimaryScan);
            await context.Documents.AttachDocumentInstanceAsync(
                secondItem.Value.ItemId,
                secondAsset.Value.FileAssetId,
                DocumentInstanceType.PrimaryScan);

            IReadOnlyList<DuplicateItemPair> pairs = await context.DuplicateDetection.FindDuplicatesAsync();

            pairs.Should().ContainSingle();
            pairs[0].Reasons.Should().Contain(DuplicateItemReason.FileHashMatch);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task FindDuplicatesAsync_merges_multiple_reasons_for_same_pair()
    {
        await using TestContext context = await CreateContextAsync();
        string firstPath = await TemporaryFile.WriteAsync("multi-rule duplicate content");
        string secondPath = await TemporaryFile.WriteAsync("multi-rule duplicate content");

        try
        {
            Result<FileAsset> firstAsset = await context.FileAssets.RegisterFileAsync(firstPath);
            Result<FileAsset> secondAsset = await context.FileAssets.RegisterFileAsync(secondPath);

            Result<ItemMetadata> first = await context.Items.CreateItemAsync(
                "article",
                "Shared Title",
                creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Lovelace", "Ada")],
                dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1843]]")]);
            Result<ItemMetadata> second = await context.Items.CreateItemAsync(
                "article",
                "Shared Title",
                creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Lovelace", "Ada")],
                dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1843]]")]);

            await context.Items.AddIdentifierAsync(first.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/multi",
                null);
            await context.Items.AddIdentifierAsync(second.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/multi",
                null);

            await context.Documents.AttachDocumentInstanceAsync(
                first.Value.ItemId,
                firstAsset.Value.FileAssetId,
                DocumentInstanceType.PrimaryScan);
            await context.Documents.AttachDocumentInstanceAsync(
                second.Value.ItemId,
                secondAsset.Value.FileAssetId,
                DocumentInstanceType.PrimaryScan);

            IReadOnlyList<DuplicateItemPair> pairs = await context.DuplicateDetection.FindDuplicatesAsync();

            pairs.Should().ContainSingle();
            pairs[0].Reasons.Should().BeEquivalentTo(new[]
            {
                DuplicateItemReason.IdentifierMatch,
                DuplicateItemReason.SimilarMetadata,
                DuplicateItemReason.FileHashMatch
            });
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task FindDuplicatesAsync_excludes_deleted_items()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> active = await context.Items.CreateItemAsync("article", "Active");
        Result<ItemMetadata> deleted = await context.Items.CreateItemAsync("article", "Deleted");

        await context.Items.AddIdentifierAsync(active.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/deleted",
            null);
        await context.Items.AddIdentifierAsync(deleted.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/deleted",
            null);
        await context.Items.DeleteItemAsync(deleted.Value.ItemId);

        IReadOnlyList<DuplicateItemPair> pairs = await context.DuplicateDetection.FindDuplicatesAsync();

        pairs.Should().BeEmpty();
    }

    [Fact]
    public async Task FindDuplicatesAsync_excludes_merged_tombstones()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> source = await context.Items.CreateItemAsync("article", "Source");
        Result<ItemMetadata> target = await context.Items.CreateItemAsync("article", "Target");
        Result<ItemMetadata> other = await context.Items.CreateItemAsync("article", "Other");

        await context.Items.AddIdentifierAsync(source.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/merged",
            null);
        await context.Items.AddIdentifierAsync(target.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/merged",
            null);
        await context.Items.AddIdentifierAsync(other.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/merged",
            null);

        Result mergeResult = await context.MergeItems.MergeAsync(
            source.Value.ItemId,
            target.Value.ItemId,
            [],
            _ => false);
        mergeResult.IsSuccess.Should().BeTrue();

        IReadOnlyList<DuplicateItemPair> pairs = await context.DuplicateDetection.FindDuplicatesAsync();

        pairs.Should().ContainSingle();
        pairs[0].ItemIdA.Should().BeOneOf(target.Value.ItemId, other.Value.ItemId);
        pairs[0].ItemIdB.Should().BeOneOf(target.Value.ItemId, other.Value.ItemId);
    }

    [Fact]
    public async Task FindDuplicatesAsync_does_not_write_when_only_detecting()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> first = await context.Items.CreateItemAsync("article", "First");
        Result<ItemMetadata> second = await context.Items.CreateItemAsync("article", "Second");

        await context.Items.AddIdentifierAsync(first.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/readonly",
            null);
        await context.Items.AddIdentifierAsync(second.Value.ItemId, BuiltInIdentifierSchemes.DOI, "10.1234/readonly",
            null);

        int activeItemCountBefore = await GetActiveItemCountAsync(context);

        await context.DuplicateDetection.FindDuplicatesAsync();

        int activeItemCountAfter = await GetActiveItemCountAsync(context);
        activeItemCountAfter.Should().Be(activeItemCountBefore);
    }

    private static async Task<int> GetActiveItemCountAsync(TestContext context)
    {
        await using SqliteConnection connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
            "select count(1) from items where deleted_at is null and merged_into_item_id is null;");
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Duplicate Detection Test");
        CapturingRevisionService revisions = new();
        ItemService items = new(database.ConnectionFactory, library, clock, revisions);
        DocumentInstanceService documents = new(database.ConnectionFactory, clock, revisions);
        FileAssetService fileAssets = new(database.ConnectionFactory, library, clock, revisions: revisions);
        ItemMergeService mergeItems = new(database.ConnectionFactory, clock, library, revisions);
        DuplicateItemDetectionService duplicateDetection = new(database.ConnectionFactory, library);
        return new TestContext(database, clock, items, documents, fileAssets, mergeItems, duplicateDetection);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        public TestContext(
            TemporarySqliteDatabase database,
            FixedClock clock,
            ItemService items,
            DocumentInstanceService documents,
            FileAssetService fileAssets,
            ItemMergeService mergeItems,
            DuplicateItemDetectionService duplicateDetection)
        {
            Database = database;
            Clock = clock;
            Items = items;
            Documents = documents;
            FileAssets = fileAssets;
            MergeItems = mergeItems;
            DuplicateDetection = duplicateDetection;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public ItemService Items { get; }
        public DocumentInstanceService Documents { get; }
        public FileAssetService FileAssets { get; }
        public ItemMergeService MergeItems { get; }
        public DuplicateItemDetectionService DuplicateDetection { get; }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class CapturingRevisionService : ILibraryRevisionService
    {
        public event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted;
        public int PublishCount { get; private set; }

        public void ResetPublishCount()
        {
            PublishCount = 0;
        }

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
            PublishCount++;
            ChangeCommitted?.Invoke(this, new LibraryRevisionCommittedEventArgs(changeSet));
        }
    }

    private static class TemporaryFile
    {
        public static async Task<string> WriteAsync(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), $"patchouli-dup-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, content);
            return path;
        }
    }
}
