using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Microsoft.Data.Sqlite;

namespace Patchouli.Tests;

public sealed class PageLayoutTests
{
    [Fact]
    public async Task CreatePage_creates_page_for_document_instance()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();

        var page = await context.PageService.CreatePageAsync(
            context.DocumentInstanceId, 0, "i", 100, 200, 0,
            CoordinateBasis.NormalizedPage, 100, 200, "renderer-v1", "hash");

        page.IsSuccess.Should().BeTrue();
        page.Value.DocumentInstanceId.Should().Be(context.DocumentInstanceId);
        page.Value.PageIndex.Should().Be(0);
        page.Value.CoordinateBasis.Should().Be(CoordinateBasis.NormalizedPage);
    }

    [Fact]
    public async Task CreatePage_rejects_negative_page_index()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();

        var result = await context.PageService.CreatePageAsync(
            context.DocumentInstanceId, -1, null, null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task CreatePage_rejects_invalid_rotation()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();

        var result = await context.PageService.CreatePageAsync(
            context.DocumentInstanceId, 0, null, null, null, 45,
            CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task CreatePage_rejects_duplicate_page_index()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        await context.CreatePageAsync(0);

        var duplicate = await context.CreatePageAsync(0);

        duplicate.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
    }

    [Fact]
    public async Task ListPages_returns_pages_in_page_index_order()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        await context.CreatePageAsync(2);
        await context.CreatePageAsync(0);
        await context.CreatePageAsync(1);

        var pages = await context.PageService.ListPagesAsync(context.DocumentInstanceId);

        pages.Value.Select(page => page.PageIndex).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task CreatePage_rejects_missing_document_instance()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();

        var result = await context.PageService.CreatePageAsync(
            DocumentInstanceId.New(), 0, null, null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "renderer-v1", null);

        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task CreateLayoutRevision_creates_revision()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();

        var revision = await context.LayoutTreeService.CreateLayoutRevisionAsync(context.DocumentInstanceId, LayoutRevisionSource.Manual);

        revision.IsSuccess.Should().BeTrue();
        revision.Value.DocumentInstanceId.Should().Be(context.DocumentInstanceId);
        revision.Value.Source.Should().Be(LayoutRevisionSource.Manual);
    }

    [Fact]
    public async Task CreateLayoutRevision_makeCurrent_sets_current()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();

        var revision = await context.LayoutTreeService.CreateLayoutRevisionAsync(context.DocumentInstanceId, LayoutRevisionSource.Mock, makeCurrent: true);
        var current = await context.LayoutTreeService.GetCurrentRevisionAsync(context.DocumentInstanceId);

        current.Value.LayoutRevisionId.Should().Be(revision.Value.LayoutRevisionId);
        current.Value.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task SetCurrentRevision_keeps_only_one_current_per_document_instance()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var first = await context.LayoutTreeService.CreateLayoutRevisionAsync(context.DocumentInstanceId, LayoutRevisionSource.Mock, makeCurrent: true);
        var second = await context.LayoutTreeService.CreateLayoutRevisionAsync(context.DocumentInstanceId, LayoutRevisionSource.Manual);

        var result = await context.LayoutTreeService.SetCurrentRevisionAsync(context.DocumentInstanceId, second.Value.LayoutRevisionId);

        await using var connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var currentCount = await connection.ExecuteScalarAsync<int>("select count(1) from layout_revisions where document_instance_id = @Id and is_current = 1;", new { Id = context.DocumentInstanceId.ToString() });
        var currentId = await connection.ExecuteScalarAsync<string>("select layout_revision_id from layout_revisions where document_instance_id = @Id and is_current = 1;", new { Id = context.DocumentInstanceId.ToString() });

        result.IsSuccess.Should().BeTrue();
        currentCount.Should().Be(1);
        currentId.Should().Be(second.Value.LayoutRevisionId.ToString());
        first.Value.LayoutRevisionId.Should().NotBe(second.Value.LayoutRevisionId);
    }

    [Fact]
    public async Task CreateLayoutRevision_rejects_parent_from_other_document_instance()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        await using var other = await PageLayoutTestContext.CreateAsync();
        var foreignParent = await other.LayoutTreeService.CreateLayoutRevisionAsync(other.DocumentInstanceId, LayoutRevisionSource.Mock);

        var result = await context.LayoutTreeService.CreateLayoutRevisionAsync(
            context.DocumentInstanceId,
            LayoutRevisionSource.Manual,
            parentRevisionId: foreignParent.Value.LayoutRevisionId);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddNode_creates_node_with_valid_bbox()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();

        var node = await context.LayoutTreeService.AddNodeAsync(
            setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph,
            new NormalizedBBox(0.1, 0.1, 0.2, 0.2), "Text", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        node.IsSuccess.Should().BeTrue();
        node.Value.BBox.Should().Be(new NormalizedBBox(0.1, 0.1, 0.2, 0.2));
    }

    [Fact]
    public async Task AddNode_rejects_bbox_outside_normalized_page()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();

        var result = await context.LayoutTreeService.AddNodeAsync(
            setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph,
            new NormalizedBBox(0.9, 0.9, 0.2, 0.2), "Text", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddNode_rejects_overlap_for_ordinary_nodes()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(0.1, 0.1, 0.4, 0.4), "A", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        var result = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Block, new NormalizedBBox(0.2, 0.2, 0.2, 0.2), "B", TextPolicy.Own, 2, LayoutNodeSource.Manual);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.Conflicts.Should().ContainSingle(conflict => conflict.ConflictCode == ConflictCode.LayoutBBoxOrdinaryOverlap);
    }

    [Fact]
    public async Task UpdateNodeBBox_overlap_returns_cf06_conflict_descriptor()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(0.1, 0.1, 0.2, 0.2), "A", TextPolicy.Own, 1, LayoutNodeSource.Manual);
        var node = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Block, new NormalizedBBox(0.5, 0.5, 0.2, 0.2), "B", TextPolicy.Own, 2, LayoutNodeSource.Manual);

        var result = await context.LayoutTreeService.UpdateNodeBBoxAsync(node.Value.NodeId, new NormalizedBBox(0.15, 0.15, 0.2, 0.2));

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.Conflicts.Should().ContainSingle(conflict => conflict.ConflictCode == ConflictCode.LayoutBBoxOrdinaryOverlap);
    }

    [Fact]
    public async Task AddNode_allows_overlap_for_annotation_or_marginalia_or_seal()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(0.1, 0.1, 0.4, 0.4), "A", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        var annotation = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Annotation, new NormalizedBBox(0.2, 0.2, 0.2, 0.2), "note", TextPolicy.Own, 2, LayoutNodeSource.Manual);
        var marginalia = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Marginalia, new NormalizedBBox(0.22, 0.22, 0.2, 0.2), "margin", TextPolicy.Own, 3, LayoutNodeSource.Manual);
        var seal = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Seal, new NormalizedBBox(0.24, 0.24, 0.2, 0.2), "seal", TextPolicy.Own, 4, LayoutNodeSource.Manual);

        annotation.IsSuccess.Should().BeTrue();
        marginalia.IsSuccess.Should().BeTrue();
        seal.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task AddNode_rejects_parent_from_other_revision_or_page()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var first = await context.CreatePageAndRevisionAsync();
        var secondPage = await context.CreatePageAsync(1);
        var parent = await context.LayoutTreeService.AddNodeAsync(first.RevisionId, first.PageId, null, LayoutNodeType.Block, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);

        var result = await context.LayoutTreeService.AddNodeAsync(first.RevisionId, secondPage.Value.PageId, parent.Value.NodeId, LayoutNodeType.Line, null, "bad", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task MoveNode_changes_parent_and_reading_order()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var parent = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Block, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);
        var child = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Line, null, "line", TextPolicy.Own, 9, LayoutNodeSource.Manual);

        var result = await context.LayoutTreeService.MoveNodeAsync(child.Value.NodeId, parent.Value.NodeId, 2);
        var nodes = await context.LayoutTreeService.ListNodesForPageAsync(setup.PageId, setup.RevisionId);

        result.IsSuccess.Should().BeTrue();
        nodes.Value.Single(node => node.NodeId == child.Value.NodeId).ParentNodeId.Should().Be(parent.Value.NodeId);
        nodes.Value.Single(node => node.NodeId == child.Value.NodeId).ReadingOrder.Should().Be(2);
    }

    [Fact]
    public async Task MoveNode_rejects_cycle()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var parent = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Block, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);
        var child = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, parent.Value.NodeId, LayoutNodeType.Line, null, "line", TextPolicy.Own, 2, LayoutNodeSource.Manual);

        var result = await context.LayoutTreeService.MoveNodeAsync(parent.Value.NodeId, child.Value.NodeId, 3);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task MarkIgnored_excludes_node_from_plain_text()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var node = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "ignore me", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        await context.LayoutTreeService.MarkIgnoredAsync(node.Value.NodeId, true);
        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateNodeText_changes_plain_text()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var node = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "old", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        await context.LayoutTreeService.UpdateNodeTextAsync(node.Value.NodeId, "new");
        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("new");
    }

    [Fact]
    public async Task BuildPagePlainText_orders_by_reading_order()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "second", TextPolicy.Own, 2, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "first", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("first\n\nsecond");
    }

    [Fact]
    public async Task BuildPagePlainText_excludes_header_footer_page_number()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Header, null, "header", TextPolicy.Own, 1, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "body", TextPolicy.Own, 2, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Footer, null, "footer", TextPolicy.Own, 3, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.PageNumber, null, "1", TextPolicy.Own, 4, LayoutNodeSource.Manual);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("body");
    }

    [Fact]
    public async Task BuildPagePlainText_excludes_marginalia_annotation_by_default()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Marginalia, null, "margin", TextPolicy.Own, 1, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Annotation, null, "note", TextPolicy.Own, 2, LayoutNodeSource.Manual);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildPagePlainText_aggregate_children()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var block = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Block, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, block.Value.NodeId, LayoutNodeType.Line, null, "line 1", TextPolicy.Own, 1, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, block.Value.NodeId, LayoutNodeType.Line, null, "line 2", TextPolicy.Own, 2, LayoutNodeSource.Manual);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("line 1\nline 2");
    }

    [Fact]
    public async Task BuildPagePlainText_text_policy_none_outputs_nothing()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "hidden", TextPolicy.None, 1, LayoutNodeSource.Manual);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildPagePlainText_table_degrades_to_Table_marker()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Table, null, "cells", TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("[Table]");
    }

    [Fact]
    public async Task BuildPagePlainText_outputs_markdown_for_regular_table_with_cell_metadata()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var table = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Table, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);
        var header = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, table.Value.NodeId, LayoutNodeType.TableRow, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);
        var body = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, table.Value.NodeId, LayoutNodeType.TableRow, null, null, TextPolicy.AggregateChildren, 2, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, header.Value.NodeId, LayoutNodeType.TableCell, null, "Name", TextPolicy.Own, 1, LayoutNodeSource.Manual, rowIndex: 0, colIndex: 0, rowSpan: 1, colSpan: 1, isHeader: true);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, header.Value.NodeId, LayoutNodeType.TableCell, null, "Value", TextPolicy.Own, 2, LayoutNodeSource.Manual, rowIndex: 0, colIndex: 1, rowSpan: 1, colSpan: 1, isHeader: true);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, body.Value.NodeId, LayoutNodeType.TableCell, null, "Pages", TextPolicy.Own, 1, LayoutNodeSource.Manual, rowIndex: 1, colIndex: 0, rowSpan: 1, colSpan: 1);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, body.Value.NodeId, LayoutNodeType.TableCell, null, "12", TextPolicy.Own, 2, LayoutNodeSource.Manual, rowIndex: 1, colIndex: 1, rowSpan: 1, colSpan: 1);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("| Name | Value |\n| --- | --- |\n| Pages | 12 |");
    }

    [Fact]
    public async Task BuildPagePlainText_degrades_irregular_table_without_inventing_markdown()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var table = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Table, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);
        await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, table.Value.NodeId, LayoutNodeType.TableCell, null, "spanning", TextPolicy.Own, 1, LayoutNodeSource.Manual, rowIndex: 0, colIndex: 0, rowSpan: 1, colSpan: 2, isHeader: true);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("[Table]");
    }

    [Fact]
    public async Task UpdateTableCellMetadata_enables_markdown_table_after_manual_correction()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var table = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Table, null, null, TextPolicy.AggregateChildren, 1, LayoutNodeSource.Manual);
        var h1 = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, table.Value.NodeId, LayoutNodeType.TableCell, null, "A", TextPolicy.Own, 1, LayoutNodeSource.Manual);
        var h2 = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, table.Value.NodeId, LayoutNodeType.TableCell, null, "B", TextPolicy.Own, 2, LayoutNodeSource.Manual);
        var c1 = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, table.Value.NodeId, LayoutNodeType.TableCell, null, "1", TextPolicy.Own, 3, LayoutNodeSource.Manual);
        var c2 = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, table.Value.NodeId, LayoutNodeType.TableCell, null, "2", TextPolicy.Own, 4, LayoutNodeSource.Manual);

        (await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId)).Value.Text.Should().Be("[Table]");
        await context.LayoutTreeService.UpdateTableCellMetadataAsync(h1.Value.NodeId, 0, 0, 1, 1, true);
        await context.LayoutTreeService.UpdateTableCellMetadataAsync(h2.Value.NodeId, 0, 1, 1, 1, true);
        await context.LayoutTreeService.UpdateTableCellMetadataAsync(c1.Value.NodeId, 1, 0, 1, 1, false);
        await context.LayoutTreeService.UpdateTableCellMetadataAsync(c2.Value.NodeId, 1, 1, 1, 1, false);

        var text = await context.LayoutTreeService.BuildPagePlainTextAsync(setup.PageId, setup.RevisionId);

        text.Value.Text.Should().Be("| A | B |\n| --- | --- |\n| 1 | 2 |");
    }

    [Fact]
    public async Task UpdateTableCellMetadata_rejects_non_cell_nodes()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();
        var paragraph = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "not a cell", TextPolicy.Own, 1, LayoutNodeSource.Manual);

        var result = await context.LayoutTreeService.UpdateTableCellMetadataAsync(paragraph.Value.NodeId, 0, 0, 1, 1, true);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task AddNode_rejects_table_cell_metadata_on_non_cell_nodes()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();
        var setup = await context.CreatePageAndRevisionAsync();

        var result = await context.LayoutTreeService.AddNodeAsync(setup.RevisionId, setup.PageId, null, LayoutNodeType.Paragraph, null, "not a cell", TextPolicy.Own, 1, LayoutNodeSource.Manual, rowIndex: 0, colIndex: 0);

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task MigrationRunner_applies_pages_and_layout_migration()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);

        await runner.RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var tableCount = await connection.ExecuteScalarAsync<int>(
            """
            select count(1)
            from sqlite_master
            where type = 'table'
              and name in ('pages', 'layout_revisions', 'layout_nodes');
            """);

        tableCount.Should().Be(3);
    }

    [Fact]
    public async Task MigrationRunner_adds_table_cell_metadata_columns()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var columns = (await connection.QueryAsync<string>("select name from pragma_table_info('layout_nodes');")).ToArray();

        columns.Should().Contain(["row_index", "col_index", "row_span", "col_span", "is_header"]);
    }

    [Fact]
    public async Task Foreign_keys_prevent_orphan_layout_node()
    {
        await using var context = await PageLayoutTestContext.CreateAsync();

        await using var connection = context.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        Func<Task> action = () => connection.ExecuteAsync(
            """
            insert into layout_nodes (
                node_id, document_instance_id, page_id, node_type, text_policy,
                reading_order, source, revision_id
            )
            values (@NodeId, @DocumentInstanceId, @PageId, 'paragraph', 'own', 1, 'manual', @RevisionId);
            """,
            new
            {
                NodeId = LayoutNodeId.New().ToString(),
                DocumentInstanceId = context.DocumentInstanceId.ToString(),
                PageId = PageId.New().ToString(),
                RevisionId = LayoutRevisionId.New().ToString()
            });

        await action.Should().ThrowAsync<SqliteException>();
    }

    private sealed class PageLayoutTestContext : IAsyncDisposable
    {
        private PageLayoutTestContext(
            TemporarySqliteDatabase database,
            DocumentInstanceId documentInstanceId,
            PageService pageService,
            LayoutTreeService layoutTreeService)
        {
            Database = database;
            DocumentInstanceId = documentInstanceId;
            PageService = pageService;
            LayoutTreeService = layoutTreeService;
        }

        public TemporarySqliteDatabase Database { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageService PageService { get; }
        public LayoutTreeService LayoutTreeService { get; }

        public static async Task<PageLayoutTestContext> CreateAsync()
        {
            var database = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(new DateTimeOffset(2026, 6, 19, 4, 0, 0, TimeSpan.Zero));
            var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);
            await runner.RunAsync();

            var libraryService = new LibraryIdentityService(database.ConnectionFactory, clock);
            await libraryService.CreateLibraryAsync("Layout library");
            var itemService = new ItemService(database.ConnectionFactory, libraryService, clock);
            var documentInstanceService = new DocumentInstanceService(database.ConnectionFactory, clock);
            var item = await itemService.CreateItemAsync("book", "Layout source");
            var instance = await documentInstanceService.AttachDocumentInstanceAsync(
                item.Value.ItemId,
                null,
                DocumentInstanceType.PrimaryScan);

            return new PageLayoutTestContext(
                database,
                instance.Value.DocumentInstanceId,
                new PageService(database.ConnectionFactory, clock),
                new LayoutTreeService(database.ConnectionFactory, clock));
        }

        public Task<Result<Page>> CreatePageAsync(int pageIndex)
        {
            return PageService.CreatePageAsync(
                DocumentInstanceId,
                pageIndex,
                pageLabel: null,
                width: 100,
                height: 200,
                rotation: 0,
                coordinateBasis: CoordinateBasis.NormalizedPage,
                basisWidth: 100,
                basisHeight: 200,
                rendererBasisVersion: "renderer-v1",
                sourceFileHash: null);
        }

        public async Task<(PageId PageId, LayoutRevisionId RevisionId)> CreatePageAndRevisionAsync()
        {
            var page = await CreatePageAsync(0);
            var revision = await LayoutTreeService.CreateLayoutRevisionAsync(
                DocumentInstanceId,
                LayoutRevisionSource.Mock,
                makeCurrent: true);

            return (page.Value.PageId, revision.Value.LayoutRevisionId);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
