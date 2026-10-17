using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: migrations 048 (file search root sort order) and 049 (file import
/// recovery records) apply cleanly on a fresh database.
/// </summary>
public sealed class FileImportMigrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"patchouli-migration-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(
            Path.GetDirectoryName(_databasePath));
    }

    private static async Task<SqliteConnection> MigrateAsync(string databasePath)
    {
        var factory = new SqliteConnectionFactory(databasePath);
        await new MigrationRunner(factory,
            Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task Migrations_048_and_049_apply_on_fresh_database()
    {
        await using SqliteConnection connection = await MigrateAsync(_databasePath);

        IReadOnlyList<string> applied = (await connection.QueryAsync<string>(
            "select id from schema_migrations;")).ToArray();
        applied.Should().Contain("048");
        applied.Should().Contain("049");
    }

    [Fact]
    public async Task Migration_048_adds_sort_order_to_search_root_definitions()
    {
        await using SqliteConnection connection = await MigrateAsync(_databasePath);

        IReadOnlyList<string> columns = (await connection.QueryAsync<string>(
            "select name from pragma_table_info('file_search_root_definitions');")).ToArray();

        columns.Should().Contain("sort_order");
    }

    [Fact]
    public async Task Migration_049_creates_recovery_records_table_with_status_check()
    {
        await using SqliteConnection connection = await MigrateAsync(_databasePath);

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
}
