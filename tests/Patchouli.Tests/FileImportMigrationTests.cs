using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: migrations 050 (file search root sort order) and 051 (file import
/// recovery records) apply cleanly on a fresh database.
/// </summary>
public sealed class FileImportMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-migration-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    private string DatabasePath => Path.Combine(_root, "test.sqlite");

    private static async Task<SqliteConnection> MigrateAsync(string databasePath)
    {
        SqliteConnectionFactory factory = new(databasePath);
        await new MigrationRunner(factory,
            Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
        SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task Migrations_050_and_051_apply_on_fresh_database()
    {
        await using SqliteConnection connection = await MigrateAsync(DatabasePath);

        IReadOnlyList<string> applied = (await connection.QueryAsync<string>(
            "select id from schema_migrations;")).ToArray();
        applied.Should().Contain("050");
        applied.Should().Contain("051");
    }

    [Fact]
    public async Task Migration_050_adds_sort_order_to_search_root_definitions()
    {
        await using SqliteConnection connection = await MigrateAsync(DatabasePath);

        IReadOnlyList<string> columns = (await connection.QueryAsync<string>(
            "select name from pragma_table_info('file_search_root_definitions');")).ToArray();

        columns.Should().Contain("sort_order");
    }

    [Fact]
    public async Task Migration_051_creates_recovery_records_table_with_status_check()
    {
        await using SqliteConnection connection = await MigrateAsync(DatabasePath);

        // A valid status inserts fine.
        await connection.ExecuteAsync(
            """
            insert into file_import_recovery_records
                (record_id, created_at, trigger, kind, original_path, moved_path,
                 restored_path, status, diagnosis)
            values
                ('r1', '2026-10-17T00:00:00Z', 'McpAdd', 'Pdf', '/orig/a.pdf',
                 '/root/a.pdf', '/orig/a.pdf', 'import_failed_restored', 'ok');
            """);

        // An unknown status violates the check constraint.
        // The assertion below awaits the delegate before this connection is disposed.
        // ReSharper disable once AccessToDisposedClosure
        Func<Task> insert = () => connection.ExecuteAsync(
            """
            insert into file_import_recovery_records
                (record_id, created_at, trigger, kind, original_path,
                 restored_path, status, diagnosis)
            values
                ('r2', '2026-10-17T00:00:00Z', 'McpAdd', 'Pdf', '/orig/b.pdf',
                 null, 'bogus_status', 'x');
            """);

        await insert.Should().ThrowAsync<SqliteException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_preserves_main_and_legacy_feature_migration_histories(bool legacyFeature)
    {
        using TemporaryMigrationDirectory baseline = TemporaryMigrationDirectory.Create();
        foreach (string migration in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql"))
        {
            string name = Path.GetFileName(migration);
            if (StringComparer.Ordinal.Compare(name, legacyFeature ? "048_" : "050_") < 0)
            {
                File.Copy(migration, Path.Combine(baseline.Path, name));
            }
        }

        SqliteConnectionFactory factory = new(DatabasePath);
        await new MigrationRunner(factory, baseline.Path).RunAsync();
        await using SqliteConnection connection = factory.CreateConnection();
        await connection.OpenAsync();
        if (legacyFeature)
        {
            foreach ((string Id, string Name, string CurrentId) migration in new[]
                     {
                         (Id: "048", Name: "file_search_root_sort_order", CurrentId: "050"),
                         (Id: "049", Name: "file_import_recovery", CurrentId: "051")
                     })
            {
                string sql = await File.ReadAllTextAsync(Path.Combine(TestPaths.MigrationsDirectory,
                    $"{migration.CurrentId}_{migration.Name}.sql"));
                await connection.ExecuteAsync(sql);
                await connection.ExecuteAsync(
                    "insert into schema_migrations (id, name, applied_at) values (@Id, @Name, @AppliedAt);",
                    new
                    {
                        migration.Id, Name = $"{migration.Id}_{migration.Name}",
                        AppliedAt = DateTimeOffset.UtcNow.ToString("O")
                    });
            }
        }

        MigrationRunner runner = new(factory, TestPaths.MigrationsDirectory);
        await runner.RunAsync();
        AppliedMigration[] migrations = (await connection.QueryAsync<AppliedMigration>(
            "select id, name from schema_migrations where id >= '048' order by id;")).ToArray();
        migrations.Should().Equal(
            new AppliedMigration("048", "048_normalize_document_box_types"),
            new AppliedMigration("049", "049_add_csl_language_locale_mappings"),
            new AppliedMigration("050", "050_file_search_root_sort_order"),
            new AppliedMigration("051", "051_file_import_recovery"),
            new AppliedMigration("052", "052_create_document_page_maps"),
            new AppliedMigration("053", "053_add_page_translation_target_language"),
            new AppliedMigration("054", "054_document_box_continuation_state"));
        (await connection.ExecuteScalarAsync<int>(
                "select count(*) from pragma_table_info('file_search_root_definitions') where name = 'sort_order';"))
            .Should().Be(1);
        (await connection.ExecuteScalarAsync<int>(
                "select count(*) from pragma_table_info('csl_settings') where name = 'language_locale_mappings_json';"))
            .Should().Be(1);
        (await connection.ExecuteScalarAsync<int>(
                "select count(*) from pragma_table_info('document_boxes') where name = 'continuation_state';"))
            .Should().Be(1);
        (await connection.ExecuteScalarAsync<int>(
                "select count(*) from sqlite_master where type = 'table' and name = 'continuation_migration_diagnostics';"))
            .Should().Be(1);
        (await runner.RunAsync()).Should().BeEmpty();
    }
}
