using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class V3T7SchemaMigrationsTests
{
    [Fact]
    public async Task Library_metadata_has_a_persistent_revision_column_defaulting_to_one()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string[] columns = (await connection.QueryAsync<string>(
            "select name from pragma_table_info('library_metadata');")).ToArray();

        columns.Should().Contain("library_revision");

        long defaultValue = await connection.ExecuteScalarAsync<long>(
            """
            select dflt_value
            from pragma_table_info('library_metadata')
            where name = 'library_revision';
            """);
        defaultValue.Should().Be(1);
    }

    [Fact]
    public async Task First_screen_composite_indexes_are_created()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string[] indexes = (await connection.QueryAsync<string>(
            """
            select name
            from sqlite_master
            where type = 'index'
              and name in ('idx_items_active_created', 'idx_document_instances_item_primary',
                           'idx_ocr_runs_document_hidden_created', 'idx_ocr_page_results_run_created',
                           'idx_search_units_document_status');
            """)).ToArray();

        indexes.Should().Contain(new[]
        {
            "idx_items_active_created",
            "idx_document_instances_item_primary",
            "idx_ocr_runs_document_hidden_created",
            "idx_ocr_page_results_run_created",
            "idx_search_units_document_status"
        });
    }
}
