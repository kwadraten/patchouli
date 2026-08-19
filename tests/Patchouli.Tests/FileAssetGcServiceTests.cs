using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class FileAssetGcServiceTests
{
    [Fact]
    public async Task Preview_excludes_file_assets_referenced_by_document_instances()
    {
        await using Ctx c = await Ctx.Create();
        FileAssetId fileAssetId = await c.CreateAvailableFileAssetAsync();
        DocumentInstanceId documentId = await c.AttachDocumentInstanceAsync(fileAssetId);

        IReadOnlyList<FileAssetGcCandidate> before = await c.Gc.PreviewAsync();

        before.Should().BeEmpty();

        await c.DetachDocumentInstanceAsync(documentId);

        IReadOnlyList<FileAssetGcCandidate> after = await c.Gc.PreviewAsync();

        after.Should().ContainSingle()
            .Which.FileAssetId.Should().Be(fileAssetId);
    }

    [Fact]
    public async Task Preview_excludes_file_assets_referenced_by_trashed_item_documents()
    {
        await using Ctx c = await Ctx.Create();
        FileAssetId fileAssetId = await c.CreateAvailableFileAssetAsync();
        DocumentInstanceId documentId = await c.AttachDocumentInstanceAsync(fileAssetId, true);

        IReadOnlyList<FileAssetGcCandidate> before = await c.Gc.PreviewAsync();

        before.Should().BeEmpty();

        await c.DetachDocumentInstanceAsync(documentId);

        IReadOnlyList<FileAssetGcCandidate> after = await c.Gc.PreviewAsync();

        after.Should().ContainSingle()
            .Which.FileAssetId.Should().Be(fileAssetId);
    }

    [Fact]
    public async Task Preview_excludes_file_assets_referenced_by_ocr_runs()
    {
        await using Ctx c = await Ctx.Create();
        FileAssetId fileAssetId = await c.CreateAvailableFileAssetAsync();
        DocumentInstanceId documentId = await c.AttachDocumentInstanceAsync(fileAssetId);
        await c.InsertOcrRunAsync(documentId);

        IReadOnlyList<FileAssetGcCandidate> candidates = await c.Gc.PreviewAsync();

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_excludes_file_assets_referenced_by_document_box_payload()
    {
        await using Ctx c = await Ctx.Create();
        FileAssetId fileAssetId = await c.CreateAvailableFileAssetAsync();
        await c.AttachDocumentBoxPayloadAsync(fileAssetId);

        IReadOnlyList<FileAssetGcCandidate> candidates = await c.Gc.PreviewAsync();

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_excludes_file_assets_referenced_by_snapshot()
    {
        await using Ctx c = await Ctx.Create();
        FileAssetId fileAssetId = await c.CreateAvailableFileAssetAsync();
        await c.PublishSnapshotAsync();

        IReadOnlyList<FileAssetGcCandidate> candidates = await c.Gc.PreviewAsync();

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_deletes_orphan_file_asset_and_known_file_locations_but_keeps_original_file()
    {
        await using Ctx c = await Ctx.Create();
        FileAssetId fileAssetId = await c.CreateAvailableFileAssetAsync();

        (await c.CountAsync("file_assets")).Should().Be(1);
        (await c.CountAsync("known_file_locations")).Should().Be(1);

        FileAssetGcResult result = await c.Gc.RunAsync(new FileAssetGcOptions());

        result.Deleted.Should().ContainSingle().Which.Should().Be(fileAssetId);
        result.Failed.Should().BeEmpty();
        File.Exists(c.OriginalFilePath).Should().BeTrue();
        (await c.CountAsync("file_assets")).Should().Be(0);
        (await c.CountAsync("known_file_locations")).Should().Be(0);
        c.Logger.Logs.Should().Contain(log => log.Contains("deleted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preview_and_run_are_consistent()
    {
        await using Ctx c = await Ctx.Create();
        FileAssetId orphanId = await c.CreateAvailableFileAssetAsync();
        FileAssetId referencedId = await c.CreateAvailableFileAssetWithContentAsync("referenced");
        await c.AttachDocumentInstanceAsync(referencedId);

        IReadOnlyList<FileAssetGcCandidate> preview = await c.Gc.PreviewAsync();

        preview.Should().ContainSingle()
            .Which.FileAssetId.Should().Be(orphanId);

        FileAssetGcResult result = await c.Gc.RunAsync(new FileAssetGcOptions());

        result.Deleted.Should().Equal(new[] { orphanId });
        result.Failed.Should().BeEmpty();
        (await c.CountAsync("file_assets")).Should().Be(1);
    }

    [Fact]
    public async Task Run_respects_delay()
    {
        await using Ctx c = await Ctx.Create();
        await c.CreateAvailableFileAssetAsync();
        DateTimeOffset start = DateTimeOffset.UtcNow;

        FileAssetGcResult result = await c.Gc.RunAsync(new FileAssetGcOptions(TimeSpan.FromMilliseconds(150)));

        (DateTimeOffset.UtcNow - start).Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));
        result.Deleted.Should().ContainSingle();
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(
            TemporarySqliteDatabase database,
            FixedClock clock,
            LibraryIdentityService library,
            LibraryRevisionService revisions,
            ItemService items,
            FileAssetService files,
            FileAssetGcService gc,
            TestBindingStore bindingStore,
            FakeAppLogger logger,
            string originalFilePath,
            string syncRoot)
        {
            Database = database;
            Clock = clock;
            Library = library;
            Revisions = revisions;
            Items = items;
            Files = files;
            Gc = gc;
            BindingStore = bindingStore;
            Logger = logger;
            OriginalFilePath = originalFilePath;
            SyncRoot = syncRoot;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public LibraryRevisionService Revisions { get; }
        public ItemService Items { get; }
        public FileAssetService Files { get; }
        public FileAssetGcService Gc { get; }
        public TestBindingStore BindingStore { get; }
        public FakeAppLogger Logger { get; }
        public string OriginalFilePath { get; }
        public string SyncRoot { get; }
        public List<string> ExtraFiles { get; } = new();

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            LibraryRevisionService revisions = new(database.ConnectionFactory);
            await library.CreateLibraryAsync("GC Test");
            ItemService items = new(database.ConnectionFactory, library, clock, revisions);
            FileAssetService files = new(database.ConnectionFactory, library, clock, revisions: revisions);
            string originalFilePath = Path.Combine(Path.GetTempPath(), $"gc-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(originalFilePath, "original");
            string syncRoot = Path.Combine(Path.GetTempPath(), $"gc-sync-{Guid.NewGuid():N}");
            Directory.CreateDirectory(syncRoot);
            TestBindingStore bindingStore = new(database.Path, syncRoot);
            FakeAppLogger logger = new();
            FileAssetGcService gc = new(database.ConnectionFactory, bindingStore, logger);

            return new Ctx(database, clock, library, revisions, items, files, gc, bindingStore, logger,
                originalFilePath, syncRoot);
        }

        public async ValueTask DisposeAsync()
        {
            if (File.Exists(OriginalFilePath))
            {
                File.Delete(OriginalFilePath);
            }

            foreach (string path in ExtraFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            await Database.DisposeAsync();
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(SyncRoot))
            {
                try
                {
                    Directory.Delete(SyncRoot, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public async Task<FileAssetId> CreateAvailableFileAssetAsync()
        {
            Result<FileAsset> result = await Files.RegisterFileAsync(OriginalFilePath);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            return result.Value.FileAssetId;
        }

        public async Task<FileAssetId> CreateAvailableFileAssetWithContentAsync(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), $"gc-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, content);
            ExtraFiles.Add(path);
            Result<FileAsset> result = await Files.RegisterFileAsync(path);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            return result.Value.FileAssetId;
        }

        public async Task<DocumentInstanceId> AttachDocumentInstanceAsync(FileAssetId fileAssetId,
            bool trashItem = false)
        {
            Result<ItemMetadata> item = await Items.CreateItemAsync("book", "Reference Holder");
            item.IsSuccess.Should().BeTrue();

            if (trashItem)
            {
                Result deleted = await Items.DeleteItemAsync(item.Value.ItemId);
                deleted.IsSuccess.Should().BeTrue();
            }

            LibraryMetadata library = (await Library.GetCurrentLibraryAsync()).Value;
            string now = Clock.UtcNow.ToString("O");
            DocumentInstanceId documentId = DocumentInstanceId.New();

            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_instances (
                    document_instance_id,
                    item_id,
                    file_asset_id,
                    instance_type,
                    is_primary,
                    status,
                    created_at,
                    updated_at
                )
                values (
                    @DocumentId,
                    @ItemId,
                    @FileAssetId,
                    'primary_scan',
                    1,
                    'active',
                    @Now,
                    @Now
                );
                """,
                new
                {
                    DocumentId = documentId.ToString(),
                    ItemId = item.Value.ItemId.ToString(),
                    FileAssetId = fileAssetId.ToString(),
                    Now = now
                });

            return documentId;
        }

        public async Task InsertOcrRunAsync(DocumentInstanceId documentId)
        {
            string now = Clock.UtcNow.ToString("O");
            OcrPresetId presetId = OcrPresetId.New();
            OcrPresetVersionId presetVersionId = OcrPresetVersionId.New();
            OcrRunId runId = OcrRunId.New();
            LibraryMetadata library = (await Library.GetCurrentLibraryAsync()).Value;

            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into ocr_presets (
                    preset_id,
                    library_id,
                    name,
                    description,
                    archived,
                    current_version_id,
                    created_at,
                    updated_at
                )
                values (
                    @PresetId,
                    @LibraryId,
                    'Test',
                    'Test',
                    0,
                    @PresetVersionId,
                    @Now,
                    @Now
                );

                insert into ocr_preset_versions (
                    preset_version_id,
                    preset_id,
                    engine_id,
                    model_id,
                    parameters_json,
                    apply_on_success,
                    created_at
                )
                values (
                    @PresetVersionId,
                    @PresetId,
                    'mock',
                    'mock-default',
                    '{}',
                    0,
                    @Now
                );

                insert into ocr_runs (
                    ocr_run_id,
                    document_instance_id,
                    preset_id,
                    preset_version_id,
                    engine_id,
                    model_id,
                    parameters_snapshot_json,
                    state,
                    created_at,
                    updated_at
                )
                values (
                    @RunId,
                    @DocumentId,
                    @PresetId,
                    @PresetVersionId,
                    'mock',
                    'mock-default',
                    '{}',
                    @Pending,
                    @Now,
                    @Now
                );
                """,
                new
                {
                    PresetId = presetId.ToString(),
                    LibraryId = library.LibraryId.ToString(),
                    PresetVersionId = presetVersionId.ToString(),
                    RunId = runId.ToString(),
                    DocumentId = documentId.ToString(),
                    Now = now,
                    Pending = OcrRunState.Pending
                });
        }

        public async Task AttachDocumentBoxPayloadAsync(FileAssetId fileAssetId)
        {
            Result<ItemMetadata> item = await Items.CreateItemAsync("book", "Payload Holder");
            item.IsSuccess.Should().BeTrue();

            string now = Clock.UtcNow.ToString("O");
            DocumentInstanceId documentId = DocumentInstanceId.New();
            PageId pageId = PageId.New();
            DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
            DocumentBoxId boxId = DocumentBoxId.New();
            string payload = $"{{\"assetId\":\"{fileAssetId}\",\"description\":\"test\"}}";

            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into document_instances (
                    document_instance_id,
                    item_id,
                    file_asset_id,
                    instance_type,
                    is_primary,
                    status,
                    created_at,
                    updated_at
                )
                values (
                    @DocumentId,
                    @ItemId,
                    null,
                    'primary_scan',
                    1,
                    'active',
                    @Now,
                    @Now
                );

                insert into pages (
                    page_id,
                    document_instance_id,
                    page_index,
                    rotation,
                    coordinate_basis,
                    renderer_basis_version,
                    created_at,
                    updated_at
                )
                values (
                    @PageId,
                    @DocumentId,
                    0,
                    0,
                    'normalized_page',
                    'test',
                    @Now,
                    @Now
                );

                insert into document_tree_revisions (
                    tree_revision_id,
                    document_instance_id,
                    page_id,
                    source,
                    status,
                    is_current,
                    created_at,
                    committed_at
                )
                values (
                    @RevisionId,
                    @DocumentId,
                    @PageId,
                    'manual_edit',
                    'committed',
                    1,
                    @Now,
                    @Now
                );

                insert into document_boxes (
                    tree_revision_id,
                    box_id,
                    document_instance_id,
                    page_id,
                    box_type,
                    bbox_x,
                    bbox_y,
                    bbox_width,
                    bbox_height,
                    payload_json,
                    suppressed
                )
                values (
                    @RevisionId,
                    @BoxId,
                    @DocumentId,
                    @PageId,
                    'image',
                    0.1,
                    0.1,
                    0.8,
                    0.1,
                    @Payload,
                    0
                );
                """,
                new
                {
                    DocumentId = documentId.ToString(),
                    ItemId = item.Value.ItemId.ToString(),
                    PageId = pageId.ToString(),
                    RevisionId = revisionId.ToString(),
                    BoxId = boxId.ToString(),
                    Payload = payload,
                    Now = now
                });
        }

        public async Task PublishSnapshotAsync()
        {
            SnapshotPublisher publisher = new(Clock);
            Result<SnapshotPublishResult> result = await publisher.PublishSnapshotAsync(
                new SnapshotPublishRequest(Database.Path, SyncRoot, "test-device"));
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        }

        public async Task DetachDocumentInstanceAsync(DocumentInstanceId documentId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "delete from document_instances where document_instance_id = @DocumentId;",
                new { DocumentId = documentId.ToString() });
        }

        public async Task<int> CountAsync(string table)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>($"select count(1) from {table};");
        }
    }

    private sealed class TestBindingStore : ISnapshotSyncBindingStore
    {
        private readonly string _runtimeDatabasePath;
        private readonly string _syncRoot;

        public TestBindingStore(string runtimeDatabasePath, string syncRoot)
        {
            _runtimeDatabasePath = runtimeDatabasePath;
            _syncRoot = syncRoot;
        }

        public Task<Result<SnapshotSyncBinding>> GetBindingAsync(CancellationToken cancellationToken = default)
        {
            SnapshotSyncBinding binding = new(
                _runtimeDatabasePath,
                "test-root",
                _syncRoot,
                Path.Combine(Path.GetTempPath(), "staging"),
                "test-device",
                SnapshotSyncLocalState.NotConfigured,
                null,
                null);

            return Task.FromResult(Result<SnapshotSyncBinding>.Success(binding));
        }

        public Task<Result> SaveLocalStateAsync(SnapshotSyncLocalState state,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class FakeAppLogger : IAppLogger
    {
        public List<string> Logs { get; } = new();

        public Task LogAsync(string operation, string message)
        {
            Logs.Add($"{operation} {message}");
            return Task.CompletedTask;
        }
    }
}
