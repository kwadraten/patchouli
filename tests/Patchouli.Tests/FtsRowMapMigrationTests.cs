using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class FtsRowMapMigrationTests
{
    [Fact]
    public async Task Legacy_backfill_preserves_rowids_and_removes_duplicate_or_malformed_cache_rows()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
                                      drop table fts_row_map;
                                      drop table fts_cache_state;
                                      insert into search_units_fts(rowid, unit_id, document_instance_id, page_id, resolved_text)
                                      values (17, 'unit-a', 'doc-a', 'page-a', 'kept'),
                                             (91, 'unit-a', 'doc-a', 'page-a', 'duplicate'),
                                             (105, 'unit-b', 'doc-b', 'page-b', 'other'),
                                             (200, '', 'doc-a', 'page-a', 'malformed');
                                      """);

        string migration = await File.ReadAllTextAsync(Path.Combine(
            TestPaths.MigrationsDirectory, "043_create_fts_row_map.sql"));
        await connection.ExecuteAsync(migration);

        long[] rowIds = (await connection.QueryAsync<long>(
            "select fts_row_id from fts_row_map order by fts_row_id;")).ToArray();
        rowIds.Should().Equal(17, 105);
        (await connection.ExecuteScalarAsync<string>(
            "select resolved_text from search_units_fts where rowid = 17;")).Should().Be("kept");
        (await connection.ExecuteScalarAsync<int>("select count(*) from search_units_fts;")).Should().Be(2);
        (await connection.ExecuteScalarAsync<int>("select cache_version from fts_cache_state;")).Should().Be(1);
    }
}
