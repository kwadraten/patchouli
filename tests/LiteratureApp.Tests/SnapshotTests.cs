using Dapper;
using FluentAssertions;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Files;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Results;
using LiteratureApp.Evidence;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Evidence;
using LiteratureApp.Infrastructure.Files;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Ocr;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Infrastructure.Snapshots;
using LiteratureApp.Ocr;
using Microsoft.Data.Sqlite;

namespace LiteratureApp.Tests;

public sealed class SnapshotTests
{
    [Fact] public async Task PublishSnapshot_creates_manifest_shard_and_current_pointer() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); File.Exists(r.Value.ManifestPath).Should().BeTrue(); File.Exists(Path.Combine(c.SyncRoot, r.Value.Shards.Single().FileName)).Should().BeTrue(); File.Exists(r.Value.CurrentPointerPath).Should().BeTrue(); }
    [Fact] public async Task Manifest_contains_required_identity_fields() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); var m = await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(r.Value.ManifestPath, default); m!.LibraryId.Should().Be(c.LibraryId.ToString()); m.DeviceId.Should().Be("device-a"); m.SchemaVersion.Should().Be(1); m.Shards.Should().HaveCount(1); }
    [Fact] public async Task PublishSnapshot_refuses_runtime_db_inside_sync_root() { var sync = TempDir(); var runtime = Path.Combine(sync, "runtime.sqlite"); await File.WriteAllTextAsync(runtime, "x"); var r = await new SnapshotPublisher(new FixedClock(DateTimeOffset.UtcNow)).PublishSnapshotAsync(new SnapshotPublishRequest(runtime, sync, "device")); r.ErrorCode.Should().Be("validation_failed"); Directory.Delete(sync, true); }
    [Fact] public async Task PublishSnapshot_does_not_copy_wal_or_shm() { await using var c = await SnapshotTestContext.CreateAsync(); File.WriteAllText(c.Database.Path + "-wal", "wal"); File.WriteAllText(c.Database.Path + "-shm", "shm"); await c.PublishAsync(); Directory.EnumerateFiles(c.SyncRoot, "*", SearchOption.AllDirectories).Should().NotContain(p => p.EndsWith("-wal") || p.EndsWith("-shm")); }
    [Fact] public async Task PublishSnapshot_computes_and_verifies_shard_hash() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); (await SnapshotPublisher.VerifyShardAsync(c.SyncRoot, r.Value.Shards.Single())).Should().BeTrue(); }
    [Fact] public async Task PublishSnapshot_increments_logical_generation() { await using var c = await SnapshotTestContext.CreateAsync(); var first = await c.PublishAsync(); var second = await c.PublishAsync(parent: first.Value.SnapshotId); second.Value.LogicalGeneration.Should().Be(first.Value.LogicalGeneration + 1); }
    [Fact] public async Task PublishSnapshot_creates_sensitive_mutable_shard_when_credential_store_exists() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); var m = await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(r.Value.ManifestPath, default); m!.SensitiveMutableShards.Should().ContainSingle(s => s.Kind == "sensitive_mutable" && !s.IsImmutable); }
    [Fact] public async Task PublishSnapshot_does_not_treat_fts_as_canonical() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); await using var cn = OpenShard(c.SyncRoot, r.Value.Shards.Single()); (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().BeGreaterThan(0); (await cn.ExecuteScalarAsync<int>("select count(1) from search_units_fts;")).Should().Be(0); }
    [Fact] public async Task ValidateSnapshot_succeeds_for_valid_manifest() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); (await c.Importer.ValidateSnapshotAsync(r.Value.ManifestPath)).Value.IsValid.Should().BeTrue(); }
    [Fact] public async Task ValidateSnapshot_fails_for_hash_mismatch() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); await File.AppendAllTextAsync(Path.Combine(c.SyncRoot, r.Value.Shards.Single().FileName), "corrupt"); var v = await c.Importer.ValidateSnapshotAsync(r.Value.ManifestPath); v.Value.IsValid.Should().BeFalse(); v.Value.Errors.Should().Contain(e => e.Contains("hash") || e.Contains("size")); }
    [Fact] public async Task ImportSnapshotToStaging_does_not_replace_active_runtime_db() { await using var c = await SnapshotTestContext.CreateAsync(); var before = await File.ReadAllBytesAsync(c.Database.Path); var r = await c.PublishAsync(); await c.ImportAsync(r.Value.ManifestPath); (await File.ReadAllBytesAsync(c.Database.Path)).Should().Equal(before); }
    [Fact] public async Task ImportSnapshotToStaging_creates_staging_copy() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); var imported = await c.ImportAsync(r.Value.ManifestPath); File.Exists(imported.Value.StagingDatabasePath).Should().BeTrue(); }
    [Fact] public async Task ImportSnapshotToStaging_detects_library_mismatch() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); var imported = await c.Importer.ImportSnapshotToStagingAsync(new SnapshotImportRequest(r.Value.ManifestPath, c.StagingRoot, LibraryId.New())); imported.Value.IsLibraryMatch.Should().BeFalse(); imported.Value.StagingDatabasePath.Should().BeNull(); }
    [Fact] public async Task ImportSnapshotToStaging_accepts_matching_library() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); var imported = await c.Importer.ImportSnapshotToStagingAsync(new SnapshotImportRequest(r.Value.ManifestPath, c.StagingRoot, c.LibraryId)); imported.Value.IsLibraryMatch.Should().BeTrue(); }
    [Fact] public async Task PublishSnapshot_detects_parent_mismatch_and_does_not_overwrite_current() { await using var c = await SnapshotTestContext.CreateAsync(); var first = await c.PublishAsync(); var conflict = await c.PublishAsync(parent: "different-parent"); conflict.Value.CreatedBranch.Should().BeTrue(); var current = await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(Path.Combine(c.SyncRoot, "current.json"), default); current!.SnapshotId.Should().Be(first.Value.SnapshotId); }
    [Fact] public async Task PublishSnapshot_writes_branch_metadata_on_conflict() { await using var c = await SnapshotTestContext.CreateAsync(); await c.PublishAsync(); var conflict = await c.PublishAsync(parent: "different-parent"); conflict.Value.BranchInfo.Should().NotBeNull(); Directory.EnumerateFiles(Path.Combine(c.SyncRoot, "branches"), "*.json").Should().HaveCount(1); }
    [Fact] public async Task DetectBranch_returns_false_when_parent_matches() { await using var c = await SnapshotTestContext.CreateAsync(); var first = await c.PublishAsync(); (await c.Importer.DetectBranchAsync(c.SyncRoot, first.Value.SnapshotId)).Value.BranchDetected.Should().BeFalse(); }
    [Fact] public async Task DetectBranch_returns_true_when_parent_differs() { await using var c = await SnapshotTestContext.CreateAsync(); await c.PublishAsync(); (await c.Importer.DetectBranchAsync(c.SyncRoot, "old")).Value.BranchDetected.Should().BeTrue(); }
    [Fact] public async Task Branch_conflict_never_silent_last_writer_wins() { await using var c = await SnapshotTestContext.CreateAsync(); var first = await c.PublishAsync(); var conflict = await c.PublishAsync(parent: "old"); conflict.Value.SnapshotId.Should().BeEmpty(); var current = await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(Path.Combine(c.SyncRoot, "current.json"), default); current!.SnapshotId.Should().Be(first.Value.SnapshotId); }
    [Fact] public async Task Snapshot_does_not_include_external_pdf_files() { await using var c = await SnapshotTestContext.CreateAsync("/tmp/external-secret.pdf"); await c.PublishAsync(); Directory.EnumerateFiles(c.SyncRoot, "*", SearchOption.AllDirectories).Should().NotContain(p => p.EndsWith("external-secret.pdf")); }
    [Fact] public async Task Snapshot_redacts_local_file_paths_from_data_shard() { const string path = "/tmp/private-source/external-secret.pdf"; await using var c = await SnapshotTestContext.CreateAsync(path); var published = await c.PublishAsync(); var shardPath = Path.Combine(c.SyncRoot, published.Value.Shards.Single().FileName); await using (var shard = OpenShard(c.SyncRoot, published.Value.Shards.Single())) { (await shard.ExecuteScalarAsync<string>("select original_path from file_assets limit 1;")).Should().Be("[redacted]"); } (await File.ReadAllTextAsync(shardPath)).Should().NotContain(path); }
    [Fact] public async Task Snapshot_redacts_local_ocr_model_path_from_data_shard() { const string path = "/opt/local/ocr/model"; await using var c = await SnapshotTestContext.CreateAsync(); var presets = new OcrPresetService(c.Database.ConnectionFactory, new LibraryIdentityService(c.Database.ConnectionFactory, new LiteratureApp.Core.Time.SystemClock()), new LiteratureApp.Core.Time.SystemClock()); await presets.CreatePresetAsync("Local", null, OcrEngineIds.LocalPlaceholder, "local-model", path, "{}", true); var published = await c.PublishAsync(); var shardPath = Path.Combine(c.SyncRoot, published.Value.Shards.Single().FileName); await using (var shard = OpenShard(c.SyncRoot, published.Value.Shards.Single())) { (await shard.ExecuteScalarAsync<string>("select model_path from ocr_preset_versions limit 1;")).Should().Be("[redacted]"); } (await File.ReadAllTextAsync(shardPath)).Should().NotContain(path); }
    [Fact] public async Task Snapshot_does_not_include_cache_directory() { await using var c = await SnapshotTestContext.CreateAsync(); Directory.CreateDirectory(Path.Combine(c.SyncRoot, "cache")); File.WriteAllText(Path.Combine(c.SyncRoot, "cache", "thumb.png"), "cache"); await c.PublishAsync(); Directory.EnumerateFiles(Path.Combine(c.SyncRoot, "shards")).Should().NotContain(p => p.Contains("thumb")); }
    [Fact] public async Task Snapshot_preserves_persisted_search_units() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); await using var cn = OpenShard(c.SyncRoot, r.Value.Shards.Single()); (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().Be(1); }
    [Fact] public async Task Imported_staging_db_can_open_and_read_library_metadata() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); var imported = await c.ImportAsync(r.Value.ManifestPath); await using var cn = OpenSqlite(imported.Value.StagingDatabasePath!); await cn.OpenAsync(); (await cn.ExecuteScalarAsync<string>("select library_id from library_metadata limit 1;")).Should().Be(c.LibraryId.ToString()); }
    [Fact] public async Task Imported_staging_db_can_read_item_and_evidence_records() { await using var c = await SnapshotTestContext.CreateAsync(); var ev = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); var r = await c.PublishAsync(); var imported = await c.ImportAsync(r.Value.ManifestPath); await using var cn = OpenSqlite(imported.Value.StagingDatabasePath!); await cn.OpenAsync(); (await cn.ExecuteScalarAsync<int>("select count(1) from items;")).Should().Be(1); (await cn.ExecuteScalarAsync<string>("select evidence_ref_id from evidence_ref_records limit 1;")).Should().Be(ev.Value.EvidenceRefId); }
    [Fact] public async Task Corrupted_manifest_returns_validation_error() { var dir = TempDir(); var manifest = Path.Combine(dir, "manifests", "bad.json"); Directory.CreateDirectory(Path.GetDirectoryName(manifest)!); await File.WriteAllTextAsync(manifest, "{bad"); var v = await new SnapshotImporter().ValidateSnapshotAsync(manifest); v.Value.IsValid.Should().BeFalse(); Directory.Delete(dir, true); }
    [Fact] public async Task Missing_shard_returns_validation_error() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); File.Delete(Path.Combine(c.SyncRoot, r.Value.Shards.Single().FileName)); (await c.Importer.ValidateSnapshotAsync(r.Value.ManifestPath)).Value.IsValid.Should().BeFalse(); }
    [Fact] public async Task Snapshot_import_does_not_trigger_OCR() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); await c.ImportAsync(r.Value.ManifestPath); await using var cn = c.OpenRuntime(); (await cn.ExecuteScalarAsync<int>("select count(1) from ocr_runs;")).Should().Be(0); }
    [Fact] public async Task Snapshot_import_does_not_trigger_index_rebuild() { await using var c = await SnapshotTestContext.CreateAsync(); var r = await c.PublishAsync(); var before = await c.FtsCountAsync(); await c.ImportAsync(r.Value.ManifestPath); (await c.FtsCountAsync()).Should().Be(before); }
    [Fact] public async Task Snapshot_publish_does_not_modify_working_runtime_db_except_checkpoint() { await using var c = await SnapshotTestContext.CreateAsync(); var before = await c.RuntimeCountsAsync(); await c.PublishAsync(); (await c.RuntimeCountsAsync()).Should().Be(before); }
    [Fact] public void Credential_store_is_created_only_by_009_migration() { Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql").Select(Path.GetFileName).Where(name => name!.Contains("credential", StringComparison.OrdinalIgnoreCase)).Should().Equal("009_create_provider_credentials.sql"); }

    private static SqliteConnection OpenShard(string syncRoot, SnapshotShard shard) { var cn = OpenSqlite(Path.Combine(syncRoot, shard.FileName)); cn.Open(); return cn; }
    private static SqliteConnection OpenSqlite(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"literatureapp-snap-{Guid.NewGuid():N}")).FullName;

    private sealed class SnapshotTestContext : IAsyncDisposable
    {
        private SnapshotTestContext(TemporarySqliteDatabase database, string syncRoot, string stagingRoot, LibraryId libraryId, SearchUnitId unitId)
        { Database = database; SyncRoot = syncRoot; StagingRoot = stagingRoot; LibraryId = libraryId; UnitId = unitId; Publisher = new SnapshotPublisher(new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"))); Importer = new SnapshotImporter(); Evidence = new EvidenceReferenceService(database.ConnectionFactory, new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"))); }
        public TemporarySqliteDatabase Database { get; }
        public string SyncRoot { get; }
        public string StagingRoot { get; }
        public LibraryId LibraryId { get; }
        public SearchUnitId UnitId { get; }
        public SnapshotPublisher Publisher { get; }
        public SnapshotImporter Importer { get; }
        public EvidenceReferenceService Evidence { get; }
        public static async Task<SnapshotTestContext> CreateAsync(string? externalPath = null)
        {
            var db = TemporarySqliteDatabase.Create(); var clock = new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(db.ConnectionFactory, clock); var lib = await library.CreateLibraryAsync("Snapshot Library");
            var item = await new ItemService(db.ConnectionFactory, library, clock).CreateItemAsync("book", "Snapshot Item");
            FileAssetId? fileId = null;
            if (externalPath is not null)
            {
                fileId = FileAssetId.New(); await using var cx = db.ConnectionFactory.CreateConnection(); await cx.OpenAsync();
                await cx.ExecuteAsync("insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at) values (@Id,@Lib,@Path,'external-secret.pdf',0,'missing',@N,@N);", new { Id = fileId.Value.ToString(), Lib = lib.Value.LibraryId.ToString(), Path = externalPath, N = clock.UtcNow.ToString("O") });
            }
            var doc = await new DocumentInstanceService(db.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, fileId, DocumentInstanceType.PrimaryScan);
            var page = await new PageService(db.ConnectionFactory, clock).CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            var layout = new LayoutTreeService(db.ConnectionFactory, clock); var rev = await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Mock, makeCurrent: true);
            await layout.AddNodeAsync(rev.Value.LayoutRevisionId, page.Value.PageId, null, LayoutNodeType.Paragraph, null, "snapshot text", TextPolicy.Own, 1, LayoutNodeSource.Mock);
            var builder = new SearchUnitBuilder(db.ConnectionFactory, clock); var rebuilder = new SearchIndexRebuilder(db.ConnectionFactory, clock);
            await builder.RebuildForDocumentInstanceAsync(doc.Value.DocumentInstanceId); await rebuilder.RebuildFtsForLibraryAsync();
            await using var cn = db.ConnectionFactory.CreateConnection(); await cn.OpenAsync();
            var unit = SearchUnitId.Parse((await cn.ExecuteScalarAsync<string>("select unit_id from search_units limit 1;"))!);
            return new SnapshotTestContext(db, TempDir(), TempDir(), lib.Value.LibraryId, unit);
        }
        public Task<Result<SnapshotPublishResult>> PublishAsync(string? parent = null) => Publisher.PublishSnapshotAsync(new SnapshotPublishRequest(Database.Path, SyncRoot, "device-a", parent));
        public Task<Result<SnapshotImportResult>> ImportAsync(string manifest) => Importer.ImportSnapshotToStagingAsync(new SnapshotImportRequest(manifest, StagingRoot, LibraryId, Database.Path));
        public SqliteConnection OpenRuntime() { var cn = Database.ConnectionFactory.CreateConnection(); cn.Open(); return cn; }
        public async Task<int> FtsCountAsync() { await using var cn = OpenRuntime(); return await cn.ExecuteScalarAsync<int>("select count(1) from search_units_fts;"); }
        public async Task<(int Items, int Units, int Evidence)> RuntimeCountsAsync() { await using var cn = OpenRuntime(); return (await cn.ExecuteScalarAsync<int>("select count(1) from items;"), await cn.ExecuteScalarAsync<int>("select count(1) from search_units;"), await cn.ExecuteScalarAsync<int>("select count(1) from evidence_ref_records;")); }
        public async ValueTask DisposeAsync() { await Database.DisposeAsync(); if (Directory.Exists(SyncRoot)) Directory.Delete(SyncRoot, true); if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true); }
    }
}
