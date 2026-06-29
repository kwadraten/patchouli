using Dapper;
using FluentAssertions;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Files;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Files;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;

namespace LiteratureApp.Tests;

public sealed class BibliographicCoreTests
{
    [Fact]
    public async Task CreateItem_creates_item_under_current_library()
    {
        await using var context = await BibliographicTestContext.CreateAsync();

        var item = await context.ItemService.CreateItemAsync("book", "Local Gazetteer");

        item.IsSuccess.Should().BeTrue();
        item.Value.LibraryId.Should().Be(context.LibraryId);
        item.Value.Title.Should().Be("Local Gazetteer");
        item.Value.CreatorsJson.Should().Be("[]");
        item.Value.CustomFieldsJson.Should().Be("{}");
    }

    [Fact]
    public async Task CreateItem_rejects_blank_title()
    {
        await using var context = await BibliographicTestContext.CreateAsync();

        var result = await context.ItemService.CreateItemAsync("book", " ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task CreateItem_rejects_blank_item_type()
    {
        await using var context = await BibliographicTestContext.CreateAsync();

        var result = await context.ItemService.CreateItemAsync(" ", "Title");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddIdentifier_allows_builtin_scheme()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("article", "Article");

        var identifier = await context.ItemService.AddIdentifierAsync(
            item.Value.ItemId,
            BuiltInIdentifierSchemes.DOI,
            "10.1234/example",
            note: null);

        identifier.IsSuccess.Should().BeTrue();
        identifier.Value.Scheme.Should().Be(BuiltInIdentifierSchemes.DOI);
    }

    [Fact]
    public async Task AddIdentifier_allows_custom_scheme()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("manuscript", "Custom");

        var identifier = await context.ItemService.AddIdentifierAsync(
            item.Value.ItemId,
            "local_catalog",
            "A-001",
            "Imported from local notes");

        identifier.IsSuccess.Should().BeTrue();
        identifier.Value.Scheme.Should().Be("local_catalog");
    }

    [Fact]
    public async Task AddIdentifier_rejects_blank_scheme_or_value()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");

        var blankScheme = await context.ItemService.AddIdentifierAsync(item.Value.ItemId, " ", "A", null);
        var blankValue = await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "archive_id", " ", null);

        blankScheme.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        blankValue.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddIdentifier_rejects_duplicate_scheme_value_for_same_item()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");

