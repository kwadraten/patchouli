using System.Globalization;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class RetiredTablesMigrationTests
{
    [Fact]
    public async Task Fresh_schema_excludes_retired_tables()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        int retiredCount = await connection.ExecuteScalarAsync<int>(
            """
            select count(1) from sqlite_master
            where type = 'table' and name in ('library_preferences', 'file_search_roots');
            """);

        retiredCount.Should().Be(0);

        int activeRootTableCount = await connection.ExecuteScalarAsync<int>(
            """
            select count(1) from sqlite_master
            where type = 'table'
              and name in ('file_search_root_definitions', 'file_search_root_bindings');
            """);

        activeRootTableCount.Should().Be(2);
    }

    [Fact]
    public async Task Upgrading_pre_027_library_preserves_active_root_data_and_drops_old_table()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        using TemporaryMigrationDirectory pre027Directory = TemporaryMigrationDirectory.Create();
        foreach (string migration in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql",
                     SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(migration);
            if (StringComparer.Ordinal.Compare(fileName, "027_") < 0)
            {
                File.Copy(migration, Path.Combine(pre027Directory.Path, fileName));
            }
        }

        await new MigrationRunner(database.ConnectionFactory, pre027Directory.Path).RunAsync();

        string libraryId = Guid.NewGuid().ToString("D");
        string rootId = Guid.NewGuid().ToString("D");
        string itemId = Guid.NewGuid().ToString("D");
        string rootPath = Path.Combine(Path.GetTempPath(), $"patchouli-root-{Guid.NewGuid():N}");
        string now = DateTimeOffset.Parse("2026-06-20T00:00:00Z", CultureInfo.InvariantCulture).ToString("O");
        byte[] authorizationPayload = [1, 2, 3, 4];
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into library_metadata (library_id, display_name, schema_version, created_at, updated_at)
                values (@LibraryId, 'Pre 027 Library', @SchemaVersion, @Now, @Now);
                insert into items (item_id, library_id, item_type, title, created_at, updated_at)
                values (@ItemId, @LibraryId, 'book', 'Preserved Item', @Now, @Now);
                insert into file_search_roots
                    (root_id, library_id, root_path, is_available, created_at, updated_at,
                     authorization_kind, authorization_payload, authorization_payload_version, authorization_updated_at)
                values (@RootId, @LibraryId, @RootPath, 1, @Now, @Now, 'bookmark', @Payload, 1, @Now);
                """,
                new
                {
                    LibraryId = libraryId, ItemId = itemId, RootId = rootId, RootPath = rootPath, Now = now,
                    SchemaVersion = AppSchemaVersion.Current, Payload = authorizationPayload
                });
        }

        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using SqliteConnection final = database.ConnectionFactory.CreateConnection();
        await final.OpenAsync();

        (await final.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where type = 'table' and name = 'file_search_roots';"))
            .Should().Be(0);
        (await final.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where type = 'table' and name = 'library_preferences';"))
            .Should().Be(0);

        int definitionCount = await final.ExecuteScalarAsync<int>(
            "select count(1) from file_search_root_definitions where root_id = @RootId and library_id = @LibraryId;",
            new { RootId = rootId, LibraryId = libraryId });
        definitionCount.Should().Be(1);

        string? storedPath = await final.ExecuteScalarAsync<string>(
            "select root_path from file_search_root_bindings where root_id = @RootId;", new { RootId = rootId });
        storedPath.Should().Be(rootPath);

        int storedAvailability = await final.ExecuteScalarAsync<int>(
            "select is_available from file_search_root_bindings where root_id = @RootId;", new { RootId = rootId });
        storedAvailability.Should().Be(1);

        string? storedDeviceId = await final.ExecuteScalarAsync<string>(
            "select device_id from file_search_root_bindings where root_id = @RootId;", new { RootId = rootId });
        storedDeviceId.Should().Be("legacy-device");

        string? storedProviderIdentity = await final.ExecuteScalarAsync<string>(
            "select provider_identity from file_search_root_bindings where root_id = @RootId;",
            new { RootId = rootId });
        storedProviderIdentity.Should().Be("sqlite_migration");

        string? storedAuthorizationKind = await final.ExecuteScalarAsync<string>(
            "select authorization_kind from file_search_root_bindings where root_id = @RootId;",
            new { RootId = rootId });
        storedAuthorizationKind.Should().Be("bookmark");

        byte[]? storedPayload = await final.ExecuteScalarAsync<byte[]>(
            "select authorization_payload from file_search_root_bindings where root_id = @RootId;",
            new { RootId = rootId });
        storedPayload.Should().BeEquivalentTo(authorizationPayload);

        int? storedPayloadVersion = await final.ExecuteScalarAsync<int?>(
            "select authorization_payload_version from file_search_root_bindings where root_id = @RootId;",
            new { RootId = rootId });
        storedPayloadVersion.Should().Be(1);

        string? storedAuthorizationUpdatedAt = await final.ExecuteScalarAsync<string>(
            "select authorization_updated_at from file_search_root_bindings where root_id = @RootId;",
            new { RootId = rootId });
        storedAuthorizationUpdatedAt.Should().Be(now);

        string? storedItemTitle = await final.ExecuteScalarAsync<string>(
            "select title from items where item_id = @ItemId;", new { ItemId = itemId });
        storedItemTitle.Should().Be("Preserved Item");

        string? storedLibrary = await final.ExecuteScalarAsync<string>(
            "select library_id from library_metadata limit 1;");
        storedLibrary.Should().Be(libraryId);

        IReadOnlyList<AppliedMigration> rerun =
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        rerun.Should().BeEmpty();
    }
}
