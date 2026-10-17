using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

/// <summary>
/// MCP contract tests for the <c>patchouli://texts/{document-id}/map.yml</c> resource:
/// <c>find</c> discovers <c>map.yml</c> in the document scope (even with no stored map),
/// <c>fetch</c> returns the empty-map projection for documents without a map, and
/// <c>put</c> failures leave the stored map untouched.
/// </summary>
public sealed class PageMapMcpContractTests
{
    private sealed class McpFixture : IAsyncDisposable
    {
        private McpFixture(PageMapTestDocument document, McpCommandService commands,
            DocumentPageMapService maps)
        {
            Document = document;
            Commands = commands;
            Maps = maps;
        }

        public PageMapTestDocument Document { get; }

        public McpCommandService Commands { get; }

        public DocumentPageMapService Maps { get; }

        public DocumentInstanceId DocumentId => Document.DocumentId;

        public string MapUri => McpResourceUris.MapUri(DocumentId);

        public ValueTask DisposeAsync()
        {
            return Document.DisposeAsync();
        }

        public static async Task<McpFixture> CreateAsync()
        {
            PageMapTestDocument document = await PageMapTestDocument.CreateAsync(pageCount: 2);
            var connectionFactory = document.ConnectionFactory;
            FixedClock clock = document.Clock;

            DocumentPageMapService maps = document.CreateMapService();
            McpReadApi read = new(connectionFactory, new SqliteSearchService(connectionFactory),
                pageMaps: maps);
            McpWriteApi write = new(document.Items, new BiblatexHelperClient(),
                new CslStyleStore(connectionFactory, clock), pageMaps: maps);
            BiblatexHelperClient helper = new();
            BiblatexImportService biblatex = new(helper, document.Items,
                new FileAssetService(connectionFactory, document.Libraries, clock), document.Documents);
            McpCommandService commands = new(read, write, biblatex, document.Items,
                new VersionedEvidenceReader(connectionFactory, document.Libraries, document.Trees,
                    new DocumentMarkdownCompiler(document.Trees, new MarkdigMarkdownEngine())));

            return new McpFixture(document, commands, maps);
        }
    }

    private static string ValidMapYaml(McpFixture fixture)
    {
        DocumentPageMap map = new(fixture.DocumentId,
            [],
            [new LogicalPaginationRule(1, 2, 1,
                [
                    new LogicalPageReadingEntry(fixture.Document.BoxA, 1),
                    new LogicalPageReadingEntry(fixture.Document.BoxB, 1),
                    new LogicalPageReadingEntry(fixture.Document.BoxC, 2)
                ])],
            [],
            [ChapterNode.Leaf("ch1", "第一章", new NavigationPosition(2, fixture.Document.BoxC))]);
        return DocumentPageMapYamlCodec.Encode(map, true);
    }

    [Fact]
    public async Task Find_discovers_map_yml_in_document_scope_without_a_stored_map()
    {
        await using McpFixture fixture = await McpFixture.CreateAsync();

        McpCommandResult<McpFindMeta, object> found = await fixture.Commands.FindAsync(
            new McpFindRequest(null, McpResourceUris.DocumentUri(fixture.DocumentId), null));

        found.IsSuccess.Should().BeTrue(found.Error?.Detail);
        McpFindEntry mapEntry = found.Envelope!.Entries.OfType<McpFindEntry>()
            .Single(entry => entry.Uri == fixture.MapUri);
        mapEntry.Type.Should().Be("file");
        mapEntry.Title.Should().Contain("map.yml");
    }

    [Fact]
    public async Task Find_still_discovers_map_yml_after_a_map_is_stored()
    {
        await using McpFixture fixture = await McpFixture.CreateAsync();

        McpCommandResult<McpPutMeta, McpPutResult> put =
            await fixture.Commands.PutAsync(new McpPutRequest(fixture.MapUri, ValidMapYaml(fixture)));
        put.IsSuccess.Should().BeTrue(put.Error?.Detail);

        McpCommandResult<McpFindMeta, object> found = await fixture.Commands.FindAsync(
            new McpFindRequest(null, McpResourceUris.DocumentUri(fixture.DocumentId), null));

        found.IsSuccess.Should().BeTrue(found.Error?.Detail);
        found.Envelope!.Entries.OfType<McpFindEntry>()
            .Should().Contain(entry => entry.Uri == fixture.MapUri);
    }