        var first = await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "archive_id", "A-1", null);
        var second = await context.ItemService.AddIdentifierAsync(item.Value.ItemId, "archive_id", "A-1", null);

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task RegisterFile_existing_file_sets_available_and_metadata()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var filePath = await TemporaryFile.WriteAsync("historical source text");

        try
        {
            var asset = await context.FileAssetService.RegisterFileAsync(filePath);

            asset.IsSuccess.Should().BeTrue();
            asset.Value.Status.Should().Be(FileAssetStatus.Available);
            asset.Value.FileName.Should().Be(Path.GetFileName(filePath));
            asset.Value.SizeBytes.Should().Be(new FileInfo(filePath).Length);
            asset.Value.MtimeUtc.Should().NotBeNull();
            asset.Value.QuickHash.Should().NotBeNullOrWhiteSpace();
            asset.Value.FullBlake3.Should().BeNull();
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RegisterFile_missing_file_creates_missing_asset()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pdf");

        var asset = await context.FileAssetService.RegisterFileAsync(missingPath);

        asset.IsSuccess.Should().BeTrue();
        asset.Value.Status.Should().Be(FileAssetStatus.Missing);
        asset.Value.SizeBytes.Should().Be(0);
        asset.Value.MtimeUtc.Should().BeNull();
        asset.Value.QuickHash.Should().BeNull();
    }

    [Fact]
    public async Task RegisterFile_does_not_store_file_content_in_database()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var fileContent = $"do-not-store-this-content-{Guid.NewGuid():N}";
        var filePath = await TemporaryFile.WriteAsync(fileContent);

        try
        {
            await context.FileAssetService.RegisterFileAsync(filePath);

            await using var connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            var columns = (await connection.QueryAsync<(string Name, string Type)>(
                "select name as Name, type as Type from pragma_table_info('file_assets');")).ToArray();

            columns.Select(c => c.Name).Should().NotContain(name => name.Contains("content", StringComparison.OrdinalIgnoreCase));
            columns.Select(c => c.Type).Should().NotContain(type => type.Contains("blob", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RegisterFile_uses_current_library_id()
    {
        await using var context = await BibliographicTestContext.CreateAsync();

        var asset = await context.FileAssetService.RegisterFileAsync(Path.Combine(Path.GetTempPath(), "absent.pdf"));

        asset.Value.LibraryId.Should().Be(context.LibraryId);
    }

    [Fact]
    public async Task AttachDocumentInstance_first_instance_becomes_primary()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");

        var instance = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            fileAssetId: null,
            DocumentInstanceType.PrimaryScan);

        instance.IsSuccess.Should().BeTrue();
        instance.Value.IsPrimary.Should().BeTrue();
        instance.Value.Status.Should().Be(DocumentInstanceStatus.Active);
    }

    [Fact]
    public async Task AttachDocumentInstance_second_instance_not_primary_by_default()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);

        var second = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            null,
            DocumentInstanceType.AlternateScan);

        second.Value.IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task AttachDocumentInstance_makePrimary_switches_primary()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");
        var first = await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);

        var second = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            null,
            DocumentInstanceType.AlternateScan,
            makePrimary: true);

        var instances = await context.DocumentInstanceService.ListDocumentInstancesForItemAsync(item.Value.ItemId);

        second.Value.IsPrimary.Should().BeTrue();
        instances.Value.Single(instance => instance.DocumentInstanceId == first.Value.DocumentInstanceId).IsPrimary.Should().BeFalse();
        instances.Value.Single(instance => instance.DocumentInstanceId == second.Value.DocumentInstanceId).IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task SetPrimaryDocumentInstance_keeps_only_one_primary_per_item()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");
        var first = await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.Supplement);

        var result = await context.DocumentInstanceService.SetPrimaryDocumentInstanceAsync(
            item.Value.ItemId,
            first.Value.DocumentInstanceId);
        var instances = await context.DocumentInstanceService.ListDocumentInstancesForItemAsync(item.Value.ItemId);

        result.IsSuccess.Should().BeTrue();
        instances.Value.Count(instance => instance.IsPrimary).Should().Be(1);
        instances.Value.Single(instance => instance.IsPrimary).DocumentInstanceId.Should().Be(first.Value.DocumentInstanceId);
    }

    [Fact]
    public async Task AttachDocumentInstance_rejects_missing_item()
    {
        await using var context = await BibliographicTestContext.CreateAsync();

        var result = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            ItemId.New(),
            null,
            DocumentInstanceType.PrimaryScan);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task AttachDocumentInstance_rejects_file_asset_from_different_library()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");
        var foreignLibraryId = LibraryId.New();
        var foreignFileAssetId = FileAssetId.New();

        await using (var connection = context.Database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("pragma foreign_keys = off;");
            await connection.ExecuteAsync(
                """
                insert into file_assets (
                    file_asset_id, library_id, original_path, file_name, size_bytes,
                    status, created_at, updated_at
                )
                values (@FileAssetId, @LibraryId, '/tmp/foreign.pdf', 'foreign.pdf', 0,
                    'missing', @Now, @Now);
                """,
                new
                {
                    FileAssetId = foreignFileAssetId.ToString(),
                    LibraryId = foreignLibraryId.ToString(),
                    Now = DateTimeOffset.UnixEpoch.ToString("O")
                });
            await connection.ExecuteAsync("pragma foreign_keys = on;");
        }

        var result = await context.DocumentInstanceService.AttachDocumentInstanceAsync(
            item.Value.ItemId,
            foreignFileAssetId,
            DocumentInstanceType.PrimaryScan);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.LibraryMismatch);
    }

    [Fact]
    public async Task ListDocumentInstancesForItem_returns_instances()
    {
        await using var context = await BibliographicTestContext.CreateAsync();
        var item = await context.ItemService.CreateItemAsync("book", "Book");
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
        await context.DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.Supplement);

        var instances = await context.DocumentInstanceService.ListDocumentInstancesForItemAsync(item.Value.ItemId);

        instances.IsSuccess.Should().BeTrue();
        instances.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task MigrationRunner_applies_bibliographic_core_migration()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);

        await runner.RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var tableCount = await connection.ExecuteScalarAsync<int>(
            """
            select count(1)
            from sqlite_master
            where type = 'table'
              and name in ('items', 'item_identifiers', 'file_assets', 'document_instances');
            """);

        tableCount.Should().Be(4);
    }

    [Fact]
    public async Task Foreign_keys_prevent_orphan_identifier_if_supported()
    {
        await using var context = await BibliographicTestContext.CreateAsync();

        await using var connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var foreignKeysEnabled = await connection.ExecuteScalarAsync<int>("pragma foreign_keys;");
        Func<Task> action = () => connection.ExecuteAsync(
            """
            insert into item_identifiers (identifier_id, item_id, scheme, value, created_at)
            values (@IdentifierId, @ItemId, 'archive_id', 'orphan', @CreatedAt);
            """,
            new
            {
                IdentifierId = IdentifierId.New().ToString(),
                ItemId = ItemId.New().ToString(),
                CreatedAt = DateTimeOffset.UnixEpoch.ToString("O")
            });

        foreignKeysEnabled.Should().Be(1);
        await action.Should().ThrowAsync<SqliteException>();
    }

    private sealed class BibliographicTestContext : IAsyncDisposable
    {
        private BibliographicTestContext(
            TemporarySqliteDatabase database,
            LibraryId libraryId,
            ItemService itemService,
            FileAssetService fileAssetService,
            DocumentInstanceService documentInstanceService)
        {
            Database = database;
            LibraryId = libraryId;
            ItemService = itemService;
            FileAssetService = fileAssetService;
            DocumentInstanceService = documentInstanceService;
        }

        public TemporarySqliteDatabase Database { get; }
        public LibraryId LibraryId { get; }
        public ItemService ItemService { get; }
        public FileAssetService FileAssetService { get; }
        public DocumentInstanceService DocumentInstanceService { get; }

        public static async Task<BibliographicTestContext> CreateAsync()
        {
            var database = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(new DateTimeOffset(2026, 6, 19, 2, 0, 0, TimeSpan.Zero));
            var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);
            await runner.RunAsync();

            var libraryService = new LibraryIdentityService(database.ConnectionFactory, clock);
            var library = await libraryService.CreateLibraryAsync("Test library");
            var itemService = new ItemService(database.ConnectionFactory, libraryService, clock);
            var fileAssetService = new FileAssetService(database.ConnectionFactory, libraryService, clock);
            var documentInstanceService = new DocumentInstanceService(database.ConnectionFactory, clock);

            return new BibliographicTestContext(
                database,
                library.Value.LibraryId,
                itemService,
                fileAssetService,
                documentInstanceService);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private static class TemporaryFile
    {
        public static async Task<string> WriteAsync(string content)
        {
            var path = Path.Combine(Path.GetTempPath(), $"literatureapp-file-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, content);
            return path;
        }
    }
}
