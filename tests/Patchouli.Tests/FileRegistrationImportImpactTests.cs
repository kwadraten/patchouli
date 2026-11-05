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
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Import;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class FileRegistrationImportImpactTests : IAsyncLifetime
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), $"patchouli-impact-test-{Guid.NewGuid():N}");
    private TemporarySqliteDatabase _database = null!;
    private LibraryIdentityService _library = null!;
    private LibraryRevisionService _revisions = null!;
    private FileAssetService _files = null!;
    private ImportBatchWriter _writer = null!;
    private ItemService _items = null!;
    private DocumentInstanceService _documents = null!;
    private FixedClock _clock = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_testDir);
        _database = TemporarySqliteDatabase.Create();
        _clock = new FixedClock(DateTimeOffset.Parse("2026-11-05T00:00:00Z"));
        await new MigrationRunner(_database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        _library = new LibraryIdentityService(_database.ConnectionFactory, _clock);
        (await _library.CreateLibraryAsync("Impact Tests")).IsSuccess.Should().BeTrue();
        _revisions = new LibraryRevisionService(_database.ConnectionFactory);
        _files = new FileAssetService(_database.ConnectionFactory, _library, _clock, revisions: _revisions);
        _writer = new ImportBatchWriter(_database.ConnectionFactory, _revisions);
        _items = new ItemService(_database.ConnectionFactory, _library, _clock);
        _documents = new DocumentInstanceService(_database.ConnectionFactory, _clock);
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public async Task Registration_shared_path_impact_includes_every_required_owner_and_excludes_unrelated()
    {
        string path1 = Path.Combine(_testDir, "doc1.pdf");
        string path3 = Path.Combine(_testDir, "doc3.pdf");
        await File.WriteAllTextAsync(path1, "PDF Content 1");
        await File.WriteAllTextAsync(path3, "PDF Content 3");

        // 1. Register asset 1
        FileAsset asset1 = (await _files.RegisterFileAsync(path1)).Value;
        ItemMetadata item1 = (await _items.CreateItemAsync(new CreateItemRequest("book", "Item 1"))).Value;
        DocumentInstance doc1 = (await _documents.AttachDocumentInstanceAsync(
            item1.ItemId, asset1.FileAssetId, DocumentInstanceType.PrimaryScan)).Value;

        // 2. Pre-insert asset 2 into database with a shared location at path1 (shared path collision)
        // Asset must exist in file_assets before AttachDocumentInstanceAsync is called.
        FileAssetId asset2Id = FileAssetId.New();
        await using (SqliteConnection connection = _database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into file_assets (
                    file_asset_id, library_id, original_path, file_name, size_bytes,
                    full_blake3, status, created_at, updated_at
                ) values (
                    @FileAssetId, @LibraryId, @Path, 'doc1.pdf', 100,
                    'fakeblake3hashforasset2colliding0', 'available', '2026-11-05T00:00:00Z', '2026-11-05T00:00:00Z'
                );
                insert into known_file_locations (location_id, file_asset_id, path, last_seen_at, status)
                values (@LocationId, @FileAssetId, @Path, '2026-11-05T00:00:00Z', 'available');
                """,
                new
                {
                    FileAssetId = asset2Id.ToString(),
                    LibraryId = asset1.LibraryId.ToString(),
                    Path = Path.GetFullPath(path1),
                    LocationId = KnownFileLocationId.New().ToString()
                });
        }

        // Attach document 2 to asset 2
        ItemMetadata item2 = (await _items.CreateItemAsync(new CreateItemRequest("book", "Item 2"))).Value;
        DocumentInstance doc2 = (await _documents.AttachDocumentInstanceAsync(
            item2.ItemId, asset2Id, DocumentInstanceType.PrimaryScan)).Value;

        // 3. Register asset 3 at path3 (unrelated asset/path)
        FileAsset asset3 = (await _files.RegisterFileAsync(path3)).Value;
        ItemMetadata item3 = (await _items.CreateItemAsync(new CreateItemRequest("book", "Item 3 (Unrelated)"))).Value;
        DocumentInstance doc3 = (await _documents.AttachDocumentInstanceAsync(
            item3.ItemId, asset3.FileAssetId, DocumentInstanceType.PrimaryScan)).Value;

        // 4. Trigger an existing asset location change on asset1 by registering a copy:
        // Preserves exact content hash so RegisterFileAsync resolves existingAsset and updates locations.
        string path1Copy = Path.Combine(_testDir, "doc1_copy.pdf");
        File.Copy(path1, path1Copy);

        LibraryChangeSet? published = null;
        _revisions.ChangeCommitted += (_, args) => published = args.ChangeSet;

        Result<FileAsset> reRegistered = await _files.RegisterFileAsync(path1Copy);
        reRegistered.IsSuccess.Should().BeTrue(reRegistered.ErrorMessage);

        published.Should().NotBeNull();
        // Shared path collision must include both owners:
        published!.ItemIds.Should().Contain(item1.ItemId);
        published.ItemIds.Should().Contain(item2.ItemId);
        published.DocumentInstanceIds.Should().Contain(doc1.DocumentInstanceId);
        published.DocumentInstanceIds.Should().Contain(doc2.DocumentInstanceId);

        // Unrelated document must NOT be in the change set:
        published.ItemIds.Should().NotContain(item3.ItemId);
        published.DocumentInstanceIds.Should().NotContain(doc3.DocumentInstanceId);
    }

    [Fact]
    public async Task Registration_multiple_locations_for_same_asset_does_not_duplicate_change_set_ids()
    {
        string pathA = Path.Combine(_testDir, "multi_a.pdf");
        string pathB = Path.Combine(_testDir, "multi_b.pdf");
        await File.WriteAllTextAsync(pathA, "Multi Location Content");
        File.Copy(pathA, pathB);

        FileAsset asset = (await _files.RegisterFileAsync(pathA)).Value;
        ItemMetadata item = (await _items.CreateItemAsync(new CreateItemRequest("book", "Multi Location Item"))).Value;
        DocumentInstance doc = (await _documents.AttachDocumentInstanceAsync(
            item.ItemId, asset.FileAssetId, DocumentInstanceType.PrimaryScan)).Value;

        LibraryChangeSet? published = null;
        _revisions.ChangeCommitted += (_, args) => published = args.ChangeSet;

        // Register the copy at pathB (same hash):
        // Observes a new location for the existing asset and triggers IncrementRevisionAsync.
        // The asset now has multiple locations (pathA and pathB).
        Result<FileAsset> registeredCopy = await _files.RegisterFileAsync(pathB);
        registeredCopy.IsSuccess.Should().BeTrue(registeredCopy.ErrorMessage);

        published.Should().NotBeNull();
        published!.ItemIds.Count(id => id == item.ItemId).Should().Be(1);
        published.DocumentInstanceIds.Count(id => id == doc.DocumentInstanceId).Should().Be(1);
    }

    [Fact]
    public async Task Registration_brand_new_file_without_documents_publishes_empty_scope()
    {
        string path = Path.Combine(_testDir, "brand_new.pdf");
        await File.WriteAllTextAsync(path, "Brand new file");

        LibraryChangeSet? published = null;
        _revisions.ChangeCommitted += (_, args) => published = args.ChangeSet;

        Result<FileAsset> result = await _files.RegisterFileAsync(path);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        published.Should().NotBeNull();
        published!.ItemIds.Should().BeEmpty();
        published.DocumentInstanceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_batch_writer_revisions_include_shared_path_owners_and_exclude_unrelated()
    {
        string importPath = Path.Combine(_testDir, "import.pdf");
        string unrelatedPath = Path.Combine(_testDir, "unrelated.pdf");
        await File.WriteAllTextAsync(importPath, "Import PDF Content");
        await File.WriteAllTextAsync(unrelatedPath, "Unrelated Content");

        // 1. Unrelated asset & document
        FileAsset unrelatedAsset = (await _files.RegisterFileAsync(unrelatedPath)).Value;
        ItemMetadata unrelatedItem =
            (await _items.CreateItemAsync(new CreateItemRequest("book", "Unrelated Item"))).Value;
        DocumentInstance unrelatedDoc = (await _documents.AttachDocumentInstanceAsync(
            unrelatedItem.ItemId, unrelatedAsset.FileAssetId, DocumentInstanceType.PrimaryScan)).Value;

        // 2. Pre-insert an existing colliding asset at importPath FIRST
        // Asset must exist in file_assets before AttachDocumentInstanceAsync is called.
        FileAssetId collidingAssetId = FileAssetId.New();
        await using (SqliteConnection connection = _database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into file_assets (
                    file_asset_id, library_id, original_path, file_name, size_bytes,
                    full_blake3, status, created_at, updated_at
                ) values (
                    @FileAssetId, @LibraryId, @Path, 'import.pdf', 100,
                    'fakeblake3collidingimportassetid', 'available', '2026-11-05T00:00:00Z', '2026-11-05T00:00:00Z'
                );
                insert into known_file_locations (location_id, file_asset_id, path, last_seen_at, status)
                values (@LocationId, @FileAssetId, @Path, '2026-11-05T00:00:00Z', 'available');
                """,
                new
                {
                    FileAssetId = collidingAssetId.ToString(),
                    LibraryId = unrelatedAsset.LibraryId.ToString(),
                    Path = Path.GetFullPath(importPath),
                    LocationId = KnownFileLocationId.New().ToString()
                });
        }

        ItemMetadata collidingItem =
            (await _items.CreateItemAsync(new CreateItemRequest("book", "Colliding Item"))).Value;
        DocumentInstance collidingDoc = (await _documents.AttachDocumentInstanceAsync(
            collidingItem.ItemId, collidingAssetId, DocumentInstanceType.PrimaryScan)).Value;

        LibraryChangeSet? published = null;
        _revisions.ChangeCommitted += (_, args) => published = args.ChangeSet;

        FileAsset importAsset = new(
            FileAssetId.New(),
            unrelatedAsset.LibraryId,
            importPath,
            "import.pdf",
            100,
            _clock.UtcNow,
            "quickhash",
            "fullblake3newimportcontenthash00",
            null,
            null,
            FileAssetStatus.Available,
            _clock.UtcNow,
            _clock.UtcNow);

        (ItemMetadata newItem, IReadOnlyList<ItemCreatorInput> creators, IReadOnlyList<ItemDateInput> dates) =
            ItemService.CreateItemMetadata(unrelatedAsset.LibraryId,
                new CreateItemRequest("book", "New Imported Item"), _clock.UtcNow);

        DocumentInstance newDoc = new(
            DocumentInstanceId.New(),
            newItem.ItemId,
            importAsset.FileAssetId,
            "New Imported Doc",
            DocumentInstanceType.PrimaryScan,
            true,
            DocumentInstanceStatus.Active,
            _clock.UtcNow,
            _clock.UtcNow);

        PdfImportBatch batch = new(
            _clock.UtcNow,
            importAsset,
            Path.GetFullPath(importPath),
            newItem,
            creators,
            dates,
            newDoc,
            [],
            []);

        Result<PdfImportCommitResult> commitResult = await _writer.CommitAsync(batch);
        commitResult.IsSuccess.Should().BeTrue(commitResult.ErrorMessage);

        published.Should().NotBeNull();
        // Must contain new item & document
        published!.ItemIds.Should().Contain(newItem.ItemId);
        published.DocumentInstanceIds.Should().Contain(newDoc.DocumentInstanceId);

        // Must contain colliding owner on that path
        published.ItemIds.Should().Contain(collidingItem.ItemId);
        published.DocumentInstanceIds.Should().Contain(collidingDoc.DocumentInstanceId);

        // Must NOT contain unrelated item or document
        published.ItemIds.Should().NotContain(unrelatedItem.ItemId);
        published.DocumentInstanceIds.Should().NotContain(unrelatedDoc.DocumentInstanceId);
    }

    [Fact]
    public async Task Query_plans_prove_no_full_document_scan_and_use_indexed_lookups()
    {
        await using SqliteConnection connection = _database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();

        const string fileAssetQuery =
            """
            explain query plan
            with affected_assets as (
                select @FileAssetId as file_asset_id
                union
                select affected_location.file_asset_id
                from known_file_locations current_location
                join known_file_locations affected_location on affected_location.path = current_location.path
                where current_location.file_asset_id = @FileAssetId
            )
            select distinct di.document_instance_id as DocumentInstanceId, di.item_id as ItemId
            from affected_assets a
            join document_instances di on di.file_asset_id = a.file_asset_id;
            """;

        const string importIncrementQuery =
            """
            explain query plan
            select distinct d.item_id as ItemId, d.document_instance_id as DocumentInstanceId
            from known_file_locations k
            join document_instances d on d.file_asset_id = k.file_asset_id
            where k.path = @Path;
            """;

        const string importUpdateQuery =
            """
            explain query plan
            with affected_assets as (
                select @Id as file_asset_id
                union
                select k.file_asset_id
                from known_file_locations k
                where k.path = @Path
            )
            select distinct d.item_id as ItemId, d.document_instance_id as DocumentInstanceId
            from affected_assets a
            join document_instances d on d.file_asset_id = a.file_asset_id;
            """;

        List<string> fileAssetPlan = (await connection.QueryAsync<PlanRow>(
                fileAssetQuery, new { FileAssetId = FileAssetId.New().ToString() }))
            .Select(r => r.Detail).ToList();
        List<string> importIncrementPlan = (await connection.QueryAsync<PlanRow>(
                importIncrementQuery, new { Path = "C:\\path\\doc.pdf" }))
            .Select(r => r.Detail).ToList();
        List<string> importUpdatePlan = (await connection.QueryAsync<PlanRow>(
                importUpdateQuery, new { Id = FileAssetId.New().ToString(), Path = "C:\\path\\doc.pdf" }))
            .Select(r => r.Detail).ToList();

        // 1. No full table scan on document_instances in any of the plans
        fileAssetPlan.Should().NotContain(line => line.Contains("SCAN di", StringComparison.OrdinalIgnoreCase) ||
                                                  line.Contains("SCAN document_instances",
                                                      StringComparison.OrdinalIgnoreCase));
        importIncrementPlan.Should().NotContain(line => line.Contains("SCAN d", StringComparison.OrdinalIgnoreCase) ||
                                                        line.Contains("SCAN document_instances",
                                                            StringComparison.OrdinalIgnoreCase));
        importUpdatePlan.Should().NotContain(line => line.Contains("SCAN d", StringComparison.OrdinalIgnoreCase) ||
                                                     line.Contains("SCAN document_instances",
                                                         StringComparison.OrdinalIgnoreCase));

        // 2. All queries must search document_instances using the foreign key index
        fileAssetPlan.Should().Contain(line =>
            line.Contains("idx_document_instances_file_asset_id", StringComparison.OrdinalIgnoreCase));
        importIncrementPlan.Should().Contain(line =>
            line.Contains("idx_document_instances_file_asset_id", StringComparison.OrdinalIgnoreCase));
        importUpdatePlan.Should().Contain(line =>
            line.Contains("idx_document_instances_file_asset_id", StringComparison.OrdinalIgnoreCase));

        // 3. Path lookups must use the path-first index on known_file_locations
        fileAssetPlan.Should().Contain(line =>
            line.Contains("idx_known_file_locations_path_file_asset_id", StringComparison.OrdinalIgnoreCase));
        importIncrementPlan.Should().Contain(line =>
            line.Contains("idx_known_file_locations_path_file_asset_id", StringComparison.OrdinalIgnoreCase));
        importUpdatePlan.Should().Contain(line =>
            line.Contains("idx_known_file_locations_path_file_asset_id", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class PlanRow
    {
        public string Detail { get; init; } = string.Empty;
    }
}
