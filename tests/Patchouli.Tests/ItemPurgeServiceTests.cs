using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.Core.Search;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class ItemPurgeServiceTests
{
    [Fact]
    public async Task Purge_deletes_payload_and_versioned_evidence_resolves_to_not_found()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        string versionedUri = await c.CreateVersionedEvidenceUriAsync(itemId);

        Result<EvidencePageText> before = await c.Evidence.GetBoxTextAsync(
            c.DocumentInstanceId, 1, c.TreeRevisionId, c.BoxId);
        before.IsSuccess.Should().BeTrue();
        before.Value.Markdown.Should().Contain("sample");

        Result result = await c.Purge.PurgeItemsAsync([itemId]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        (await c.Count("items")).Should().Be(0);
        (await c.Count("document_instances")).Should().Be(0);
        (await c.Count("pages")).Should().Be(0);
        (await c.Count("document_tree_revisions")).Should().Be(0);
        (await c.Count("document_boxes")).Should().Be(0);
        (await c.Count("search_units")).Should().Be(0);
        (await c.Count("search_units_fts")).Should().Be(0);
        (await c.Count("item_purge_records")).Should().Be(1);

        Result<EvidencePageText> after = await c.Evidence.GetBoxTextAsync(
            c.DocumentInstanceId, 1, c.TreeRevisionId, c.BoxId);
        after.IsFailure.Should().BeTrue();
        after.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        File.Exists(c.OriginalFilePath).Should().BeTrue();
        (await c.Count("file_assets")).Should().Be(1);
    }

    [Fact]
    public async Task Purge_empties_trash_and_blocks_active_ocr()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.InsertActiveOcrRunAsync(itemId);

        Result blocked = await c.Purge.PurgeItemsAsync([itemId]);

        blocked.IsFailure.Should().BeTrue();
        blocked.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Purge_increments_revision_once()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        long before = await c.RevisionAsync();
        int eventCount = 0;
        c.Revisions.ChangeCommitted += (_, _) => eventCount++;

        Result result = await c.Purge.PurgeItemsAsync([itemId]);

        result.IsSuccess.Should().BeTrue();
        (await c.RevisionAsync()).Should().Be(before + 1);
        eventCount.Should().Be(1);
    }

    [Fact]
    public async Task BuildReport_reflects_dependencies_without_evidence_count()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.CreateVersionedEvidenceUriAsync(itemId);

        Result<ItemPurgeDependencyReport> report = await c.Purge.BuildPurgeReportAsync(itemId);

        report.IsSuccess.Should().BeTrue(report.ErrorMessage);
        report.Value.ItemId.Should().Be(itemId);
        report.Value.HasActiveOcr.Should().BeFalse();
        report.Value.HasOcrCandidates.Should().BeFalse();
        report.Value.HasWorking.Should().BeFalse();
    }

    [Fact]
    public async Task BuildReport_blocks_when_working_revision_exists()
    {
        await using Ctx c = await Ctx.Create();
        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.BeginWorkingRevisionAsync(itemId);

        Result<ItemPurgeDependencyReport> report = await c.Purge.BuildPurgeReportAsync(itemId);

        report.IsSuccess.Should().BeTrue();
        report.Value.HasWorking.Should().BeTrue();
    }

    [Fact]
    public async Task Purge_rejects_active_items_and_clears_item_satellite_tables()
    {
        await using Ctx c = await Ctx.Create();
        Result<ItemMetadata> active = await c.Items.CreateItemAsync("book", "Still Active");
        Result blocked = await c.Purge.PurgeItemsAsync([active.Value.ItemId]);
        blocked.IsFailure.Should().BeTrue();
        blocked.ErrorCode.Should().Be(AppErrorCodes.NotFound);

        ItemId itemId = await c.InsertTrashedItemWithPayloadAsync();
        await c.Items.AddIdentifierAsync(itemId, "doi", "10.1/purge", null);
        Result purged = await c.Purge.PurgeItemsAsync([itemId]);
        purged.IsSuccess.Should().BeTrue(purged.ErrorMessage);

        (await c.CountForItem("item_identifiers", itemId)).Should().Be(0);
        (await c.CountForItem("item_creators", itemId)).Should().Be(0);
        (await c.CountForItem("item_dates", itemId)).Should().Be(0);
        (await c.Count("items")).Should().Be(1);
        (await c.Count("item_purge_records")).Should().Be(1);
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(
            TemporarySqliteDatabase database,
            FixedClock clock,
            LibraryIdentityService library,
            LibraryRevisionService revisions,
            ItemService items,
            IVersionedEvidenceReader evidence,
            ItemPurgeService purge,
            string originalFilePath)
        {
            Database = database;
            Clock = clock;
            Library = library;
            Revisions = revisions;
            Items = items;
            Evidence = evidence;
            Purge = purge;
            OriginalFilePath = originalFilePath;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public LibraryRevisionService Revisions { get; }
        public ItemService Items { get; }
        public IVersionedEvidenceReader Evidence { get; }
        public ItemPurgeService Purge { get; }
        public string OriginalFilePath { get; }
        public DocumentInstanceId DocumentInstanceId { get; private set; }
        public PageId PageId { get; private set; }
        public DocumentTreeRevisionId TreeRevisionId { get; private set; }
        public DocumentBoxId BoxId { get; private set; }

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            LibraryRevisionService revisions = new(database.ConnectionFactory);
            await library.CreateLibraryAsync("Purge Test");
            ItemService items = new(database.ConnectionFactory, library, clock, revisions);
            DocumentTreeService trees = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
            IVersionedEvidenceReader evidence = new VersionedEvidenceReader(
                database.ConnectionFactory,
                library,
                trees,
                new DocumentMarkdownCompiler(trees, new MarkdigMarkdownEngine()));
            ItemPurgeService purge = new(database.ConnectionFactory, clock, library, revisions: revisions);
            string originalFilePath = Path.Combine(Path.GetTempPath(), $"purge-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(originalFilePath, "original");
            return new Ctx(database, clock, library, revisions, items, evidence, purge, originalFilePath);
        }

        public async ValueTask DisposeAsync()
        {
            if (File.Exists(OriginalFilePath))
            {
                File.Delete(OriginalFilePath);
            }

            await Database.DisposeAsync();
        }

        public async Task<ItemId> InsertTrashedItemWithPayloadAsync()
        {
            Result<ItemMetadata> item = await Items.CreateItemAsync("book", "Purge Me");
            item.IsSuccess.Should().BeTrue();
            await Items.DeleteItemAsync(item.Value.ItemId);

            LibraryMetadata library = (await Library.GetCurrentLibraryAsync()).Value;
            string now = Clock.UtcNow.ToString("O");
            FileAssetId fileAssetId = FileAssetId.New();
            DocumentInstanceId documentId = DocumentInstanceId.New();
            PageId pageId = PageId.New();
            DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
            DocumentBoxId boxId = DocumentBoxId.New();
            SearchUnitId unitId = SearchUnitId.New();

            DocumentInstanceId = documentId;
            PageId = pageId;
            TreeRevisionId = revisionId;
            BoxId = boxId;

            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at)
                values (@FileAssetId, @LibraryId, @OriginalPath, 'original.txt', 8, 'available', @Now, @Now);

                insert into document_instances (document_instance_id, item_id, file_asset_id, instance_type, is_primary, status, created_at, updated_at)
                values (@DocumentId, @ItemId, @FileAssetId, 'primary_scan', 1, 'active', @Now, @Now);

                insert into pages (page_id, document_instance_id, page_index, rotation, coordinate_basis, renderer_basis_version, created_at, updated_at)
                values (@PageId, @DocumentId, 0, 0, 'normalized_page', 'test', @Now, @Now);

                insert into document_tree_revisions (tree_revision_id, document_instance_id, page_id, source, status, is_current, created_at, committed_at)
                values (@RevisionId, @DocumentId, @PageId, 'manual_edit', 'committed', 1, @Now, @Now);

                insert into document_boxes (tree_revision_id, box_id, document_instance_id, page_id, box_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json, suppressed)
                values (@RevisionId, @BoxId, @DocumentId, @PageId, 'text', 0.1, 0.1, 0.8, 0.1, '{"markdown":"sample"}', 0);

                insert into search_units (unit_id, document_instance_id, page_id, box_id, tree_revision_id, resolved_text, bbox_json, box_type, ordinal, status, created_at, updated_at)
                values (@UnitId, @DocumentId, @PageId, @BoxId, @RevisionId, 'sample text', '{"x":0.1,"y":0.1,"width":0.8,"height":0.1}', 'text', 1, @Current, @Now, @Now);

                insert into search_units_fts (unit_id, document_instance_id, page_id, resolved_text)
                values (@UnitId, @DocumentId, @PageId, 'sample text');
                """,
                new
                {
                    FileAssetId = fileAssetId.ToString(),
                    LibraryId = library.LibraryId.ToString(),
                    OriginalPath = OriginalFilePath,
                    DocumentId = documentId.ToString(),
                    ItemId = item.Value.ItemId.ToString(),
                    PageId = pageId.ToString(),
                    RevisionId = revisionId.ToString(),
                    BoxId = boxId.ToString(),
                    UnitId = unitId.ToString(),
                    Now = now,
                    Current = SearchUnitStatus.Current
                });

            return item.Value.ItemId;
        }

        public async Task<string> CreateVersionedEvidenceUriAsync(ItemId itemId)
        {
            _ = itemId;
            return $"patchouli://texts/{DocumentInstanceId}/page-1.md?rev={TreeRevisionId}&box={BoxId}";
        }

        public async Task BeginWorkingRevisionAsync(ItemId itemId)
        {
            _ = itemId;
            DocumentTreeService trees = new(Database.ConnectionFactory, Clock, new MarkdigMarkdownEngine());
            await trees.BeginWorkingRevisionAsync(
                DocumentInstanceId,
                PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("working draft"))
                ],
                DocumentTreeRevisionSource.ManualEdit);
        }

        public async Task InsertActiveOcrRunAsync(ItemId itemId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            string now = Clock.UtcNow.ToString("O");
            string? documentId = await connection.ExecuteScalarAsync<string>(
                "select document_instance_id from document_instances where item_id = @ItemId;",
                new { ItemId = itemId.ToString() });
            string presetId = OcrPresetId.New().ToString();
            string presetVersionId = OcrPresetVersionId.New().ToString();
            await connection.ExecuteAsync(
                """
                insert into ocr_presets (preset_id, library_id, name, description, archived, current_version_id, created_at, updated_at)
                values (@PresetId, @LibraryId, 'Test', 'Test', 0, @PresetVersionId, @Now, @Now);

                insert into ocr_preset_versions (preset_version_id, preset_id, engine_id, model_id, parameters_json, apply_on_success, created_at)
                values (@PresetVersionId, @PresetId, 'mock', 'mock-default', '{}', 0, @Now);

                insert into ocr_runs (ocr_run_id, document_instance_id, preset_id, preset_version_id, engine_id, model_id, parameters_snapshot_json, state, created_at, updated_at)
                values (@RunId, @DocumentId, @PresetId, @PresetVersionId, 'mock', 'mock-default', '{}', @Pending, @Now, @Now);
                """,
                new
                {
                    PresetId = presetId,
                    LibraryId = (await Library.GetCurrentLibraryAsync()).Value.LibraryId.ToString(),
                    PresetVersionId = presetVersionId,
                    RunId = OcrRunId.New().ToString(),
                    DocumentId = documentId,
                    Now = now,
                    Pending = OcrRunState.Pending
                });
        }

        public async Task<int> Count(string table)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>($"select count(1) from {table}");
        }

        public async Task<int> CountForItem(string table, ItemId itemId)
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(
                $"select count(1) from {table} where item_id = @ItemId;",
                new { ItemId = itemId.ToString() });
        }

        public async Task<long> RevisionAsync()
        {
            await using SqliteConnection connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<long>("select library_revision from library_metadata limit 1;");
        }
    }
}
