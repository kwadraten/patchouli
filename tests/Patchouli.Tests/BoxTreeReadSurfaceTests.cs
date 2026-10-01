using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.Search;

namespace Patchouli.Tests;

public sealed class BoxTreeReadSurfaceTests
{
    [Fact]
    public async Task Search_evidence_and_mcp_share_box_tree_identity_and_suppression_policy()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        LibraryMetadata library = (await libraries.CreateLibraryAsync("Read surfaces")).Value;
        ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
            .CreateItemAsync("document", "Box source")).Value;
        DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
            .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Page page = (await new Infrastructure.Layout.PageService(database.ConnectionFactory, clock)
            .CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
        DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
        const string complexTableHtml = "<table><tr><td rowspan=\"2\">Merged</td></tr></table>";
        DocumentTreeRevision working = (await trees.BeginWorkingRevisionAsync(document.DocumentInstanceId, page.PageId,
        [
            new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("canonical searchable phrase")),
            new DocumentBoxSeed(null, null, 1, DocumentBoxType.Header, null, null,
                new NormalizedBBox(.1, .01, .8, .05), new TextBoxPayload("suppressed running head"),
                Suppressed: true),
            new DocumentBoxSeed(null, null, 2, DocumentBoxType.Table, null, null,
                new NormalizedBBox(.1, .3, .8, .2), new TableBoxPayload("[Table]", complexTableHtml))
        ], DocumentTreeRevisionSource.Import)).Value;
        DocumentTreeRevision committed = (await trees.CommitWorkingRevisionAsync(working.TreeRevisionId)).Value;

        SearchUnitBuilder units = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
        await units.RebuildForDocumentInstanceAsync(document.DocumentInstanceId);
        await new SearchIndexRebuilder(database.ConnectionFactory, clock)
            .RebuildFtsForDocumentInstanceAsync(document.DocumentInstanceId);
        SqliteSearchService search = new(database.ConnectionFactory);
        SearchResultPage found = (await search.SearchLibraryAsync(new SearchRequest("searchable"))).Value;
        found.Results.SelectMany(result => result.MatchedUnits).Should().ContainSingle();
        SearchMatchedUnit matched = found.Results.Single().MatchedUnits.Single();
        matched.TreeRevisionId.Should().Be(committed.TreeRevisionId);

        VersionedEvidenceReader evidence = new(
            database.ConnectionFactory,
            libraries,
            trees,
            new DocumentMarkdownCompiler(trees, new MarkdigMarkdownEngine()));
        Result<EvidencePageText> evidenceText = await evidence.GetBoxTextAsync(
            document.DocumentInstanceId,
            page.PageIndex + 1,
            committed.TreeRevisionId,
            matched.BoxId);
        evidenceText.IsSuccess.Should().BeTrue();
        evidenceText.Value.Markdown.Should().Contain("canonical searchable phrase");
        evidenceText.Value.TreeRevisionId.Should().Be(committed.TreeRevisionId);

        MarkdigMarkdownEngine markdown = new();
        DocumentMarkdownCompiler compiler = new(trees, markdown);
        CompiledMarkdown desktopMarkdown = (await compiler.CompilePageMarkdownAsync(committed.TreeRevisionId)).Value;
        desktopMarkdown.Markdown.Should().Contain("[Table]").And.NotContain(complexTableHtml);
        McpReadApi mcp = new(database.ConnectionFactory, search, markdownCompiler: compiler);
        McpPageTextResponse currentText = (await mcp.GetPageTextAsync(new McpPageTextRequest(page.PageId))).Value;
        currentText.Text.Should().Contain("canonical searchable phrase").And.NotContain("running head")
            .And.Contain(complexTableHtml).And.NotContain("[Table]");
        McpPageTextResponse allText = (await mcp.GetPageTextAsync(
            new McpPageTextRequest(page.PageId, true))).Value;
        allText.Text.Should().Contain("suppressed running head");
        IReadOnlyList<McpPageBlock> blocks = (await mcp.GetPageBlocksAsync(
            new McpPageBlocksRequest(page.PageId, true))).Value.Blocks;
        blocks.Should().HaveCount(2);
        McpPageBlock matchedBlock = blocks.Single(block => block.BoxId == matched.BoxId);
        matchedBlock.TreeRevisionId.Should().Be(committed.TreeRevisionId);
        matchedBlock.BBox.Should().NotBeNull();
    }

    [Fact]
    public async Task Mcp_document_and_page_reads_use_scoped_indexes_with_unrelated_page_data()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("Scoped MCP reads");
        ItemService items = new(database.ConnectionFactory, libraries, clock);
        ItemMetadata targetItem = (await items.CreateItemAsync("document", "Empty target")).Value;
        ItemMetadata unrelatedItem = (await items.CreateItemAsync("document", "Unrelated pages")).Value;
        DocumentInstanceService documents = new(database.ConnectionFactory, clock);
        DocumentInstance targetDocument = (await documents.AttachDocumentInstanceAsync(
            targetItem.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        DocumentInstance unrelatedDocument = (await documents.AttachDocumentInstanceAsync(
            unrelatedItem.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
        Page targetPage = (await pages.CreatePageAsync(targetDocument.DocumentInstanceId, 0, "target", null, null,
            0, CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
        DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
        DocumentTreeRevision targetRevision = (await trees.BeginWorkingRevisionAsync(
            targetDocument.DocumentInstanceId,
            targetPage.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.Header, null, null,
                    new NormalizedBBox(.1, .02, .8, .05), new TextBoxPayload("suppressed only"), Suppressed: true)
            ],
            DocumentTreeRevisionSource.Import)).Value;
        await trees.CommitWorkingRevisionAsync(targetRevision.TreeRevisionId);

        const int unrelatedPageCount = 128;
        for (int index = 0; index < unrelatedPageCount; index++)
        {
            Page page = (await pages.CreatePageAsync(unrelatedDocument.DocumentInstanceId, index, $"{index + 1}",
                null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            DocumentTreeRevision revision = (await trees.BeginWorkingRevisionAsync(
                unrelatedDocument.DocumentInstanceId,
                page.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload($"unrelated page {index}"))
                ],
                DocumentTreeRevisionSource.Import)).Value;
            (await trees.CommitWorkingRevisionAsync(revision.TreeRevisionId)).IsSuccess.Should().BeTrue();
        }

        MarkdigMarkdownEngine markdown = new();
        SqliteSearchService search = new(database.ConnectionFactory);
        McpReadApi mcp = new(database.ConnectionFactory, search,
            markdownCompiler: new DocumentMarkdownCompiler(trees, markdown));
        McpDocumentStatusResponse status = (await mcp.GetDocumentStatusAsync(targetDocument.DocumentInstanceId)).Value;
        McpPageTextResponse text = (await mcp.GetPageTextAsync(new McpPageTextRequest(targetPage.PageId))).Value;

        status.HasCurrentLayout.Should().BeTrue();
        status.HasOcrText.Should().BeFalse();
        text.TreeRevisionId.Should().Be(targetRevision.TreeRevisionId);

        await using SqliteConnection connection = database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        string[] statusPlan = (await connection.QueryAsync<PlanRow>(
                """
                explain query plan
                select exists (
                    select 1
                    from document_tree_revisions r
                    where r.document_instance_id = @DocumentId
                      and r.status = 'committed' and r.is_current = 1
                      and exists (
                          select 1 from document_boxes b
                          where b.document_instance_id = @DocumentId
                            and b.tree_revision_id = r.tree_revision_id
                            and b.suppressed = 0 and b.payload_json is not null
                      )
                );
                """,
                new { DocumentId = targetDocument.DocumentInstanceId.ToString() }))
            .Select(row => row.Detail)
            .ToArray();
        statusPlan.Should().Contain(detail => detail.Contains("SEARCH r", StringComparison.Ordinal));
        statusPlan.Should().Contain(detail => detail.Contains("SEARCH b", StringComparison.Ordinal));

        string[] pagePlan = (await connection.QueryAsync<PlanRow>(
                """
                explain query plan
                select tree_revision_id
                from document_tree_revisions
                where page_id = @PageId and status = 'committed' and is_current = 1
                order by committed_at desc, tree_revision_id desc
                limit 1;
                """,
                new { PageId = targetPage.PageId.ToString() }))
            .Select(row => row.Detail)
            .ToArray();
        pagePlan.Should().Contain(detail =>
            detail.Contains("idx_document_tree_revisions_page_status_current", StringComparison.Ordinal));
    }

    public sealed class PlanRow
    {
        public string Detail { get; set; } = "";
    }
}
