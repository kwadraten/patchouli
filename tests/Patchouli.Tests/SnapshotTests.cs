using Dapper;
using FluentAssertions;
using Patchouli.Core;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Operations;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Ocr;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Library;

namespace Patchouli.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public async Task PublishSnapshot_creates_manifest_shard_and_current_pointer()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        File.Exists(r.Value.ManifestPath).Should().BeTrue();
        File.Exists(Path.Combine(c.SyncRoot, r.Value.Shards.Single().FileName)).Should().BeTrue();
        File.Exists(r.Value.CurrentPointerPath).Should().BeTrue();
    }

    [Fact]
    public async Task Manifest_contains_required_identity_fields()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        SnapshotManifest? m = await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(r.Value.ManifestPath, default);
        m!.LibraryId.Should().Be(c.LibraryId.ToString());
        m.DeviceId.Should().Be("device-a");
        m.SchemaVersion.Should().Be(AppSchemaVersion.Current);
        m.Shards.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishSnapshot_refuses_runtime_db_inside_sync_root()
    {
        string sync = TempDir();
        string runtime = Path.Combine(sync, "runtime.sqlite");
        await File.WriteAllTextAsync(runtime, "x");
        Result<SnapshotPublishResult> r =
            await new SnapshotPublisher(new FixedClock(DateTimeOffset.UtcNow)).PublishSnapshotAsync(
                new SnapshotPublishRequest(runtime, sync, "device"));
        r.ErrorCode.Should().Be("validation_failed");
        Directory.Delete(sync, true);
    }

    [Fact]
    public async Task PublishSnapshot_does_not_copy_wal_or_shm()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        File.WriteAllText(c.Database.Path + "-wal", "wal");
        File.WriteAllText(c.Database.Path + "-shm", "shm");
        await c.PublishAsync();
        Directory.EnumerateFiles(c.SyncRoot, "*", SearchOption.AllDirectories).Should()
            .NotContain(p => p.EndsWith("-wal") || p.EndsWith("-shm"));
    }

    [Fact]
    public async Task PublishSnapshot_computes_and_verifies_shard_hash()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        (await SnapshotPublisher.VerifyShardAsync(c.SyncRoot, r.Value.Shards.Single())).Should().BeTrue();
    }

    [Fact]
    public async Task PublishSnapshot_splits_data_shards_when_runtime_exceeds_target_size()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync(targetShardSizeBytes: 1);
        r.Value.Shards.Should().HaveCountGreaterThan(1);
        foreach (SnapshotShard shard in r.Value.Shards)
        {
            (await SnapshotPublisher.VerifyShardAsync(c.SyncRoot, shard)).Should().BeTrue();
        }

        Result<SnapshotImportResult> imported = await c.ImportAsync(r.Value.ManifestPath);
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
        imported.Value.StagingDatabasePath.Should().NotBeNull(string.Join("; ", imported.Value.Warnings));
        await using SqliteConnection cn = OpenSqlite(imported.Value.StagingDatabasePath!);
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<int>("select count(1) from items;")).Should().Be(1);
        (await cn.ExecuteScalarAsync<int>("select count(1) from pages;")).Should().Be(1);
        (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().Be(1);
    }

    [Fact]
    public async Task PublishSnapshot_row_splits_oversized_data_tables()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        await c.AddSearchUnitCopiesAsync(12, new string('x', 8192));
        Result<SnapshotPublishResult> r = await c.PublishAsync(targetShardSizeBytes: 4096);
        r.Value.Shards.Should().Contain(s => s.Kind.StartsWith("data:04:01:", StringComparison.Ordinal));
        Result<SnapshotImportResult> imported = await c.ImportAsync(r.Value.ManifestPath);
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
        imported.Value.StagingDatabasePath.Should().NotBeNull(string.Join("; ", imported.Value.Warnings));
        await using SqliteConnection cn = OpenSqlite(imported.Value.StagingDatabasePath!);
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().Be(13);
    }

    [Fact]
    public async Task PublishSnapshot_reuses_unchanged_immutable_data_shards()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> first = await c.PublishAsync();
        Result<SnapshotPublishResult> second = await c.PublishAsync(first.Value.SnapshotId);
        second.Value.Shards.Single().FileName.Should().Be(first.Value.Shards.Single().FileName);
        Directory.EnumerateFiles(Path.Combine(c.SyncRoot, "shards"), "*.sqlite").Should().HaveCount(1);
    }

    [Fact]
    public async Task Publish_and_import_preserve_opted_in_non_secret_setting_records()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        await using (SqliteConnection connection = c.OpenRuntime())
        {
            await connection.ExecuteAsync(
                """
                insert into library_setting_records (
                    setting_key, schema_version, value_json, revision, updated_at, updated_by_device_id, merge_policy)
                values ('metadata_lookup', 1, '{"sources":[]}', 1, '2026-07-13T00:00:00.0000000+00:00',
                        'device-a', 'scalar_replace');
                """);
        }

        Result<SnapshotPublishResult> published = await c.PublishAsync();
        Result<SnapshotImportResult> imported = await c.ImportAsync(published.Value.ManifestPath);
        await using SqliteConnection staging = OpenSqlite(imported.Value.StagingDatabasePath!);
        await staging.OpenAsync();

        (await staging.ExecuteScalarAsync<string>(
                "select value_json from library_setting_records where setting_key = 'metadata_lookup';"))
            .Should().Be("{\"sources\":[]}");
    }

    [Fact]
    public async Task PublishSnapshot_increments_logical_generation()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> first = await c.PublishAsync();
        Result<SnapshotPublishResult> second = await c.PublishAsync(first.Value.SnapshotId);
        second.Value.LogicalGeneration.Should().Be(first.Value.LogicalGeneration + 1);
    }

    [Fact]
    public async Task PublishSnapshot_does_not_create_sensitive_mutable_shard_for_device_credentials()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        SnapshotManifest? m = await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(r.Value.ManifestPath, default);
        m!.SensitiveMutableShards.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishSnapshot_does_not_treat_fts_as_canonical()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        await using SqliteConnection cn = OpenShard(c.SyncRoot, r.Value.Shards.Single());
        (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().BeGreaterThan(0);
        (await cn.ExecuteScalarAsync<int>("select count(1) from search_units_fts;")).Should().Be(0);
    }

    [Fact]
    public async Task ValidateSnapshot_succeeds_for_valid_manifest()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        (await c.Importer.ValidateSnapshotAsync(r.Value.ManifestPath)).Value.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateSnapshot_rejects_shard_with_unknown_or_empty_schema_epoch()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> published = await c.PublishAsync();
        await using (SqliteConnection shard = OpenShard(c.SyncRoot, published.Value.Shards.Single()))
        {
            await shard.ExecuteAsync("pragma foreign_keys = off;");
            await shard.ExecuteAsync("delete from library_metadata;");
        }

        SnapshotValidationResult validation =
            (await c.Importer.ValidateSnapshotAsync(published.Value.ManifestPath)).Value;
        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().Contain(error => error.Contains("schema", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateSnapshot_fails_for_hash_mismatch()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        await File.AppendAllTextAsync(Path.Combine(c.SyncRoot, r.Value.Shards.Single().FileName), "corrupt");
        Result<SnapshotValidationResult> v = await c.Importer.ValidateSnapshotAsync(r.Value.ManifestPath);
        v.Value.IsValid.Should().BeFalse();
        v.Value.Errors.Should().Contain(e => e.Contains("hash") || e.Contains("size"));
    }

    [Fact]
    public async Task ImportSnapshotToStaging_does_not_replace_active_runtime_db()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        (int Items, int Units, int Evidence) before = await c.RuntimeCountsAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        await c.ImportAsync(r.Value.ManifestPath);
        (await c.RuntimeCountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task ImportSnapshotToStaging_creates_staging_copy()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        Result<SnapshotImportResult> imported = await c.ImportAsync(r.Value.ManifestPath);
        File.Exists(imported.Value.StagingDatabasePath).Should().BeTrue();
    }

    [Fact]
    public async Task ImportSnapshotToStaging_detects_library_mismatch()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        Result<SnapshotImportResult> imported =
            await c.Importer.ImportSnapshotToStagingAsync(new SnapshotImportRequest(r.Value.ManifestPath, c.StagingRoot,
                LibraryId.New()));
        imported.Value.IsLibraryMatch.Should().BeFalse();
        imported.Value.StagingDatabasePath.Should().BeNull();
    }

    [Fact]
    public async Task ImportSnapshotToStaging_accepts_matching_library()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        Result<SnapshotImportResult> imported =
            await c.Importer.ImportSnapshotToStagingAsync(new SnapshotImportRequest(r.Value.ManifestPath, c.StagingRoot,
                c.LibraryId));
        imported.Value.IsLibraryMatch.Should().BeTrue();
    }

    [Fact]
    public async Task
        ImportSnapshotToStaging_validation_failure_records_blocking_operation_and_leaves_runtime_db_unchanged()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        (int Items, int Units, int Evidence) before = await c.RuntimeCountsAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        await File.AppendAllTextAsync(Path.Combine(c.SyncRoot, r.Value.Shards.Single().FileName), "corrupt");
        Result<SnapshotImportResult> imported = await c.Importer.ImportSnapshotToStagingAsync(
            new SnapshotImportRequest(r.Value.ManifestPath, c.StagingRoot, c.LibraryId, c.Database.Path));
        Result<IReadOnlyList<BlockingOperation>> operations = await c.BlockingOperations.ListAsync(
            BlockingOperationStatus.Failed, BlockingOperationTypes.SnapshotImportValidation,
            BlockingOperationScopeTypes.SnapshotImport, Path.GetFileName(r.Value.ManifestPath));
        imported.IsSuccess.Should().BeTrue();
        imported.Value.IsValid.Should().BeFalse();
        imported.Value.StagingDatabasePath.Should().BeNull();
        (await c.RuntimeCountsAsync()).Should().Be(before);
        operations.IsSuccess.Should().BeTrue();
        operations.Value.Should().ContainSingle();
        operations.Value.Single().FailureCode.Should().Be(AppErrorCodes.ValidationFailed);
        operations.Value.Single().FailureMessage.Should().Contain("mismatch");
    }

    [Fact]
    public async Task PublishSnapshot_explicitly_replaces_current_when_parent_is_not_current()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> first = await c.PublishAsync();
        Result<SnapshotPublishResult> published = await c.PublishAsync("different-parent");
        published.IsSuccess.Should().BeTrue();
        published.Value.SnapshotId.Should().NotBe(first.Value.SnapshotId);
        SnapshotCurrentPointer? current =
            await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(Path.Combine(c.SyncRoot, "current.json"),
                default);
        current!.SnapshotId.Should().Be(published.Value.SnapshotId);
    }

    [Fact]
    public async Task PublishSnapshot_does_not_create_a_branches_directory()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> published = await c.PublishAsync();

        published.IsSuccess.Should().BeTrue();
        Directory.Exists(Path.Combine(c.SyncRoot, "branches")).Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_does_not_include_external_pdf_files()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync("/tmp/external-secret.pdf");
        await c.PublishAsync();
        Directory.EnumerateFiles(c.SyncRoot, "*", SearchOption.AllDirectories).Should()
            .NotContain(p => p.EndsWith("external-secret.pdf"));
    }

    [Fact]
    public async Task Snapshot_redacts_local_file_paths_from_data_shard()
    {
        const string path = "/tmp/private-source/external-secret.pdf";
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync(path);
        Result<SnapshotPublishResult> published = await c.PublishAsync();
        string shardPath = Path.Combine(c.SyncRoot, published.Value.Shards.Single().FileName);
        await using (SqliteConnection shard = OpenShard(c.SyncRoot, published.Value.Shards.Single()))
        {
            (await shard.ExecuteScalarAsync<string>("select original_path from file_assets limit 1;")).Should()
                .Be("[redacted]");
        }

        (await File.ReadAllTextAsync(shardPath)).Should().NotContain(path);
    }

    [Fact]
    public async Task Snapshot_redacts_local_ocr_model_path_from_data_shard()
    {
        const string path = "/opt/local/ocr/model";
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        OcrPresetService presets = new(c.Database.ConnectionFactory,
            new LibraryIdentityService(c.Database.ConnectionFactory, new Core.Time.SystemClock()),
            new Core.Time.SystemClock());
        await presets.CreatePresetAsync("Local", null, OcrEngineIds.LocalPlaceholder, "local-model", path, "{}", true);
        Result<SnapshotPublishResult> published = await c.PublishAsync();
        string shardPath = Path.Combine(c.SyncRoot, published.Value.Shards.Single().FileName);
        await using (SqliteConnection shard = OpenShard(c.SyncRoot, published.Value.Shards.Single()))
        {
            (await shard.ExecuteScalarAsync<string>("select model_path from ocr_preset_versions limit 1;")).Should()
                .Be("[redacted]");
        }

        (await File.ReadAllTextAsync(shardPath)).Should().NotContain(path);
    }

    [Fact]
    public async Task Snapshot_does_not_include_cache_directory()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Directory.CreateDirectory(Path.Combine(c.SyncRoot, "cache"));
        File.WriteAllText(Path.Combine(c.SyncRoot, "cache", "thumb.png"), "cache");
        await c.PublishAsync();
        Directory.EnumerateFiles(Path.Combine(c.SyncRoot, "shards")).Should().NotContain(p => p.Contains("thumb"));
    }

    [Fact]
    public async Task Snapshot_preserves_persisted_search_units()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        await using SqliteConnection cn = OpenShard(c.SyncRoot, r.Value.Shards.Single());
        (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().Be(1);
    }

    [Fact]
    public async Task Imported_staging_db_can_open_and_read_library_metadata()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        Result<SnapshotImportResult> imported = await c.ImportAsync(r.Value.ManifestPath);
        await using SqliteConnection cn = OpenSqlite(imported.Value.StagingDatabasePath!);
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<string>("select library_id from library_metadata limit 1;")).Should()
            .Be(c.LibraryId.ToString());
    }

    [Fact]
    public async Task Imported_staging_db_can_read_item_and_evidence_records()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<EvidenceRefRecord> ev = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId);
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        Result<SnapshotImportResult> imported = await c.ImportAsync(r.Value.ManifestPath);
        await using SqliteConnection cn = OpenSqlite(imported.Value.StagingDatabasePath!);
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<int>("select count(1) from items;")).Should().Be(1);
        (await cn.ExecuteScalarAsync<string>("select evidence_ref_id from evidence_ref_records limit 1;")).Should()
            .Be(ev.Value.EvidenceRefId);
    }

    [Fact]
    public async Task Corrupted_manifest_returns_validation_error()
    {
        string dir = TempDir();
        string manifest = Path.Combine(dir, "manifests", "bad.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        await File.WriteAllTextAsync(manifest, "{bad");
        Result<SnapshotValidationResult> v = await new SnapshotImporter().ValidateSnapshotAsync(manifest);
        v.Value.IsValid.Should().BeFalse();
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Missing_shard_returns_validation_error()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        File.Delete(Path.Combine(c.SyncRoot, r.Value.Shards.Single().FileName));
        (await c.Importer.ValidateSnapshotAsync(r.Value.ManifestPath)).Value.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_import_does_not_trigger_OCR()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        await c.ImportAsync(r.Value.ManifestPath);
        await using SqliteConnection cn = c.OpenRuntime();
        (await cn.ExecuteScalarAsync<int>("select count(1) from ocr_runs;")).Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_import_does_not_trigger_index_rebuild()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> r = await c.PublishAsync();
        int before = await c.FtsCountAsync();
        await c.ImportAsync(r.Value.ManifestPath);
        (await c.FtsCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Snapshot_publish_does_not_modify_working_runtime_db_except_checkpoint()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        (int Items, int Units, int Evidence) before = await c.RuntimeCountsAsync();
        await c.PublishAsync();
        (await c.RuntimeCountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Sync_coordinator_publishes_then_records_local_lineage()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        MemorySnapshotSyncBindingStore bindings = new(new SnapshotSyncBinding(
            c.Database.Path,
            "sync-root-a",
            c.SyncRoot,
            c.StagingRoot,
            "device-a",
            SnapshotSyncLocalState.NotConfigured));
        FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SnapshotSyncCoordinator coordinator = new(
            c.Publisher,
            c.Importer,
            new SnapshotBranchInspectionService(
                c.Importer,
                c.Database.ConnectionFactory,
                new LibraryIdentityService(c.Database.ConnectionFactory, clock)),
            bindings,
            clock);

        Result<SnapshotPublishResult> published = await coordinator.PublishAsync();

        published.IsSuccess.Should().BeTrue();
        bindings.State.OperationState.Should().Be(SnapshotSyncOperationState.Published);
        bindings.State.LastPublishedSnapshotId.Should().Be(published.Value.SnapshotId);
        bindings.State.LineageSnapshotId.Should().Be(published.Value.SnapshotId);
        (await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(Path.Combine(c.SyncRoot, "current.json"),
                default))!
            .SnapshotId.Should().Be(published.Value.SnapshotId);
    }

    [Fact]
    public async Task Sync_coordinator_records_cancelled_state_when_publish_is_cancelled()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        MemorySnapshotSyncBindingStore bindings = new(new SnapshotSyncBinding(
            c.Database.Path,
            "sync-root-a",
            c.SyncRoot,
            c.StagingRoot,
            "device-a",
            SnapshotSyncLocalState.NotConfigured));
        FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        using CancellationTokenSource cancellation = new();
        CancellationToken cancellationToken = cancellation.Token;
        SnapshotSyncCoordinator coordinator = new(
            new CancellingSnapshotPublisher(cancellation),
            c.Importer,
            new SnapshotBranchInspectionService(
                c.Importer,
                c.Database.ConnectionFactory,
                new LibraryIdentityService(c.Database.ConnectionFactory, clock)),
            bindings,
            clock);

        Func<Task> action = async () => await coordinator.PublishAsync(cancellationToken);

        await action.Should().ThrowAsync<OperationCanceledException>();
        bindings.State.OperationState.Should().Be(SnapshotSyncOperationState.Cancelled);
        bindings.State.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Sync_coordinator_exports_a_valid_portable_package_without_a_current_pointer()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        string destination = Path.Combine(Path.GetTempPath(), $"patchouli-export-{Guid.NewGuid():N}");
        try
        {
            MemorySnapshotSyncBindingStore bindings = new(new SnapshotSyncBinding(
                c.Database.Path,
                "sync-root-a",
                c.SyncRoot,
                c.StagingRoot,
                "device-a",
                SnapshotSyncLocalState.NotConfigured));
            FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            SnapshotSyncCoordinator coordinator = new(
                c.Publisher,
                c.Importer,
                new SnapshotBranchInspectionService(
                    c.Importer,
                    c.Database.ConnectionFactory,
                    new LibraryIdentityService(c.Database.ConnectionFactory, clock)),
                bindings,
                clock);

            Result<SnapshotExportResult> exported =
                await coordinator.ExportAsync(new SnapshotExportRequest(destination));

            exported.IsSuccess.Should().BeTrue();
            File.Exists(exported.Value.ManifestPath).Should().BeTrue();
            File.Exists(Path.Combine(destination, "current.json")).Should().BeFalse();
            (await c.Importer.ValidateSnapshotAsync(exported.Value.ManifestPath)).Value.IsValid.Should().BeTrue();
            File.Exists(Path.Combine(c.SyncRoot, "current.json")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }
        }
    }

    [Fact]
    public async Task Sync_coordinator_rejects_an_inspected_plan_when_local_content_changes()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        MemorySnapshotSyncBindingStore bindings = new(new SnapshotSyncBinding(
            c.Database.Path,
            "sync-root-a",
            c.SyncRoot,
            c.StagingRoot,
            "device-a",
            SnapshotSyncLocalState.NotConfigured));
        FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SnapshotSyncCoordinator coordinator = new(
            c.Publisher,
            c.Importer,
            new SnapshotBranchInspectionService(
                c.Importer,
                c.Database.ConnectionFactory,
                new LibraryIdentityService(c.Database.ConnectionFactory, clock)),
            bindings,
            clock);
        (await coordinator.PublishAsync()).IsSuccess.Should().BeTrue();
        Result<SnapshotIncomingPlan> inspected =
            await coordinator.InspectIncomingAsync(SnapshotIncomingRequest.CurrentSyncRoot);
        inspected.IsSuccess.Should().BeTrue();

        await using (SqliteConnection connection = c.OpenRuntime())
        {
            await connection.ExecuteAsync("update items set title = 'Changed after inspection';");
        }

        Result<SnapshotApplyResult> applied = await coordinator.ApplyAsync(
            inspected.Value.ContentPlan with { IsExplicitlyConfirmed = true });

        applied.ErrorCode.Should().Be("snapshot_plan_superseded");
    }

    [Fact]
    public async Task Sync_coordinator_keeps_an_inspected_branch_as_a_copy_then_releases_staging()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        MemorySnapshotSyncBindingStore bindings = new(new SnapshotSyncBinding(
            c.Database.Path,
            "sync-root-a",
            c.SyncRoot,
            c.StagingRoot,
            "device-a",
            SnapshotSyncLocalState.NotConfigured));
        FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SnapshotSyncCoordinator coordinator = new(
            c.Publisher,
            c.Importer,
            new SnapshotBranchInspectionService(
                c.Importer,
                c.Database.ConnectionFactory,
                new LibraryIdentityService(c.Database.ConnectionFactory, clock)),
            bindings,
            clock);
        (await coordinator.PublishAsync()).IsSuccess.Should().BeTrue();
        Result<SnapshotIncomingPlan> inspected =
            await coordinator.InspectIncomingAsync(SnapshotIncomingRequest.CurrentSyncRoot);
        string destination = Path.Combine(c.StagingRoot, "preserved-library.sqlite");

        Result<string> kept = await coordinator.KeepIncomingAsSeparateLibraryCopyAsync(
            inspected.Value.ContentPlan,
            destination);

        kept.IsSuccess.Should().BeTrue(kept.ErrorMessage);
        File.Exists(destination).Should().BeTrue();
        File.Exists(inspected.Value.Branch.StagingDatabasePath).Should().BeFalse();
        bindings.State.OperationState.Should().Be(SnapshotSyncOperationState.Ready);
    }

    [Fact]
    public async Task Sync_coordinator_resolves_an_item_conflict_before_applying_the_replacement_plan()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        MemorySnapshotSyncBindingStore bindings = new(new SnapshotSyncBinding(
            c.Database.Path,
            "sync-root-a",
            c.SyncRoot,
            c.StagingRoot,
            "device-a",
            SnapshotSyncLocalState.NotConfigured));
        FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        SnapshotSyncCoordinator coordinator = new(
            c.Publisher,
            c.Importer,
            new SnapshotBranchInspectionService(
                c.Importer,
                c.Database.ConnectionFactory,
                new LibraryIdentityService(c.Database.ConnectionFactory, clock)),
            bindings,
            clock);
        (await coordinator.PublishAsync()).IsSuccess.Should().BeTrue();

        await using (SqliteConnection connection = c.OpenRuntime())
        {
            await connection.ExecuteAsync("update items set title = 'Local title';");
        }

        Result<SnapshotIncomingPlan> inspected =
            await coordinator.InspectIncomingAsync(SnapshotIncomingRequest.CurrentSyncRoot);
        ConflictDescriptor conflict = inspected.Value.Conflicts.Single(candidate =>
            candidate.ConflictCode == ConflictCode.SameIdDifferentContent);

        Result<SnapshotContentResolutionPlan> itemResolved = await coordinator.ResolveContentConflictAsync(
            inspected.Value.ContentPlan,
            conflict.ConflictId!,
            new ConflictActionSelection("keep_local"));

        itemResolved.IsSuccess.Should().BeTrue();
        itemResolved.Value.BranchImportPlan.Conflicts.Single(candidate => candidate.ConflictId == conflict.ConflictId)
            .ResolutionStatus.Should().Be(ConflictResolutionStatus.Resolved);
        itemResolved.Value.BranchImportPlan.ItemsToImport.Should().BeEmpty();

        ConflictDescriptor primaryDocumentConflict = itemResolved.Value.BranchImportPlan.Conflicts.Single(candidate =>
            candidate.ConflictCode == ConflictCode.PrimaryDocumentConflict);
        Result<SnapshotContentResolutionPlan> resolved = await coordinator.ResolveContentConflictAsync(
            itemResolved.Value,
            primaryDocumentConflict.ConflictId!,
            new ConflictActionSelection("keep_local_without_incoming"));

        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.BranchImportPlan.Conflicts.Should().OnlyContain(candidate =>
            candidate.ResolutionStatus == ConflictResolutionStatus.Resolved);

        Result<SnapshotApplyResult> applied = await coordinator.ApplyAsync(
            resolved.Value with { IsExplicitlyConfirmed = true });

        applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
        await using SqliteConnection verification = c.OpenRuntime();
        (await verification.ExecuteScalarAsync<string>("select title from items limit 1;")).Should().Be("Local title");
    }

    [Fact]
    public async Task ValidateSnapshot_rejects_a_shard_path_that_escapes_the_package_root()
    {
        await using SnapshotTestContext c = await SnapshotTestContext.CreateAsync();
        Result<SnapshotPublishResult> published = await c.PublishAsync();
        SnapshotManifest manifest =
            (await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(published.Value.ManifestPath, default))!;
        SnapshotManifest unsafeManifest = manifest with
        {
            Shards = [manifest.Shards.Single() with { FileName = Path.Combine("..", "outside.sqlite") }]
        };
        await SnapshotPublisher.WriteJsonAtomicAsync(published.Value.ManifestPath, unsafeManifest, default);

        Result<SnapshotValidationResult> validation =
            await c.Importer.ValidateSnapshotAsync(published.Value.ManifestPath);

        validation.Value.IsValid.Should().BeFalse();
        validation.Value.Errors.Should()
            .Contain(error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Credential_store_is_created_only_by_009_migration()
    {
        Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql").Select(Path.GetFileName)
            .Where(name => name!.Contains("credential", StringComparison.OrdinalIgnoreCase)).Should()
            .BeEmpty();
    }

    private static SqliteConnection OpenShard(string syncRoot, SnapshotShard shard)
    {
        SqliteConnection cn = OpenSqlite(Path.Combine(syncRoot, shard.FileName));
        cn.Open();
        return cn;
    }

    private static SqliteConnection OpenSqlite(string path)
    {
        return new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
    }

    private static string TempDir()
    {
        return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-snap-{Guid.NewGuid():N}"))
            .FullName;
    }

    private sealed class SnapshotTestContext : IAsyncDisposable
    {
        private SnapshotTestContext(TemporarySqliteDatabase database, string syncRoot, string stagingRoot,
            LibraryId libraryId, SearchUnitId unitId, IBlockingOperationService blockingOperations)
        {
            Database = database;
            SyncRoot = syncRoot;
            StagingRoot = stagingRoot;
            LibraryId = libraryId;
            UnitId = unitId;
            BlockingOperations = blockingOperations;
            Publisher = new SnapshotPublisher(new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
            Importer = new SnapshotImporter(blockingOperations);
            Evidence = new EvidenceReferenceService(database.ConnectionFactory,
                new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        }

        public TemporarySqliteDatabase Database { get; }
        public string SyncRoot { get; }
        public string StagingRoot { get; }
        public LibraryId LibraryId { get; }
        public SearchUnitId UnitId { get; }
        public IBlockingOperationService BlockingOperations { get; }
        public SnapshotPublisher Publisher { get; }
        public SnapshotImporter Importer { get; }
        public EvidenceReferenceService Evidence { get; }

        public static async Task<SnapshotTestContext> CreateAsync(string? externalPath = null)
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            Result<LibraryMetadata> lib = await library.CreateLibraryAsync("Snapshot Library");
            Result<ItemMetadata> item =
                await new ItemService(db.ConnectionFactory, library, clock).CreateItemAsync("book", "Snapshot Item");
            FileAssetId? fileId = null;
            if (externalPath is not null)
            {
                fileId = FileAssetId.New();
                await using SqliteConnection cx = db.ConnectionFactory.CreateConnection();
                await cx.OpenAsync();
                await cx.ExecuteAsync(
                    "insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at) values (@Id,@Lib,@Path,'external-secret.pdf',0,'missing',@N,@N);",
                    new
                    {
                        Id = fileId.Value.ToString(), Lib = lib.Value.LibraryId.ToString(), Path = externalPath,
                        N = clock.UtcNow.ToString("O")
                    });
            }

            Result<DocumentInstance> doc =
                await new DocumentInstanceService(db.ConnectionFactory, clock).AttachDocumentInstanceAsync(
                    item.Value.ItemId, fileId, DocumentInstanceType.PrimaryScan);
            Result<Page> page = await new PageService(db.ConnectionFactory, clock).CreatePageAsync(
                doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null,
                "renderer-v1", null);
            await BoxTreeTestData.CommitTextAsync(db.ConnectionFactory, clock, doc.Value.DocumentInstanceId,
                page.Value.PageId, "snapshot text");
            SearchUnitBuilder builder = new(db.ConnectionFactory, clock);
            SearchIndexRebuilder rebuilder = new(db.ConnectionFactory, clock);
            await builder.RebuildForDocumentInstanceAsync(doc.Value.DocumentInstanceId);
            await rebuilder.RebuildFtsForLibraryAsync();
            await using SqliteConnection cn = db.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            SearchUnitId unit =
                SearchUnitId.Parse((await cn.ExecuteScalarAsync<string>("select unit_id from search_units limit 1;"))!);
            BlockingOperationService blockingOperations = new(db.ConnectionFactory, clock);
            return new SnapshotTestContext(db, TempDir(), TempDir(), lib.Value.LibraryId, unit, blockingOperations);
        }

        public Task<Result<SnapshotPublishResult>> PublishAsync(string? parent = null,
            long? targetShardSizeBytes = null)
        {
            return Publisher.PublishSnapshotAsync(new SnapshotPublishRequest(Database.Path, SyncRoot, "device-a",
                parent, TargetShardSizeBytes: targetShardSizeBytes ?? 512L * 1024L * 1024L));
        }

        public Task<Result<SnapshotImportResult>> ImportAsync(string manifest)
        {
            return Importer.ImportSnapshotToStagingAsync(new SnapshotImportRequest(manifest, StagingRoot, LibraryId,
                Database.Path));
        }

        public SqliteConnection OpenRuntime()
        {
            SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            cn.Open();
            return cn;
        }

        public async Task<int> FtsCountAsync()
        {
            await using SqliteConnection cn = OpenRuntime();
            return await cn.ExecuteScalarAsync<int>("select count(1) from search_units_fts;");
        }

        public async Task<(int Items, int Units, int Evidence)> RuntimeCountsAsync()
        {
            await using SqliteConnection cn = OpenRuntime();
            return (await cn.ExecuteScalarAsync<int>("select count(1) from items;"),
                await cn.ExecuteScalarAsync<int>("select count(1) from search_units;"),
                await cn.ExecuteScalarAsync<int>("select count(1) from evidence_ref_records;"));
        }

        public async Task AddSearchUnitCopiesAsync(int count, string text)
        {
            await using SqliteConnection cn = OpenRuntime();
            SearchUnitTemplate t = await cn.QuerySingleAsync<SearchUnitTemplate>(
                "select document_instance_id as DocumentInstanceId, page_id as PageId, tree_revision_id as TreeRevisionId, box_type as BoxType from search_units limit 1;");
            for (int i = 0; i < count; i++)
            {
                string pageId = PageId.New().ToString();
                string revisionId = DocumentTreeRevisionId.New().ToString();
                string boxId = DocumentBoxId.New().ToString();
                await cn.ExecuteAsync(
                    "insert into pages(page_id,document_instance_id,page_index,rotation,coordinate_basis,renderer_basis_version,created_at,updated_at) values(@Page,@DocumentInstanceId,@Index,0,'normalized_page','test',@Now,@Now);insert into document_tree_revisions(tree_revision_id,document_instance_id,page_id,source,status,is_current,created_at,committed_at) values(@Revision,@DocumentInstanceId,@Page,'manual_edit','committed',1,@Now,@Now);insert into document_boxes(tree_revision_id,box_id,document_instance_id,page_id,box_type,bbox_x,bbox_y,bbox_width,bbox_height,payload_json,suppressed) values(@Revision,@Box,@DocumentInstanceId,@Page,'text',0.01,0.01,0.01,0.01,@Payload,0);",
                    new
                    {
                        Page = pageId, Revision = revisionId, Box = boxId, t.DocumentInstanceId,
                        Index = i + 1, Now = "2026-01-01T00:00:00.0000000Z",
                        Payload = System.Text.Json.JsonSerializer.Serialize(new { markdown = text })
                    });
                await cn.ExecuteAsync(
                    "insert into search_units (unit_id,document_instance_id,page_id,box_id,tree_revision_id,resolved_text,bbox_json,box_type,ordinal,status,created_at,updated_at) values(@UnitId,@DocumentInstanceId,@PageId,@BoxId,@TreeRevisionId,@Text,'{}',@BoxType,@Ordinal,'current',@Now,@Now);",
                    new
                    {
                        UnitId = SearchUnitId.New().ToString(), t.DocumentInstanceId, PageId = pageId, BoxId = boxId,
                        TreeRevisionId = revisionId, Text = text, t.BoxType, Ordinal = i + 2,
                        Now = "2026-01-01T00:00:00.0000000Z"
                    });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (Directory.Exists(SyncRoot))
            {
                Directory.Delete(SyncRoot, true);
            }

            if (Directory.Exists(StagingRoot))
            {
                Directory.Delete(StagingRoot, true);
            }
        }

        private sealed record SearchUnitTemplate(
            string DocumentInstanceId,
            string PageId,
            string TreeRevisionId,
            string BoxType);
    }

    private sealed class MemorySnapshotSyncBindingStore : ISnapshotSyncBindingStore
    {
        private SnapshotSyncBinding _binding;

        public MemorySnapshotSyncBindingStore(SnapshotSyncBinding binding)
        {
            _binding = binding;
        }

        public SnapshotSyncLocalState State => _binding.LocalState;

        public Task<Result<SnapshotSyncBinding>> GetBindingAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(Result<SnapshotSyncBinding>.Success(_binding));
        }

        public Task<Result> SaveLocalStateAsync(
            SnapshotSyncLocalState state,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            _binding = _binding with { LocalState = state };
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class CancellingSnapshotPublisher : ISnapshotPublisher
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingSnapshotPublisher(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public Task<Result<SnapshotPublishResult>> PublishSnapshotAsync(
            SnapshotPublishRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _cancellation.Cancel();
            return Task.FromCanceled<Result<SnapshotPublishResult>>(cancellationToken);
        }
    }
}
