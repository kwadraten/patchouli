using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
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
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class McpVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsync_reports_searchable_when_fts_contains_term()
    {
        await using VerificationContext context = await VerificationContext.CreateAsync();
        await context.SeedSearchDataAsync("important research content");

        Result<McpVerificationResult> result = await context.Verification.VerifyAsync(
            context.DocumentInstanceId.ToString(), "important");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsSearchable.Should().BeTrue();
        result.Value.MatchedUnitCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VerifyAsync_reports_not_searchable_when_fts_empty()
    {
        await using VerificationContext context = await VerificationContext.CreateAsync();

        Result<McpVerificationResult> result = await context.Verification.VerifyAsync(
            context.DocumentInstanceId.ToString(), "anything");

        result.IsSuccess.Should().BeTrue();
        result.Value.MatchedUnitCount.Should().Be(0);
    }

    [Fact]
    public async Task VerifyAsync_result_contains_no_local_path_or_secret()
    {
        await using VerificationContext context = await VerificationContext.CreateAsync();
        await context.SeedSearchDataAsync("sample text for verification");

        Result<McpVerificationResult> result = await context.Verification.VerifyAsync(
            context.DocumentInstanceId.ToString(), "sample");

        result.IsSuccess.Should().BeTrue();
        string json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        json.Should().NotContain(":\\");
        json.Should().NotContain("token");
        json.Should().NotContain("secret");
        json.Should().NotContain("mineru");
    }

    private sealed class VerificationContext : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; }
        public McpVerificationService Verification { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public LibraryIdentityService Library { get; }
        public IClock Clock { get; }

        private VerificationContext(TemporarySqliteDatabase db, McpVerificationService verification,
            DocumentInstanceId docId, LibraryIdentityService library, IClock clock)
        {
            Database = db;
            Verification = verification;
            DocumentInstanceId = docId;
            Library = library;
            Clock = clock;
        }

        public static async Task<VerificationContext> CreateAsync()
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Test Lib");
            ItemService items = new(db.ConnectionFactory, library, clock);
            Result<ItemMetadata> item = await items.CreateItemAsync("document", "Test Doc");
            DocumentInstanceService docs = new(db.ConnectionFactory, clock);
            Result<DocumentInstance> doc =
                await docs.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            PageService pages = new(db.ConnectionFactory, clock);
            Result<Page> page = await pages.CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null);

            await BoxTreeTestData.CommitTextAsync(db.ConnectionFactory, clock, doc.Value.DocumentInstanceId,
                page.Value.PageId, "test text");

            SqliteSearchService search = new(db.ConnectionFactory);
            EvidenceReferenceService evidence = new(db.ConnectionFactory, clock);
            McpReadApi mcp = new(db.ConnectionFactory, search, evidence);
            McpVerificationService verification = new(db.ConnectionFactory, mcp);

            return new VerificationContext(db, verification, doc.Value.DocumentInstanceId, library, clock);
        }

        public async Task SeedSearchDataAsync(string text)
        {
            SearchUnitBuilder builder = new(Database.ConnectionFactory, Clock);
            SearchIndexRebuilder index = new(Database.ConnectionFactory, Clock);
            await builder.RebuildForDocumentInstanceAsync(DocumentInstanceId);
            await index.RebuildFtsForDocumentInstanceAsync(DocumentInstanceId);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
        }
    }
}
