using Dapper;
using FluentAssertions;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Evidence;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Mcp;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Infrastructure.Workflows;
using LiteratureApp.Mcp;
using LiteratureApp.Search;

namespace LiteratureApp.Tests;

public sealed class McpVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsync_reports_searchable_when_fts_contains_term()
    {
        await using var context = await VerificationContext.CreateAsync();
        await context.SeedSearchDataAsync("important research content");

        var result = await context.Verification.VerifyAsync(
            context.DocumentInstanceId.ToString(), "important");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsSearchable.Should().BeTrue();
        result.Value.MatchedUnitCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VerifyAsync_reports_not_searchable_when_fts_empty()
    {
        await using var context = await VerificationContext.CreateAsync();

        var result = await context.Verification.VerifyAsync(
            context.DocumentInstanceId.ToString(), "anything");

        result.IsSuccess.Should().BeTrue();
        result.Value.MatchedUnitCount.Should().Be(0);
    }

    [Fact]
    public async Task VerifyAsync_result_contains_no_local_path_or_secret()
    {
        await using var context = await VerificationContext.CreateAsync();
        await context.SeedSearchDataAsync("sample text for verification");

        var result = await context.Verification.VerifyAsync(
            context.DocumentInstanceId.ToString(), "sample");

        result.IsSuccess.Should().BeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
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

        private VerificationContext(TemporarySqliteDatabase db, McpVerificationService verification, DocumentInstanceId docId, LibraryIdentityService library, IClock clock)
        {
            Database = db;
            Verification = verification;
            DocumentInstanceId = docId;
            Library = library;
            Clock = clock;
        }

        public static async Task<VerificationContext> CreateAsync()
        {
            var db = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(db.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Test Lib");
            var items = new ItemService(db.ConnectionFactory, library, clock);
            var item = await items.CreateItemAsync("document", "Test Doc");
            var docs = new DocumentInstanceService(db.ConnectionFactory, clock);
            var doc = await docs.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var pages = new PageService(db.ConnectionFactory, clock);
            var page = await pages.CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);

            // Create a layout revision
            var layout = new LayoutTreeService(db.ConnectionFactory, clock);
            var rev = await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Import, true);
            await layout.AddNodeAsync(rev.Value.LayoutRevisionId, page.Value.PageId, null, LayoutNodeType.Paragraph, null, "test text", TextPolicy.Own, 1, LayoutNodeSource.Import);

            var search = new SqliteSearchService(db.ConnectionFactory);
            var evidence = new EvidenceReferenceService(db.ConnectionFactory, clock);
            var mcp = new McpReadApi(db.ConnectionFactory, search, evidence);
            var verification = new McpVerificationService(db.ConnectionFactory, mcp);

            return new VerificationContext(db, verification, doc.Value.DocumentInstanceId, library, clock);
        }

        public async Task SeedSearchDataAsync(string text)
        {
            var builder = new SearchUnitBuilder(Database.ConnectionFactory, Clock);
            var index = new SearchIndexRebuilder(Database.ConnectionFactory, Clock);
            await builder.RebuildForDocumentInstanceAsync(DocumentInstanceId);
            await index.RebuildFtsForDocumentInstanceAsync(DocumentInstanceId);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }
}
