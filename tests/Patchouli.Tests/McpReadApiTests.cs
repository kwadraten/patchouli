using System.Reflection;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class McpReadApiTests
{
    [Fact] public async Task SearchLibrary_returns_evidence_refs_for_matched_units() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle text"); var r = await c.Api.SearchLibraryAsync(new McpSearchLibraryRequest("needle")); r.Value.Results.Single().MatchedUnits.Single().EvidenceRef.Should().StartWith("evref:v1:"); }
    [Fact] public async Task SearchLibrary_does_not_return_local_paths() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle text", "/Users/alice/private/source.pdf"); var json = JsonSerializer.Serialize((await c.Api.SearchLibraryAsync(new McpSearchLibraryRequest("needle"))).Value); json.Should().NotContain("/Users/alice/private/source.pdf").And.NotContain("source.pdf"); }
    [Fact] public async Task SearchLibrary_returns_index_status() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle text"); var r = await c.Api.SearchLibraryAsync(new McpSearchLibraryRequest("needle")); r.Value.IndexStatus.Should().Be(SearchIndexStatusValue.Current); }
    [Fact] public async Task MCP_search_library_can_include_rewrite_plan_without_paths_or_secrets() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle text"); var r = await c.Api.SearchLibraryAsync(new McpSearchLibraryRequest("needle", IncludeRewritePlan: true)); r.Value.RewritePlan.Should().NotBeNull(); var json=JsonSerializer.Serialize(r.Value.RewritePlan); json.Should().NotContain("/Users/").And.NotContain("secret"); }
    [Fact] public async Task SearchLibrary_does_not_trigger_index_rebuild() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle text"); var before = await c.FtsCountAsync(); await c.Api.SearchLibraryAsync(new McpSearchLibraryRequest("needle")); (await c.FtsCountAsync()).Should().Be(before); }
    [Fact] public async Task SearchLibrary_handles_evidence_creation_failure_without_path_leak() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle text", "/Users/alice/private/source.pdf", failingEvidence: true); var r = await c.Api.SearchLibraryAsync(new McpSearchLibraryRequest("needle")); r.Value.Results.Single().MatchedUnits.Single().EvidenceRef.Should().BeNull(); JsonSerializer.Serialize(r.Value).Should().NotContain("/Users/alice/private/source.pdf"); r.Value.Warning.Should().Contain("Evidence ref unavailable"); }
    [Fact] public async Task GetItemMetadata_returns_bibliographic_metadata() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text"); var r = await c.Api.GetItemMetadataAsync(c.ItemId); r.Value.Title.Should().Be("MCP Item"); r.Value.ItemType.Should().Be("book"); }
    [Fact] public async Task GetItemMetadata_returns_identifiers() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text"); await c.ItemService.AddIdentifierAsync(c.ItemId, BuiltInIdentifierSchemes.DOI, "10.1/test", null); var r = await c.Api.GetItemMetadataAsync(c.ItemId); r.Value.Identifiers.Single().Value.Should().Be("10.1/test"); }
    [Fact] public async Task GetItemMetadata_does_not_return_file_paths() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text", "/Users/alice/private/source.pdf"); var json = JsonSerializer.Serialize((await c.Api.GetItemMetadataAsync(c.ItemId)).Value); json.Should().NotContain("/Users/alice/private/source.pdf").And.NotContain("source.pdf"); }
    [Fact] public async Task GetDocumentStatus_returns_has_current_layout() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text"); (await c.Api.GetDocumentStatusAsync(c.DocumentInstanceId)).Value.HasCurrentLayout.Should().BeTrue(); }
    [Fact] public async Task GetDocumentStatus_returns_is_search_indexed() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text"); (await c.Api.GetDocumentStatusAsync(c.DocumentInstanceId)).Value.IsSearchIndexed.Should().BeTrue(); }
    [Theory] [InlineData(FileAssetStatus.Available, McpSourceFileStatus.Available)] [InlineData(FileAssetStatus.Missing, McpSourceFileStatus.Missing)] [InlineData(FileAssetStatus.Changed, McpSourceFileStatus.Changed)] [InlineData(FileAssetStatus.Conflict, McpSourceFileStatus.Conflict)] [InlineData(FileAssetStatus.OfflineRoot, McpSourceFileStatus.OfflineRoot)] public async Task GetDocumentStatus_maps_available_missing_changed_conflict_offline_root(string internalStatus, string exposed) { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text"); await c.SetFileStatusAsync(internalStatus); (await c.Api.GetDocumentStatusAsync(c.DocumentInstanceId)).Value.SourceFileStatus.Should().Be(exposed); }
    [Fact] public async Task GetDocumentStatus_maps_moved_candidate_to_unknown_without_path() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text", "/Users/alice/private/source.pdf"); await c.SetFileStatusAsync(FileAssetStatus.MovedCandidate); var r = await c.Api.GetDocumentStatusAsync(c.DocumentInstanceId); r.Value.SourceFileStatus.Should().Be(McpSourceFileStatus.Unknown); r.Value.Warning.Should().Contain("moved candidates"); JsonSerializer.Serialize(r.Value).Should().NotContain("/Users/alice/private/source.pdf"); }
    [Fact] public async Task GetDocumentStatus_does_not_call_resolve_file_scan() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("text"); await c.Api.GetDocumentStatusAsync(c.DocumentInstanceId); (await c.KnownLocationsCountAsync()).Should().Be(0); }
    [Fact] public async Task GetPageText_current_returns_layout_plain_text() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("plain text"); (await c.Api.GetPageTextAsync(new McpPageTextRequest(c.PageId))).Value.Text.Should().Contain("plain text"); }
    [Fact] public async Task GetPageText_current_does_not_return_bbox() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("plain text"); JsonSerializer.Serialize((await c.Api.GetPageTextAsync(new McpPageTextRequest(c.PageId))).Value).Should().NotContain("BBox"); }
    [Fact] public async Task GetPageText_pinned_requires_evidence_ref() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("plain text"); (await c.Api.GetPageTextAsync(new McpPageTextRequest(c.PageId, McpReadMode.Pinned))).ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); }
    [Fact] public async Task GetPageText_pinned_returns_pinned_text() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("pinned text"); var ev = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.UpdateUnitTextAsync("current text"); (await c.Api.GetPageTextAsync(new McpPageTextRequest(c.PageId, McpReadMode.Pinned, ev.Value.EvidenceRefId))).Value.Text.Should().Be("pinned text"); }
    [Fact] public async Task GetPageText_compare_returns_pinned_and_current_or_change_warning() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("pinned text"); var ev = await c.Evidence.CreateFromSearchUnitAsync(c.UnitId); await c.UpdateUnitTextAsync("current text"); var text = (await c.Api.GetPageTextAsync(new McpPageTextRequest(c.PageId, McpReadMode.Compare, ev.Value.EvidenceRefId))).Value.Text; text.Should().Contain("[Pinned]").And.Contain("[Current]").And.Contain("current text"); }
    [Fact] public async Task GetPageBlocks_default_does_not_return_bbox() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("block text"); var r = await c.Api.GetPageBlocksAsync(new McpPageBlocksRequest(c.PageId)); r.Value.Blocks.Single().BBox.Should().BeNull(); }
    [Fact] public async Task GetPageBlocks_include_bbox_returns_normalized_bbox() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("block text"); var r = await c.Api.GetPageBlocksAsync(new McpPageBlocksRequest(c.PageId, IncludeBbox: true)); r.Value.Blocks.Single().BBox.Should().NotBeNull(); }
    [Fact] public async Task GetPageBlocks_excludes_annotations_by_default() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("block text"); await c.AddAnnotationAsync("note"); var r = await c.Api.GetPageBlocksAsync(new McpPageBlocksRequest(c.PageId)); r.Value.Blocks.Select(b => b.Text).Should().NotContain("note"); }
    [Fact] public async Task GetPageBlocks_include_annotations_returns_annotations() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("block text"); await c.AddAnnotationAsync("note"); var r = await c.Api.GetPageBlocksAsync(new McpPageBlocksRequest(c.PageId, IncludeAnnotations: true)); r.Value.Blocks.Select(b => b.Text).Should().Contain("note"); }
    [Fact] public async Task GetPageBlocks_does_not_return_images_or_cache_paths() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("block text", "/Users/alice/cache/image.png"); var json = JsonSerializer.Serialize((await c.Api.GetPageBlocksAsync(new McpPageBlocksRequest(c.PageId))).Value); json.Should().NotContain("/Users/alice/cache/image.png").And.NotContain("image.png"); }
    [Fact] public async Task GetSearchResultContext_returns_context_with_evidence_refs() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("context text"); var r = await c.Api.GetSearchResultContextAsync(new McpSearchContextRequest(c.UnitId)); r.Value.Units.Single().EvidenceRef.Should().StartWith("evref:v1:"); }
    [Fact] public async Task GetSearchResultContext_does_not_cross_page() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("context text"); var otherPage = await c.AddSecondPageUnitAsync("other"); var r = await c.Api.GetSearchResultContextAsync(new McpSearchContextRequest(c.UnitId, 10, 10)); r.Value.Units.Should().OnlyContain(u => u.PageId == c.PageId); otherPage.Should().NotBe(c.PageId); }
    [Fact] public async Task GetSearchResultContext_caps_before_after() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("u0"); for (var i = 1; i < 30; i++) await c.AddUnitAsync($"u{i}", i + 1); await c.RebuildAsync(); var r = await c.Api.GetSearchResultContextAsync(new McpSearchContextRequest(c.UnitId, 99, 99)); r.Value.Units.Count.Should().BeLessThanOrEqualTo(21); }
    [Fact] public async Task GetSearchResultContext_does_not_return_whole_page_text() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("match"); await c.AddUnitAsync("far away", 30); await c.RebuildAsync(); var r = await c.Api.GetSearchResultContextAsync(new McpSearchContextRequest(c.UnitId, 0, 0)); r.Value.Units.Should().HaveCount(1); JsonSerializer.Serialize(r.Value).Should().NotContain("far away"); }
    [Fact] public void IMcpReadApi_exposes_only_read_methods() { var allowed = new[] { nameof(IMcpReadApi.SearchLibraryAsync), nameof(IMcpReadApi.GetItemMetadataAsync), nameof(IMcpReadApi.GetDocumentStatusAsync), nameof(IMcpReadApi.GetPageTextAsync), nameof(IMcpReadApi.GetPageBlocksAsync), nameof(IMcpReadApi.GetSearchResultContextAsync) }; typeof(IMcpReadApi).GetMethods().Select(m => m.Name).Should().OnlyContain(n => allowed.Contains(n)); typeof(IMcpReadApi).GetMethods().Select(m => m.Name).Should().NotContain(n => n.Contains("Run", StringComparison.OrdinalIgnoreCase) || n.Contains("Edit", StringComparison.OrdinalIgnoreCase) || n.Contains("Delete", StringComparison.OrdinalIgnoreCase) || n.Contains("Update", StringComparison.OrdinalIgnoreCase) || n.Contains("Purge", StringComparison.OrdinalIgnoreCase)); }
    [Fact] public void MCP_has_no_queue_control_or_queue_task_exposure() { var names=typeof(IMcpReadApi).GetMethods().Select(m=>m.Name).ToArray(); names.Should().NotContain(n=>n.Contains("Queue",StringComparison.OrdinalIgnoreCase)||n.Contains("Task",StringComparison.OrdinalIgnoreCase)); typeof(IMcpReadApi).Assembly.GetTypes().SelectMany(t=>t.GetProperties()).Should().NotContain(p=>p.PropertyType.Name.Contains("OcrQueue",StringComparison.Ordinal)); }
    [Fact] public async Task MCP_responses_do_not_contain_original_path_or_resolved_path() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle", "/Users/alice/private/source.pdf"); var json = await c.AllResponsesJsonAsync("needle"); json.Should().NotContain("/Users/alice/private/source.pdf").And.NotContain("original_path").And.NotContain("resolved_path"); }
    [Fact] public async Task MCP_responses_do_not_contain_file_url_or_cache_path() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle", "file:///Users/alice/cache/source.pdf"); var json = await c.AllResponsesJsonAsync("needle"); json.Should().NotContain("file://").And.NotContain("/Users/alice/cache/source.pdf"); }
    [Fact] public async Task MCP_does_not_expose_provider_secret_or_model_path() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle"); await c.Connection().ExecuteAsync("insert into ocr_presets (preset_id, library_id, name, current_version_id, created_at, updated_at) values (@P,@L,'secret',null,@N,@N); insert into ocr_preset_versions (preset_version_id,preset_id,engine_id,model_id,model_path,parameters_json,apply_on_success,created_at) values (@V,@P,'mock','mock-basic','/Users/alice/model_path/secret.bin','{}',0,@N);", new { P = OcrPresetId.New().ToString(), V = OcrPresetVersionId.New().ToString(), L = c.LibraryId.ToString(), N = DateTimeOffset.UtcNow.ToString("O") }); (await c.AllResponsesJsonAsync("needle")).Should().NotContain("secret.bin").And.NotContain("model_path"); }
    [Fact] public async Task MCP_does_not_trigger_OCR() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle"); var before = await c.Connection().ExecuteScalarAsync<int>("select count(1) from ocr_runs;"); await c.AllResponsesJsonAsync("needle"); (await c.Connection().ExecuteScalarAsync<int>("select count(1) from ocr_runs;")).Should().Be(before); }
    [Fact] public async Task MCP_does_not_trigger_index_rebuild() { await using var c = await McpTestContext.CreateWithIndexedUnitAsync("needle"); var before = await c.FtsCountAsync(); await c.AllResponsesJsonAsync("needle"); (await c.FtsCountAsync()).Should().Be(before); }
    [Fact] public void Step9_does_not_add_migration() { Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql").Select(Path.GetFileName).Should().NotContain("009_create_mcp_read_api.sql"); }

    private sealed class McpTestContext : IAsyncDisposable
    {
        private McpTestContext(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId, ItemId itemId, DocumentInstanceId documentInstanceId, PageId pageId, LayoutRevisionId revisionId, SearchUnitId unitId, McpReadApi api, EvidenceReferenceService evidence, ItemService itemService)
        { Database = database; Clock = clock; LibraryId = libraryId; ItemId = itemId; DocumentInstanceId = documentInstanceId; PageId = pageId; RevisionId = revisionId; UnitId = unitId; Api = api; Evidence = evidence; ItemService = itemService; }
        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public ItemId ItemId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageId PageId { get; }
        public LayoutRevisionId RevisionId { get; }
        public SearchUnitId UnitId { get; }
        public McpReadApi Api { get; }
        public EvidenceReferenceService Evidence { get; }
        public ItemService ItemService { get; }
        public static async Task<McpTestContext> CreateWithIndexedUnitAsync(string text, string? originalPath = null, bool failingEvidence = false)
        {
            var db = TemporarySqliteDatabase.Create(); var clock = new FixedClock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var librarySvc = new LibraryIdentityService(db.ConnectionFactory, clock); var lib = await librarySvc.CreateLibraryAsync("MCP Library");
            var itemSvc = new ItemService(db.ConnectionFactory, librarySvc, clock); var item = await itemSvc.CreateItemAsync("book", "MCP Item");
            FileAssetId? fileAssetId = null;
            if (originalPath is not null)
            {
                fileAssetId = FileAssetId.New();
                await using var cn = db.ConnectionFactory.CreateConnection(); await cn.OpenAsync();
                await cn.ExecuteAsync("insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at) values (@Id,@Lib,@Path,'source.pdf',0,'available',@N,@N);", new { Id = fileAssetId.Value.ToString(), Lib = lib.Value.LibraryId.ToString(), Path = originalPath, N = clock.UtcNow.ToString("O") });
            }
            var doc = await new DocumentInstanceService(db.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, fileAssetId, DocumentInstanceType.PrimaryScan);
            var page = await new PageService(db.ConnectionFactory, clock).CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            var layout = new LayoutTreeService(db.ConnectionFactory, clock); var rev = await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Mock, makeCurrent: true);
            await layout.AddNodeAsync(rev.Value.LayoutRevisionId, page.Value.PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(0.1, 0.1, 0.2, 0.2), text, TextPolicy.Own, 1, LayoutNodeSource.Mock);
            var builder = new SearchUnitBuilder(db.ConnectionFactory, clock); var rebuilder = new SearchIndexRebuilder(db.ConnectionFactory, clock);
            await builder.RebuildForDocumentInstanceAsync(doc.Value.DocumentInstanceId); await rebuilder.RebuildFtsForDocumentInstanceAsync(doc.Value.DocumentInstanceId); await rebuilder.RebuildFtsForLibraryAsync();
            await using var conn = db.ConnectionFactory.CreateConnection(); await conn.OpenAsync();
            var unitId = SearchUnitId.Parse((await conn.ExecuteScalarAsync<string>("select unit_id from search_units limit 1;"))!);
            var search = new SqliteSearchService(db.ConnectionFactory, new SearchProfileService(db.ConnectionFactory, librarySvc, clock));
            var evidence = new EvidenceReferenceService(db.ConnectionFactory, clock);
            IEvidenceReferenceService evidenceForApi = failingEvidence ? new FailingEvidenceService() : evidence;
            var api = new McpReadApi(db.ConnectionFactory, search, evidenceForApi);
            return new McpTestContext(db, clock, lib.Value.LibraryId, item.Value.ItemId, doc.Value.DocumentInstanceId, page.Value.PageId, rev.Value.LayoutRevisionId, unitId, api, evidence, itemSvc);
        }
        public async Task AddAnnotationAsync(string text) => await new LayoutTreeService(Database.ConnectionFactory, Clock).AddNodeAsync(RevisionId, PageId, null, LayoutNodeType.Annotation, new NormalizedBBox(0.4, 0.4, 0.1, 0.1), text, TextPolicy.Own, 99, LayoutNodeSource.Mock);
        public async Task AddUnitAsync(string text, int readingOrder) { await new LayoutTreeService(Database.ConnectionFactory, Clock).AddNodeAsync(RevisionId, PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(0.01 * readingOrder, 0.01, 0.001, 0.001), text, TextPolicy.Own, readingOrder, LayoutNodeSource.Mock); }
        public async Task<PageId> AddSecondPageUnitAsync(string text) { var p = await new PageService(Database.ConnectionFactory, Clock).CreatePageAsync(DocumentInstanceId, 1, "2", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null); await new LayoutTreeService(Database.ConnectionFactory, Clock).AddNodeAsync(RevisionId, p.Value.PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(0.5, 0.5, 0.1, 0.1), text, TextPolicy.Own, 1, LayoutNodeSource.Mock); await RebuildAsync(); return p.Value.PageId; }
        public async Task RebuildAsync() { var builder = new SearchUnitBuilder(Database.ConnectionFactory, Clock); var rebuilder = new SearchIndexRebuilder(Database.ConnectionFactory, Clock); await builder.RebuildForDocumentInstanceAsync(DocumentInstanceId); await rebuilder.RebuildFtsForLibraryAsync(); }
        public async Task UpdateUnitTextAsync(string text) => await Connection().ExecuteAsync("update search_units set resolved_text=@Text where unit_id=@Unit;", new { Text = text, Unit = UnitId.ToString() });
        public async Task SetFileStatusAsync(string status)
        {
            await using var cn = Connection();
            var existing = await cn.ExecuteScalarAsync<string?>("select file_asset_id from document_instances where document_instance_id=@Doc;", new { Doc = DocumentInstanceId.ToString() });
            if (existing is null)
            {
                existing = FileAssetId.New().ToString();
                await cn.ExecuteAsync("insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes, status, created_at, updated_at) values (@Id,@Lib,'/Users/alice/private/source.pdf','source.pdf',0,@Status,@N,@N); update document_instances set file_asset_id=@Id where document_instance_id=@Doc;", new { Id = existing, Lib = LibraryId.ToString(), Status = status, N = Clock.UtcNow.ToString("O"), Doc = DocumentInstanceId.ToString() });
            }
            else
            {
                await cn.ExecuteAsync("update file_assets set status=@Status where file_asset_id=@Id;", new { Status = status, Id = existing });
            }
        }
        public async Task<int> KnownLocationsCountAsync() => await Connection().ExecuteScalarAsync<int>("select count(1) from known_file_locations;");
        public async Task<int> FtsCountAsync() => await Connection().ExecuteScalarAsync<int>("select count(1) from search_units_fts;");
        public async Task<string> AllResponsesJsonAsync(string query) => JsonSerializer.Serialize(new object?[] { (await Api.SearchLibraryAsync(new McpSearchLibraryRequest(query))).Value, (await Api.GetItemMetadataAsync(ItemId)).Value, (await Api.GetDocumentStatusAsync(DocumentInstanceId)).Value, (await Api.GetPageTextAsync(new McpPageTextRequest(PageId))).Value, (await Api.GetPageBlocksAsync(new McpPageBlocksRequest(PageId))).Value, (await Api.GetSearchResultContextAsync(new McpSearchContextRequest(UnitId))).Value });
        public Microsoft.Data.Sqlite.SqliteConnection Connection() { var c = Database.ConnectionFactory.CreateConnection(); c.Open(); return c; }
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class FailingEvidenceService : IEvidenceReferenceService
    {
        public Task<Result<EvidenceRefRecord>> CreateFromSearchUnitAsync(SearchUnitId unitId, CancellationToken cancellationToken = default) => Task.FromResult(Result<EvidenceRefRecord>.Failure(AppErrorCodes.DatabaseError, "simulated failure"));
        public Task<Result<EvidenceResolutionResult>> ResolveAsync(string evidenceRefId, string mode = EvidenceResolutionMode.Pinned, CancellationToken cancellationToken = default) => Task.FromResult(Result<EvidenceResolutionResult>.Failure(AppErrorCodes.DatabaseError, "simulated failure"));
        public Task<Result<EvidenceMarkdown>> CreateMarkdownAsync(string evidenceRefId, CancellationToken cancellationToken = default) => Task.FromResult(Result<EvidenceMarkdown>.Failure(AppErrorCodes.DatabaseError, "simulated failure"));
        public Task<Result> MarkSupersededAsync(string evidenceRefId, string successorEvidenceRefId, string reason, CancellationToken cancellationToken = default) => Task.FromResult(Result.Failure(AppErrorCodes.DatabaseError, "simulated failure"));
        public Task<Result> TombstoneAsync(string evidenceRefId, string reason, CancellationToken cancellationToken = default) => Task.FromResult(Result.Failure(AppErrorCodes.DatabaseError, "simulated failure"));
        public Task<Result> PurgeAsync(string evidenceRefId, string reason, CancellationToken cancellationToken = default) => Task.FromResult(Result.Failure(AppErrorCodes.DatabaseError, "simulated failure"));
    }
}
