using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Mcp;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class AlphaRegressionWorkflowTests
{
    [Fact]
    public async Task AlphaRegressionWorkflow_minimal_end_to_end_path_succeeds()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        string syncRoot = CreateTempDirectory();
        string stagingRoot = CreateTempDirectory();
        const string fakeLocalPath = "/tmp/patchouli-alpha-private/source.pdf";
        const string fakeSecret = "alpha-provider-secret-value";

        try
        {
            FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraryService = new(database.ConnectionFactory, clock);
            Result<LibraryMetadata> library = await libraryService.CreateLibraryAsync("Alpha Regression Library");
            ItemService items = new(database.ConnectionFactory, libraryService, clock);
            Result<ItemMetadata> item = await items.CreateItemAsync("book", "Alpha Regression Item");
            FileAssetService files = new(database.ConnectionFactory, libraryService, clock);
            Result<FileAsset> file = await files.RegisterFileAsync(fakeLocalPath);
            DocumentInstanceService documents = new(database.ConnectionFactory, clock);
            Result<DocumentInstance> document = await documents.AttachDocumentInstanceAsync(item.Value.ItemId,
                file.Value.FileAssetId, DocumentInstanceType.PrimaryScan);
            PageService pages = new(database.ConnectionFactory, clock);
            Result<Page> page = await pages.CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "alpha-test", null);
            LayoutTreeService layout = new(database.ConnectionFactory, clock);
            Result<LayoutRevision> revision = await layout.CreateLayoutRevisionAsync(document.Value.DocumentInstanceId,
                LayoutRevisionSource.Manual, true);
            await layout.AddNodeAsync(revision.Value.LayoutRevisionId, page.Value.PageId, null,
                LayoutNodeType.Paragraph, new NormalizedBBox(.1, .1, .8, .2), "这是 Alpha 中文回归文本。", TextPolicy.Own, 1,
                LayoutNodeSource.Manual);
            (await layout.BuildPagePlainTextAsync(page.Value.PageId, revision.Value.LayoutRevisionId)).Value.Text
                .Should().Contain("中文回归");

            SearchUnitBuilder builder = new(database.ConnectionFactory, clock);
            SearchIndexRebuilder index = new(database.ConnectionFactory, clock);
            await builder.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);
            await index.RebuildFtsForLibraryAsync();
            SqliteSearchService search = new(database.ConnectionFactory);
            Result<SearchResultPage> searched = await search.SearchLibraryAsync(new SearchRequest("中文回归"));
            searched.Value.Results.Should().ContainSingle();
            SearchUnitId unitId = searched.Value.Results.Single().MatchedUnits.Single().UnitId;

            EvidenceReferenceService evidence = new(database.ConnectionFactory, clock);
            Result<EvidenceRefRecord> record = await evidence.CreateFromSearchUnitAsync(unitId);
            Result<EvidenceMarkdown> markdown = await evidence.CreateMarkdownAsync(record.Value.EvidenceRefId);
            markdown.Value.Markdown.Should().NotBeNullOrWhiteSpace();
            (await evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Pinned)).Value.PinnedText
                .Should().Contain("中文回归");
            (await evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Current)).Value.CurrentText
                .Should().Contain("中文回归");
            (await evidence.ResolveAsync(record.Value.EvidenceRefId, EvidenceResolutionMode.Compare)).Value
                .HasTextChanged.Should().BeFalse();

            string credentialPath = Path.Combine(Path.GetTempPath(), $"patchouli-credential-{Guid.NewGuid():N}.json");
            await new CredentialStore(credentialPath).SaveAsync(
                "test-provider", "Alpha credential", fakeSecret);
            McpReadApi mcp = new(database.ConnectionFactory, search, evidence);
            Result<McpSearchLibraryResponse> mcpResult =
                await mcp.SearchLibraryAsync(new McpSearchLibraryRequest("中文回归"));
            string mcpJson = JsonSerializer.Serialize(mcpResult.Value);
            mcpJson.Should().NotContain(fakeLocalPath).And.NotContain(fakeSecret);

            byte[] beforeImport = await File.ReadAllBytesAsync(database.Path);
            SnapshotPublisher publisher = new(clock);
            Result<SnapshotPublishResult> published =
                await publisher.PublishSnapshotAsync(
                    new SnapshotPublishRequest(database.Path, syncRoot, "alpha-device"));
            File.Exists(published.Value.ManifestPath).Should().BeTrue();
            Result<SnapshotImportResult> imported = await new SnapshotImporter().ImportSnapshotToStagingAsync(
                new SnapshotImportRequest(published.Value.ManifestPath, stagingRoot, library.Value.LibraryId,
                    database.Path));
            File.Exists(imported.Value.StagingDatabasePath).Should().BeTrue();
            File.Exists(database.Path).Should().BeTrue();
            (await File.ReadAllBytesAsync(database.Path)).Should().Equal(beforeImport);
        }
        finally
        {
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, true);
            }

            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
        }
    }

    private static string CreateTempDirectory()
    {
        return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"patchouli-alpha-{Guid.NewGuid():N}"))
            .FullName;
    }
}
