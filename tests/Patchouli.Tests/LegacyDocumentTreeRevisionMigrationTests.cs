using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class LegacyDocumentTreeRevisionMigrationTests
{
    [Fact]
    public async Task Upgrading_removes_inaccessible_candidates_without_losing_committed_history_or_ocr_audit()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        using TemporaryMigrationDirectory previousMigrations = TemporaryMigrationDirectory.Create();
        foreach (string file in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql"))
        {
            if (string.Compare(Path.GetFileName(file), "060_", StringComparison.Ordinal) < 0)
            {
                File.Copy(file, Path.Combine(previousMigrations.Path, Path.GetFileName(file)));
            }
        }

        await new MigrationRunner(database.ConnectionFactory, previousMigrations.Path).RunAsync();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into library_metadata(library_id, display_name, schema_version, created_at, updated_at)
                values ('library', 'Library', @SchemaVersion, @Now, @Now);
                insert into items(item_id, library_id, item_type, title, created_at, updated_at)
                values ('item', 'library', 'book', 'Book', @Now, @Now);
                insert into document_instances(document_instance_id, item_id, instance_type, status, created_at, updated_at)
                values ('document', 'item', 'pdf', 'active', @Now, @Now);
                insert into pages(page_id, document_instance_id, page_index, coordinate_basis, renderer_basis_version, created_at, updated_at)
                values ('page', 'document', 0, 'NormalizedPage', 'test', @Now, @Now);
                insert into document_tree_revisions(tree_revision_id, document_instance_id, page_id, source, status, is_current, created_at, committed_at)
                values
                    ('old', 'document', 'page', 'import', 'committed', 0, @Now, @Now),
                    ('current', 'document', 'page', 'ocr_adopted', 'committed', 1, @Now, @Now),
                    ('discarded', 'document', 'page', 'import', 'discarded', 0, @Now, null),
                    ('staging', 'document', 'page', 'import', 'staging', 0, @Now, null),
                    ('draft', 'document', 'page', 'manual_edit', 'draft', 0, @Now, null),
                    ('working', 'document', 'page', 'import', 'working', 0, @Now, null);
                update document_tree_revisions set parent_tree_revision_id = 'old'
                where tree_revision_id = 'current';
                update document_tree_revisions set parent_tree_revision_id = 'discarded'
                where tree_revision_id = 'working';
                insert into document_boxes(tree_revision_id, box_id, document_instance_id, page_id, box_type,
                    bbox_x, bbox_y, bbox_width, bbox_height, payload_json)
                values
                    ('old', 'old-box', 'document', 'page', 'text', 0.1, 0.1, 0.1, 0.1, '{"markdown":"old"}'),
                    ('current', 'current-box', 'document', 'page', 'text', 0.1, 0.1, 0.1, 0.1, '{"markdown":"current"}'),
                    ('discarded', 'discarded-box', 'document', 'page', 'text', 0.1, 0.1, 0.1, 0.1, '{"markdown":"discarded"}'),
                    ('staging', 'staging-box', 'document', 'page', 'text', 0.1, 0.1, 0.1, 0.1, '{"markdown":"staging"}'),
                    ('draft', 'draft-box', 'document', 'page', 'text', 0.1, 0.1, 0.1, 0.1, '{"markdown":"draft"}'),
                    ('working', 'working-box', 'document', 'page', 'text', 0.1, 0.1, 0.1, 0.1, '{"markdown":"working"}');
                insert into document_commits(commit_id, document_instance_id, source, created_at)
                values ('commit', 'document', 'ocr_adopted', @Now),
                    ('legacy-commit', 'document', 'import', @Now);
                insert into document_commit_pages(commit_id, page_id, tree_revision_id)
                values ('commit', 'page', 'current'), ('legacy-commit', 'page', 'discarded');
                insert into ocr_presets(preset_id, library_id, name, created_at, updated_at)
                values ('preset', 'library', 'Preset', @Now, @Now);
                insert into ocr_preset_versions(preset_version_id, preset_id, engine_id, model_id, parameters_json,
                    apply_on_success, created_at)
                values ('version', 'preset', 'engine', 'model', '{}', 1, @Now);
                insert into ocr_runs(ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id,
                    model_id, parameters_snapshot_json, source_tree_revision_id, output_tree_revision_id,
                    state, created_at, updated_at)
                values
                    ('adopted-run', 'document', 'preset', 'version', 'engine', 'model', '{}', 'old', 'discarded',
                     'completed', @Now, @Now),
                    ('unadopted-run', 'document', 'preset', 'version', 'engine', 'model', '{}', 'staging', 'staging',
                     'completed', @Now, @Now);
                insert into ocr_page_results(result_id, ocr_run_id, page_id, state, working_tree_revision_id,
                    created_at, updated_at)
                values
                    ('adopted-result', 'adopted-run', 'page', 'succeeded', 'discarded', @Now, @Now),
                    ('unadopted-result', 'unadopted-run', 'page', 'succeeded', 'staging', @Now, @Now);
                insert into ocr_candidate_adoptions(adoption_id, ocr_run_id, document_instance_id,
                    adopted_tree_revisions_json, adopted_pages_json, created_at)
                values ('adoption', 'adopted-run', 'document', '["current"]', '["page"]', @Now);
                insert into continuation_migration_diagnostics(migration_id, box_id, tree_revision_id,
                    action, detail, created_at)
                values
                    ('054', 'discarded-box', 'discarded', 'kept_explicit', 'legacy', @Now),
                    ('054', 'current-box', 'current', 'kept_explicit', 'retained', @Now);
                insert into page_translations(page_id, source_tree_revision_id, updated_at)
                values ('page', 'staging', @Now);
                insert into search_units(unit_id, document_instance_id, page_id, box_id, tree_revision_id,
                    resolved_text, bbox_json, box_type, ordinal, status, created_at, updated_at)
                values
                    ('legacy-unit', 'document', 'page', 'discarded-box', 'discarded', 'discarded', '{}',
                     'text', 0, 'stale', @Now, @Now),
                    ('current-unit', 'document', 'page', 'current-box', 'current', 'current', '{}',
                     'text', 1, 'current', @Now, @Now);
                update search_units set supersedes_unit_id = 'legacy-unit' where unit_id = 'current-unit';
                insert into fts_row_map(fts_row_id, document_instance_id, unit_id)
                values (1, 'document', 'legacy-unit'), (2, 'document', 'current-unit');
                insert into search_units_fts(rowid, unit_id, document_instance_id, page_id, resolved_text)
                values
                    (1, 'legacy-unit', 'document', 'page', 'discarded'),
                    (2, 'current-unit', 'document', 'page', 'current');
                """,
                new { SchemaVersion = AppSchemaVersion.Current, Now = DateTimeOffset.UtcNow.ToString("O") });
        }

        IReadOnlyList<AppliedMigration> applied =
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        applied.Should().ContainSingle().Which.Id.Should().Be("060");

        await using SqliteConnection upgraded = database.ConnectionFactory.CreateConnection();
        await upgraded.OpenAsync();
        (await upgraded.ExecuteScalarAsync<int>(
                "select count(*) from document_tree_revisions where status in ('discarded', 'staging', 'draft');"))
            .Should().Be(0);
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from document_boxes;")).Should().Be(3);
        (await upgraded.QueryAsync<string>(
                "select tree_revision_id from document_tree_revisions order by tree_revision_id;"))
            .Should().Equal("current", "old", "working");
        (await upgraded.ExecuteScalarAsync<string>(
                "select parent_tree_revision_id from document_tree_revisions where tree_revision_id = 'current';"))
            .Should().Be("old");
        (await upgraded.ExecuteScalarAsync<string?>(
                "select parent_tree_revision_id from document_tree_revisions where tree_revision_id = 'working';"))
            .Should().BeNull();
        (await upgraded.QueryAsync<string?>(
                "select working_tree_revision_id from ocr_page_results order by result_id;"))
            .Should().OnlyContain(id => id == null);
        (await upgraded.QueryAsync<string>(
                "select state from ocr_page_results order by result_id;"))
            .Should().OnlyContain(state => state == "succeeded");
        (await upgraded.QueryAsync<string?>(
                "select output_tree_revision_id from ocr_runs order by ocr_run_id;"))
            .Should().OnlyContain(id => id == null);
        (await upgraded.ExecuteScalarAsync<string>(
                "select source_tree_revision_id from ocr_runs where ocr_run_id = 'adopted-run';"))
            .Should().Be("old");
        (await upgraded.ExecuteScalarAsync<string?>(
                "select source_tree_revision_id from ocr_runs where ocr_run_id = 'unadopted-run';"))
            .Should().BeNull();
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from ocr_candidate_adoptions;"))
            .Should().Be(1);
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from document_commit_pages;"))
            .Should().Be(1);
        (await upgraded.ExecuteScalarAsync<string>(
            "select tree_revision_id from document_commit_pages;")).Should().Be("current");
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from continuation_migration_diagnostics;"))
            .Should().Be(1);
        (await upgraded.ExecuteScalarAsync<string>(
            "select source_tree_revision_id from page_translations;")).Should().BeEmpty();
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from search_units;")).Should().Be(1);
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from search_units_fts;")).Should().Be(1);
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from fts_row_map;")).Should().Be(1);
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from fts_cache_state;")).Should().Be(0);
        (await upgraded.ExecuteScalarAsync<string?>(
                "select supersedes_unit_id from search_units where unit_id = 'current-unit';"))
            .Should().BeNull();
        (await upgraded.ExecuteScalarAsync<int>("select count(*) from pragma_foreign_key_check;"))
            .Should().Be(0);
        (await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync())
            .Should().BeEmpty();
    }
}
