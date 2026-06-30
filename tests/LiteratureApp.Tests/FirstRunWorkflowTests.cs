using System.IO.Compression;
using FluentAssertions;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Import;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Evidence;
using LiteratureApp.Infrastructure.Files;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Mcp;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Ocr.MinerU;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Infrastructure.Workflows;
using LiteratureApp.Mcp;
using LiteratureApp.Ocr.MinerU;

namespace LiteratureApp.Tests;

public sealed class FirstRunWorkflowTests
{
    [Fact]
    public async Task RunMinerUExtractionAsync_requires_token_before_creating_client()
    {
        await using var context = await WorkflowContext.CreateAsync();
        var workflow = context.CreateWorkflow(_ => throw new InvalidOperationException("Client should not be created without a token."));

        var state = await workflow.RunMinerUExtractionAsync(
            new MinerUConfiguration("", null, "vlm", true, true, true),
            "input.pdf",
            Path.GetTempPath(),
            Guid.NewGuid().ToString());

        state.CurrentStep.Should().Be(FirstRunStep.MinerUConfig);
        state.LastError.Should().Contain("MinerU API token");
    }

    [Fact]
    public async Task RunMinerUExtractionAsync_imports_rebuilds_fts_and_verifies_mcp()
    {
        await using var context = await WorkflowContext.CreateAsync();
        var pdfPath = Path.Combine(Path.GetTempPath(), $"first-run-{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(pdfPath, "%PDF-1.4");
        var import = await context.PdfImport.ImportPdfAsync(new PdfImportRequest(pdfPath, "Workflow Doc", null, 1));
        import.Success.Should().BeTrue(import.ErrorMessage);

        var zipPath = CreateZip("""
        [
          { "type": "text", "page_idx": 0, "text": "closed loop searchable mineru text", "bbox": [0, 0, 1000, 100] }
        ]
        """);

        try
        {
            var fakeClient = new FakeMinerUClient(zipPath);
            var workflow = context.CreateWorkflow(_ => fakeClient);

            var state = await workflow.RunMinerUExtractionAsync(
                new MinerUConfiguration("token", null, "vlm", true, true, true),
                pdfPath,
                Path.GetTempPath(),
                import.CreatedDocumentInstanceId!);

            state.IsComplete.Should().BeTrue(state.LastError);
            state.CurrentStep.Should().Be(FirstRunStep.Complete);
            state.ProgressText.Should().Contain("MCP verification passed");
            fakeClient.Uploaded.Should().BeTrue();
        }
        finally
        {
            File.Delete(pdfPath);
            File.Delete(zipPath);
        }
    }

    private static string CreateZip(string contentListJson)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"mineru-workflow-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("sample_content_list.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contentListJson);
        return zipPath;
    }

    private sealed class WorkflowContext : IAsyncDisposable
    {
        public TemporarySqliteDatabase Database { get; }
        public PdfImportWorkflow PdfImport { get; }
        private LibraryIdentityService Library { get; }
        private PdfDiscoveryService PdfDiscovery { get; }
        private MinerUResultImporter MinerUImporter { get; }
        private SearchUnitBuilder SearchUnits { get; }
        private SearchIndexRebuilder SearchIndex { get; }
        private McpVerificationService Verification { get; }

        private WorkflowContext(
            TemporarySqliteDatabase database,
            PdfImportWorkflow pdfImport,
            LibraryIdentityService library,
            PdfDiscoveryService pdfDiscovery,
            MinerUResultImporter minerUImporter,
            SearchUnitBuilder searchUnits,
            SearchIndexRebuilder searchIndex,
            McpVerificationService verification)
        {
            Database = database;
            PdfImport = pdfImport;
            Library = library;
            PdfDiscovery = pdfDiscovery;
            MinerUImporter = minerUImporter;
            SearchUnits = searchUnits;
            SearchIndex = searchIndex;
            Verification = verification;
        }

        public static async Task<WorkflowContext> CreateAsync()
        {
            var db = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(db.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Test Library");
            var pdfImport = new PdfImportWorkflow(
                new FileAssetService(db.ConnectionFactory, library, clock),
                new ItemService(db.ConnectionFactory, library, clock),
                new DocumentInstanceService(db.ConnectionFactory, clock),
                new PageService(db.ConnectionFactory, clock),
                new FixedMetadataReader(1),
                clock);
            var search = new SqliteSearchService(db.ConnectionFactory);
            var evidence = new EvidenceReferenceService(db.ConnectionFactory, clock);
            var mcp = new McpReadApi(db.ConnectionFactory, search, evidence);
            var verification = new McpVerificationService(db.ConnectionFactory, mcp);

            return new WorkflowContext(
                db,
                pdfImport,
                library,
                new PdfDiscoveryService(),
                new MinerUResultImporter(db.ConnectionFactory, clock),
                new SearchUnitBuilder(db.ConnectionFactory, clock),
                new SearchIndexRebuilder(db.ConnectionFactory, clock),
                verification);
        }

        public FirstRunWorkflow CreateWorkflow(Func<MinerUConfiguration, IMinerUClient> clientFactory) =>
            new(Library, PdfDiscovery, PdfImport, MinerUImporter, SearchUnits, SearchIndex, Verification, clientFactory);

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class FixedMetadataReader : IPdfMetadataReader
    {
        private readonly int _pageCount;
        public FixedMetadataReader(int pageCount) => _pageCount = pageCount;
        public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(_pageCount);
    }

    private sealed class FakeMinerUClient : IMinerUClient
    {
        private readonly string _zipPath;
        public FakeMinerUClient(string zipPath) => _zipPath = zipPath;
        public bool IsConfigured => true;
        public bool Uploaded { get; private set; }

        public Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(IReadOnlyList<MinerUUploadRequest> files, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinerUUploadBatch>.Success(new MinerUUploadBatch("batch-1", [new MinerUFileUploadUrl(files[0].FileName, "https://upload.example.test/file", "file-1")])));

        public Task<Result> UploadFileAsync(string uploadUrl, string localPath, CancellationToken cancellationToken = default)
        {
            Uploaded = true;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinerUPollResult>.Success(new MinerUPollResult(batchId, MinerUProviderStatus.Done, "https://download.example.test/result.zip", null)));

        public Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(string batchId, string downloadDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinerUDownloadedResult>.Success(new MinerUDownloadedResult(batchId, _zipPath, MinerUProviderStatus.Done)));
    }
}
