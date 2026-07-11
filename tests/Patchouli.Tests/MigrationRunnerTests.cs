using Dapper;
using FluentAssertions;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class MigrationRunnerTests
{
    [Fact]
    public async Task RunAsync_creates_schema_migrations_table()
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
            where type = 'table' and name = 'schema_migrations';
            """);

        tableCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_is_idempotent_for_already_applied_migrations()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);
        var expectedMigrationCount = Directory
            .EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .Count();

        var firstRun = await runner.RunAsync();
        var secondRun = await runner.RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var appliedCount = await connection.ExecuteScalarAsync<int>("select count(1) from schema_migrations;");

        firstRun.Should().Contain(m => m.Id == "0001" && m.Name == "0001_create_schema_migrations");
        firstRun.Should().Contain(m => m.Id == "002" && m.Name == "002_create_library_metadata");
        firstRun.Should().Contain(m => m.Id == "003" && m.Name == "003_create_bibliographic_core");
        firstRun.Should().Contain(m => m.Id == "004" && m.Name == "004_create_file_resolution");
        firstRun.Should().Contain(m => m.Id == "005" && m.Name == "005_create_pages_and_layout");
        firstRun.Should().Contain(m => m.Id == "006" && m.Name == "006_create_ocr_lifecycle");
        firstRun.Should().Contain(m => m.Id == "007" && m.Name == "007_create_search_units_and_fts");
        firstRun.Should().Contain(m => m.Id == "008" && m.Name == "008_create_evidence_refs");
        firstRun.Should().Contain(m => m.Id == "009" && m.Name == "009_create_provider_credentials");
        firstRun.Should().HaveCount(expectedMigrationCount);
        secondRun.Should().BeEmpty();
        appliedCount.Should().Be(expectedMigrationCount);
    }

    [Fact]
    public async Task Identifier_scheme_migration_enforces_lowercase_for_direct_writes()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);
        await runner.RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var libraryId = Guid.NewGuid().ToString();
        var itemId = Guid.NewGuid().ToString();
        await connection.ExecuteAsync(
            """
            insert into library_metadata(library_id, display_name, created_at, updated_at, schema_version)
            values (@LibraryId, 'Test', @Now, @Now, 1);
            insert into items(item_id, library_id, item_type, title, creators_json, tags_json, collections_json, custom_fields_json, created_at, updated_at)
            values (@ItemId, @LibraryId, 'book', 'Test', '[]', '[]', '[]', '{}', @Now, @Now);
            insert into item_identifiers(identifier_id, item_id, scheme, value, created_at)
            values (@IdentifierId, @ItemId, 'NDLBibID', '123456', @Now);
            """,
            new { LibraryId = libraryId, ItemId = itemId, IdentifierId = Guid.NewGuid().ToString(), Now = now });

        var scheme = await connection.ExecuteScalarAsync<string>("select scheme from item_identifiers where item_id = @ItemId;", new { ItemId = itemId });

        scheme.Should().Be("ndlbibid");
    }

    [Fact]
    public async Task File_search_root_authorization_migration_adds_device_local_columns()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var columns = (await connection.QueryAsync<string>(
            "select name from pragma_table_info('file_search_roots');")).ToArray();

        columns.Should().Contain([
            "authorization_kind",
            "authorization_payload",
            "authorization_payload_version",
            "authorization_updated_at"]);
    }
}
