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
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Search;
using Patchouli.Evidence;
using Patchouli.Ocr;
using Patchouli.Search;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Library;

namespace Patchouli.Tests;

public sealed class SearchTests
{
    [Fact]
    public async Task RebuildForDocumentInstance_creates_units_from_current_layout()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("alpha");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.CountUnitsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RebuildForDocumentInstance_uses_current_revision_only()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("old");
        Result<LayoutRevision> next =
            await c.LayoutTreeService.CreateLayoutRevisionAsync(c.DocumentInstanceId, LayoutRevisionSource.Manual,
                true);
        c.RevisionId = next.Value.LayoutRevisionId;
        await c.AddNodeAsync("new");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.UnitTextsAsync()).Should().Equal("new");
    }

    [Fact]
    public async Task SearchUnit_excludes_ignored_nodes()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("hidden", ignored: true);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.CountUnitsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SearchUnit_excludes_header_footer_page_number()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("h", LayoutNodeType.Header);
        await c.AddNodeAsync("f", LayoutNodeType.Footer);
        await c.AddNodeAsync("1", LayoutNodeType.PageNumber);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.CountUnitsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SearchUnit_excludes_marginalia_annotation_by_default()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("m", LayoutNodeType.Marginalia);
        await c.AddNodeAsync("a", LayoutNodeType.Annotation);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.CountUnitsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TextPolicy_own_creates_unit()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("own", textPolicy: TextPolicy.Own);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.UnitTextsAsync()).Should().Contain("own");
    }

    [Fact]
    public async Task TextPolicy_aggregate_children_creates_aggregated_unit()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> parent = await c.AddNodeAsync(null, LayoutNodeType.Block, TextPolicy.AggregateChildren);
        await c.AddNodeAsync("child one", parentNodeId: parent.Value.NodeId);
        await c.AddNodeAsync("child two", parentNodeId: parent.Value.NodeId, readingOrder: 2);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.UnitTextsAsync()).Should().Contain(t => t.Contains("child one") && t.Contains("child two"));
        (await c.CountUnitsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TextPolicy_none_creates_no_unit()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("none", textPolicy: TextPolicy.None);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.CountUnitsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Table_degrades_without_duplicate_child_indexing()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> table = await c.AddNodeAsync(null, LayoutNodeType.Table, TextPolicy.AggregateChildren);
        await c.AddNodeAsync("cell text", LayoutNodeType.TableCell, parentNodeId: table.Value.NodeId);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.UnitTextsAsync()).Should().Equal("[Table]");
    }

    [Fact]
    public async Task Table_with_regular_cell_metadata_indexes_markdown()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> table = await c.AddNodeAsync(null, LayoutNodeType.Table, TextPolicy.AggregateChildren);
        Result<LayoutNode> header = await c.AddNodeAsync(null, LayoutNodeType.TableRow, TextPolicy.AggregateChildren,
            parentNodeId: table.Value.NodeId);
        Result<LayoutNode> body = await c.AddNodeAsync(null, LayoutNodeType.TableRow, TextPolicy.AggregateChildren, 2,
            parentNodeId: table.Value.NodeId);
        await c.AddNodeAsync("Name", LayoutNodeType.TableCell, parentNodeId: header.Value.NodeId, rowIndex: 0,
            colIndex: 0, isHeader: true);
        await c.AddNodeAsync("Value", LayoutNodeType.TableCell, readingOrder: 2, parentNodeId: header.Value.NodeId,
            rowIndex: 0, colIndex: 1, isHeader: true);
        await c.AddNodeAsync("Pages", LayoutNodeType.TableCell, parentNodeId: body.Value.NodeId, rowIndex: 1,
            colIndex: 0);
        await c.AddNodeAsync("12", LayoutNodeType.TableCell, readingOrder: 2, parentNodeId: body.Value.NodeId,
            rowIndex: 1, colIndex: 1);
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.UnitTextsAsync()).Should().Equal("| Name | Value |\n| --- | --- |\n| Pages | 12 |");
    }

    [Fact]
    public async Task Rebuild_preserves_unit_id_for_text_edit_same_node_revision()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> node = await c.AddNodeAsync("before");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        string[] first = await c.UnitIdsAsync();
        await c.LayoutTreeService.UpdateNodeTextAsync(node.Value.NodeId, "after");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.UnitIdsAsync()).Should().Equal(first);
        (await c.UnitTextsAsync()).Should().Equal("after");
    }

    [Fact]
    public async Task Rebuild_links_evidence_successor_when_new_revision_replaces_unit()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("before");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        EvidenceReferenceService evidence = new(c.Database.ConnectionFactory, c.Clock);
        Result<EvidenceRefRecord> oldRef =
            await evidence.CreateFromSearchUnitAsync(SearchUnitId.Parse((await c.UnitIdsAsync()).Single()));
        Result<LayoutRevision> next =
            await c.LayoutTreeService.CreateLayoutRevisionAsync(c.DocumentInstanceId, LayoutRevisionSource.OcrAdopted,
                true);
        c.RevisionId = next.Value.LayoutRevisionId;
        await c.AddNodeAsync("after");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        Result<EvidenceResolutionResult> pinned = await evidence.ResolveAsync(oldRef.Value.EvidenceRefId);
        Result<EvidenceResolutionResult> current =
            await evidence.ResolveAsync(oldRef.Value.EvidenceRefId, EvidenceResolutionMode.Current);
        pinned.Value.Status.Should().Be(EvidenceResolutionStatus.Superseded);
        pinned.Value.SuccessorEvidenceRefs.Should().HaveCount(1);
        current.Value.Status.Should().Be(EvidenceResolutionStatus.FoundCurrent);
        current.Value.CurrentText.Should().Be("after");
        (await c.UnitTextsAsync()).Should().Equal("after");
    }

    [Fact]
    public async Task Deleted_or_no_longer_indexable_node_marks_unit_deleted_or_stale()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> node = await c.AddNodeAsync("gone");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        await c.LayoutTreeService.UpdateNodeTextAsync(node.Value.NodeId, " ");
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.CountUnitsByStatusAsync(SearchUnitStatus.Deleted)).Should().Be(1);
    }

    [Fact]
    public async Task Bbox_union_is_recorded()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("box", bbox: new NormalizedBBox(0.1, 0.2, 0.3, 0.4));
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.Connection().ExecuteScalarAsync<string>("select bbox_union_json from search_units limit 1;")).Should()
            .Contain("0.1");
    }

    [Fact]
    public async Task Layout_edit_type_and_bbox_preserves_search_unit_identity()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> node = await c.AddNodeAsync("alpha", bbox: new NormalizedBBox(.1, .1, .2, .1));
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        string[] before = await c.UnitIdsAsync();
        await c.LayoutTreeService.UpdateNodeTypeAsync(node.Value.NodeId, LayoutNodeType.Heading);
        await c.LayoutTreeService.UpdateNodeBBoxAsync(node.Value.NodeId, new NormalizedBBox(.1, .2, .2, .1));
        await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.UnitIdsAsync()).Should().Equal(before);
        LayoutNode updated = (await c.LayoutTreeService.ListNodesForPageAsync(c.PageId, c.RevisionId)).Value.Single();
        updated.NodeType.Should().Be(LayoutNodeType.Heading);
        updated.BBox.Should().Be(new NormalizedBBox(.1, .2, .2, .1));
    }

    [Fact]
    public async Task Layout_update_bbox_rejects_ordinary_overlap()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("alpha", bbox: new NormalizedBBox(.1, .1, .2, .1));
        Result<LayoutNode> second = await c.AddNodeAsync("beta", bbox: new NormalizedBBox(.5, .1, .2, .1));
        Result result =
            await c.LayoutTreeService.UpdateNodeBBoxAsync(second.Value.NodeId, new NormalizedBBox(.15, .1, .2, .1));
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Layout_split_node_text_creates_following_node()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> node = await c.AddNodeAsync("alpha beta");
        Result<LayoutNode> split = await c.LayoutTreeService.SplitNodeTextAsync(node.Value.NodeId, 5);
        split.Value.OwnText.Should().Be("beta");
        (await c.NodeTextsAsync()).Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task Layout_merge_text_nodes_keeps_first_and_removes_second()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> first = await c.AddNodeAsync("alpha", bbox: new NormalizedBBox(.1, .1, .2, .1));
        Result<LayoutNode> second =
            await c.AddNodeAsync("beta", readingOrder: 2, bbox: new NormalizedBBox(.4, .1, .2, .1));
        Result<LayoutNode> merged =
            await c.LayoutTreeService.MergeTextNodesAsync(first.Value.NodeId, second.Value.NodeId);
        merged.Value.OwnText.Should().Be("alpha\nbeta");
        (await c.NodeTextsAsync()).Should().Equal("alpha\nbeta");
    }

    [Fact]
    public async Task Layout_merge_text_nodes_rejects_nodes_with_children()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> first = await c.AddNodeAsync("alpha");
        await c.AddNodeAsync("child", parentNodeId: first.Value.NodeId);
        Result<LayoutNode> second = await c.AddNodeAsync("beta", readingOrder: 2);
        Result<LayoutNode> result =
            await c.LayoutTreeService.MergeTextNodesAsync(first.Value.NodeId, second.Value.NodeId);
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await c.NodeTextsAsync()).Should().Contain("child");
    }

    [Fact]
    public async Task Layout_create_parent_for_nodes_groups_selection()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        Result<LayoutNode> first = await c.AddNodeAsync("alpha", bbox: new NormalizedBBox(.1, .1, .2, .1));
        Result<LayoutNode> second =
            await c.AddNodeAsync("beta", readingOrder: 2, bbox: new NormalizedBBox(.4, .1, .2, .1));
        Result<LayoutNode> parent = await c.LayoutTreeService.CreateParentForNodesAsync(
            [first.Value.NodeId, second.Value.NodeId], LayoutNodeType.Block, TextPolicy.AggregateChildren, 1);
        parent.Value.BBox!.Value.X.Should().BeApproximately(.1, 0.000001);
        parent.Value.BBox.Value.Width.Should().BeApproximately(.5, 0.000001);
        (await c.LayoutTreeService.BuildPagePlainTextAsync(c.PageId, c.RevisionId)).Value.Text.Should()
            .Be("alpha\nbeta");
    }

    [Fact]
    public async Task RebuildFtsForDocumentInstance_populates_fts()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("latin alpha");
        await c.Rebuilder.RebuildFtsForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.CountFtsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RebuildFtsForLibrary_populates_all_current_units()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("latin alpha");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        (await c.CountFtsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RebuildFts_marks_index_status_current()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("latin alpha");
        await c.Rebuilder.RebuildFtsForDocumentInstanceAsync(c.DocumentInstanceId);
        (await c.StatusAsync(SearchIndexScopeType.DocumentInstance, c.DocumentInstanceId.ToString())).Should()
            .Be(SearchIndexStatusValue.Current);
    }

    [Fact]
    public async Task MarkDocumentInstanceDirty_sets_stale_status()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.Builder.MarkDocumentInstanceDirtyAsync(c.DocumentInstanceId);
        (await c.StatusAsync(SearchIndexScopeType.DocumentInstance, c.DocumentInstanceId.ToString())).Should()
            .Be(SearchIndexStatusValue.Stale);
    }

    [Fact]
    public async Task SetIndexUnavailable_causes_search_to_return_empty_without_fallback()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("only in units");
        await c.Rebuilder.SetIndexUnavailableAsync(SearchIndexScopeType.Library, c.LibraryId.ToString(),
            "fts unavailable");
        Result<SearchResultPage> result = await c.Search.SearchLibraryAsync(new SearchRequest("only"));
        result.Value.Results.Should().BeEmpty();
        result.Value.AffectedScopesSummary.Should().Be("fts unavailable");
    }

    [Fact]
    public async Task SearchLibrary_rejects_blank_query()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        (await c.Search.SearchLibraryAsync(new SearchRequest(" "))).ErrorCode.Should()
            .Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task SearchLibrary_returns_page_level_results()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("latin alpha");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        Result<SearchResultPage> result = await c.Search.SearchLibraryAsync(new SearchRequest("alpha"));
        result.Value.Results.Single().PageId.Should().Be(c.PageId);
    }

    [Fact]
    public async Task SearchLibrary_groups_multiple_units_by_page()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("alpha one");
        await c.AddNodeAsync("alpha two", readingOrder: 2);
        await c.RebuildAllAsync();
        Result<SearchResultPage> result = await c.Search.SearchLibraryAsync(new SearchRequest("alpha"));
        result.Value.Results.Should().HaveCount(1);
        result.Value.Results.Single().MatchedUnits.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchLibrary_limits_matched_units_per_page_to_5()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        for (int i = 0; i < 6; i++)
        {
            await c.AddNodeAsync($"alpha {i}", readingOrder: i);
        }

        await c.RebuildAllAsync();
        Result<SearchResultPage> result = await c.Search.SearchLibraryAsync(new SearchRequest("alpha"));
        result.Value.Results.Single().MatchedUnits.Should().HaveCount(5);
        result.Value.Results.Single().MatchedUnitsHasMore.Should().BeTrue();
    }

    [Fact]
    public async Task SearchLibrary_supports_page_size_and_cursor()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("alpha p0");
        Result<Page> p1 = await c.CreatePageAsync(1);
        await c.AddNodeAsync("alpha p1", pageId: p1.Value.PageId);
        Result<Page> p2 = await c.CreatePageAsync(2);
        await c.AddNodeAsync("alpha p2", pageId: p2.Value.PageId);
        await c.RebuildAllAsync();
        Result<SearchResultPage> first = await c.Search.SearchLibraryAsync(new SearchRequest("alpha", PageSize: 1));
        Result<SearchResultPage> second =
            await c.Search.SearchLibraryAsync(new SearchRequest("alpha", PageSize: 1, Cursor: first.Value.NextCursor));
        first.Value.Results.Should().HaveCount(1);
        second.Value.Results.Should().HaveCount(1);
        second.Value.Results.Single().PageId.Should().NotBe(first.Value.Results.Single().PageId);
    }

    [Fact]
    public async Task SearchLibrary_returns_partial_or_stale_index_status()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("alpha");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        await c.Builder.MarkDocumentInstanceDirtyAsync(c.DocumentInstanceId);
        Result<SearchResultPage> result = await c.Search.SearchLibraryAsync(new SearchRequest("alpha"));
        result.Value.IndexStatus.Should().Be(SearchIndexStatusValue.Stale);
        result.Value.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchLibrary_does_not_return_evidence_ref()
    {
        typeof(SearchMatchedUnit).GetProperties().Select(p => p.Name).Should()
            .NotContain(name => name.Contains("Evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchLibrary_can_find_chinese_text()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("这是 OCR 测试文本。");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        (await c.Search.SearchLibraryAsync(new SearchRequest("测试"))).Value.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchLibrary_can_find_cjk_trigram_substrings()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("晚清地方档案目录");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        (await c.Search.SearchLibraryAsync(new SearchRequest("地方档"))).Value.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchLibrary_can_find_mixed_latin_and_cjk_tokens()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("Patchouli 中文 OCR Archive 2026");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        (await c.Search.SearchLibraryAsync(new SearchRequest("中文 archive"))).Value.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchLibrary_can_find_latin_text()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("Latin archive keyword");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        (await c.Search.SearchLibraryAsync(new SearchRequest("archive"))).Value.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchLibrary_filters_by_document_instance_when_requested()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("alpha");
        DocumentInstanceId other = await c.CreateDocumentInstanceAsync("Other");
        Result<Page> page = await c.PageService.CreatePageAsync(other, 0, null, null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
        Result<LayoutRevision> rev =
            await c.LayoutTreeService.CreateLayoutRevisionAsync(other, LayoutRevisionSource.Mock, true);
        await c.LayoutTreeService.AddNodeAsync(rev.Value.LayoutRevisionId, page.Value.PageId, null,
            LayoutNodeType.Paragraph, null, "alpha", TextPolicy.Own, 1, LayoutNodeSource.Mock);
        await c.Builder.RebuildForDocumentInstanceAsync(other);
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        DocumentInstanceId expectedDocument = c.DocumentInstanceId;
        Result<SearchResultPage> result =
            await c.Search.SearchLibraryAsync(new SearchRequest("alpha", expectedDocument));
        result.Value.Results.Should().OnlyContain(r => r.DocumentInstanceId == expectedDocument);
    }

    [Fact]
    public async Task GetSearchResultContext_returns_sibling_units()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("before", readingOrder: 1);
        await c.AddNodeAsync("match", readingOrder: 2);
        await c.AddNodeAsync("after", readingOrder: 3);
        await c.RebuildAllAsync();
        string id = (await c.UnitIdsAsync())[1];
        Result<IReadOnlyList<SearchMatchedUnit>> ctx =
            await c.Search.GetSearchResultContextAsync(SearchUnitId.Parse(id));
        ctx.Value.Select(u => u.Text).Should().Equal("before", "match", "after");
    }

    [Fact]
    public async Task GetSearchResultContext_does_not_cross_page()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await c.AddNodeAsync("same");
        Result<Page> p = await c.CreatePageAsync(1);
        await c.AddNodeAsync("other", pageId: p.Value.PageId);
        await c.RebuildAllAsync();
        await using SqliteConnection cn = c.Connection();
        string? id =
            await cn.ExecuteScalarAsync<string>("select unit_id from search_units where resolved_text = 'same';");
        Result<IReadOnlyList<SearchMatchedUnit>> ctx =
            await c.Search.GetSearchResultContextAsync(SearchUnitId.Parse(id!), 10, 10);
        PageId expectedPage = c.PageId;
        ctx.Value.Should().OnlyContain(u => u.PageId == expectedPage);
    }

    [Fact]
    public async Task GetSearchResultContext_caps_before_after_at_10()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        for (int i = 0; i < 30; i++)
        {
            await c.AddNodeAsync($"u{i}", readingOrder: i);
        }

        await c.RebuildAllAsync();
        string id = (await c.UnitIdsAsync())[15];
        Result<IReadOnlyList<SearchMatchedUnit>> ctx =
            await c.Search.GetSearchResultContextAsync(SearchUnitId.Parse(id), 99, 99);
        ctx.Value.Should().HaveCount(21);
    }

    [Fact]
    public async Task GetSearchResultContext_marks_match_unit()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("match");
        string id = (await c.UnitIdsAsync()).Single();
        Result<IReadOnlyList<SearchMatchedUnit>> ctx =
            await c.Search.GetSearchResultContextAsync(SearchUnitId.Parse(id));
        ctx.Value.Single().IsMatch.Should().BeTrue();
    }

    [Fact]
    public async Task MigrationRunner_applies_search_migration()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using SqliteConnection cn = db.ConnectionFactory.CreateConnection();
        await cn.OpenAsync();
        (await cn.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where name in ('search_units','search_index_status','search_units_fts');"))
            .Should().Be(3);
    }

    [Fact]
    public async Task FTS5_table_is_created_and_queryable()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("alpha");
        await c.Rebuilder.RebuildFtsForLibraryAsync();
        (await c.Connection()
                .ExecuteScalarAsync<int>("select count(1) from search_units_fts where search_units_fts match 'alpha';"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Foreign_keys_prevent_orphan_search_unit()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        await using SqliteConnection connection = c.Connection();
        // FluentAssertions invokes and awaits the delegate before the connection leaves this scope.
        // ReSharper disable once AccessToDisposedClosure
        Func<Task<int>> act = () => connection.ExecuteAsync(
            "insert into search_units (unit_id, document_instance_id, page_id, root_node_id, text_revision_id, bbox_revision_id, layout_revision_id, resolved_text, node_type, reading_order, status, created_at, updated_at) values (@Id, @Doc, @Page, @Node, @Rev, @Rev, @Rev, 'x', 'paragraph', 1, 'current', @Now, @Now);",
            new
            {
                Id = SearchUnitId.New().ToString(), Doc = DocumentInstanceId.New().ToString(),
                Page = PageId.New().ToString(), Node = LayoutNodeId.New().ToString(),
                Rev = LayoutRevisionId.New().ToString(), Now = DateTimeOffset.UtcNow.ToString("O")
            });
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Search_does_not_use_SQL_LIKE_when_fts_unavailable()
    {
        await using SearchTestContext c = await SearchTestContext.CreateWithUnitAsync("not in fts fallback text");
        await c.Rebuilder.SetIndexUnavailableAsync(SearchIndexScopeType.Library, c.LibraryId.ToString(), "off");
        Result<SearchResultPage> result = await c.Search.SearchLibraryAsync(new SearchRequest("fallback"));
        result.Value.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task Ocr_adoption_marks_document_instance_dirty_when_marker_is_configured()
    {
        await using SearchTestContext c = await SearchTestContext.CreateAsync();
        LibraryIdentityService library = new(c.Database.ConnectionFactory, c.Clock);
        OcrPresetService preset = new(c.Database.ConnectionFactory, library, c.Clock);
        Result<OcrPreset> created =
            await preset.CreatePresetAsync("mock", null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", true);
        OcrRunCoordinator coordinator = new(c.Database.ConnectionFactory, c.Clock, searchDirtyMarker: c.Builder);
        await coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, created.Value.PresetId, [c.PageId]);
        (await c.StatusAsync(SearchIndexScopeType.DocumentInstance, c.DocumentInstanceId.ToString())).Should()
            .Be(SearchIndexStatusValue.Stale);
    }

    private sealed class SearchTestContext : IAsyncDisposable
    {
        private SearchTestContext(TemporarySqliteDatabase database, FixedClock clock, LibraryId libraryId,
            ItemId itemId, DocumentInstanceId documentInstanceId, PageId pageId, LayoutRevisionId revisionId)
        {
            Database = database;
            Clock = clock;
            LibraryId = libraryId;
            ItemId = itemId;
            DocumentInstanceId = documentInstanceId;
            PageId = pageId;
            RevisionId = revisionId;
            PageService = new PageService(database.ConnectionFactory, clock);
            LayoutTreeService = new LayoutTreeService(database.ConnectionFactory, clock);
            Builder = new SearchUnitBuilder(database.ConnectionFactory, clock);
            Rebuilder = new SearchIndexRebuilder(database.ConnectionFactory, clock);
            Search = new SqliteSearchService(database.ConnectionFactory);
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public ItemId ItemId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageId PageId { get; }
        public LayoutRevisionId RevisionId { get; set; }
        public PageService PageService { get; }
        public LayoutTreeService LayoutTreeService { get; }
        public SearchUnitBuilder Builder { get; }
        public SearchIndexRebuilder Rebuilder { get; }
        public SqliteSearchService Search { get; }

        public static async Task<SearchTestContext> CreateAsync()
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            Result<LibraryMetadata> library =
                await new LibraryIdentityService(db.ConnectionFactory, clock).CreateLibraryAsync("Search Library");
            Result<ItemMetadata> item =
                await new ItemService(db.ConnectionFactory, new LibraryIdentityService(db.ConnectionFactory, clock),
                    clock).CreateItemAsync("book", "Search Item");
            Result<DocumentInstance> doc =
                await new DocumentInstanceService(db.ConnectionFactory, clock).AttachDocumentInstanceAsync(
                    item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            PageService pageService = new(db.ConnectionFactory, clock);
            Result<Page> page = await pageService.CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
            LayoutTreeService layout = new(db.ConnectionFactory, clock);
            Result<LayoutRevision> revision =
                await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Mock, true);
            return new SearchTestContext(db, clock, library.Value.LibraryId, item.Value.ItemId,
                doc.Value.DocumentInstanceId, page.Value.PageId, revision.Value.LayoutRevisionId);
        }

        public static async Task<SearchTestContext> CreateWithUnitAsync(string text)
        {
            SearchTestContext c = await CreateAsync();
            await c.AddNodeAsync(text);
            await c.Builder.RebuildForDocumentInstanceAsync(c.DocumentInstanceId);
            return c;
        }

        public async Task<Result<Page>> CreatePageAsync(int index)
        {
            return await PageService.CreatePageAsync(DocumentInstanceId, index, (index + 1).ToString(), null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);
        }

        public async Task<DocumentInstanceId> CreateDocumentInstanceAsync(string title)
        {
            Result<ItemMetadata> item = await new ItemService(Database.ConnectionFactory,
                new LibraryIdentityService(Database.ConnectionFactory, Clock), Clock).CreateItemAsync("book", title);
            Result<DocumentInstance> doc =
                await new DocumentInstanceService(Database.ConnectionFactory, Clock).AttachDocumentInstanceAsync(
                    item.Value.ItemId, null, DocumentInstanceType.AlternateScan);
            return doc.Value.DocumentInstanceId;
        }

        public async Task<Result<LayoutNode>> AddNodeAsync(string? text, string nodeType = LayoutNodeType.Paragraph,
            string textPolicy = TextPolicy.Own, int readingOrder = 1, PageId? pageId = null,
            LayoutNodeId? parentNodeId = null, NormalizedBBox? bbox = null, bool ignored = false, int? rowIndex = null,
            int? colIndex = null, int? rowSpan = null, int? colSpan = null, bool isHeader = false)
        {
            return await LayoutTreeService.AddNodeAsync(RevisionId, pageId ?? PageId, parentNodeId, nodeType, bbox,
                text, textPolicy, readingOrder, LayoutNodeSource.Mock, ignored: ignored, rowIndex: rowIndex,
                colIndex: colIndex, rowSpan: rowSpan, colSpan: colSpan, isHeader: isHeader);
        }

        public async Task RebuildAllAsync()
        {
            await Builder.RebuildForDocumentInstanceAsync(DocumentInstanceId);
            await Rebuilder.RebuildFtsForLibraryAsync();
        }

        public SqliteConnection Connection()
        {
            SqliteConnection c = Database.ConnectionFactory.CreateConnection();
            c.Open();
            return c;
        }

        public async Task<int> CountUnitsAsync()
        {
            await using SqliteConnection cn = Connection();
            return await cn.ExecuteScalarAsync<int>("select count(1) from search_units where status = 'current';");
        }

        public async Task<int> CountUnitsByStatusAsync(string status)
        {
            await using SqliteConnection cn = Connection();
            return await cn.ExecuteScalarAsync<int>("select count(1) from search_units where status = @Status;",
                new { Status = status });
        }

        public async Task<int> CountFtsAsync()
        {
            await using SqliteConnection cn = Connection();
            return await cn.ExecuteScalarAsync<int>("select count(1) from search_units_fts;");
        }

        public async Task<string[]> UnitTextsAsync()
        {
            await using SqliteConnection cn = Connection();
            return (await cn.QueryAsync<string>(
                    "select resolved_text from search_units where status = 'current' order by reading_order, unit_id;"))
                .ToArray();
        }

        public async Task<string[]> UnitIdsAsync()
        {
            await using SqliteConnection cn = Connection();
            return (await cn.QueryAsync<string>(
                    "select unit_id from search_units where status = 'current' order by page_id, reading_order, unit_id;"))
                .ToArray();
        }

        public async Task<string?[]> NodeTextsAsync()
        {
            await using SqliteConnection cn = Connection();
            return (await cn.QueryAsync<string?>(
                "select own_text from layout_nodes where revision_id = @RevisionId order by reading_order, node_id;",
                new { RevisionId = RevisionId.ToString() })).ToArray();
        }

        public async Task<string?> StatusAsync(string scopeType, string scopeId)
        {
            await using SqliteConnection cn = Connection();
            return await cn.ExecuteScalarAsync<string?>(
                "select status from search_index_status where scope_type = @ScopeType and scope_id = @ScopeId;",
                new { ScopeType = scopeType, ScopeId = scopeId });
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
