using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

public sealed class SnapshotBranchInspectionTests
{
    [Fact]
    public async Task OpenBranchForInspection_valid_manifest_returns_branch_info()
    {
        await using Ctx c = await Ctx.Create();
        byte[] before = await File.ReadAllBytesAsync(c.Db.Path);
        Result<SnapshotBranchInspectionInfo> branch = await c.Open();
        branch.Value.IsLibraryMatch.Should().BeTrue();
        File.Exists(branch.Value.StagingDatabasePath).Should().BeTrue();
        (await File.ReadAllBytesAsync(c.Db.Path)).Should().Equal(before);
    }

    [Fact]
    public async Task ListBranchItems_and_documents_return_summaries()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotBranchInspectionInfo b = (await c.Open()).Value;
        Result<IReadOnlyList<BranchItemSummary>> items = await c.Service.ListBranchItemsAsync(b);
        Result<IReadOnlyList<BranchDocumentInstanceSummary>> docs = await c.Service.ListBranchDocumentInstancesAsync(b);
        items.Value.Single().Title.Should().Be("Branch Item");
        items.Value.Single().DocumentInstanceCount.Should().Be(1);
        docs.Value.Single().PageCount.Should().Be(1);
        docs.Value.Single().TreeRevisionCount.Should().Be(1);
        docs.Value.Single().SearchUnitCount.Should().Be(1);
    }

    [Fact]
    public async Task Import_plan_includes_owning_dependencies_and_hides_secret()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotBranchInspectionInfo b = (await c.Open()).Value;
        Result<BranchImportPlan> p = await c.Service.BuildImportPlanAsync(b, [c.Item], []);
        p.Value.ItemsToImport.Should().Contain(c.Item);
        p.Value.DocumentInstancesToImport.Should().Contain(c.Doc);
        p.Value.PagesToImport.Should().Be(1);
        p.Value.TreeRevisionsToImport.Should().Be(1);
        p.Value.SearchUnitsToImport.Should().Be(1);
        System.Text.Json.JsonSerializer.Serialize(p.Value).Should().NotContain(c.Secret);
    }

    [Fact]
    public async Task Apply_requires_confirmation_then_imports_and_marks_stale()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotBranchInspectionInfo b = (await c.Open()).Value;
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync(b, [], [c.Doc])).Value;
        (await c.Service.ApplyImportPlanAsync(p, false)).ErrorCode.Should().Be("requires_confirmation");
        (await c.Count("items")).Should().Be(0);
        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(p, true);
        applied.IsSuccess.Should().BeTrue();
        (await c.Count("items")).Should().Be(1);
        (await c.Count("pages")).Should().Be(1);
        (await c.Count("search_units")).Should().Be(1);
        (await c.Status(c.Doc)).Should().Be("stale");
    }

    [Fact]
    public async Task Apply_imports_opted_in_library_settings_in_the_same_branch_transaction()
    {
        await using Ctx c = await Ctx.Create(true);
        SnapshotBranchInspectionInfo branch = (await c.Open()).Value;
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync(branch, [], [c.Doc])).Value;

        Result<BranchImportResult> applied = await c.Service.ApplyImportPlanAsync(
            plan,
            true,
            [LibrarySettingKeys.MetadataLookup]);

        applied.IsSuccess.Should().BeTrue(applied.ErrorMessage);
        (await c.Scalar<string>("select value_json from library_setting_records where setting_key = @Key;",
            new { Key = "metadata_lookup" })).Should().Be("{\"sources\":[]}");
    }

    [Fact]
    public async Task Conflict_blocks_overwrite_and_branch_actions_do_not_touch_active()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotBranchInspectionInfo b = (await c.Open()).Value;
        await c.InsertConflictingItem();
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync(b, [c.Item], [])).Value;
        p.Conflicts.Should().Contain(x => x.ConflictCode == ConflictCode.SameIdDifferentContent);
        Result<BranchImportResult> apply = await c.Service.ApplyImportPlanAsync(p, true);
        apply.ErrorCode.Should().Be("conflict_unresolved");
        apply.Conflicts.Should().Contain(x => x.ConflictCode == ConflictCode.SameIdDifferentContent);
        (await c.Title()).Should().Be("Existing");
        string copy = Path.Combine(c.Root, "copy.sqlite");
        (await c.Service.KeepBranchAsSeparateLibraryCopyAsync(b, copy)).IsSuccess.Should().BeTrue();
        File.Exists(copy).Should().BeTrue();
        (await c.Service.DiscardBranchAsync(b)).IsSuccess.Should().BeTrue();
        File.Exists(c.Db.Path).Should().BeTrue();
    }

    [Fact]
    public void MCP_has_no_branch_import_methods()
    {
        typeof(Mcp.IMcpReadApi).GetMethods().Select(x => x.Name).Should().NotContain(x =>
            x.Contains("branch", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("import", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenBranchForInspection_library_mismatch_returns_warning_and_blocks_plan()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotManifest? m = await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(c.Pub.ManifestPath, default);
        await SnapshotPublisher.WriteJsonAtomicAsync(c.Pub.ManifestPath,
            m! with { LibraryId = LibraryId.New().ToString() }, default);
        Result<SnapshotBranchInspectionInfo> b = await c.Open();
        b.Value.IsLibraryMatch.Should().BeFalse();
        b.Value.Warnings.Should().NotBeEmpty();
        (await c.Service.BuildImportPlanAsync(b.Value, [c.Item], [])).ErrorCode.Should().Be("library_mismatch");
    }

    [Fact]
    public async Task Plan_excludes_cache_paths_and_provider_secret()
    {
        await using Ctx c = await Ctx.Create();
        string cache = Path.Combine(c.Root, "staging", "cache", "page-renders", "secret.png");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllTextAsync(cache, "cache");
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        string json = System.Text.Json.JsonSerializer.Serialize(p);
        json.Should().NotContain(cache).And.NotContain(c.Secret);
    }

    [Fact]
    public async Task Apply_does_not_import_provider_secret_or_copy_original_file()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotBranchInspectionInfo b = (await c.Open()).Value;
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync(b, [c.Item], [])).Value;
        await c.Service.ApplyImportPlanAsync(p, true);
        File.Exists("/tmp/never-copy.pdf").Should().BeFalse();
        (await c.Count("file_assets")).Should().Be(1);
    }

    [Fact]
    public async Task Apply_imports_evidence_without_local_path()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotBranchInspectionInfo b = (await c.Open()).Value;
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync(b, [c.Item], [])).Value;
        await c.Service.ApplyImportPlanAsync(p, true);
        (await c.Count("evidence_ref_records")).Should().Be(1);
        string json = await c.EvidenceJson();
        json.Should().NotContain("/tmp").And.NotContain(c.Secret);
    }

    [Fact]
    public void Agent_prd_documents_branch_safety()
    {
        string prd = File.ReadAllText(TestPaths.FromRepositoryRoot(".agent", "PRD.md"));
        prd.Should().Contain("作为独立分支打开以供检查").And.Contain("v1 不执行自动对象级合并").And.Contain("不得在分支间静默执行最后写入者胜出").And
            .Contain("提供程序凭据");
    }

    [Fact]
    public async Task BuildImportPlan_selected_document_includes_owning_item()
    {
        await using Ctx c = await Ctx.Create();
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [], [c.Doc])).Value;
        p.ItemsToImport.Should().Contain(c.Item);
        p.DocumentInstancesToImport.Should().Contain(c.Doc);
    }

    [Fact]
    public async Task BuildImportPlan_detects_primary_document_conflict()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertExistingPrimary();
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        p.Conflicts.Should().Contain(x =>
            x.ConflictCode == ConflictCode.PrimaryDocumentConflict && x.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public async Task BuildImportPlan_rejects_same_document_id_with_different_content()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertConflictingDocumentIdentity();

        Result<BranchImportPlan> plan = await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], []);

        plan.ErrorCode.Should().Be("id_content_collision");
    }

    [Fact]
    public async Task ApplyImportPlan_marks_search_index_stale_for_imported_document()
    {
        await using Ctx c = await Ctx.Create();
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        await c.Service.ApplyImportPlanAsync(p, true);
        (await c.Status(c.Doc)).Should().Be("stale");
    }

    [Fact]
    public void MCP_does_not_expose_staging_or_manifest_paths()
    {
        Type[] types = typeof(Mcp.IMcpReadApi).Assembly.GetTypes();
        System.Text.Json.JsonSerializer.Serialize(types.Select(t => t.Name)).Should().NotContain("StagingDatabasePath")
            .And.NotContain("ManifestPath");
    }

    [Fact]
    public async Task Plan_result_does_not_include_cache_path()
    {
        await using Ctx c = await Ctx.Create();
        string path = Path.Combine(c.Root, "cache", "render.png");
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        System.Text.Json.JsonSerializer.Serialize(p).Should().NotContain(path);
    }

    [Fact]
    public async Task Apply_conflict_does_not_create_partial_documents()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertConflictingItem();
        BranchImportPlan p = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        await c.Service.ApplyImportPlanAsync(p, true);
        (await c.Count("document_instances")).Should().Be(0);
    }

    [Fact]
    public async Task Same_item_keep_local_removes_incoming_item_and_document_dependencies_from_plan()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertConflictingItem();
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        ConflictDescriptor conflict = plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.SameIdDifferentContent);

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("keep_local"))).Value;

        plan.ItemsToImport.Should().NotContain(c.Item);
        plan.DocumentInstancesToImport.Should().NotContain(c.Doc);
        (await c.Service.ApplyImportPlanAsync(plan, true)).IsSuccess.Should().BeTrue();
        (await c.Title()).Should().Be("Existing");
        (await c.Count("document_instances")).Should().Be(0);
    }

    [Fact]
    public async Task Same_item_import_as_new_remaps_the_item_without_rebinding_the_incoming_document()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertConflictingItem();
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        ConflictDescriptor conflict = plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.SameIdDifferentContent);

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("import_as_new_item"))).Value;

        plan.ItemIdRemappings.Should().ContainKey(c.Item.ToString());
        (await c.Service.ApplyImportPlanAsync(plan, true)).IsSuccess.Should().BeTrue();
        (await c.Count("items")).Should().Be(2);
        (await c.Count("document_instances")).Should().Be(1);
        (await c.Scalar<string>("select item_id from document_instances where document_instance_id = @Id",
            new { Id = c.Doc.ToString() })).Should().Be(plan.ItemIdRemappings[c.Item.ToString()]);
    }

    [Fact]
    public async Task Primary_document_resolution_can_import_incoming_as_secondary()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertExistingPrimary();
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        ConflictDescriptor conflict =
            plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.PrimaryDocumentConflict);

        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("keep_local_with_incoming_secondary"))).Value;

        (await c.Service.ApplyImportPlanAsync(plan, true)).IsSuccess.Should().BeTrue();
        (await c.Count("document_instances")).Should().Be(2);
        (await c.Scalar<long>("select count(*) from document_instances where item_id = @Id and is_primary = 1",
            new { Id = c.Item.ToString() })).Should().Be(1);
        (await c.Scalar<long>("select is_primary from document_instances where document_instance_id = @Id",
            new { Id = c.Doc.ToString() })).Should().Be(0);
    }

    [Fact]
    public async Task Resolved_plan_is_rejected_as_superseded_when_local_state_changes()
    {
        await using Ctx c = await Ctx.Create();
        await c.InsertConflictingItem();
        BranchImportPlan plan = (await c.Service.BuildImportPlanAsync((await c.Open()).Value, [c.Item], [])).Value;
        ConflictDescriptor conflict = plan.Conflicts.Single(x => x.ConflictCode == ConflictCode.SameIdDifferentContent);
        plan = (await c.Service.ResolveConflictAsync(plan, conflict.ConflictId!,
            new ConflictActionSelection("keep_local"))).Value;
        await c.UpdateItemTitle("Changed after resolution");

        Result<BranchImportResult> result = await c.Service.ApplyImportPlanAsync(plan, true);

        result.ErrorCode.Should().Be("plan_stale");
        result.Conflicts.Should().ContainSingle(x => x.ResolutionStatus == ConflictResolutionStatus.Superseded);
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(TemporarySqliteDatabase db, string root, SnapshotBranchInspectionService service,
            SnapshotPublishResult pub, ItemId item, DocumentInstanceId doc, string secret)
        {
            Db = db;
            Root = root;
            Service = service;
            Pub = pub;
            Item = item;
            Doc = doc;
            Secret = secret;
        }

        public TemporarySqliteDatabase Db { get; }
        public string Root { get; }
        public SnapshotBranchInspectionService Service { get; }
        public SnapshotPublishResult Pub { get; }
        public ItemId Item { get; }
        public DocumentInstanceId Doc { get; }
        public string Secret { get; }

        public static async Task<Ctx> Create(bool includeLibrarySetting = false)
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            string root = Directory
                .CreateDirectory(Path.Combine(Path.GetTempPath(), "branch-" + Guid.NewGuid().ToString("N"))).FullName;
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
            string secret = "branch-secret-123";
            await using (SqliteConnection cn = db.ConnectionFactory.CreateConnection())
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync(
                    "insert into items(item_id,library_id,item_type,title,creators_json,tags_json,collections_json,custom_fields_json,created_at,updated_at) values(@I,@L,'book','Branch Item','[]','[]','[]','{}',@N,@N);insert into item_identifiers(identifier_id,item_id,scheme,value,created_at) values(@X,@I,'DOI','10/test',@N);insert into file_assets(file_asset_id,library_id,original_path,file_name,size_bytes,status,created_at,updated_at) values(@F,@L,'/tmp/never-copy.pdf','never-copy.pdf',0,'missing',@N,@N);insert into document_instances(document_instance_id,item_id,file_asset_id,instance_type,is_primary,status,created_at,updated_at) values(@D,@I,@F,'primary_scan',1,'active',@N,@N);insert into pages(page_id,document_instance_id,page_index,rotation,coordinate_basis,renderer_basis_version,created_at,updated_at) values(@P,@D,0,0,'normalized_page','test',@N,@N);insert into document_tree_revisions(tree_revision_id,document_instance_id,page_id,source,status,is_current,created_at,committed_at) values(@R,@D,@P,'manual_edit','committed',1,@N,@N);insert into document_boxes(tree_revision_id,box_id,document_instance_id,page_id,box_type,bbox_x,bbox_y,bbox_width,bbox_height,payload_json,suppressed) values(@R,@O,@D,@P,'text',0.1,0.1,0.8,0.1,'{\"markdown\":\"branch text\"}',0);insert into search_units(unit_id,document_instance_id,page_id,box_id,tree_revision_id,resolved_text,bbox_json,box_type,ordinal,status,created_at,updated_at) values(@U,@D,@P,@O,@R,'branch text','{\"x\":0.1,\"y\":0.1,\"width\":0.8,\"height\":0.1}','text',1,'current',@N,@N);insert into evidence_ref_records(evidence_record_id,evidence_ref_id,library_id,document_instance_id,page_id,unit_id,tree_revision_id,box_id,pinned_text,source_title,page_index,status,created_at) values(@E,'evref:v2:test',@L,@D,@P,@U,@R,@O,'branch text','Branch Item',0,'active',@N);",
                    new
                    {
                        I = item.ToString(), L = lib.LibraryId.ToString(), N = now, X = IdentifierId.New().ToString(),
                        F = FileAssetId.New().ToString(), D = doc.ToString(), P = page.ToString(), R = rev.ToString(),
                        O = box.ToString(), U = unit.ToString(), E = EvidenceRefId.New().ToString(),
                        C = CredentialId.New().ToString(), S = secret
                    });
                if (includeLibrarySetting)
                {
                    await cn.ExecuteAsync(
                        "insert into library_setting_records(setting_key,schema_version,value_json,revision,updated_at,updated_by_device_id,merge_policy) values('metadata_lookup',1,'{\"sources\":[]}',1,@N,'device','scalar_replace');",
                        new { N = now });
                }
            }

            SnapshotPublishResult pub =
                (await new SnapshotPublisher(clock).PublishSnapshotAsync(
                    new SnapshotPublishRequest(
                        db.Path,
                        Path.Combine(root, "sync"),
                        "device",
                        EnabledSettingKeys: includeLibrarySetting ? [LibrarySettingKeys.MetadataLookup] : []))).Value;
            await using (SqliteConnection cn = db.ConnectionFactory.CreateConnection())
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync(
                    "delete from evidence_ref_records; delete from items; delete from search_index_status; delete from library_setting_records;");
            }

            return new Ctx(db, root,
                new SnapshotBranchInspectionService(new SnapshotImporter(), db.ConnectionFactory, libSvc), pub, item,
                doc, secret);
        }

        public Task<Result<SnapshotBranchInspectionInfo>> Open()
        {
            return Service.OpenBranchForInspectionAsync(Pub.ManifestPath, Path.Combine(Root, "staging"));
        }

        public async Task<int> Count(string t)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return await c.ExecuteScalarAsync<int>($"select count(*) from {t}");
        }

        public async Task<string?> Status(DocumentInstanceId d)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return await c.ExecuteScalarAsync<string?>("select status from search_index_status where scope_id=@D",
                new { D = d.ToString() });
        }

        public async Task<string> EvidenceJson()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return await c.ExecuteScalarAsync<string>(
                "select evidence_ref_id||pinned_text from evidence_ref_records limit 1") ?? "";
        }

        public async Task InsertConflictingItem()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "insert into items(item_id,library_id,item_type,title,creators_json,tags_json,collections_json,custom_fields_json,created_at,updated_at) select @I,library_id,'book','Existing','[]','[]','[]','{}',@N,@N from library_metadata",
                new { I = Item.ToString(), N = DateTimeOffset.UtcNow.ToString("O") });
        }

        public async Task InsertExistingPrimary()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "insert into items(item_id,library_id,item_type,title,creators_json,tags_json,collections_json,custom_fields_json,created_at,updated_at) select @I,library_id,'book','Branch Item','[]','[]','[]','{}',@N,@N from library_metadata;insert into document_instances(document_instance_id,item_id,instance_type,is_primary,status,created_at,updated_at) values(@D,@I,'primary_scan',1,'active',@N,@N)",
                new
                {
                    I = Item.ToString(), D = DocumentInstanceId.New().ToString(),
                    N = DateTimeOffset.UtcNow.ToString("O")
                });
        }

        public async Task InsertConflictingDocumentIdentity()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync(
                "insert into items(item_id,library_id,item_type,title,creators_json,tags_json,collections_json,custom_fields_json,created_at,updated_at) select @I,library_id,'book','Branch Item','[]','[]','[]','{}',@N,@N from library_metadata;insert into document_instances(document_instance_id,item_id,title,instance_type,is_primary,status,created_at,updated_at) values(@D,@I,'Different document','supplement',0,'active',@N,@N)",
                new { I = Item.ToString(), D = Doc.ToString(), N = DateTimeOffset.UtcNow.ToString("O") });
        }

        public async Task<string> Title()
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return (await c.ExecuteScalarAsync<string>("select title from items where item_id=@I",
                new { I = Item.ToString() }))!;
        }

        public async Task<T> Scalar<T>(string sql, object parameters)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            return (await c.ExecuteScalarAsync<T>(sql, parameters))!;
        }

        public async Task UpdateItemTitle(string title)
        {
            await using SqliteConnection c = Db.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync("update items set title = @Title where item_id = @ItemId;",
                new { Title = title, ItemId = Item.ToString() });
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }
}