    [Fact]
    public async Task Fetch_returns_empty_map_projection_for_document_without_map()
    {
        await using McpFixture fixture = await McpFixture.CreateAsync();

        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await fixture.Commands.FetchAsync(
            new McpFetchRequest([fixture.MapUri], null, null));

        fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);
        McpFetchResult entry = fetched.Envelope!.Entries.Single();
        entry.Uri.Should().Be(fixture.MapUri);
        entry.ResourceType.Should().Be("document_page_map");
        entry.Content.Should().Contain("\"profile\": \"patchouli-page-map-1\"");
        entry.Content.Should().Contain("\"map_present\": false");

        Result<DocumentPageMap> decoded =
            DocumentPageMapYamlCodec.Decode(entry.Content!, fixture.DocumentId);
        decoded.IsSuccess.Should().BeTrue(decoded.ErrorMessage);
        decoded.Value.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Put_valid_map_then_fetch_returns_the_stored_projection()
    {
        await using McpFixture fixture = await McpFixture.CreateAsync();
        string yaml = ValidMapYaml(fixture);

        McpCommandResult<McpPutMeta, McpPutResult> put =
            await fixture.Commands.PutAsync(new McpPutRequest(fixture.MapUri, yaml));

        put.IsSuccess.Should().BeTrue(put.Error?.Detail);
        McpPutResult result = put.Envelope!.Entries.Single();
        result.Uri.Should().Be(fixture.MapUri);
        result.ResourceType.Should().Be("document_page_map");
        result.Committed.Should().BeTrue();

        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await fixture.Commands.FetchAsync(
            new McpFetchRequest([fixture.MapUri], null, null));
        fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);

        string content = fetched.Envelope!.Entries.Single().Content!;
        content.Should().Contain("\"map_present\": true");
        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(content, fixture.DocumentId);
        decoded.IsSuccess.Should().BeTrue(decoded.ErrorMessage);
        decoded.Value.LogicalRules.Should().HaveCount(1);
        decoded.Value.Chapters.Should().ContainSingle().Which.ChapterId.Should().Be("ch1");
    }

    [Fact]
    public async Task Put_with_schema_invalid_yaml_fails_without_touching_existing_data()
    {
        await using McpFixture fixture = await McpFixture.CreateAsync();
        McpCommandResult<McpPutMeta, McpPutResult> first =
            await fixture.Commands.PutAsync(new McpPutRequest(fixture.MapUri, ValidMapYaml(fixture)));
        first.IsSuccess.Should().BeTrue(first.Error?.Detail);

        McpCommandResult<McpPutMeta, McpPutResult> bad = await fixture.Commands.PutAsync(
            new McpPutRequest(fixture.MapUri, "profile: something-else\n"));

        bad.IsSuccess.Should().BeFalse("a schema-invalid map.yml put must fail, not succeed");

        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await fixture.Commands.FetchAsync(
            new McpFetchRequest([fixture.MapUri], null, null));
        fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);
        Result<DocumentPageMap> decoded = DocumentPageMapYamlCodec.Decode(
            fetched.Envelope!.Entries.Single().Content!, fixture.DocumentId);
        decoded.IsSuccess.Should().BeTrue(decoded.ErrorMessage);
        decoded.Value.LogicalRules.Should().HaveCount(1);
        decoded.Value.Chapters.Should().ContainSingle().Which.ChapterId.Should().Be("ch1");
    }

    [Fact]
    public async Task Put_with_malformed_yaml_fails_gracefully_without_touching_existing_data()
    {
        await using McpFixture fixture = await McpFixture.CreateAsync();
        McpCommandResult<McpPutMeta, McpPutResult> first =
            await fixture.Commands.PutAsync(new McpPutRequest(fixture.MapUri, ValidMapYaml(fixture)));
        first.IsSuccess.Should().BeTrue(first.Error?.Detail);

        // Syntactically broken YAML: the parse failure must surface as a failed command
        // result, never as an escaped exception, and must not touch the stored map.
        McpCommandResult<McpPutMeta, McpPutResult> bad = await fixture.Commands.PutAsync(
            new McpPutRequest(fixture.MapUri,
                "profile: patchouli-page-map-1\n" +
                $"document_id: {fixture.DocumentId}\n" +
                "pagination: [unclosed\n"));

        bad.IsSuccess.Should().BeFalse("malformed YAML must fail as a result, not throw");

        Result<ResolvedDocumentPageMap?> resolved =
            await fixture.Maps.GetResolvedAsync(fixture.DocumentId);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.Should().NotBeNull();
        resolved.Value!.Definition.LogicalRules.Should().HaveCount(1);
    }
}
