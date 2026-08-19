using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

public sealed class SnapshotItemLevelConflictTests
{
    [Fact]
    public async Task Local_trash_vs_incoming_active_generates_CF08_keep_local_preserves_trash()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertLocalItem(c.Clock.UtcNow.ToString("O"));
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;

        ConflictDescriptor conflict = plan.Conflicts.Should()
            .ContainSingle(x => x.ConflictCode == ConflictCode.ItemLevelBranch).Subject;

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("resolve_item_branch", "keep_local_item"))).Value;
        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(plan, true);
        if (!applied.IsSuccess)
        {
            throw new InvalidOperationException(
                $"ApplyImportPlanAsync failed: {applied.ErrorCode} {applied.ErrorMessage}");
        }

        (await c.LocalDeletedAt()).Should().NotBeNullOrEmpty();
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Local_trash_vs_incoming_active_use_incoming_restores_active()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertLocalItem(c.Clock.UtcNow.ToString("O"));
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        ConflictDescriptor conflict = plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.ItemLevelBranch);

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("resolve_item_branch", "use_incoming_item"))).Value;
        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(plan, true);
        applied.IsSuccess.Should().BeTrue();

        (await c.LocalDeletedAt()).Should().BeNullOrEmpty();
        (await c.LocalMergedInto()).Should().BeNullOrEmpty();
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Local_active_vs_incoming_trash_use_incoming_soft_deletes_local()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertLocalItem();
        SnapshotBranchInspectionInfo branch = (await c.Open()).Value;
        await c.SetIncomingDeleted(branch);
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync(branch, [c.Item], [])).Value;
        ConflictDescriptor conflict = plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.ItemLevelBranch);

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("resolve_item_branch", "use_incoming_item"))).Value;
        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(plan, true);
        if (!applied.IsSuccess)
        {
            throw new InvalidOperationException(
                $"ApplyImportPlanAsync failed: {applied.ErrorCode} {applied.ErrorMessage}");
        }

        (await c.LocalDeletedAt()).Should().NotBeNullOrEmpty();
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Local_merged_vs_incoming_active_keep_local_preserves_merge_redirect()
    {
        await using Ctx c = await Ctx.Create();
        ItemId targetId = ItemId.New();
        await c.InsertLocalItem(mergedIntoItemId: targetId.ToString());
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        ConflictDescriptor conflict = plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.ItemLevelBranch);

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("resolve_item_branch", "keep_local_item"))).Value;
        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(plan, true);
        applied.IsSuccess.Should().BeTrue();

        (await c.LocalMergedInto()).Should().Be(targetId.ToString());
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Local_merged_vs_incoming_active_use_incoming_restores_active()
    {
        await using Ctx c = await Ctx.Create();
        ItemId targetId = ItemId.New();
        await c.InsertLocalItem(mergedIntoItemId: targetId.ToString());
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        ConflictDescriptor conflict = plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.ItemLevelBranch);

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("resolve_item_branch", "use_incoming_item"))).Value;
        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(plan, true);
        applied.IsSuccess.Should().BeTrue();

        (await c.LocalMergedInto()).Should().BeNullOrEmpty();
        (await c.LocalDeletedAt()).Should().BeNullOrEmpty();
        (await c.Count("items")).Should().Be(1);
    }

    [Fact]
    public async Task Local_purged_vs_incoming_active_imports_as_new_item_without_conflict()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertLocalItem();
        await c.PurgeLocalItem();
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;

        plan.Conflicts.Should().NotContain(x => x.ConflictCode == ConflictCode.ItemLevelBranch);
        plan.ItemIdRemappings.Should().ContainKey(c.Item.ToString());

        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(plan, true);
        applied.IsSuccess.Should().BeTrue();
        (await c.Count("items")).Should().Be(1);
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(TemporarySqliteDatabase db, string root, SnapshotBranchInspectionService service,
            SnapshotPublishResult pub, ItemId item, DocumentInstanceId doc, FixedClock clock)
        {
            Db = db;
            Root = root;
            Service = service;
            Pub = pub;
            Item = item;
            Doc = doc;
            Clock = clock;
        }

        public TemporarySqliteDatabase Db { get; }
        public string Root { get; }
        public SnapshotBranchInspectionService Service { get; }
        public SnapshotPublishResult Pub { get; }
        public ItemId Item { get; }
        public DocumentInstanceId Doc { get; }
        public FixedClock Clock { get; }

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            string root = Directory
                .CreateDirectory(Path.Combine(Path.GetTempPath(), "branch-item-" + Guid.NewGuid().ToString("N")))
                .FullName;
            FixedClock clock = new(DateTimeOffset.UtcNow);
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libSvc = new(db.ConnectionFactory, clock);
            LibraryMetadata lib = (await libSvc.CreateLibraryAsync("L")).Value;
            ItemId item = ItemId.New();
            DocumentInstanceId doc = DocumentInstanceId.New();
            PageId page = PageId.New();
            DocumentTreeRevisionId rev = DocumentTreeRevisionId.New();
            DocumentBoxId box = DocumentBoxId.New();
            SearchUnitId unit = SearchUnitId.New();
            string now = clock.UtcNow.ToString("O");
            await using (SqliteConnection cn = db.ConnectionFactory.CreateConnection())
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync(
                    """
                    insert into items(item_id,library_id,item_type,title,creators_json,tags_json,collections_json,custom_fields_json,created_at,updated_at)
                    values(@I,@L,'book','Branch Item','[]','[]','[]','{}',@N,@N);
                    insert into file_assets(file_asset_id,library_id,original_path,file_name,size_bytes,status,created_at,updated_at)
                    values(@F,@L,'/tmp/never-copy.pdf','never-copy.pdf',0,'missing',@N,@N);
                    insert into document_instances(document_instance_id,item_id,file_asset_id,instance_type,is_primary,status,created_at,updated_at)
                    values(@D,@I,@F,'primary_scan',1,'active',@N,@N);
                    insert into pages(page_id,document_instance_id,page_index,rotation,coordinate_basis,renderer_basis_version,created_at,updated_at)
                    values(@P,@D,0,0,'normalized_page','test',@N,@N);
                    insert into document_tree_revisions(tree_revision_id,document_instance_id,page_id,source,status,is_current,created_at,committed_at)
                    values(@R,@D,@P,'manual_edit','committed',1,@N,@N);
                    insert into document_boxes(tree_revision_id,box_id,document_instance_id,page_id,box_type,bbox_x,bbox_y,bbox_width,bbox_height,payload_json,suppressed)
                    values(@R,@O,@D,@P,'text',0.1,0.1,0.8,0.1,'{"markdown":"branch text"}',0);
                    insert into search_units(unit_id,document_instance_id,page_id,box_id,tree_revision_id,resolved_text,bbox_json,box_type,ordinal,status,created_at,updated_at)
                    values(@U,@D,@P,@O,@R,'branch text','{"x":0.1,"y":0.1,"width":0.8,"height":0.1}','text',1,'current',@N,@N);
                    """,
                    new
                    {
                        I = item.ToString(), L = lib.LibraryId.ToString(), N = now,
                        F = FileAssetId.New().ToString(), D = doc.ToString(), P = page.ToString(), R = rev.ToString(),
                        O = box.ToString(), U = unit.ToString()
                    });
            }

            SnapshotPublishResult pub =
                (await new SnapshotPublisher(clock).PublishSnapshotAsync(
                    new SnapshotPublishRequest(db.Path, Path.Combine(root, "sync"), "device"))).Value;
            await using (SqliteConnection cn = db.ConnectionFactory.CreateConnection())
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync(
                    "delete from search_units; delete from document_boxes; delete from document_tree_revisions; delete from pages; delete from document_instances; delete from file_assets; delete from items; delete from search_index_status;");
            }

            return new Ctx(db, root,
                new SnapshotBranchInspectionService(new SnapshotImporter(), db.ConnectionFactory, libSvc), pub, item,
                doc, clock);
        }

        public Task<Result<SnapshotBranchInspectionInfo>> Open()
        {
            return Service.OpenBranchForInspectionAsync(Pub.ManifestPath, Path.Combine(Root, "staging"));
        }

        public async Task InsertLocalItem(string? deletedAt = null, string? mergedIntoItemId = null)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync(
                """
                insert into items(item_id,library_id,item_type,title,creators_json,tags_json,collections_json,custom_fields_json,created_at,updated_at,deleted_at,merged_into_item_id)
                select @I,library_id,'book','Branch Item','[]','[]','[]','{}',@N,@N,@D,@M from library_metadata;
                """,
                new
                {
                    I = Item.ToString(),
                    N = DateTimeOffset.UtcNow.ToString("O"),
                    D = deletedAt,
                    M = mergedIntoItemId
                });
        }

        public async Task SetIncomingDeleted(SnapshotBranchInspectionInfo branch)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "attach database @Path as branch; update branch.items set deleted_at = @Now where item_id = @I; detach database branch;",
                new
                {
                    Path = branch.StagingDatabasePath, Now = DateTimeOffset.UtcNow.ToString("O"), I = Item.ToString()
                });
        }

        public async Task PurgeLocalItem()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "delete from items where item_id = @I; insert into item_purge_records(item_id,purged_at,purge_reason) values(@I,@Now,'test_purge');",
                new { I = Item.ToString(), Now = DateTimeOffset.UtcNow.ToString("O") });
        }

        public async Task<string?> LocalDeletedAt()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return await c.ExecuteScalarAsync<string?>("select deleted_at from items where item_id=@I",
                new { I = Item.ToString() });
        }

        public async Task<string?> LocalMergedInto()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return await c.ExecuteScalarAsync<string?>("select merged_into_item_id from items where item_id=@I",
                new { I = Item.ToString() });
        }

        public async Task<int> Count(string t)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return await c.ExecuteScalarAsync<int>("select count(*) from " + t);
        }

        public async ValueTask DisposeAsync()
        {
            SqliteTestCleanup.ReleasePoolsInDirectory(Root);
            await Db.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }
}
