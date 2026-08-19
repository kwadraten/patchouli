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
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Credentials;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Mcp;
using Patchouli.Core.Search;

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

            DocumentTreeService treeService = new(database.ConnectionFactory, clock, new MarkdigMarkdownEngine());
            DocumentTreeRevision working = (await treeService.BeginWorkingRevisionAsync(
                document.Value.DocumentInstanceId,
                page.Value.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("这是 Alpha 中文回归文本。"))
                ],
                DocumentTreeRevisionSource.Import)).Value;
            DocumentTreeRevision revision =
                (await treeService.CommitWorkingRevisionAsync(working.TreeRevisionId)).Value;

            DocumentMarkdownCompiler compiler = new(treeService, new MarkdigMarkdownEngine());
            (await compiler.CompilePageMarkdownAsync(revision.TreeRevisionId)).Value.Markdown.Should()
                .Contain("中文回归");

            SearchUnitBuilder builder = new(database.ConnectionFactory, clock);
            SearchIndexRebuilder index = new(database.ConnectionFactory, clock);
            await builder.RebuildForDocumentInstanceAsync(document.Value.DocumentInstanceId);
            await index.RebuildFtsForLibraryAsync();
            SqliteSearchService search = new(database.ConnectionFactory);
            Result<SearchResultPage> searched = await search.SearchLibraryAsync(new SearchRequest("中文回归"));
            searched.Value.Results.Should().ContainSingle();
            SearchUnitId unitId = searched.Value.Results.Single().MatchedUnits.Single().UnitId;
            DocumentBoxId boxId = searched.Value.Results.Single().MatchedUnits.Single().BoxId;

            string versionedUri = McpResourceUris.EvidencePageUri(
                document.Value.DocumentInstanceId, 1, revision.TreeRevisionId, boxId);
            versionedUri.Should().StartWith("patchouli://texts/").And.Contain("?rev=").And.Contain("&box=");

            IVersionedEvidenceReader evidence = new VersionedEvidenceReader(
                database.ConnectionFactory,
                libraryService,
                treeService,
                compiler);
            Result<EvidencePageText> evidenceText = await evidence.GetBoxTextAsync(
                document.Value.DocumentInstanceId, 1, revision.TreeRevisionId, boxId);
            evidenceText.Value.Markdown.Should().Contain("中文回归");

            string credentialPath = Path.Combine(Path.GetTempPath(), $"patchouli-credential-{Guid.NewGuid():N}.json");
            await new CredentialStore(credentialPath).SaveAsync(
                "test-provider", "Alpha credential", fakeSecret);
            McpReadApi mcp = new(database.ConnectionFactory, search);
            Result<McpSearchLibraryResponse> mcpResult =
                await mcp.SearchLibraryAsync(new McpSearchLibraryRequest("中文回归"));
            string mcpJson = JsonSerializer.Serialize(mcpResult.Value);
            mcpJson.Should().NotContain(fakeLocalPath).And.NotContain(fakeSecret).And.NotContain("evref");

            SnapshotPublisher publisher = new(clock);
            Result<SnapshotPublishResult> published =
                await publisher.PublishSnapshotAsync(
                    new SnapshotPublishRequest(database.Path, syncRoot, "alpha-device"));
            File.Exists(published.Value.ManifestPath).Should().BeTrue();
            Result<SnapshotImportResult> imported = await new SnapshotImporter().ImportSnapshotToStagingAsync(
                new SnapshotImportRequest(published.Value.ManifestPath, stagingRoot, library.Value.LibraryId,
                    database.Path));
            string stagingDatabasePath = imported.Value.StagingDatabasePath!;
            File.Exists(stagingDatabasePath).Should().BeTrue();
            Path.GetFullPath(stagingDatabasePath).Should().NotBe(Path.GetFullPath(database.Path));
            (await libraryService.GetCurrentLibraryAsync()).Value.LibraryId.Should().Be(library.Value.LibraryId);
            (await search.SearchLibraryAsync(new SearchRequest("中文回归"))).Value.Results.Should().ContainSingle();

            IVersionedEvidenceReader stagingEvidence = new VersionedEvidenceReader(
                new SqliteConnectionFactory(stagingDatabasePath),
                libraryService,
                new DocumentTreeService(new SqliteConnectionFactory(stagingDatabasePath), clock,
                    new MarkdigMarkdownEngine()),
                new DocumentMarkdownCompiler(
                    new DocumentTreeService(new SqliteConnectionFactory(stagingDatabasePath), clock,
                        new MarkdigMarkdownEngine()),
                    new MarkdigMarkdownEngine()));
            Result<EvidencePageText> stagingText = await stagingEvidence.GetBoxTextAsync(
                document.Value.DocumentInstanceId, 1, revision.TreeRevisionId, boxId);
            stagingText.IsSuccess.Should().BeTrue();
            stagingText.Value.Markdown.Should().Contain("中文回归");
        }
        finally
        {
            SqliteTestCleanup.ReleasePoolsInDirectory(syncRoot);
            if (Directory.Exists(syncRoot))
            {
                Directory.Delete(syncRoot, true);
            }

            SqliteTestCleanup.ReleasePoolsInDirectory(stagingRoot);
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
