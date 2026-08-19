using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class DocumentTreeSchemaTests
{
    [Fact]
    public async Task Fresh_schema_contains_only_the_0_2_document_tree_model()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();

        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string[] tables = (await connection.QueryAsync<string>(
            "select name from sqlite_master where type = 'table';")).ToArray();
        string[] columns = (await connection.QueryAsync<string>(
            "select name from pragma_table_info('document_boxes');")).ToArray();

        AppSchemaVersion.Current.Should().Be(2);
        tables.Should().Contain(["document_tree_revisions", "document_boxes"]);
        tables.Should().NotContain(["layout_revisions", "layout_nodes"]);
        columns.Should().Contain([
            "parent_box_id", "next_sibling_box_id", "payload_json", "suppressed", "continues_from_box_id"
        ]);
        columns.Should().NotContain(["reading_order", "text_policy", "row_index", "col_index"]);
    }

    [Fact]
    public async Task Legacy_library_is_rejected_without_migration_or_deletion()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                create table library_metadata (
                    library_id text primary key,
                    display_name text not null,
                    schema_version integer not null,
                    created_at text not null,
                    updated_at text not null
                );
                insert into library_metadata values ('legacy', 'Legacy', 1, 'now', 'now');
                create table layout_nodes (node_id text primary key);
                """);
        }

        MigrationRunner runner = new(database.ConnectionFactory, TestPaths.MigrationsDirectory);

        UnsupportedLibrarySchemaException error =
            await Assert.ThrowsAsync<UnsupportedLibrarySchemaException>(() => runner.RunAsync());

        error.Message.Should().Contain("not supported by Patchouli 0.3.1");
        await using SqliteConnection verify = database.ConnectionFactory.CreateConnection();
        await verify.OpenAsync();
        int rows = await verify.ExecuteScalarAsync<int>("select count(1) from layout_nodes;");
        rows.Should().Be(0);
    }
}
