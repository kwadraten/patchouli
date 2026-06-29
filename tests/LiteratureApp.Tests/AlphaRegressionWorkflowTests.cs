using System.Text.Json;
using FluentAssertions;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Files;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Time;
using LiteratureApp.Evidence;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Credentials;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Evidence;
using LiteratureApp.Infrastructure.Files;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Mcp;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Infrastructure.Snapshots;
using LiteratureApp.Mcp;
using LiteratureApp.Search;

namespace LiteratureApp.Tests;

public sealed class AlphaRegressionWorkflowTests
{
    [Fact]
    public async Task AlphaRegressionWorkflow_minimal_end_to_end_path_succeeds()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var syncRoot = CreateTempDirectory();
        var stagingRoot = CreateTempDirectory();
        const string fakeLocalPath = "/tmp/literatureapp-alpha-private/source.pdf";
        const string fakeSecret = "alpha-provider-secret-value";

        try
        {
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var libraryService = new LibraryIdentityService(database.ConnectionFactory, clock);
            var library = await libraryService.CreateLibraryAsync("Alpha Regression Library");
            var items = new ItemService(database.ConnectionFactory, libraryService, clock);
            var item = await items.CreateItemAsync("book", "Alpha Regression Item");
            var files = new FileAssetService(database.ConnectionFactory, libraryService, clock);
            var file = await files.RegisterFileAsync(fakeLocalPath);
            var documents = new DocumentInstanceService(database.ConnectionFactory, clock);
            var document = await documents.AttachDocumentInstanceAsync(item.Value.ItemId, file.Value.FileAssetId, DocumentInstanceType.PrimaryScan);
            var pages = new PageService(database.ConnectionFactory, clock);
            var page = await pages.CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "alpha-test", null);
            var layout = new LayoutTreeService(database.ConnectionFactory, clock);
            var revision = await layout.CreateLayoutRevisionAsync(document.Value.DocumentInstanceId, LayoutRevisionSource.Manual, makeCurrent: true);
            await layout.AddNodeAsync(revision.Value.LayoutRevisionId, page.Value.PageId, null, LayoutNodeType.Paragraph, new NormalizedBBox(.1, .1, .8, .2), "这是 Alpha 中文回归文本。", TextPolicy.Own, 1, LayoutNodeSource.Manual);
            (await layout.BuildPagePlainTextAsync(page.Value.PageId, revision.Value.LayoutRevisionId)).Value.Text.Should().Contain("中文回归");

            var builder = new SearchUnitBuilder(database.ConnectionFactory, clock);
            var index = new SearchIndexRebuilder(database.ConnectionFactory, clock);
            await builder.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);
            await index.RebuildFtsForLibraryAsync();
            var search = new SqliteSearchService(database.ConnectionFactory);
            var searched = await search.SearchLibraryAsync(new SearchRequest("中文回归"));
            searched.Value.Results.Should().ContainSingle();
            var unitId = searched.Value.Results.Single().MatchedUnits.Single().UnitId;

            var evidence = new EvidenceReferenceService(database.ConnectionFactory, clock);
            var record = await evidence.CreateFromSearchUnitAsync(unitId);
            var markdown = await evidence.CreateMarkdownAsync(record.Value.EvidenceRefId);
            markdown.Value.Markdown.Should().NotBeNullOrWhiteSpace();
            (await evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Pinned)).Value.PinnedText.Should().Contain("中文回归");
            (await evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Current)).Value.CurrentText.Should().Contain("中文回归");
            (await evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Compare)).Value.HasTextChanged.Should().BeFalse();

            await new CredentialStore(database.ConnectionFactory, libraryService, clock).SaveCredentialAsync("test-provider", "Alpha credential", fakeSecret);
            var mcp = new McpReadApi(database.ConnectionFactory, search, evidence);
            var mcpResult = await mcp.SearchLibraryAsync(new McpSearchLibraryRequest("中文回归"));
            var mcpJson = JsonSerializer.Serialize(mcpResult.Value);
            mcpJson.Should().NotContain(fakeLocalPath).And.NotContain(fakeSecret);

            var beforeImport = await File.ReadAllBytesAsync(database.Path);
            var publisher = new SnapshotPublisher(clock);
            var published = await publisher.PublishSnapshotAsync(new SnapshotPublishRequest(database.Path, syncRoot, "alpha-device"));
            File.Exists(published.Value.ManifestPath).Should().BeTrue();
            var imported = await new SnapshotImporter().ImportSnapshotToStagingAsync(new SnapshotImportRequest(published.Value.ManifestPath, stagingRoot, library.Value.LibraryId, database.Path));
            File.Exists(imported.Value.StagingDatabasePath).Should().BeTrue();
            File.Exists(database.Path).Should().BeTrue();
            (await File.ReadAllBytesAsync(database.Path)).Should().Equal(beforeImport);
        }
        finally
        {
            if (Directory.Exists(syncRoot)) Directory.Delete(syncRoot, true);
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
        }
    }

    private static string CreateTempDirectory() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"literatureapp-alpha-{Guid.NewGuid():N}")).FullName;
}
