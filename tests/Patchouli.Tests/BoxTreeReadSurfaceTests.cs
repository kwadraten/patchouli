using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Search;

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
        DocumentTreeRevision staging = (await trees.StagePageAsync(document.DocumentInstanceId, page.PageId,
        [
            new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("canonical searchable phrase")),
            new DocumentBoxSeed(null, null, 1, DocumentBoxType.Header, null, null,
                new NormalizedBBox(.1, .01, .8, .05), new TextBoxPayload("suppressed running head"),
                Suppressed: true)
        ])).Value;
        DocumentTreeRevision committed = (await trees.AdoptStagingRevisionAsync(staging.TreeRevisionId)).Value;

        SearchUnitBuilder units = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
        await units.RebuildForDocumentInstanceAsync(document.DocumentInstanceId);
        await new SearchIndexRebuilder(database.ConnectionFactory, clock)
            .RebuildFtsForDocumentInstanceAsync(document.DocumentInstanceId);
        SqliteSearchService search = new(database.ConnectionFactory);
        SearchResultPage found = (await search.SearchLibraryAsync(new SearchRequest("searchable"))).Value;
        found.Results.SelectMany(result => result.MatchedUnits).Should().ContainSingle();
        SearchMatchedUnit matched = found.Results.Single().MatchedUnits.Single();
        matched.TreeRevisionId.Should().Be(committed.TreeRevisionId);

        EvidenceReferenceService evidence = new(database.ConnectionFactory, clock);
        EvidenceRefRecord evidenceRecord = (await evidence.CreateFromSearchUnitAsync(matched.UnitId)).Value;
        evidenceRecord.TreeRevisionId.Should().Be(committed.TreeRevisionId);
        evidenceRecord.BoxId.Should().Be(matched.BoxId);
        evidenceRecord.EvidenceRefId.Should().StartWith("evref:v2:");

        MarkdigMarkdownEngine markdown = new();
        McpReadApi mcp = new(database.ConnectionFactory, search, evidence, markdown: markdown,
            markdownCompiler: new DocumentMarkdownCompiler(trees, markdown));
        McpPageTextResponse currentText = (await mcp.GetPageTextAsync(new McpPageTextRequest(page.PageId))).Value;
        currentText.Text.Should().Contain("canonical searchable phrase").And.NotContain("running head");
        McpPageTextResponse allText = (await mcp.GetPageTextAsync(
            new McpPageTextRequest(page.PageId, IncludeSuppressed: true))).Value;
        allText.Text.Should().Contain("suppressed running head");
        IReadOnlyList<McpPageBlock> blocks = (await mcp.GetPageBlocksAsync(
            new McpPageBlocksRequest(page.PageId, IncludeBbox: true))).Value.Blocks;
        blocks.Should().ContainSingle();
        blocks.Single().BoxId.Should().Be(matched.BoxId);
        blocks.Single().TreeRevisionId.Should().Be(committed.TreeRevisionId);
        blocks.Single().BBox.Should().NotBeNull();
    }
}
