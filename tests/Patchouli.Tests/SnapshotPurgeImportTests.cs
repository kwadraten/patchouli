using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

public sealed class SnapshotPurgeImportTests
{
    [Fact]
    public async Task Purged_item_is_imported_as_new_identity_from_existing_snapshot()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotPublishResult pub = await c.PublishSnapshotAsync();
        await c.SoftDeleteAndPurgeItemAsync();

        SnapshotBranchInspectionInfo branch = (await c.BranchInspection.OpenBranchForInspectionAsync(
            pub.ManifestPath,
            Path.Combine(c.Root, "staging"))).Value;

        BranchImportPlan plan = (await c.BranchInspection.BuildImportPlanAsync(branch, [c.Item], [])).Value;
        plan.ItemIdRemappings.Should().ContainKey(c.Item.ToString());

        Result<BranchImportResult> applied = await c.BranchInspection.ApplyImportPlanAsync(plan, true);
        applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
        (await c.Count("items")).Should().Be(1);
        (await c.Count("document_instances")).Should().Be(1);
        (await c.Count("item_purge_records")).Should().Be(1);

        string importedItemId = await c.Scalar<string>(
            "select item_id from document_instances where document_instance_id = @Id;",
            new { Id = c.Doc.ToString() });
        importedItemId.Should().Be(plan.ItemIdRemappings[c.Item.ToString()]);
        importedItemId.Should().NotBe(c.Item.ToString());
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(
            TemporarySqliteDatabase db,
            string root,
            FixedClock clock,
            LibraryIdentityService library,
            LibraryRevisionService revisions,
            ItemService items,
            ItemPurgeService purge,
            SnapshotBranchInspectionService branchInspection,
            ItemId item,
            DocumentInstanceId doc)
        {
            Db = db;
            Root = root;
            Clock = clock;
            Library = library;
            Revisions = revisions;
            Items = items;
            Purge = purge;
            BranchInspection = branchInspection;
            Item = item;
            Doc = doc;
        }

        public TemporarySqliteDatabase Db { get; }
        public string Root { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public LibraryRevisionService Revisions { get; }
        public ItemService Items { get; }
        public ItemPurgeService Purge { get; }
        public SnapshotBranchInspectionService BranchInspection { get; }
        public ItemId Item { get; }
        public DocumentInstanceId Doc { get; }

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            string root = Directory
                .CreateDirectory(Path.Combine(Path.GetTempPath(), "purge-import-" + Guid.NewGuid().ToString("N")))
                .FullName;
            FixedClock clock = new(DateTimeOffset.UtcNow);
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            LibraryRevisionService revisions = new(db.ConnectionFactory);
            LibraryMetadata lib = (await library.CreateLibraryAsync("Purge Import Test")).Value;
            ItemService items = new(db.ConnectionFactory, library, clock, revisions);
            ItemPurgeService purge = new(db.ConnectionFactory, clock, library, revisions: revisions);
            SnapshotBranchInspectionService branchInspection =
                new(new SnapshotImporter(), db.ConnectionFactory, library);

            ItemId item = ItemId.New();
            DocumentInstanceId doc = DocumentInstanceId.New();
            PageId page = PageId.New();
            DocumentTreeRevisionId rev = DocumentTreeRevisionId.New();
            DocumentBoxId box = DocumentBoxId.New();
            SearchUnitId unit = SearchUnitId.New();
            FileAssetId fileAsset = FileAssetId.New();
            string now = clock.UtcNow.ToString("O");
            string originalPath = Path.Combine(root, "original.pdf");
            await File.WriteAllTextAsync(originalPath, "original");

            await using (SqliteConnection cn = db.ConnectionFactory.CreateConnection())
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync(
                    """
                    insert into items (item_id, library_id, item_type, title, creators_json, tags_json, collections_json, custom_fields_json, created_at, updated_at)
                    values (@I, @L, 'book', 'Purge Import Item', '[]', '[]', '[]', '{}', @N, @N);

                    insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at)
                    values (@F, @L, @OriginalPath, 'original.pdf', 8, 'available', @N, @N);

                    insert into document_instances (document_instance_id, item_id, file_asset_id, instance_type, is_primary, status, created_at, updated_at)
                    values (@D, @I, @F, 'primary_scan', 1, 'active', @N, @N);

                    insert into pages (page_id, document_instance_id, page_index, rotation, coordinate_basis, renderer_basis_version, created_at, updated_at)
                    values (@P, @D, 0, 0, 'normalized_page', 'test', @N, @N);

                    insert into document_tree_revisions (tree_revision_id, document_instance_id, page_id, source, status, is_current, created_at, committed_at)
                    values (@R, @D, @P, 'manual_edit', 'committed', 1, @N, @N);

                    insert into document_boxes (tree_revision_id, box_id, document_instance_id, page_id, box_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json, suppressed)
                    values (@R, @O, @D, @P, 'text', 0.1, 0.1, 0.8, 0.1, '{"markdown":"text"}', 0);

                    insert into search_units (unit_id, document_instance_id, page_id, box_id, tree_revision_id, resolved_text, bbox_json, box_type, ordinal, status, created_at, updated_at)
                    values (@U, @D, @P, @O, @R, 'text', '{"x":0.1,"y":0.1,"width":0.8,"height":0.1}', 'text', 1, 'current', @N, @N);

                    insert into search_units_fts (unit_id, document_instance_id, page_id, resolved_text)
                    values (@U, @D, @P, 'text');
                    """,
                    new
                    {
                        I = item.ToString(),
                        L = lib.LibraryId.ToString(),
                        F = fileAsset.ToString(),
                        OriginalPath = originalPath,
                        D = doc.ToString(),
                        P = page.ToString(),
                        R = rev.ToString(),
                        O = box.ToString(),
                        U = unit.ToString(),
                        N = now
                    });
            }

            return new Ctx(db, root, clock, library, revisions, items, purge, branchInspection, item, doc);
        }

        public async Task<SnapshotPublishResult> PublishSnapshotAsync()
        {
            SnapshotPublisher publisher = new(Clock);
            Result<SnapshotPublishResult> result = await publisher.PublishSnapshotAsync(
                new SnapshotPublishRequest(Db.Path, Path.Combine(Root, "sync"), "device"));
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            return result.Value;
        }

        public async Task SoftDeleteAndPurgeItemAsync()
        {
            Result deleted = await Items.DeleteItemAsync(Item);
            deleted.IsSuccess.Should().BeTrue();
            Result purged = await Purge.PurgeItemsAsync([Item]);
            purged.IsSuccess.Should().BeTrue(purged.ErrorMessage);
        }

        public async Task<int> Count(string table)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return await c.ExecuteScalarAsync<int>($"select count(*) from {table}");
        }

        public async Task<T> Scalar<T>(string sql, object parameters)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return (await c.ExecuteScalarAsync<T>(sql, parameters))!;
        }

        public async ValueTask DisposeAsync()
        {
            SqliteTestCleanup.ReleasePoolsInDirectory(Root);
            await Db.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
