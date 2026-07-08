using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Evidence;
using Patchouli.Search;
using Microsoft.Data.Sqlite;

namespace Patchouli.Tests;

public sealed class OcrLifecycleTests
{
    [Fact] public async Task CreatePreset_creates_current_version() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); p.Value.CurrentVersionId.Should().NotBeNull(); }
    [Fact] public async Task CreatePreset_rejects_blank_name() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.PresetService.CreatePresetAsync(" ", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false); p.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); }
    [Fact] public async Task CreatePresetVersion_updates_current_version() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var old = p.Value.CurrentVersionId; var v = await c.PresetService.CreatePresetVersionAsync(p.Value.PresetId, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{\"failPages\":[1]}", true); var updated = await c.PresetService.GetPresetAsync(p.Value.PresetId); updated.Value.CurrentVersionId.Should().Be(v.Value.PresetVersionId); updated.Value.CurrentVersionId.Should().NotBe(old); }
    [Fact] public async Task ArchivePreset_blocks_future_runs() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); await c.PresetService.ArchivePresetAsync(p.Value.PresetId); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); r.ErrorCode.Should().Be(AppErrorCodes.InvalidState); }
    [Fact] public async Task RunPresetOnPages_creates_run_and_page_results() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, c.Pages.Select(x => x.PageId).ToArray()); var results = await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId); r.Value.State.Should().Be(OcrRunState.Completed); results.Value.Should().HaveCount(3); }
    [Fact] public async Task RunPresetOnDocument_creates_run_and_page_results_for_all_pages() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnDocumentAsync(c.DocumentInstanceId, p.Value.PresetId); var results = await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId); r.Value.State.Should().Be(OcrRunState.Completed); results.Value.Select(x => x.PageId).Should().BeEquivalentTo(c.Pages.Select(x => x.PageId)); }
    [Fact] public async Task RunPresetOnDocument_rejects_document_without_pages_and_does_not_create_empty_run() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var emptyDocument = await c.CreateDocumentInstanceWithoutPagesAsync(); var r = await c.Coordinator.RunPresetOnDocumentAsync(emptyDocument, p.Value.PresetId); r.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); (await c.CountRunsAsync()).Should().Be(0); }
    [Fact] public async Task Successful_mock_run_creates_staging_revision() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); r.Value.OutputRevisionId.Should().NotBeNull(); (await c.CountNodesAsync(r.Value.OutputRevisionId!.Value)).Should().Be(1); }
    [Fact] public async Task apply_on_success_false_does_not_set_current_revision() { await using var c = await OcrTestContext.CreateAsync(); var current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId); var p = await c.CreatePresetAsync(apply: false); await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); var after = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId); after.Value.LayoutRevisionId.Should().Be(current.Value.LayoutRevisionId); }
    [Fact] public async Task apply_on_success_true_sets_staging_revision_as_current() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(apply: true); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); var current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId); current.Value.LayoutRevisionId.Should().Be(r.Value.OutputRevisionId); }
    [Fact] public async Task Partial_failure_results_in_completed_with_errors() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(parameters: "{\"failPages\":[1]}"); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, c.Pages.Select(x => x.PageId).ToArray()); r.Value.State.Should().Be(OcrRunState.CompletedWithErrors); }
    [Fact] public async Task All_pages_failed_results_in_failed() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(parameters: "{\"failPages\":[0,1,2]}"); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, c.Pages.Select(x => x.PageId).ToArray()); r.Value.State.Should().Be(OcrRunState.Failed); }
    [Fact] public async Task Bbox_coordinate_failure_skips_page_and_writes_no_node() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(parameters: "{\"bboxFailurePages\":[2]}"); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[2].PageId]); var results = await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId); r.Value.State.Should().Be(OcrRunState.CompletedWithErrors); results.Value.Single().State.Should().Be(OcrPageResultState.Skipped); results.Value.Single().ErrorCode.Should().Be(OcrFailureCode.BBoxCoordinateTransformFailed); (await c.CountNodesAsync(r.Value.OutputRevisionId!.Value)).Should().Be(0); }
    [Fact] public async Task Run_rejects_page_from_other_document_instance() { await using var c = await OcrTestContext.CreateAsync(); var otherItem = await c.ItemService.CreateItemAsync("book", "Other"); var otherDoc = await c.DocumentInstanceService.AttachDocumentInstanceAsync(otherItem.Value.ItemId, null, DocumentInstanceType.PrimaryScan); var otherPage = await c.PageService.CreatePageAsync(otherDoc.Value.DocumentInstanceId, 0, null, 100, 200, 0, CoordinateBasis.NormalizedPage, 100, 200, "renderer-v1", null); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [otherPage.Value.PageId]); r.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); }
    [Fact] public async Task Run_rejects_missing_page() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [PageId.New()]); r.ErrorCode.Should().Be(AppErrorCodes.NotFound); }
    [Fact] public async Task Run_rejects_archived_preset() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); await c.PresetService.ArchivePresetAsync(p.Value.PresetId); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); r.ErrorCode.Should().Be(AppErrorCodes.InvalidState); }
    [Fact] public async Task Run_rejects_empty_page_list() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, []); r.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); }
    [Fact] public async Task Cancel_pending_or_running_run_marks_cancelled_and_does_not_change_current() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId); var pending = await c.Coordinator.CreatePendingRunForTestAsync(c.DocumentInstanceId, p.Value.PresetId); var cancel = await c.Coordinator.CancelRunAsync(pending.Value.OcrRunId); var after = await c.Coordinator.GetRunAsync(pending.Value.OcrRunId); var currentAfter = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId); cancel.IsSuccess.Should().BeTrue(); after.Value.State.Should().Be(OcrRunState.Cancelled); currentAfter.Value.LayoutRevisionId.Should().Be(current.Value.LayoutRevisionId); }
    [Fact] public async Task HideOcrRun_marks_run_hidden_and_default_get_filters_it() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var run = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); var hidden = await c.Coordinator.HideOcrRunAsync(run.Value.OcrRunId); var fetched = await c.Coordinator.GetRunAsync(run.Value.OcrRunId); hidden.IsSuccess.Should().BeTrue(); fetched.ErrorCode.Should().Be(AppErrorCodes.NotFound); (await c.IsRunHiddenAsync(run.Value.OcrRunId)).Should().BeTrue(); }
    [Fact] public async Task HideCurrentOcrRun_removes_default_search_and_mcp_visibility_but_preserves_pinned_evidence() { await using var c = await OcrTestContext.CreateAsync(withSearchDirtyMarker: true); var p = await c.CreatePresetAsync(apply: true); var run = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); await c.RebuildSearchAsync(); (await c.SearchTextAsync("OCR")).Results.Should().NotBeEmpty(); var evidence = new EvidenceReferenceService(c.Database.ConnectionFactory, c.Clock); var ev = await evidence.CreateFromSearchUnitAsync(await c.CurrentSearchUnitIdAsync()); var hidden = await c.Coordinator.HideOcrRunAsync(run.Value.OcrRunId); var mcp = await c.McpSearchTextAsync("OCR", evidence); hidden.IsSuccess.Should().BeTrue(); (await c.CountCurrentRevisionsAsync()).Should().Be(0); (await c.CountCurrentSearchUnitsAsync()).Should().Be(0); (await c.SearchTextAsync("OCR")).Results.Should().BeEmpty(); mcp.Results.Should().BeEmpty(); (await evidence.ResolveAsync(ev.Value.EvidenceRefId)).Value.Status.Should().Be(EvidenceResolutionStatus.FoundPinned); }
    [Fact] public async Task UnsetCurrentOcr_clears_current_ocr_revision_search_units_and_marks_search_dirty() { await using var c = await OcrTestContext.CreateAsync(withSearchDirtyMarker: true); var p = await c.CreatePresetAsync(apply: true); await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); await c.RebuildSearchAsync(); (await c.CountCurrentRevisionsAsync()).Should().Be(1); (await c.CountCurrentSearchUnitsAsync()).Should().Be(1); var unset = await c.Coordinator.UnsetCurrentOcrAsync(c.DocumentInstanceId); unset.IsSuccess.Should().BeTrue(); (await c.CountCurrentRevisionsAsync()).Should().Be(0); (await c.CountCurrentSearchUnitsAsync()).Should().Be(0); (await c.SearchIndexStatusAsync()).Should().Be(SearchIndexStatusValue.Stale); }
    [Fact] public async Task AdoptCandidateRun_sets_current_revision() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); var adoption = await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId); var current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId); current.Value.LayoutRevisionId.Should().Be(adoption.Value.AdoptedRevisionId); }
    [Fact] public async Task Adopted_ocr_rebuild_links_evidence_successor_for_previous_current_revision() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var first = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); await c.Coordinator.AdoptCandidateRunAsync(first.Value.OcrRunId); await c.RebuildSearchAsync(); var evidence = new EvidenceReferenceService(c.Database.ConnectionFactory, c.Clock); var oldRef = await evidence.CreateFromSearchUnitAsync(await c.CurrentSearchUnitIdAsync()); var second = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); await c.Coordinator.AdoptCandidateRunAsync(second.Value.OcrRunId); await c.RebuildSearchAsync(); var pinned = await evidence.ResolveAsync(oldRef.Value.EvidenceRefId); var current = await evidence.ResolveAsync(oldRef.Value.EvidenceRefId, EvidenceResolutionMode.Current); pinned.Value.Status.Should().Be(EvidenceResolutionStatus.Superseded); pinned.Value.SuccessorEvidenceRefs.Should().HaveCount(1); current.Value.Status.Should().Be(EvidenceResolutionStatus.FoundCurrent); (await c.CountCurrentSearchUnitsAsync()).Should().Be(1); }
    [Fact] public async Task AdoptCandidateRun_selected_pages_only_copies_selected_pages() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, c.Pages.Select(x => x.PageId).ToArray()); var adoption = await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId, [c.Pages[1].PageId]); (await c.PageIdsWithNodesAsync(adoption.Value.AdoptedRevisionId)).Should().Equal(c.Pages[1].PageId); }
    [Fact]
    public async Task AdoptCandidateRun_selected_pages_preserves_layout_tree_and_table_metadata()
    {
        await using var c = await OcrTestContext.CreateAsync();
        var p = await c.CreatePresetAsync();
        var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, c.Pages.Select(x => x.PageId).ToArray());
        await c.AddTableHierarchyAsync(r.Value.OutputRevisionId!.Value, c.Pages[1].PageId);

        var adoption = await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId, [c.Pages[1].PageId]);

        adoption.IsSuccess.Should().BeTrue();
        (await c.CountOrphanParentRefsAsync(adoption.Value.AdoptedRevisionId)).Should().Be(0);
        var cells = await c.TableCellsAsync(adoption.Value.AdoptedRevisionId);
        cells.Select(c => c.OwnText).Should().Equal("Name", "Value", "Pages", "12");
        cells.Where(c => c.RowIndex == 0).Should().OnlyContain(c => c.IsHeader == 1);
        cells.Single(c => c.OwnText == "Pages").ColIndex.Should().Be(0);
        (await c.CountCellsWithTableRowParentAsync(adoption.Value.AdoptedRevisionId)).Should().Be(4);
    }
    [Fact] public async Task AdoptCandidateRun_rejects_failed_pages() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(parameters: "{\"failPages\":[1]}"); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, c.Pages.Select(x => x.PageId).ToArray()); var adoption = await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId, [c.Pages[1].PageId]); adoption.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); }
    [Fact] public async Task AdoptCandidateRun_records_candidate_adoption() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId); (await c.CountAdoptionsAsync(r.Value.OcrRunId)).Should().Be(1); }
    [Fact] public async Task AdoptCandidateRun_rejects_failed_run() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(parameters: "{\"failPages\":[0]}"); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); var adoption = await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId); adoption.ErrorCode.Should().Be(AppErrorCodes.InvalidState); }
    [Fact] public async Task Adoption_keeps_only_one_current_revision() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId); (await c.CountCurrentRevisionsAsync()).Should().Be(1); }
    [Fact] public async Task Adoption_is_serialized_per_document_instance() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r1 = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); var r2 = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[1].PageId]); await Task.WhenAll(c.Coordinator.AdoptCandidateRunAsync(r1.Value.OcrRunId), c.Coordinator.AdoptCandidateRunAsync(r2.Value.OcrRunId)); (await c.CountCurrentRevisionsAsync()).Should().Be(1); (await c.CountAdoptionsTotalAsync()).Should().Be(2); }
    [Fact] public async Task Staging_revision_is_not_current_before_adoption() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); (await c.IsCurrentAsync(r.Value.OutputRevisionId!.Value)).Should().BeFalse(); }
    [Fact] public async Task Cancelled_run_does_not_leave_current_revision() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); var pending = await c.Coordinator.CreatePendingRunForTestAsync(c.DocumentInstanceId, p.Value.PresetId); await c.Coordinator.CancelRunAsync(pending.Value.OcrRunId); (await c.CountCurrentRevisionsAsync()).Should().Be(1); }
    [Fact] public async Task Ocr_run_does_not_create_search_units_or_evidence_refs() { await using var c = await OcrTestContext.CreateAsync(); var p = await c.CreatePresetAsync(); await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]); await using var cn = c.Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().Be(0); var tables = await c.TableNamesAsync(); tables.Should().NotContain("evidence_refs"); }
    [Fact] public async Task MigrationRunner_applies_ocr_lifecycle_migration() { await using var db = TemporarySqliteDatabase.Create(); await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync(); await using var cn = db.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); var count = await cn.ExecuteScalarAsync<int>("select count(1) from sqlite_master where type='table' and name in ('ocr_presets','ocr_preset_versions','ocr_runs','ocr_page_results','ocr_candidate_adoptions');"); count.Should().Be(5); }
    [Fact] public async Task MigrationRunner_adds_ocr_run_hidden_marker() { await using var db = TemporarySqliteDatabase.Create(); await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync(); await using var cn = db.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); var columns = (await cn.QueryAsync<string>("select name from pragma_table_info('ocr_runs');")).ToArray(); columns.Should().Contain("hidden"); }
    [Fact] public async Task Foreign_keys_prevent_orphan_ocr_page_result() { await using var c = await OcrTestContext.CreateAsync(); await using var cn = c.Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); Func<Task> action = () => cn.ExecuteAsync("insert into ocr_page_results (result_id, ocr_run_id, page_id, state, created_at, updated_at) values (@Id, @Run, @Page, 'pending', @Now, @Now);", new { Id = OcrPageResultId.New().ToString(), Run = OcrRunId.New().ToString(), Page = PageId.New().ToString(), Now = DateTimeOffset.UtcNow.ToString("O") }); await action.Should().ThrowAsync<SqliteException>(); }

    private sealed class OcrTestContext : IAsyncDisposable
    {
        private OcrTestContext(TemporarySqliteDatabase database, FixedClock clock, DocumentInstanceId documentInstanceId, IReadOnlyList<Core.Layout.Page> pages, ItemService itemService, DocumentInstanceService documentInstanceService, PageService pageService, OcrPresetService presetService, OcrRunCoordinator coordinator, LayoutTreeService layoutTreeService) { Database = database; Clock = clock; DocumentInstanceId = documentInstanceId; Pages = pages; ItemService = itemService; DocumentInstanceService = documentInstanceService; PageService = pageService; PresetService = presetService; Coordinator = coordinator; LayoutTreeService = layoutTreeService; }
        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public IReadOnlyList<Core.Layout.Page> Pages { get; }
        public ItemService ItemService { get; }
        public DocumentInstanceService DocumentInstanceService { get; }
        public PageService PageService { get; }
        public OcrPresetService PresetService { get; }
        public OcrRunCoordinator Coordinator { get; }
        public LayoutTreeService LayoutTreeService { get; }
        public static async Task<OcrTestContext> CreateAsync(bool withSearchDirtyMarker = false)
        {
            var db = TemporarySqliteDatabase.Create(); var clock = new FixedClock(new DateTimeOffset(2026, 6, 19, 5, 0, 0, TimeSpan.Zero));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var lib = new LibraryIdentityService(db.ConnectionFactory, clock); await lib.CreateLibraryAsync("OCR library");
            var itemService = new ItemService(db.ConnectionFactory, lib, clock);
            var documentInstanceService = new DocumentInstanceService(db.ConnectionFactory, clock);
            var item = await itemService.CreateItemAsync("book", "OCR book");
            var doc = await documentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var pageSvc = new PageService(db.ConnectionFactory, clock); var pages = new List<Core.Layout.Page>();
            for (var i = 0; i < 3; i++) pages.Add((await pageSvc.CreatePageAsync(doc.Value.DocumentInstanceId, i, null, 100, 200, 0, CoordinateBasis.NormalizedPage, 100, 200, "renderer-v1", null)).Value);
            var layout = new LayoutTreeService(db.ConnectionFactory, clock); await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Mock, makeCurrent: true);
            var preset = new OcrPresetService(db.ConnectionFactory, lib, clock);
            var dirtyMarker = withSearchDirtyMarker ? new SearchUnitBuilder(db.ConnectionFactory, clock) : null;
            var coordinator = new OcrRunCoordinator(db.ConnectionFactory, clock, new MockOcrEngine(), dirtyMarker, new OcrLayoutImporter(db.ConnectionFactory, clock));
            return new OcrTestContext(db, clock, doc.Value.DocumentInstanceId, pages, itemService, documentInstanceService, pageSvc, preset, coordinator, layout);
        }
        public Task<Result<OcrPreset>> CreatePresetAsync(bool apply = false, string parameters = "{}") => PresetService.CreatePresetAsync("Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, parameters, apply);
        public async Task<DocumentInstanceId> CreateDocumentInstanceWithoutPagesAsync() { var item = await ItemService.CreateItemAsync("book", "Empty OCR book"); var doc = await DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.Supplement); return doc.Value.DocumentInstanceId; }
        public async Task<int> CountNodesAsync(LayoutRevisionId rev) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from layout_nodes where revision_id = @Rev;", new { Rev = rev.ToString() }); }
        public async Task<int> CountRunsAsync() { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from ocr_runs;"); }
        public async Task<IReadOnlyList<PageId>> PageIdsWithNodesAsync(LayoutRevisionId rev) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); var ids = await cn.QueryAsync<string>("select distinct page_id from layout_nodes where revision_id = @Rev order by page_id;", new { Rev = rev.ToString() }); return ids.Select(PageId.Parse).ToArray(); }
        public async Task AddTableHierarchyAsync(LayoutRevisionId rev, PageId pageId)
        {
            await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync();
            var table = LayoutNodeId.New().ToString(); var header = LayoutNodeId.New().ToString(); var body = LayoutNodeId.New().ToString();
            var cells = new[]
            {
                new { Id = LayoutNodeId.New().ToString(), Parent = header, Text = "Name", Row = 0, Col = 0, Header = 1, Order = 12 },
                new { Id = LayoutNodeId.New().ToString(), Parent = header, Text = "Value", Row = 0, Col = 1, Header = 1, Order = 13 },
                new { Id = LayoutNodeId.New().ToString(), Parent = body, Text = "Pages", Row = 1, Col = 0, Header = 0, Order = 15 },
                new { Id = LayoutNodeId.New().ToString(), Parent = body, Text = "12", Row = 1, Col = 1, Header = 0, Order = 16 }
            };
            await cn.ExecuteAsync(
                """
                insert into layout_nodes (node_id, document_instance_id, page_id, parent_node_id, node_type, own_text, text_policy, reading_order, source, revision_id, confidence, ignored)
                values (@NodeId, @DocumentInstanceId, @PageId, @ParentNodeId, @NodeType, @OwnText, @TextPolicy, @ReadingOrder, @Source, @RevisionId, null, 0);
                """,
                new[]
                {
                    new { NodeId = table, DocumentInstanceId = DocumentInstanceId.ToString(), PageId = pageId.ToString(), ParentNodeId = (string?)null, NodeType = LayoutNodeType.Table, OwnText = (string?)null, TextPolicy = TextPolicy.AggregateChildren, ReadingOrder = 10, Source = LayoutNodeSource.Ocr, RevisionId = rev.ToString() },
                    new { NodeId = header, DocumentInstanceId = DocumentInstanceId.ToString(), PageId = pageId.ToString(), ParentNodeId = (string?)table, NodeType = LayoutNodeType.TableRow, OwnText = (string?)null, TextPolicy = TextPolicy.AggregateChildren, ReadingOrder = 11, Source = LayoutNodeSource.Ocr, RevisionId = rev.ToString() },
                    new { NodeId = body, DocumentInstanceId = DocumentInstanceId.ToString(), PageId = pageId.ToString(), ParentNodeId = (string?)table, NodeType = LayoutNodeType.TableRow, OwnText = (string?)null, TextPolicy = TextPolicy.AggregateChildren, ReadingOrder = 14, Source = LayoutNodeSource.Ocr, RevisionId = rev.ToString() }
                });
            foreach (var cell in cells)
            {
                await cn.ExecuteAsync(
                    """
                    insert into layout_nodes (
                        node_id, document_instance_id, page_id, parent_node_id, node_type,
                        own_text, text_policy, reading_order, source, revision_id, confidence, ignored,
                        row_index, col_index, row_span, col_span, is_header
                    )
                    values (
                        @NodeId, @DocumentInstanceId, @PageId, @ParentNodeId, @NodeType,
                        @OwnText, @TextPolicy, @ReadingOrder, @Source, @RevisionId, null, 0,
                        @RowIndex, @ColIndex, 1, 1, @IsHeader
                    );
                    """,
                    new { NodeId = cell.Id, DocumentInstanceId = DocumentInstanceId.ToString(), PageId = pageId.ToString(), ParentNodeId = cell.Parent, NodeType = LayoutNodeType.TableCell, OwnText = cell.Text, TextPolicy = TextPolicy.Own, ReadingOrder = cell.Order, Source = LayoutNodeSource.Ocr, RevisionId = rev.ToString(), RowIndex = cell.Row, ColIndex = cell.Col, IsHeader = cell.Header });
            }
        }
        public async Task<int> CountOrphanParentRefsAsync(LayoutRevisionId rev) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from layout_nodes child left join layout_nodes parent on parent.node_id = child.parent_node_id and parent.revision_id = child.revision_id where child.revision_id = @Rev and child.parent_node_id is not null and parent.node_id is null;", new { Rev = rev.ToString() }); }
        public async Task<IReadOnlyList<TableCellRow>> TableCellsAsync(LayoutRevisionId rev) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return (await cn.QueryAsync<TableCellRow>("select own_text as OwnText, row_index as RowIndex, col_index as ColIndex, row_span as RowSpan, col_span as ColSpan, is_header as IsHeader from layout_nodes where revision_id = @Rev and node_type = @NodeType order by row_index, col_index;", new { Rev = rev.ToString(), NodeType = LayoutNodeType.TableCell })).ToArray(); }
        public async Task<int> CountCellsWithTableRowParentAsync(LayoutRevisionId rev) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from layout_nodes cell join layout_nodes row on row.node_id = cell.parent_node_id and row.revision_id = cell.revision_id where cell.revision_id = @Rev and cell.node_type = @Cell and row.node_type = @Row;", new { Rev = rev.ToString(), Cell = LayoutNodeType.TableCell, Row = LayoutNodeType.TableRow }); }
        public async Task<int> CountAdoptionsAsync(OcrRunId run) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from ocr_candidate_adoptions where ocr_run_id = @Run;", new { Run = run.ToString() }); }
        public async Task<int> CountAdoptionsTotalAsync() { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from ocr_candidate_adoptions;"); }
        public async Task<int> CountCurrentRevisionsAsync() { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from layout_revisions where document_instance_id = @Doc and is_current = 1;", new { Doc = DocumentInstanceId.ToString() }); }
        public async Task<int> CountCurrentSearchUnitsAsync() { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select count(1) from search_units where document_instance_id = @Doc and status = @Status;", new { Doc = DocumentInstanceId.ToString(), Status = SearchUnitStatus.Current }); }
        public async Task<SearchUnitId> CurrentSearchUnitIdAsync() { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return SearchUnitId.Parse((await cn.ExecuteScalarAsync<string>("select unit_id from search_units where document_instance_id = @Doc and status = @Status limit 1;", new { Doc = DocumentInstanceId.ToString(), Status = SearchUnitStatus.Current }))!); }
        public async Task RebuildSearchAsync() { var builder = new SearchUnitBuilder(Database.ConnectionFactory, Clock); var rebuilder = new SearchIndexRebuilder(Database.ConnectionFactory, Clock); await builder.RebuildForDocumentInstanceAsync(DocumentInstanceId); await rebuilder.RebuildFtsForDocumentInstanceAsync(DocumentInstanceId); await rebuilder.RebuildFtsForLibraryAsync(); }
        public async Task<SearchResultPage> SearchTextAsync(string query) => (await new SqliteSearchService(Database.ConnectionFactory).SearchLibraryAsync(new SearchRequest(query))).Value;
        public async Task<McpSearchLibraryResponse> McpSearchTextAsync(string query, EvidenceReferenceService evidence) => (await new McpReadApi(Database.ConnectionFactory, new SqliteSearchService(Database.ConnectionFactory), evidence).SearchLibraryAsync(new McpSearchLibraryRequest(query))).Value;
        public async Task<bool> IsCurrentAsync(LayoutRevisionId rev) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select is_current from layout_revisions where layout_revision_id = @Rev;", new { Rev = rev.ToString() }) == 1; }
        public async Task<bool> IsRunHiddenAsync(OcrRunId run) { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<int>("select hidden from ocr_runs where ocr_run_id = @Run;", new { Run = run.ToString() }) == 1; }
        public async Task<string?> SearchIndexStatusAsync() { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return await cn.ExecuteScalarAsync<string?>("select status from search_index_status where scope_type = 'document_instance' and scope_id = @Doc;", new { Doc = DocumentInstanceId.ToString() }); }
        public async Task<IReadOnlyList<string>> TableNamesAsync() { await using var cn = Database.ConnectionFactory.CreateConnection(); await cn.OpenAsync(); return (await cn.QueryAsync<string>("select name from sqlite_master where type='table';")).ToArray(); }
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class TableCellRow
    {
        public string? OwnText { get; set; }
        public int? RowIndex { get; set; }
        public int? ColIndex { get; set; }
        public int? RowSpan { get; set; }
        public int? ColSpan { get; set; }
        public int IsHeader { get; set; }
    }
}
