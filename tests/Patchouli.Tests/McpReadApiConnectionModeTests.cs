using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Core.Search;

namespace Patchouli.Tests;

public sealed class McpReadApiConnectionModeTests
{
    [Fact]
    public async Task Mcp_read_paths_run_read_only_and_do_not_advance_library_revision()
    {
        await using LibraryContext context = await LibraryContext.SeedAsync();

        Result<McpLibraryStateResponse> before = await context.Api.GetCurrentLibraryStateAsync();
        before.IsSuccess.Should().BeTrue();

        IReadOnlyList<McpPageBlock> blocks =
            (await context.Api.GetPageBlocksAsync(new McpPageBlocksRequest(context.PageId))).Value.Blocks;
        blocks.Should().ContainSingle();
        blocks.Single().TreeRevisionId.Should().NotBeNull();
        blocks.Single().BoxId.Should().NotBeNull();

        Result<McpPageTextResponse> text =
            await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId));
        text.IsSuccess.Should().BeTrue();

        Result<McpSearchLibraryResponse> search =
            await context.Api.SearchLibraryAsync(new McpSearchLibraryRequest("canonical"));
        search.IsSuccess.Should().BeTrue();
        search.Value.Results.Should().NotBeEmpty();

        Result<McpLibraryStateResponse> after = await context.Api.GetCurrentLibraryStateAsync();
        after.Value.LibraryRevision.Should().Be(before.Value.LibraryRevision,
            "read-only MCP operations must not advance the library revision");
    }

    [Fact]
    public async Task Versioned_evidence_resolves_through_read_only_connections()
    {
        await using LibraryContext context = await LibraryContext.SeedAsync();

        McpPageBlock matched = (await context.Api.GetPageBlocksAsync(
            new McpPageBlocksRequest(context.PageId))).Value.Blocks.Single();

        Result<EvidencePageText> byRevAndBox = await context.EvidenceReader.GetBoxTextAsync(
            context.DocumentId, 1, matched.TreeRevisionId, matched.BoxId);
        byRevAndBox.IsSuccess.Should().BeTrue($"error: {byRevAndBox.ErrorCode} {byRevAndBox.ErrorMessage}");
        byRevAndBox.Value.Markdown.Should().Contain("canonical searchable phrase");
        byRevAndBox.Value.TreeRevisionId.Should().Be(matched.TreeRevisionId);
        byRevAndBox.Value.BoxId.Should().Be(matched.BoxId);

        Result<EvidencePageText> head = await context.EvidenceReader.GetBoxTextAsync(
            context.DocumentId, 1, boxId: matched.BoxId);
        head.IsSuccess.Should().BeTrue();
        head.Value.Markdown.Should().Contain("canonical searchable phrase");

        Result<EvidencePageText> wrongPage = await context.EvidenceReader.GetBoxTextAsync(
            context.DocumentId, 99, matched.TreeRevisionId, matched.BoxId);
        wrongPage.IsSuccess.Should().BeFalse();
        wrongPage.ErrorCode.Should().Be(AppErrorCodes.NotFound);

        Result<McpLibraryStateResponse> state = await context.Api.GetCurrentLibraryStateAsync();
        state.IsSuccess.Should().BeTrue();
        state.Value.LibraryRevision.Should().MatchRegex("^lib:[0-9]+$");
    }

    private sealed class LibraryContext : IAsyncDisposable
    {
        private LibraryContext(TemporarySqliteDatabase database, McpReadApi api,
            IVersionedEvidenceReader evidenceReader,
            LibraryRevisionService revisions, PageId pageId, DocumentInstanceId documentId)
        {
            Database = database;
            Api = api;
            EvidenceReader = evidenceReader;
            Revisions = revisions;
            PageId = pageId;
            DocumentId = documentId;
        }

        public TemporarySqliteDatabase Database { get; }
        public McpReadApi Api { get; }
        public IVersionedEvidenceReader EvidenceReader { get; }
        public LibraryRevisionService Revisions { get; }
        public PageId PageId { get; }
        public DocumentInstanceId DocumentId { get; }

        public static async Task<LibraryContext> SeedAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            Result<LibraryMetadata> created = await libraries.CreateLibraryAsync("Read mode test library");
            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.ErrorMessage);
            }

            ItemService items = new(database.ConnectionFactory, libraries, clock);
            Result<ItemMetadata> item =
                await items.CreateItemAsync("book", "Read mode source");
            if (item.IsFailure)
            {
                throw new InvalidOperationException(item.ErrorMessage);
            }

            Result<DocumentInstance> document = await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            if (document.IsFailure)
            {
                throw new InvalidOperationException(document.ErrorMessage);
            }

            Result<Page> page = await new PageService(database.ConnectionFactory, clock)
                .CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null);
            if (page.IsFailure)
            {
                throw new InvalidOperationException(page.ErrorMessage);
            }

            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            Result<DocumentTreeRevision> working = await trees.BeginWorkingRevisionAsync(
                document.Value.DocumentInstanceId, page.Value.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("canonical searchable phrase"))
                ],
                DocumentTreeRevisionSource.Import);
            if (working.IsFailure)
            {
                throw new InvalidOperationException(working.ErrorMessage);
            }

            Result<DocumentTreeRevision> committed =
                await trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
            if (committed.IsFailure)
            {
                throw new InvalidOperationException(committed.ErrorMessage);
            }

            Result rebuilt = await new SearchUnitBuilder(database.ConnectionFactory, clock, new MarkdigMarkdownEngine())
                .RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);
            if (rebuilt.IsFailure)
            {
                throw new InvalidOperationException(rebuilt.ErrorMessage);
            }

            Result ftsRebuilt = await new SearchIndexRebuilder(database.ConnectionFactory, clock)
                .RebuildFtsForDocumentInstanceAsync(document.Value.DocumentInstanceId);
            if (ftsRebuilt.IsFailure)
            {
                throw new InvalidOperationException(ftsRebuilt.ErrorMessage);
            }

            IMarkdownEngine markdown = new MarkdigMarkdownEngine();
            IDocumentMarkdownCompiler markdownCompiler = new DocumentMarkdownCompiler(trees, markdown);
            IVersionedEvidenceReader evidenceReader = new VersionedEvidenceReader(
                database.ConnectionFactory, libraries, trees, markdownCompiler);
            SqliteSearchService search = new(database.ConnectionFactory);
            LibraryRevisionService revisions = new(database.ConnectionFactory);
            McpReadApi api = new(database.ConnectionFactory, search);
            return new LibraryContext(database, api, evidenceReader, revisions, page.Value.PageId,
                document.Value.DocumentInstanceId);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
