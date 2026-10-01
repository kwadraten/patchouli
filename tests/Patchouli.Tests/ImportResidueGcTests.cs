using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Import;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ImportResidueGcTests
{
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-07-08T00:00:00Z");
    private static readonly DateTimeOffset Aged = BaseTime - TimeSpan.FromHours(2);
    private static readonly DateTimeOffset Recent = BaseTime - TimeSpan.FromMinutes(30);

    [Fact]
    public async Task Run_removes_zero_page_instances_and_their_item_left_without_instances()
    {
        await using Ctx c = await Ctx.Create();
        string itemId = Ctx.NewId();
        string documentId = Ctx.NewId();
        await c.InsertItemAsync(itemId, "Half Imported Book", Aged);
        await c.InsertZeroPageInstanceAsync(documentId, itemId, Aged);
        long revisionBefore = await c.GetRevisionAsync();
        List<LibraryChangeSet> committed = new();
        c.Revisions.ChangeCommitted += (_, args) => committed.Add(args.ChangeSet);

        ImportResidueGcResult result = await c.Gc.RunAsync(HermeticOptions);

        result.InstancesRemoved.Should().Be(1);
        result.ItemsRemoved.Should().Be(1);
        result.FileAssetsRemoved.Should().Be(0);
        (await c.CountAsync("document_instances")).Should().Be(0);
        (await c.CountAsync("items")).Should().Be(0);
        (await c.GetRevisionAsync()).Should().Be(revisionBefore + 1);
        committed.Should().ContainSingle()
            .Which.Should().Match<LibraryChangeSet>(changeSet =>
                changeSet.ItemIds.Select(id => id.ToString()).Contains(itemId) &&
                changeSet.DocumentInstanceIds.Select(id => id.ToString()).Contains(documentId));
        c.Logger.Logs.Should().Contain(log => log.Contains("zero-page", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_removes_orphan_item_paired_with_title_matching_unreferenced_asset()
    {
        await using Ctx c = await Ctx.Create();
        string itemId = Ctx.NewId();
        string assetId = Ctx.NewId();
        await c.InsertItemAsync(itemId, "Half Imported Book", Aged);
        await c.InsertFileAssetAsync(assetId, "D:/books/Half Imported Book.pdf", Aged);
        await c.InsertKnownFileLocationAsync(assetId, "D:/books/Half Imported Book.pdf");
        long revisionBefore = await c.GetRevisionAsync();

        ImportResidueGcResult result = await c.Gc.RunAsync(HermeticOptions);

        result.InstancesRemoved.Should().Be(0);
        result.ItemsRemoved.Should().Be(1);
        result.FileAssetsRemoved.Should().Be(1);
        (await c.CountAsync("items")).Should().Be(0);
        (await c.CountAsync("file_assets")).Should().Be(0);
        (await c.CountAsync("known_file_locations")).Should().Be(0);
        (await c.GetRevisionAsync()).Should().Be(revisionBefore + 1);
    }

    [Fact]
    public async Task Run_keeps_identifier_only_bibliographic_record_and_its_title_matching_asset()
    {
        await using Ctx c = await Ctx.Create();
        string itemId = Ctx.NewId();
        string assetId = Ctx.NewId();
        await c.InsertItemAsync(itemId, "Identified Article", Aged);
        await c.InsertIdentifierAsync(itemId, Aged);
        await c.InsertFileAssetAsync(assetId, "D:/papers/Identified Article.pdf", Aged);
        long revisionBefore = await c.GetRevisionAsync();

        ImportResidueGcResult result = await c.Gc.RunAsync(HermeticOptions);

        result.InstancesRemoved.Should().Be(0);
        result.ItemsRemoved.Should().Be(0);
        result.FileAssetsRemoved.Should().Be(0);
        (await c.CountAsync("items")).Should().Be(1);
        (await c.CountAsync("item_identifiers")).Should().Be(1);
        (await c.CountAsync("file_assets")).Should().Be(1);
        (await c.GetRevisionAsync()).Should().Be(revisionBefore);
    }

    [Fact]
    public async Task Run_keeps_orphan_item_when_no_asset_stem_matches_its_title()
    {
        await using Ctx c = await Ctx.Create();
        string pairedItemId = Ctx.NewId();
        string pairedAssetId = Ctx.NewId();
        string unpairedItemId = Ctx.NewId();
        string unrelatedAssetId = Ctx.NewId();
        await c.InsertItemAsync(pairedItemId, "Half Imported Book", Aged);
        await c.InsertFileAssetAsync(pairedAssetId, "D:/books/Half Imported Book.pdf", Aged);
        await c.InsertItemAsync(unpairedItemId, "Carefully Typed Title", Aged);
        await c.InsertFileAssetAsync(unrelatedAssetId, "D:/books/Something Else Entirely.pdf", Aged);

        ImportResidueGcResult result = await c.Gc.RunAsync(HermeticOptions);

        result.ItemsRemoved.Should().Be(1);
        result.FileAssetsRemoved.Should().Be(1);
        (await c.CountAsync("items")).Should().Be(1);
        (await c.ItemExistsAsync(unpairedItemId)).Should().BeTrue();
        (await c.CountAsync("file_assets")).Should().Be(1);
        (await c.AssetExistsAsync(unrelatedAssetId)).Should().BeTrue();
    }

    [Fact]
    public async Task Run_keeps_book_with_pages_untouched()
    {
        await using Ctx c = await Ctx.Create();
        string itemId = Ctx.NewId();
        string documentId = Ctx.NewId();
        string pageId = Ctx.NewId();
        string assetId = Ctx.NewId();
        await c.InsertItemAsync(itemId, "Real Book", Aged);
        await c.InsertFileAssetAsync(assetId, "D:/books/Real Book.pdf", Aged);
        await c.InsertInstanceAsync(documentId, itemId, assetId, Aged);
        await c.InsertPageAsync(pageId, documentId, 0, Aged);
        long revisionBefore = await c.GetRevisionAsync();

        ImportResidueGcResult result = await c.Gc.RunAsync(HermeticOptions);

        result.Should().Match<ImportResidueGcResult>(r =>
            r.InstancesRemoved == 0 && r.ItemsRemoved == 0 && r.FileAssetsRemoved == 0);
        (await c.CountAsync("items")).Should().Be(1);
        (await c.CountAsync("document_instances")).Should().Be(1);
        (await c.CountAsync("pages")).Should().Be(1);
        (await c.CountAsync("file_assets")).Should().Be(1);
        (await c.GetRevisionAsync()).Should().Be(revisionBefore);
    }

    [Fact]
    public async Task Run_keeps_recent_residue_below_minimum_age()
    {
        await using Ctx c = await Ctx.Create();
        string itemId = Ctx.NewId();
        string documentId = Ctx.NewId();
        string orphanItemId = Ctx.NewId();
        string assetId = Ctx.NewId();
        await c.InsertItemAsync(itemId, "In Flight Import", Recent);
        await c.InsertZeroPageInstanceAsync(documentId, itemId, Recent);
        await c.InsertItemAsync(orphanItemId, "In Flight Import", Recent);
        await c.InsertFileAssetAsync(assetId, "D:/books/In Flight Import.pdf", Recent);
        long revisionBefore = await c.GetRevisionAsync();

        ImportResidueGcResult result = await c.Gc.RunAsync(HermeticOptions);

        result.Should().Match<ImportResidueGcResult>(r =>
            r.InstancesRemoved == 0 && r.ItemsRemoved == 0 && r.FileAssetsRemoved == 0);
        (await c.CountAsync("items")).Should().Be(2);
        (await c.CountAsync("document_instances")).Should().Be(1);
        (await c.CountAsync("file_assets")).Should().Be(1);
        (await c.GetRevisionAsync()).Should().Be(revisionBefore);
    }

    [Fact]
    public async Task Run_on_clean_library_is_a_no_op()
    {
        await using Ctx c = await Ctx.Create();
        long revisionBefore = await c.GetRevisionAsync();

        ImportResidueGcResult result = await c.Gc.RunAsync(HermeticOptions);

        result.InstancesRemoved.Should().Be(0);
        result.ItemsRemoved.Should().Be(0);
        result.FileAssetsRemoved.Should().Be(0);
        result.Notes.Should().BeEmpty();
        (await c.GetRevisionAsync()).Should().Be(revisionBefore);
    }

    [Fact]
    public async Task Startup_cleanup_records_completion_and_skips_later_full_scans()
    {
        await using Ctx c = await Ctx.Create();
        (await c.Gc.RunOnceAsync()).IsSuccess.Should().BeTrue();
        (await c.CountAsync("local_maintenance_state")).Should().Be(1);
        string itemId = Ctx.NewId();
        await c.InsertItemAsync(itemId, "Created after cleanup", Aged);
        await c.InsertZeroPageInstanceAsync(Ctx.NewId(), itemId, Aged);
        ImportResidueGcResult second = await c.Gc.RunOnceAsync();
        second.InstancesRemoved.Should().Be(0);
        (await c.ItemExistsAsync(itemId)).Should().BeTrue();
    }

    [Fact]
    public async Task Startup_cleanup_retries_when_recent_residue_is_not_yet_eligible()
    {
        await using Ctx c = await Ctx.Create();
        string itemId = Ctx.NewId();
        await c.InsertItemAsync(itemId, "Recent residue", Recent);
        await c.InsertZeroPageInstanceAsync(Ctx.NewId(), itemId, Recent);
        await c.Gc.RunOnceAsync();
        (await c.CountAsync("local_maintenance_state")).Should().Be(0);
        (await c.ItemExistsAsync(itemId)).Should().BeTrue();
    }

    private static ImportResidueGcOptions HermeticOptions { get; } =
        new(IncludeUnreferencedFileAssets: false);

    private static string Format(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(
            TemporarySqliteDatabase database,
            FixedClock clock,
            LibraryIdentityService library,
            LibraryRevisionService revisions,
            ImportResidueGcService gc,
            FakeAppLogger logger,
            string libraryId)
        {
            Database = database;
            Clock = clock;
            Library = library;
            Revisions = revisions;
            Gc = gc;
            Logger = logger;
            LibraryId = libraryId;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public LibraryRevisionService Revisions { get; }
        public ImportResidueGcService Gc { get; }
        public FakeAppLogger Logger { get; }
        public string LibraryId { get; }

        public static string NewId()
        {
            return Guid.NewGuid().ToString();
        }

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(BaseTime);
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Residue GC");
            LibraryRevisionService revisions = new(database.ConnectionFactory);
            FakeAppLogger logger = new();
            ImportResidueGcService gc = new(database.ConnectionFactory, clock, revisions, logger: logger);
            LibraryMetadata metadata = (await library.GetCurrentLibraryAsync()).Value;
            return new Ctx(database, clock, library, revisions, gc, logger, metadata.LibraryId.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task<long> GetRevisionAsync()
        {
            Result<long> revision = await Revisions.GetCurrentRevisionAsync();
            revision.IsSuccess.Should().BeTrue(revision.ErrorMessage);
            return revision.Value;
        }

        public async Task<int> CountAsync(string table)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>($"select count(1) from {table};");
        }

        public async Task<bool> ItemExistsAsync(string itemId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(
                "select count(1) from items where item_id = @ItemId;",
                new { ItemId = itemId }) > 0;
        }

        public async Task<bool> AssetExistsAsync(string fileAssetId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(
                "select count(1) from file_assets where file_asset_id = @FileAssetId;",
                new { FileAssetId = fileAssetId }) > 0;
        }

        public async Task InsertItemAsync(string itemId, string title, DateTimeOffset createdAt)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into items (item_id, library_id, item_type, title, created_at, updated_at)
                values (@ItemId, @LibraryId, 'general', @Title, @CreatedAt, @CreatedAt);
                """,
                new
                {
                    ItemId = itemId,
                    LibraryId = LibraryId,
                    Title = title,
                    CreatedAt = Format(createdAt)
                });
        }

        public async Task InsertIdentifierAsync(string itemId, DateTimeOffset createdAt)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into item_identifiers (identifier_id, item_id, scheme, value, created_at)
                values (@IdentifierId, @ItemId, 'doi', '10.0000/residue-gc', @CreatedAt);
                """,
                new
                {
                    IdentifierId = NewId(),
                    ItemId = itemId,
                    CreatedAt = Format(createdAt)
                });
        }

        public async Task InsertFileAssetAsync(
            string fileAssetId,
            string originalPath,
            DateTimeOffset createdAt)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into file_assets (
                    file_asset_id, library_id, original_path, file_name, size_bytes, status,
                    created_at, updated_at
                )
                values (
                    @FileAssetId, @LibraryId, @OriginalPath, @FileName, 100, 'available',
                    @CreatedAt, @CreatedAt
                );
                """,
                new
                {
                    FileAssetId = fileAssetId,
                    LibraryId = LibraryId,
                    OriginalPath = originalPath,
                    FileName = Path.GetFileName(originalPath),
                    CreatedAt = Format(createdAt)
                });
        }

        public async Task InsertKnownFileLocationAsync(string fileAssetId, string path)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into known_file_locations (location_id, file_asset_id, path, last_seen_at, status)
                values (@LocationId, @FileAssetId, @Path, @LastSeenAt, 'available');
                """,
                new
                {
                    LocationId = NewId(),
                    FileAssetId = fileAssetId,
                    Path = path,
                    LastSeenAt = Format(Clock.UtcNow)
                });
        }

        public async Task InsertZeroPageInstanceAsync(
            string documentInstanceId,
            string itemId,
            DateTimeOffset createdAt)
        {
            await InsertInstanceAsync(documentInstanceId, itemId, null, createdAt);
        }

        public async Task InsertInstanceAsync(
            string documentInstanceId,
            string itemId,
            string? fileAssetId,
            DateTimeOffset createdAt)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_instances (
                    document_instance_id, item_id, file_asset_id, instance_type, is_primary, status,
                    created_at, updated_at
                )
                values (
                    @DocumentInstanceId, @ItemId, @FileAssetId, 'primary_scan', 1, 'active',
                    @CreatedAt, @CreatedAt
                );
                """,
                new
                {
                    DocumentInstanceId = documentInstanceId,
                    ItemId = itemId,
                    FileAssetId = fileAssetId,
                    CreatedAt = Format(createdAt)
                });
        }

        public async Task InsertPageAsync(
            string pageId,
            string documentInstanceId,
            int pageIndex,
            DateTimeOffset createdAt)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into pages (
                    page_id, document_instance_id, page_index, rotation, coordinate_basis,
                    renderer_basis_version, created_at, updated_at
                )
                values (
                    @PageId, @DocumentInstanceId, @PageIndex, 0, 'normalized_page', 'test',
                    @CreatedAt, @CreatedAt
                );
                """,
                new
                {
                    PageId = pageId,
                    DocumentInstanceId = documentInstanceId,
                    PageIndex = pageIndex,
                    CreatedAt = Format(createdAt)
                });
        }
    }

    private sealed class FakeAppLogger : IAppLogger
    {
        public List<string> Logs { get; } = new();

        public Task LogAsync(string operation, string message)
        {
            Logs.Add($"{operation} {message}");
            return Task.CompletedTask;
        }
    }
}
