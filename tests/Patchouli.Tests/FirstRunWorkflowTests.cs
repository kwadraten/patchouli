using System.IO.Compression;
using System.Net;
using System.Text;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Configuration;
using Patchouli.Core.Documents;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

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

    [Fact]
    public async Task RunMinerUExtractionAsync_uses_real_mineru_client_protocol_before_mcp_verification()
    {
        await using var context = await WorkflowContext.CreateAsync();
        var pdfPath = Path.Combine(Path.GetTempPath(), $"first-run-real-client-{Guid.NewGuid():N}.pdf");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"first-run-cache-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(pdfPath, "%PDF-1.4");
        var import = await context.PdfImport.ImportPdfAsync(new PdfImportRequest(pdfPath, "Real Client Doc", null, 1));
        import.Success.Should().BeTrue(import.ErrorMessage);

        var zipPath = CreateZip("""
        [
          { "type": "text", "page_idx": 0, "text": "real mineru protocol searchable text", "bbox": [0, 0, 1000, 100] }
        ]
        """);
        var zipBytes = await File.ReadAllBytesAsync(zipPath);
        var uploaded = false;

        var handler = new WorkflowMinerUHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v4/file-urls/batch")
            {
                request.Headers.Authorization?.ToString().Should().Be("Bearer token");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "code": 0,
                      "data": {
                        "batch_id": "batch-1",
                        "file_urls": ["https://upload.example.test/file"]
                      },
                      "msg": "ok"
                    }
                    """)
                };
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.Host == "upload.example.test")
            {
                uploaded = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/batch-1")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "code": 0,
                      "data": {
                        "batch_id": "batch-1",
                        "extract_result": [
                          {
                            "file_name": "first-run-real-client.pdf",
                            "state": "done",
                            "err_msg": "",
                            "full_zip_url": "https://cdn.example.test/result.zip"
                          }
                        ]
                      },
                      "msg": "ok"
                    }
                    """)
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.Host == "cdn.example.test")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        try
        {
            var workflow = context.CreateWorkflow(config => new MinerUClient(
                new HttpClient(handler),
                new MinerUOptions
                {
                    Token = config.Token,
                    BaseUrl = "https://mineru.example.test",
                    ModelVersion = config.ModelVersion ?? "vlm",
                    IsOcr = config.IsOcr,
                    EnableTable = config.EnableTable,
                    EnableFormula = config.EnableFormula,
                    PollingIntervalMs = 1,
                    PollingTimeoutSeconds = 5
                }));

            var state = await workflow.RunMinerUExtractionAsync(
                new MinerUConfiguration("token", null, "vlm", true, true, true),
                pdfPath,
                cacheDirectory,
                import.CreatedDocumentInstanceId!);

            state.IsComplete.Should().BeTrue(state.LastError);
            state.CurrentStep.Should().Be(FirstRunStep.Complete);
            state.ProgressText.Should().Contain("MCP verification passed");
            uploaded.Should().BeTrue();
        }
        finally
        {
            File.Delete(pdfPath);
            File.Delete(zipPath);
            if (Directory.Exists(cacheDirectory))
                Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MinerU_live_smoke_imports_real_provider_result_into_mcp()
    {
        if (Environment.GetEnvironmentVariable("PATCHOULI_RUN_MINERU_SMOKE") != "1")
            return;

        var token = Environment.GetEnvironmentVariable("MINERU_TOKEN")
            ?? DotEnv.ReadValue(TestPaths.FromRepositoryRoot(".env"), "MINERU_TOKEN");
        token.Should().NotBeNullOrWhiteSpace("PATCHOULI_RUN_MINERU_SMOKE=1 requires MINERU_TOKEN in the process environment or .env");

        await using var context = await WorkflowContext.CreateAsync();
        var pdfText = $"Patchouli MinerU smoke text {Guid.NewGuid():N}";
        var pdfPath = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-smoke-{Guid.NewGuid():N}.pdf");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-mineru-smoke-cache-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(pdfPath, CreateSmokePdf(pdfText));
        var import = await context.PdfImport.ImportPdfAsync(new PdfImportRequest(pdfPath, "MinerU Live Smoke", null, 1));
        import.Success.Should().BeTrue(import.ErrorMessage);

        try
        {
            var workflow = context.CreateWorkflow(config => new MinerUClient(new MinerUOptions
            {
                Token = config.Token,
                BaseUrl = "https://mineru.net",
                ModelVersion = config.ModelVersion ?? "vlm",
                IsOcr = config.IsOcr,
                EnableTable = config.EnableTable,
                EnableFormula = config.EnableFormula,
                PollingIntervalMs = 2000,
                PollingTimeoutSeconds = 360
            }));

            var state = await workflow.RunMinerUExtractionAsync(
                new MinerUConfiguration(token!, null, "vlm", true, true, true),
                pdfPath,
                cacheDirectory,
                import.CreatedDocumentInstanceId!);

            state.IsComplete.Should().BeTrue(state.LastError ?? state.ProgressText);
            await using var connection = context.Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            var indexedText = await connection.ExecuteScalarAsync<string?>(
                """
                select resolved_text
                from search_units
                where document_instance_id = @DocumentInstanceId
                  and status = 'current'
                  and length(trim(resolved_text)) > 0
                limit 1;
                """,
                new { DocumentInstanceId = import.CreatedDocumentInstanceId });

            indexedText.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(pdfPath);
            if (Directory.Exists(cacheDirectory))
                Directory.Delete(cacheDirectory, recursive: true);
        }
    }

    private static byte[] CreateSmokePdf(string text)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            CreatePdfStream($"BT /F1 20 Tf 72 720 Td ({EscapePdfText(text)}) Tj ET")
        };

        var builder = new StringBuilder();
        var offsets = new List<int> { 0 };
        builder.Append("%PDF-1.4\n");
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(i + 1).Append(" 0 obj\n");
            builder.Append(objects[i]).Append('\n');
            builder.Append("endobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n");
        builder.Append("0 ").Append(objects.Length + 1).Append('\n');
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n");
        builder.Append("<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        builder.Append("startxref\n");
        builder.Append(xrefOffset).Append('\n');
        builder.Append("%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string CreatePdfStream(string content) =>
        $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static string EscapePdfText(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

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

    private sealed class WorkflowMinerUHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public WorkflowMinerUHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
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
