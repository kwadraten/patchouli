using System.Diagnostics;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class MigrationRunnerForeignKeyTests
{
    [Fact]
    public async Task RunAsync_disables_foreign_keys_outside_transaction_so_rebuild_preserves_children()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        using TemporaryMigrationDirectory migrations = TemporaryMigrationDirectory.Create();

        migrations.Write("001_setup.sql",
            """
            create table parent (id text primary key not null);
            create table child (
                id integer primary key not null,
                parent_id text not null references parent(id) on delete cascade
            );
            insert into parent (id) values ('a'), ('b');
            insert into child (parent_id) values ('a'), ('a'), ('b');

            create table if not exists library_metadata (
                library_id text primary key not null,
                display_name text not null,
                schema_version integer not null,
                created_at text not null,
                updated_at text not null
            );
            insert into library_metadata (library_id, display_name, schema_version, created_at, updated_at)
            values ('lib-1', 'Test', 2, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """);

        migrations.Write("002_rebuild.sql",
            """
            pragma foreign_keys = off;

            create table parent_new (id text primary key not null);
            insert into parent_new (id) select id from parent;
            drop table parent;
            alter table parent_new rename to parent;

            pragma foreign_keys = on;
            """);

        MigrationRunner runner = new(database.ConnectionFactory, migrations.Path);
        IReadOnlyList<AppliedMigration> applied = await runner.RunAsync();

        applied.Should().HaveCount(2);

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        int childCount = await connection.ExecuteScalarAsync<int>("select count(1) from child;");
        childCount.Should().Be(3);
    }

    [Fact]
    public async Task Real_migrations_after_032_complete_without_cascading_data_loss()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        using TemporaryMigrationDirectory partialMigrations = TemporaryMigrationDirectory.Create();

        foreach (string file in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql"))
        {
            string fileName = Path.GetFileName(file);
            string id = fileName[..fileName.IndexOf('_')];
            if (string.Compare(id, "032", StringComparison.Ordinal) <= 0)
            {
                File.Copy(file, Path.Combine(partialMigrations.Path, fileName));
            }
        }

        await new MigrationRunner(database.ConnectionFactory, partialMigrations.Path).RunAsync();

        string now = DateTimeOffset.UtcNow.ToString("O");
        string libraryId = "lib-test";
        string itemId = "item-test";
        string documentInstanceId = "doc-instance-test";
        string pageId = "page-test";
        string currentRevisionId = "rev-current";
        string nonCurrentRevisionId = "rev-noncurrent";
        string currentBoxId = "box-current";
        string nonCurrentBoxId = "box-noncurrent";
        string unit1Id = "unit-1";
        string unit2Id = "unit-2";

        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into library_metadata (library_id, display_name, schema_version, created_at, updated_at)
                values (@LibraryId, 'Test Library', 2, @Now, @Now);

                insert into items (item_id, library_id, item_type, title, created_at, updated_at)
                values (@ItemId, @LibraryId, 'book', 'Test Item', @Now, @Now);

                insert into document_instances (
                    document_instance_id, item_id, instance_type, is_primary, status, created_at, updated_at)
                values (@DocumentInstanceId, @ItemId, 'primary', 1, 'active', @Now, @Now);

                insert into pages (
                    page_id, document_instance_id, page_index, rotation, coordinate_basis,
                    renderer_basis_version, created_at, updated_at)
                values (@PageId, @DocumentInstanceId, 0, 0, 'NormalizedPage', 'test', @Now, @Now);

                insert into document_tree_revisions (
                    tree_revision_id, document_instance_id, page_id, source, status, is_current,
                    source_basis_status, created_at, committed_at)
                values (@CurrentRevisionId, @DocumentInstanceId, @PageId, 'import', 'committed', 1,
                        'current', @Now, @Now);

                insert into document_tree_revisions (
                    tree_revision_id, document_instance_id, page_id, source, status, is_current,
                    source_basis_status, created_at, committed_at)
                values (@NonCurrentRevisionId, @DocumentInstanceId, @PageId, 'import', 'discarded', 0,
                        'current', @Now, null);

                insert into document_boxes (
                    tree_revision_id, box_id, document_instance_id, page_id, box_type,
                    bbox_x, bbox_y, bbox_width, bbox_height)
                values (@CurrentRevisionId, @CurrentBoxId, @DocumentInstanceId, @PageId, 'text',
                        0.1, 0.1, 0.1, 0.1);

                insert into document_boxes (
                    tree_revision_id, box_id, document_instance_id, page_id, box_type,
                    bbox_x, bbox_y, bbox_width, bbox_height)
                values (@NonCurrentRevisionId, @NonCurrentBoxId, @DocumentInstanceId, @PageId, 'text',
                        0.2, 0.2, 0.1, 0.1);

                insert into search_units (
                    unit_id, document_instance_id, page_id, box_id, tree_revision_id,
                    resolved_text, bbox_json, box_type, ordinal, status, created_at, updated_at)
                values (@Unit1Id, @DocumentInstanceId, @PageId, @CurrentBoxId, @CurrentRevisionId,
                        'hello', '{}', 'text', 1, 'current', @Now, @Now);

                insert into search_units (
                    unit_id, document_instance_id, page_id, box_id, tree_revision_id,
                    resolved_text, bbox_json, box_type, ordinal, status, created_at, updated_at)
                values (@Unit2Id, @DocumentInstanceId, @PageId, @NonCurrentBoxId, @NonCurrentRevisionId,
                        'world', '{}', 'text', 2, 'current', @Now, @Now);
                """,
                new
                {
                    LibraryId = libraryId,
                    ItemId = itemId,
                    DocumentInstanceId = documentInstanceId,
                    PageId = pageId,
                    CurrentRevisionId = currentRevisionId,
                    NonCurrentRevisionId = nonCurrentRevisionId,
                    CurrentBoxId = currentBoxId,
                    NonCurrentBoxId = nonCurrentBoxId,
                    Unit1Id = unit1Id,
                    Unit2Id = unit2Id,
                    Now = now
                });
        }

        int boxesBefore;
        int unitsBefore;
        int revisionsBefore;
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            boxesBefore = await connection.ExecuteScalarAsync<int>("select count(1) from document_boxes;");
            unitsBefore = await connection.ExecuteScalarAsync<int>("select count(1) from search_units;");
            revisionsBefore = await connection.ExecuteScalarAsync<int>("select count(1) from document_tree_revisions;");
        }

        MigrationRunner runner = new(database.ConnectionFactory, TestPaths.MigrationsDirectory);
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<AppliedMigration> applied = await runner.RunAsync();
        stopwatch.Stop();

        applied.Should().Contain(m => m.Id == "033");
        applied.Should().Contain(m => m.Id == "033b");
        applied.Should().Contain(m => m.Id == "034");
        applied.Should().Contain(m => m.Id == "035");
        applied.Should().Contain(m => m.Id == "036");
        applied.Should().Contain(m => m.Id == "037");

        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            string[] appliedIds = (await connection.QueryAsync<string>("select id from schema_migrations;")).ToArray();
            appliedIds.Should().Contain(["033", "033b", "034", "035", "036", "037"]);

            int boxesAfter = await connection.ExecuteScalarAsync<int>("select count(1) from document_boxes;");
            int unitsAfter = await connection.ExecuteScalarAsync<int>("select count(1) from search_units;");
            int revisionsAfter =
                await connection.ExecuteScalarAsync<int>("select count(1) from document_tree_revisions;");

            boxesAfter.Should().Be(boxesBefore);
            unitsAfter.Should().Be(unitsBefore);
            revisionsAfter.Should().Be(revisionsBefore);
        }

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }
}
