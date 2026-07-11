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
    [Fact]
    public async Task CreatePreset_creates_current_version()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        p.Value.CurrentVersionId.Should().NotBeNull();
    }

    [Fact]
    public async Task CreatePreset_rejects_blank_name()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.PresetService.CreatePresetAsync(" ", null, OcrEngineIds.Mock,
            OcrModelIds.MockBasic, null, "{}", false);
        p.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task CreatePresetVersion_updates_current_version()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        OcrPresetVersionId? old = p.Value.CurrentVersionId;
        Result<OcrPresetVersion> v = await c.PresetService.CreatePresetVersionAsync(p.Value.PresetId, OcrEngineIds.Mock,
            OcrModelIds.MockBasic, null, "{\"failPages\":[1]}", true);
        Result<OcrPreset> updated = await c.PresetService.GetPresetAsync(p.Value.PresetId);
        updated.Value.CurrentVersionId.Should().Be(v.Value.PresetVersionId);
        updated.Value.CurrentVersionId.Should().NotBe(old);
    }

    [Fact]
    public async Task ArchivePreset_blocks_future_runs()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        await c.PresetService.ArchivePresetAsync(p.Value.PresetId);
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        r.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task RunPresetOnPages_creates_run_and_page_results()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId,
            c.Pages.Select(x => x.PageId).ToArray());
        Result<IReadOnlyList<OcrPageResult>> results = await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId);
        r.Value.State.Should().Be(OcrRunState.Completed);
        results.Value.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunPresetOnDocument_creates_run_and_page_results_for_all_pages()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r = await c.Coordinator.RunPresetOnDocumentAsync(c.DocumentInstanceId, p.Value.PresetId);
        Result<IReadOnlyList<OcrPageResult>> results = await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId);
        r.Value.State.Should().Be(OcrRunState.Completed);
        results.Value.Select(x => x.PageId).Should().BeEquivalentTo(c.Pages.Select(x => x.PageId));
    }

    [Fact]
    public async Task RunPresetOnDocument_rejects_document_without_pages_and_does_not_create_empty_run()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        DocumentInstanceId emptyDocument = await c.CreateDocumentInstanceWithoutPagesAsync();
        Result<OcrRun> r = await c.Coordinator.RunPresetOnDocumentAsync(emptyDocument, p.Value.PresetId);
        r.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await c.CountRunsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Successful_mock_run_creates_staging_revision()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        r.Value.OutputRevisionId.Should().NotBeNull();
        (await c.CountNodesAsync(r.Value.OutputRevisionId!.Value)).Should().Be(1);
    }

    [Fact]
    public async Task apply_on_success_false_does_not_set_current_revision()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<LayoutRevision> current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId);
        Result<OcrPreset> p = await c.CreatePresetAsync(false);
        await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        Result<LayoutRevision> after = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId);
        after.Value.LayoutRevisionId.Should().Be(current.Value.LayoutRevisionId);
    }

    [Fact]
    public async Task apply_on_success_true_sets_staging_revision_as_current()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync(true);
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        Result<LayoutRevision> current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId);
        current.Value.LayoutRevisionId.Should().Be(r.Value.OutputRevisionId);
    }

    [Fact]
    public async Task Partial_failure_results_in_completed_with_errors()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync(parameters: "{\"failPages\":[1]}");
        Result<OcrRun> r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId,
            c.Pages.Select(x => x.PageId).ToArray());
        r.Value.State.Should().Be(OcrRunState.CompletedWithErrors);
    }

    [Fact]
    public async Task All_pages_failed_results_in_failed()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync(parameters: "{\"failPages\":[0,1,2]}");
        Result<OcrRun> r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId,
            c.Pages.Select(x => x.PageId).ToArray());
        r.Value.State.Should().Be(OcrRunState.Failed);
    }

    [Fact]
    public async Task Bbox_coordinate_failure_skips_page_and_writes_no_node()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync(parameters: "{\"bboxFailurePages\":[2]}");
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[2].PageId]);
        Result<IReadOnlyList<OcrPageResult>> results = await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId);
        r.Value.State.Should().Be(OcrRunState.CompletedWithErrors);
        results.Value.Single().State.Should().Be(OcrPageResultState.Skipped);
        results.Value.Single().ErrorCode.Should().Be(OcrFailureCode.BBoxCoordinateTransformFailed);
        (await c.CountNodesAsync(r.Value.OutputRevisionId!.Value)).Should().Be(0);
    }

    [Fact]
    public async Task Run_rejects_page_from_other_document_instance()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<ItemMetadata> otherItem = await c.ItemService.CreateItemAsync("book", "Other");
        Result<DocumentInstance> otherDoc =
            await c.DocumentInstanceService.AttachDocumentInstanceAsync(otherItem.Value.ItemId, null,
                DocumentInstanceType.PrimaryScan);
        Result<Page> otherPage = await c.PageService.CreatePageAsync(otherDoc.Value.DocumentInstanceId, 0, null, 100,
            200, 0, CoordinateBasis.NormalizedPage, 100, 200, "renderer-v1", null);
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [otherPage.Value.PageId]);
        r.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Run_rejects_missing_page()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [PageId.New()]);
        r.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Run_rejects_archived_preset()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        await c.PresetService.ArchivePresetAsync(p.Value.PresetId);
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        r.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Run_rejects_empty_page_list()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, []);
        r.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Cancel_pending_or_running_run_marks_cancelled_and_does_not_change_current()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<LayoutRevision> current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId);
        Result<OcrRun> pending =
            await c.Coordinator.CreatePendingRunForTestAsync(c.DocumentInstanceId, p.Value.PresetId);
        Result cancel = await c.Coordinator.CancelRunAsync(pending.Value.OcrRunId);
        Result<OcrRun> after = await c.Coordinator.GetRunAsync(pending.Value.OcrRunId);
        Result<LayoutRevision> currentAfter = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId);
        cancel.IsSuccess.Should().BeTrue();
        after.Value.State.Should().Be(OcrRunState.Cancelled);
        currentAfter.Value.LayoutRevisionId.Should().Be(current.Value.LayoutRevisionId);
    }

    [Fact]
    public async Task HideOcrRun_marks_run_hidden_and_default_get_filters_it()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> run =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        Result hidden = await c.Coordinator.HideOcrRunAsync(run.Value.OcrRunId);
        Result<OcrRun> fetched = await c.Coordinator.GetRunAsync(run.Value.OcrRunId);
        hidden.IsSuccess.Should().BeTrue();
        fetched.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        (await c.IsRunHiddenAsync(run.Value.OcrRunId)).Should().BeTrue();
    }

    [Fact]
    public async Task HideCurrentOcrRun_removes_default_search_and_mcp_visibility_but_preserves_pinned_evidence()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync(true);
        Result<OcrPreset> p = await c.CreatePresetAsync(true);
        Result<OcrRun> run =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        await c.RebuildSearchAsync();
        (await c.SearchTextAsync("OCR")).Results.Should().NotBeEmpty();
        EvidenceReferenceService evidence = new(c.Database.ConnectionFactory, c.Clock);
        Result<EvidenceRefRecord> ev = await evidence.CreateFromSearchUnitAsync(await c.CurrentSearchUnitIdAsync());
        Result hidden = await c.Coordinator.HideOcrRunAsync(run.Value.OcrRunId);
        McpSearchLibraryResponse mcp = await c.McpSearchTextAsync("OCR", evidence);
        hidden.IsSuccess.Should().BeTrue();
        (await c.CountCurrentRevisionsAsync()).Should().Be(0);
        (await c.CountCurrentSearchUnitsAsync()).Should().Be(0);
        (await c.SearchTextAsync("OCR")).Results.Should().BeEmpty();
        mcp.Results.Should().BeEmpty();
        (await evidence.ResolveAsync(ev.Value.EvidenceRefId)).Value.Status.Should()
            .Be(EvidenceResolutionStatus.FoundPinned);
    }

    [Fact]
    public async Task UnsetCurrentOcr_clears_current_ocr_revision_search_units_and_marks_search_dirty()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync(true);
        Result<OcrPreset> p = await c.CreatePresetAsync(true);
        await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        await c.RebuildSearchAsync();
        (await c.CountCurrentRevisionsAsync()).Should().Be(1);
        (await c.CountCurrentSearchUnitsAsync()).Should().Be(1);
        Result unset = await c.Coordinator.UnsetCurrentOcrAsync(c.DocumentInstanceId);
        unset.IsSuccess.Should().BeTrue();
        (await c.CountCurrentRevisionsAsync()).Should().Be(0);
        (await c.CountCurrentSearchUnitsAsync()).Should().Be(0);
        (await c.SearchIndexStatusAsync()).Should().Be(SearchIndexStatusValue.Stale);
    }

    [Fact]
    public async Task AdoptCandidateRun_sets_current_revision()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        Result<OcrCandidateAdoption> adoption = await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId);
        Result<LayoutRevision> current = await c.LayoutTreeService.GetCurrentRevisionAsync(c.DocumentInstanceId);
        current.Value.LayoutRevisionId.Should().Be(adoption.Value.AdoptedRevisionId);
    }

    [Fact]
    public async Task Adopted_ocr_rebuild_links_evidence_successor_for_previous_current_revision()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> first =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        await c.Coordinator.AdoptCandidateRunAsync(first.Value.OcrRunId);
        await c.RebuildSearchAsync();
        EvidenceReferenceService evidence = new(c.Database.ConnectionFactory, c.Clock);
        Result<EvidenceRefRecord> oldRef = await evidence.CreateFromSearchUnitAsync(await c.CurrentSearchUnitIdAsync());
        Result<OcrRun> second =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        await c.Coordinator.AdoptCandidateRunAsync(second.Value.OcrRunId);
        await c.RebuildSearchAsync();
        Result<EvidenceResolutionResult> pinned = await evidence.ResolveAsync(oldRef.Value.EvidenceRefId);
        Result<EvidenceResolutionResult> current =
            await evidence.ResolveAsync(oldRef.Value.EvidenceRefId, EvidenceResolutionMode.Current);
        pinned.Value.Status.Should().Be(EvidenceResolutionStatus.Superseded);
        pinned.Value.SuccessorEvidenceRefs.Should().HaveCount(1);
        current.Value.Status.Should().Be(EvidenceResolutionStatus.FoundCurrent);
        (await c.CountCurrentSearchUnitsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AdoptCandidateRun_selected_pages_only_copies_selected_pages()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId,
            c.Pages.Select(x => x.PageId).ToArray());
        Result<OcrCandidateAdoption> adoption =
            await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId, [c.Pages[1].PageId]);
        (await c.PageIdsWithNodesAsync(adoption.Value.AdoptedRevisionId)).Should().Equal(c.Pages[1].PageId);
    }

    [Fact]
    public async Task AdoptCandidateRun_selected_pages_preserves_layout_tree_and_table_metadata()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId,
            c.Pages.Select(x => x.PageId).ToArray());
        await c.AddTableHierarchyAsync(r.Value.OutputRevisionId!.Value, c.Pages[1].PageId);

        Result<OcrCandidateAdoption> adoption =
            await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId, [c.Pages[1].PageId]);

        adoption.IsSuccess.Should().BeTrue();
        (await c.CountOrphanParentRefsAsync(adoption.Value.AdoptedRevisionId)).Should().Be(0);
        IReadOnlyList<TableCellRow> cells = await c.TableCellsAsync(adoption.Value.AdoptedRevisionId);
        cells.Select(c => c.OwnText).Should().Equal("Name", "Value", "Pages", "12");
        cells.Where(c => c.RowIndex == 0).Should().OnlyContain(c => c.IsHeader == 1);
        cells.Single(c => c.OwnText == "Pages").ColIndex.Should().Be(0);
        (await c.CountCellsWithTableRowParentAsync(adoption.Value.AdoptedRevisionId)).Should().Be(4);
    }

    [Fact]
    public async Task AdoptCandidateRun_rejects_failed_pages()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync(parameters: "{\"failPages\":[1]}");
        Result<OcrRun> r = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId,
            c.Pages.Select(x => x.PageId).ToArray());
        Result<OcrCandidateAdoption> adoption =
            await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId, [c.Pages[1].PageId]);
        adoption.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AdoptCandidateRun_records_candidate_adoption()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId);
        (await c.CountAdoptionsAsync(r.Value.OcrRunId)).Should().Be(1);
    }

    [Fact]
    public async Task AdoptCandidateRun_rejects_failed_run()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync(parameters: "{\"failPages\":[0]}");
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        Result<OcrCandidateAdoption> adoption = await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId);
        adoption.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Adoption_keeps_only_one_current_revision()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        await c.Coordinator.AdoptCandidateRunAsync(r.Value.OcrRunId);
        (await c.CountCurrentRevisionsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Adoption_is_serialized_per_document_instance()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r1 =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        Result<OcrRun> r2 =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[1].PageId]);
        await Task.WhenAll(c.Coordinator.AdoptCandidateRunAsync(r1.Value.OcrRunId),
            c.Coordinator.AdoptCandidateRunAsync(r2.Value.OcrRunId));
        (await c.CountCurrentRevisionsAsync()).Should().Be(1);
        (await c.CountAdoptionsTotalAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Staging_revision_is_not_current_before_adoption()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> r =
            await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        (await c.IsCurrentAsync(r.Value.OutputRevisionId!.Value)).Should().BeFalse();
    }

    [Fact]
    public async Task Cancelled_run_does_not_leave_current_revision()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        Result<OcrRun> pending =
            await c.Coordinator.CreatePendingRunForTestAsync(c.DocumentInstanceId, p.Value.PresetId);
        await c.Coordinator.CancelRunAsync(pending.Value.OcrRunId);
        (await c.CountCurrentRevisionsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Ocr_run_does_not_create_search_units_or_evidence_refs()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        Result<OcrPreset> p = await c.CreatePresetAsync();
        await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, p.Value.PresetId, [c.Pages[0].PageId]);
        await using SqliteConnection cn = c.Database.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<int>("select count(1) from search_units;")).Should().Be(0);
        IReadOnlyList<string> tables = await c.TableNamesAsync();
        tables.Should().NotContain("evidence_refs");
    }

    [Fact]
    public async Task MigrationRunner_applies_ocr_lifecycle_migration()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using SqliteConnection cn = db.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        int count = await cn.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type='table' and name in ('ocr_presets','ocr_preset_versions','ocr_runs','ocr_page_results','ocr_candidate_adoptions');");
        count.Should().Be(5);
    }

    [Fact]
    public async Task MigrationRunner_adds_ocr_run_hidden_marker()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using SqliteConnection cn = db.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        string[] columns = (await cn.QueryAsync<string>("select name from pragma_table_info('ocr_runs');")).ToArray();
        columns.Should().Contain("hidden");
    }

    [Fact]
    public async Task Foreign_keys_prevent_orphan_ocr_page_result()
    {
        await using OcrTestContext c = await OcrTestContext.CreateAsync();
        await using SqliteConnection cn = c.Database.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        // FluentAssertions invokes and awaits the delegate before the connection leaves this scope.
        // ReSharper disable once AccessToDisposedClosure
        Func<Task> action = () =>
            cn.ExecuteAsync(
                "insert into ocr_page_results (result_id, ocr_run_id, page_id, state, created_at, updated_at) values (@Id, @Run, @Page, 'pending', @Now, @Now);",
                new
                {
                    Id = OcrPageResultId.New().ToString(), Run = OcrRunId.New().ToString(),
                    Page = PageId.New().ToString(), Now = DateTimeOffset.UtcNow.ToString("O")
                });
        await action.Should().ThrowAsync<SqliteException>();
    }

    private sealed class OcrTestContext : IAsyncDisposable
    {
        private OcrTestContext(TemporarySqliteDatabase database, FixedClock clock,
            DocumentInstanceId documentInstanceId, IReadOnlyList<Page> pages, ItemService itemService,
            DocumentInstanceService documentInstanceService, PageService pageService, OcrPresetService presetService,
            OcrRunCoordinator coordinator, LayoutTreeService layoutTreeService)
        {
            Database = database;
            Clock = clock;
            DocumentInstanceId = documentInstanceId;
            Pages = pages;
            ItemService = itemService;
            DocumentInstanceService = documentInstanceService;
            PageService = pageService;
            PresetService = presetService;
            Coordinator = coordinator;
            LayoutTreeService = layoutTreeService;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public IReadOnlyList<Page> Pages { get; }
        public ItemService ItemService { get; }
        public DocumentInstanceService DocumentInstanceService { get; }
        public PageService PageService { get; }
        public OcrPresetService PresetService { get; }
        public OcrRunCoordinator Coordinator { get; }
        public LayoutTreeService LayoutTreeService { get; }

        public static async Task<OcrTestContext> CreateAsync(bool withSearchDirtyMarker = false)
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(new DateTimeOffset(2026, 6, 19, 5, 0, 0, TimeSpan.Zero));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService lib = new(db.ConnectionFactory, clock);
            await lib.CreateLibraryAsync("OCR library");
            ItemService itemService = new(db.ConnectionFactory, lib, clock);
            DocumentInstanceService documentInstanceService = new(db.ConnectionFactory, clock);
            Result<ItemMetadata> item = await itemService.CreateItemAsync("book", "OCR book");
            Result<DocumentInstance> doc =
                await documentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
                    DocumentInstanceType.PrimaryScan);
            PageService pageSvc = new(db.ConnectionFactory, clock);
            List<Page> pages = new();
            for (int i = 0; i < 3; i++)
            {
                pages.Add((await pageSvc.CreatePageAsync(doc.Value.DocumentInstanceId, i, null, 100, 200, 0,
                    CoordinateBasis.NormalizedPage, 100, 200, "renderer-v1", null)).Value);
            }

            LayoutTreeService layout = new(db.ConnectionFactory, clock);
            await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Mock, true);
            OcrPresetService preset = new(db.ConnectionFactory, lib, clock);
            SearchUnitBuilder? dirtyMarker =
                withSearchDirtyMarker ? new SearchUnitBuilder(db.ConnectionFactory, clock) : null;
            OcrRunCoordinator coordinator = new(db.ConnectionFactory, clock, new MockOcrEngine(), dirtyMarker,
                new OcrLayoutImporter(db.ConnectionFactory, clock));
            return new OcrTestContext(db, clock, doc.Value.DocumentInstanceId, pages, itemService,
                documentInstanceService, pageSvc, preset, coordinator, layout);
        }

        public Task<Result<OcrPreset>> CreatePresetAsync(bool apply = false, string parameters = "{}")
        {
            return PresetService.CreatePresetAsync("Mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null,
                parameters, apply);
        }

        public async Task<DocumentInstanceId> CreateDocumentInstanceWithoutPagesAsync()
        {
            Result<ItemMetadata> item = await ItemService.CreateItemAsync("book", "Empty OCR book");
            Result<DocumentInstance> doc =
                await DocumentInstanceService.AttachDocumentInstanceAsync(item.Value.ItemId, null,
                    DocumentInstanceType.Supplement);
            return doc.Value.DocumentInstanceId;
        }

        public async Task<int> CountNodesAsync(LayoutRevisionId rev)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>("select count(1) from layout_nodes where revision_id = @Rev;",
                new { Rev = rev.ToString() });
        }

        public async Task<int> CountRunsAsync()
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>("select count(1) from ocr_runs;");
        }

        public async Task<IReadOnlyList<PageId>> PageIdsWithNodesAsync(LayoutRevisionId rev)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            IEnumerable<string> ids = await cn.QueryAsync<string>(
                "select distinct page_id from layout_nodes where revision_id = @Rev order by page_id;",
                new { Rev = rev.ToString() });
            return ids.Select(PageId.Parse).ToArray();
        }

        public async Task AddTableHierarchyAsync(LayoutRevisionId rev, PageId pageId)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            string table = LayoutNodeId.New().ToString();
            string header = LayoutNodeId.New().ToString();
            string body = LayoutNodeId.New().ToString();
            var cells = new[]
            {
                new
                {
                    Id = LayoutNodeId.New().ToString(), Parent = header, Text = "Name", Row = 0, Col = 0, Header = 1,
                    Order = 12
                },
                new
                {
                    Id = LayoutNodeId.New().ToString(), Parent = header, Text = "Value", Row = 0, Col = 1, Header = 1,
                    Order = 13
                },
                new
                {
                    Id = LayoutNodeId.New().ToString(), Parent = body, Text = "Pages", Row = 1, Col = 0, Header = 0,
                    Order = 15
                },
                new
                {
                    Id = LayoutNodeId.New().ToString(), Parent = body, Text = "12", Row = 1, Col = 1, Header = 0,
                    Order = 16
                }
            };
            await cn.ExecuteAsync(
                """
                insert into layout_nodes (node_id, document_instance_id, page_id, parent_node_id, node_type, own_text, text_policy, reading_order, source, revision_id, confidence, ignored)
                values (@NodeId, @DocumentInstanceId, @PageId, @ParentNodeId, @NodeType, @OwnText, @TextPolicy, @ReadingOrder, @Source, @RevisionId, null, 0);
                """,
                new[]
                {
                    new
                    {
                        NodeId = table, DocumentInstanceId = DocumentInstanceId.ToString(), PageId = pageId.ToString(),
                        ParentNodeId = (string?)null, NodeType = LayoutNodeType.Table, OwnText = (string?)null,
                        TextPolicy = TextPolicy.AggregateChildren, ReadingOrder = 10, Source = LayoutNodeSource.Ocr,
                        RevisionId = rev.ToString()
                    },
                    new
                    {
                        NodeId = header, DocumentInstanceId = DocumentInstanceId.ToString(), PageId = pageId.ToString(),
                        ParentNodeId = (string?)table, NodeType = LayoutNodeType.TableRow, OwnText = (string?)null,
                        TextPolicy = TextPolicy.AggregateChildren, ReadingOrder = 11, Source = LayoutNodeSource.Ocr,
                        RevisionId = rev.ToString()
                    },
                    new
                    {
                        NodeId = body, DocumentInstanceId = DocumentInstanceId.ToString(), PageId = pageId.ToString(),
                        ParentNodeId = (string?)table, NodeType = LayoutNodeType.TableRow, OwnText = (string?)null,
                        TextPolicy = TextPolicy.AggregateChildren, ReadingOrder = 14, Source = LayoutNodeSource.Ocr,
                        RevisionId = rev.ToString()
                    }
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
                    new
                    {
                        NodeId = cell.Id, DocumentInstanceId = DocumentInstanceId.ToString(),
                        PageId = pageId.ToString(), ParentNodeId = cell.Parent, NodeType = LayoutNodeType.TableCell,
                        OwnText = cell.Text, TextPolicy = TextPolicy.Own, ReadingOrder = cell.Order,
                        Source = LayoutNodeSource.Ocr, RevisionId = rev.ToString(), RowIndex = cell.Row,
                        ColIndex = cell.Col, IsHeader = cell.Header
                    });
            }
        }

        public async Task<int> CountOrphanParentRefsAsync(LayoutRevisionId rev)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>(
                "select count(1) from layout_nodes child left join layout_nodes parent on parent.node_id = child.parent_node_id and parent.revision_id = child.revision_id where child.revision_id = @Rev and child.parent_node_id is not null and parent.node_id is null;",
                new { Rev = rev.ToString() });
        }

        public async Task<IReadOnlyList<TableCellRow>> TableCellsAsync(LayoutRevisionId rev)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return (await cn.QueryAsync<TableCellRow>(
                "select own_text as OwnText, row_index as RowIndex, col_index as ColIndex, row_span as RowSpan, col_span as ColSpan, is_header as IsHeader from layout_nodes where revision_id = @Rev and node_type = @NodeType order by row_index, col_index;",
                new { Rev = rev.ToString(), NodeType = LayoutNodeType.TableCell })).ToArray();
        }

        public async Task<int> CountCellsWithTableRowParentAsync(LayoutRevisionId rev)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>(
                "select count(1) from layout_nodes cell join layout_nodes row on row.node_id = cell.parent_node_id and row.revision_id = cell.revision_id where cell.revision_id = @Rev and cell.node_type = @Cell and row.node_type = @Row;",
                new { Rev = rev.ToString(), Cell = LayoutNodeType.TableCell, Row = LayoutNodeType.TableRow });
        }

        public async Task<int> CountAdoptionsAsync(OcrRunId run)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>(
                "select count(1) from ocr_candidate_adoptions where ocr_run_id = @Run;", new { Run = run.ToString() });
        }

        public async Task<int> CountAdoptionsTotalAsync()
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>("select count(1) from ocr_candidate_adoptions;");
        }

        public async Task<int> CountCurrentRevisionsAsync()
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>(
                "select count(1) from layout_revisions where document_instance_id = @Doc and is_current = 1;",
                new { Doc = DocumentInstanceId.ToString() });
        }

        public async Task<int> CountCurrentSearchUnitsAsync()
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>(
                "select count(1) from search_units where document_instance_id = @Doc and status = @Status;",
                new { Doc = DocumentInstanceId.ToString(), Status = SearchUnitStatus.Current });
        }

        public async Task<SearchUnitId> CurrentSearchUnitIdAsync()
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return SearchUnitId.Parse((await cn.ExecuteScalarAsync<string>(
                "select unit_id from search_units where document_instance_id = @Doc and status = @Status limit 1;",
                new { Doc = DocumentInstanceId.ToString(), Status = SearchUnitStatus.Current }))!);
        }

        public async Task RebuildSearchAsync()
        {
            SearchUnitBuilder builder = new(Database.ConnectionFactory, Clock);
            SearchIndexRebuilder rebuilder = new(Database.ConnectionFactory, Clock);
            await builder.RebuildForDocumentInstanceAsync(DocumentInstanceId);
            await rebuilder.RebuildFtsForDocumentInstanceAsync(DocumentInstanceId);
            await rebuilder.RebuildFtsForLibraryAsync();
        }

        public async Task<SearchResultPage> SearchTextAsync(string query)
        {
            return (await new SqliteSearchService(Database.ConnectionFactory).SearchLibraryAsync(
                new SearchRequest(query))).Value;
        }

        public async Task<McpSearchLibraryResponse> McpSearchTextAsync(string query, EvidenceReferenceService evidence)
        {
            return (await new McpReadApi(Database.ConnectionFactory,
                    new SqliteSearchService(Database.ConnectionFactory), evidence)
                .SearchLibraryAsync(new McpSearchLibraryRequest(query))).Value;
        }

        public async Task<bool> IsCurrentAsync(LayoutRevisionId rev)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>(
                "select is_current from layout_revisions where layout_revision_id = @Rev;",
                new { Rev = rev.ToString() }) == 1;
        }

        public async Task<bool> IsRunHiddenAsync(OcrRunId run)
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<int>("select hidden from ocr_runs where ocr_run_id = @Run;",
                new { Run = run.ToString() }) == 1;
        }

        public async Task<string?> SearchIndexStatusAsync()
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return await cn.ExecuteScalarAsync<string?>(
                "select status from search_index_status where scope_type = 'document_instance' and scope_id = @Doc;",
                new { Doc = DocumentInstanceId.ToString() });
        }

        public async Task<IReadOnlyList<string>> TableNamesAsync()
        {
            await using SqliteConnection cn = Database.ConnectionFactory.CreateConnection();
            await cn.OpenAsync();
            return (await cn.QueryAsync<string>("select name from sqlite_master where type='table';")).ToArray();
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
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
