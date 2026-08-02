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
using Patchouli.Infrastructure.Evidence;
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
    public async Task Mcp_read_paths_run_read_only_and_keep_evidence_writes_writer_owned()
    {
        await using LibraryContext context = await LibraryContext.SeedAsync();

        IReadOnlyList<McpPageBlock> blocks =
            (await context.Api.GetPageBlocksAsync(new McpPageBlocksRequest(context.PageId))).Value.Blocks;
        blocks.Should().NotBeEmpty();
        string[] evidenceRefs = blocks.Select(block => block.EvidenceRef)
            .Where(refId => !string.IsNullOrWhiteSpace(refId))
            .Select(refId => refId!)
            .ToArray();
        evidenceRefs.Should().NotBeEmpty(
            "a nominally-read MCP operation may carry the evidence side effect, but that write must still go through the writer-owned evidence service");

        await using SqliteConnection readConnection = context.Database.ConnectionFactory.CreateReadConnection();
        await readConnection.OpenAsync();
        int persisted = await readConnection.ExecuteScalarAsync<int>(
            "select count(1) from evidence_ref_records where evidence_ref_id in @Refs;",
            new { Refs = evidenceRefs });
        persisted.Should().Be(evidenceRefs.Length,
            "the evidence side effect must be committed to the database, not lost on a read-only connection");

        Result<long> committed = await context.Revisions.CommitAsync(LibraryChangeSet.Empty);
        committed.IsSuccess.Should().BeTrue();
        Result<long> current = await context.Revisions.GetCurrentRevisionAsync();
        current.IsSuccess.Should().BeTrue();
        current.Value.Should().BeGreaterThan(0,
            "the single-writer revision commit owns writes even after heavy read-only MCP traffic");
    }

    [Fact]
    public async Task Search_evidence_resolve_and_csl_reads_work_through_read_only_connections()
    {
        await using LibraryContext context = await LibraryContext.SeedAsync();

        Result<McpPageTextResponse> text =
            await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId));
        text.IsSuccess.Should().BeTrue();
        text.Value.Text.Should().Contain("canonical searchable phrase");

        McpPageBlock matched = (await context.Api.GetPageBlocksAsync(
            new McpPageBlocksRequest(context.PageId))).Value.Blocks.Single();
        Result<McpPageTextResponse> pinned = await context.Api.GetPageTextAsync(
            new McpPageTextRequest(context.PageId, McpReadMode.Pinned, matched.EvidenceRef));
        pinned.IsSuccess.Should().BeTrue($"error: {pinned.ErrorCode} {pinned.ErrorMessage}");
        pinned.Value.Text.Should().Contain("canonical searchable phrase");

        Result<McpSearchLibraryResponse> search =
            await context.Api.SearchLibraryAsync(new McpSearchLibraryRequest("searchable"));
        search.IsSuccess.Should().BeTrue($"error: {search.ErrorCode} {search.ErrorMessage}");
        search.Value.Results.Should().NotBeEmpty();

        Result<McpLibraryStateResponse> state = await context.Api.GetCurrentLibraryStateAsync();
        state.IsSuccess.Should().BeTrue();
        state.Value.LibraryRevision.Should().MatchRegex("^lib:[0-9]+$");
    }

    private sealed class LibraryContext : IAsyncDisposable
    {
        private LibraryContext(TemporarySqliteDatabase database, McpReadApi api, LibraryRevisionService revisions,
            PageId pageId)
        {
            Database = database;
            Api = api;
            Revisions = revisions;
            PageId = pageId;
        }

        public TemporarySqliteDatabase Database { get; }
        public McpReadApi Api { get; }
        public LibraryRevisionService Revisions { get; }
        public PageId PageId { get; }

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
            Result<DocumentTreeRevision> staged = await trees.StagePageAsync(document.Value.DocumentInstanceId,
                page.Value.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("canonical searchable phrase"))
                ]);
            if (staged.IsFailure)
            {
                throw new InvalidOperationException(staged.ErrorMessage);
            }

            Result<DocumentTreeRevision> committed = await trees.AdoptStagingRevisionAsync(staged.Value.TreeRevisionId);
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

            EvidenceReferenceService evidence = new(database.ConnectionFactory, clock);
            SqliteSearchService search = new(database.ConnectionFactory);
            LibraryRevisionService revisions = new(database.ConnectionFactory);
            McpReadApi api = new(database.ConnectionFactory, search, evidence);
            return new LibraryContext(database, api, revisions, page.Value.PageId);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
