using System.IO.Compression;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Credentials;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
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
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class OcrAdapterReadinessTests
{
    [Fact] public async Task ModelPathValidator_required_missing_returns_missing_model_path() => (await new OcrModelPathValidator().ValidateModelPathAsync(null, true)).Status.Should().Be(OcrEnvironmentStatus.MissingModelPath);

    [Fact]
    public async Task ModelPathValidator_existing_file_returns_ready()
    {
        var path = Path.GetTempFileName();
        try { (await new OcrModelPathValidator().ValidateModelPathAsync(path, true)).IsReady.Should().BeTrue(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ModelPathValidator_existing_directory_returns_ready()
    {
        var path = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ocr-model-{Guid.NewGuid():N}")).FullName;
        try { (await new OcrModelPathValidator().ValidateModelPathAsync(path, true)).IsReady.Should().BeTrue(); }
        finally { Directory.Delete(path); }
    }

    [Fact] public async Task ModelPathValidator_missing_path_returns_inaccessible() => (await new OcrModelPathValidator().ValidateModelPathAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), true)).Status.Should().Be(OcrEnvironmentStatus.ModelPathInaccessible);
    [Fact] public async Task ModelPathValidator_valid_http_url_is_accepted_without_network_call() => (await new OcrModelPathValidator().ValidateModelPathAsync("https://example.invalid/model", true)).IsReady.Should().BeTrue();

    [Fact]
    public async Task RebindModelPath_creates_new_preset_version()
    {
        await using var c = await OcrReadinessContext.CreateAsync();
        var original = await c.Presets.GetCurrentVersionAsync(c.PresetId);
        var rebound = await c.Presets.RebindModelPathAsync(c.PresetId, "/not-yet-present/model");
        rebound.IsSuccess.Should().BeTrue();
        rebound.Value.PresetVersionId.Should().NotBe(original.Value.PresetVersionId);
        (await c.Presets.GetPresetAsync(c.PresetId)).Value.CurrentVersionId.Should().Be(rebound.Value.PresetVersionId);
    }

    [Fact]
    public async Task RebindModelPath_does_not_modify_old_preset_version()
    {
        await using var c = await OcrReadinessContext.CreateAsync();
        var old = await c.Presets.GetCurrentVersionAsync(c.PresetId);
        await c.Presets.RebindModelPathAsync(c.PresetId, "/new/model");
        await using var connection = c.Database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<string?>("select model_path from ocr_preset_versions where preset_version_id = @Id;", new { Id = old.Value.PresetVersionId.ToString() })).Should().Be(old.Value.ModelPath);
    }

    [Fact] public async Task RebindModelPath_rejects_blank_path() { await using var c = await OcrReadinessContext.CreateAsync(); (await c.Presets.RebindModelPathAsync(c.PresetId, " ")).ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); }
    [Fact] public void AdapterRegistry_lists_mock_capability() => CreateRegistry().ListCapabilities().Should().Contain(c => c.EngineId == OcrEngineIds.Mock);
    [Fact] public void AdapterRegistry_lists_local_placeholder_capability() => CreateRegistry().ListCapabilities().Should().Contain(c => c.EngineId == OcrEngineIds.LocalPlaceholder && c.RequiresModelPath);

    [Fact]
    public async Task LocalPlaceholder_CheckEnvironment_missing_model_path_not_ready()
    {
        var adapter = new LocalPlaceholderOcrAdapter(new OcrModelPathValidator());
        var result = await adapter.CheckEnvironmentAsync(new OcrPresetVersion(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.LocalPlaceholder, "local-model", null, "{}", false, DateTimeOffset.UtcNow));
        result.IsReady.Should().BeFalse(); result.Status.Should().Be(OcrEnvironmentStatus.MissingModelPath);
    }

    [Fact]
    public async Task LocalPlaceholder_CheckEnvironment_existing_model_path_ready()
    {
        var path = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ocr-local-{Guid.NewGuid():N}")).FullName;
        try
        {
            var adapter = new LocalPlaceholderOcrAdapter(new OcrModelPathValidator());
            (await adapter.CheckEnvironmentAsync(new OcrPresetVersion(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.LocalPlaceholder, "local-model", path, "{}", false, DateTimeOffset.UtcNow))).IsReady.Should().BeTrue();
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public async Task RunPresetOnPages_mock_still_works()
    {
        await using var c = await OcrReadinessContext.CreateAsync();
        var result = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, c.PresetId, [c.PageId]);
        result.IsSuccess.Should().BeTrue(); result.Value.State.Should().Be(OcrRunState.Completed);
    }

    [Fact]
    public async Task RunPresetOnPages_non_ready_real_adapter_returns_validation_failed_without_page_success()
    {
        await using var c = await OcrReadinessContext.CreateAsync(engineId: OcrEngineIds.LocalPlaceholder, modelPath: null);
        var result = await c.Coordinator.RunPresetOnPagesAsync(c.DocumentInstanceId, c.PresetId, [c.PageId]);
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        await using var connection = c.Database.ConnectionFactory.CreateConnection(); await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<int>("select count(1) from ocr_page_results;")).Should().Be(0);
    }

    [Fact]
    public async Task RunPresetOnDocument_mineru_preset_imports_document_through_ocr_lifecycle()
    {
        var pdfPath = Path.Combine(Path.GetTempPath(), $"mineru-preset-{Guid.NewGuid():N}.pdf");
        File.Copy(TestFixtures.RealThreePagePdf, pdfPath);
        var zipPath = CreateMinerUZip("""
        [
          { "type": "text", "page_idx": 0, "text": "mineru preset page one", "bbox": [0, 0, 1000, 100] },
          { "type": "text", "page_idx": 1, "text": "mineru preset page two", "bbox": [0, 100, 1000, 200] }
        ]
        """);
        string? tokenUsed = null;
        var zipBytes = await File.ReadAllBytesAsync(zipPath);

        try
        {
            await using var c = await OcrReadinessContext.CreateAsync(
                engineId: OcrEngineIds.MinerU,
                sourcePdfPath: pdfPath,
                pageCount: 2,
                minerUClientFactory: config =>
                {
                    tokenUsed = config.Token;
                    return CreateProtocolMinerUClient(config, zipBytes);
                });
            await c.SaveMinerUCredentialAsync("preset-token");

            var result = await c.Coordinator.RunPresetOnDocumentAsync(c.DocumentInstanceId, c.PresetId);

            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.EngineId.Should().Be(OcrEngineIds.MinerU);
            result.Value.State.Should().Be(OcrRunState.Completed);
            result.Value.OutputRevisionId.Should().NotBeNull();
            tokenUsed.Should().Be("preset-token");
            (await c.CountAsync("ocr_page_results")).Should().Be(2);
            (await c.CountAsync("layout_nodes")).Should().Be(2);
        }
        finally
        {
            File.Delete(pdfPath);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task RunPresetOnRegion_supported_adapter_creates_candidate_without_touching_current_search()
    {
        var adapter = new FakeRegionAdapter();
        await using var c = await OcrReadinessContext.CreateAsync(engineId: FakeRegionAdapter.EngineIdValue, adapter: adapter, applyOnSuccess: true);

        var result = await c.Coordinator.RunPresetOnRegionAsync(c.DocumentInstanceId, c.PresetId, c.PageId, new NormalizedBBox(0.2, 0.2, 0.3, 0.3));

        result.IsSuccess.Should().BeTrue();
        result.Value.OutputRevisionId.Should().NotBeNull();
        adapter.LastInput.Should().NotBeNull();
        adapter.LastInput!.InputKind.Should().Be(OcrInputKinds.RegionImage);
        adapter.LastInput.RegionBBox.Should().Be(new NormalizedBBox(0.2, 0.2, 0.3, 0.3));
        (await c.GetCurrentRevisionIdAsync()).Should().Be(c.InitialCurrentRevisionId);
        (await c.CountAsync("search_units")).Should().Be(0);
    }

    [Fact]
    public async Task RunPresetOnRegion_rejects_out_of_bounds_bbox_without_creating_run()
    {
        await using var c = await OcrReadinessContext.CreateAsync(engineId: FakeRegionAdapter.EngineIdValue, adapter: new FakeRegionAdapter());

        var result = await c.Coordinator.RunPresetOnRegionAsync(c.DocumentInstanceId, c.PresetId, c.PageId, new NormalizedBBox(0.9, 0.9, 0.2, 0.2));

        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await c.CountAsync("ocr_runs")).Should().Be(0);
    }

    [Fact]
    public async Task MCP_does_not_expose_model_path_after_rebind()
    {
        await using var c = await OcrReadinessContext.CreateAsync();
        const string modelPath = "/private/ocr-model-path";
        await c.Presets.RebindModelPathAsync(c.PresetId, modelPath);
        var result = await c.Mcp.GetDocumentStatusAsync(c.DocumentInstanceId);
        JsonSerializer.Serialize(result.Value).Should().NotContain(modelPath);
    }

    [Fact]
    public void Product_ocr_boundary_is_documented_in_agent_prd()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot(".agent", "PRD.md"))
            .Should().Contain("OCR Preset").And.Contain("MCP 无法读取提供程序密钥");
    }

    [Fact]
    public void Product_startup_registers_mineru_workflow_components()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "AppServices.cs"))
            .Should().Contain("MinerUResultImporter").And.Contain("MinerUOcrAdapter");
    }
    [Fact] public void No_new_migration_created_in_step_14A() => Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql").Select(Path.GetFileName).Should().NotContain(name => name!.Contains("14", StringComparison.OrdinalIgnoreCase));

    private static OcrAdapterRegistry CreateRegistry(IRealOcrAdapter? extraAdapter = null)
    {
        var registry = new OcrAdapterRegistry();
        registry.RegisterAdapter(new MockOcrAdapter());
        registry.RegisterAdapter(new LocalPlaceholderOcrAdapter(new OcrModelPathValidator()));
        registry.RegisterAdapter(new MinerUOcrAdapter());
        if (extraAdapter is not null) registry.RegisterAdapter(extraAdapter);
        return registry;
    }

    private sealed class OcrReadinessContext : IAsyncDisposable
    {
        private OcrReadinessContext(TemporarySqliteDatabase database, OcrPresetService presets, OcrRunCoordinator coordinator, McpReadApi mcp, OcrPresetId presetId, DocumentInstanceId documentInstanceId, PageId pageId, LayoutRevisionId initialCurrentRevisionId)
        { Database = database; Presets = presets; Coordinator = coordinator; Mcp = mcp; PresetId = presetId; DocumentInstanceId = documentInstanceId; PageId = pageId; InitialCurrentRevisionId = initialCurrentRevisionId; }
        public TemporarySqliteDatabase Database { get; }
        public OcrPresetService Presets { get; }
        public OcrRunCoordinator Coordinator { get; }
        public McpReadApi Mcp { get; }
        public OcrPresetId PresetId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageId PageId { get; }
        public LayoutRevisionId InitialCurrentRevisionId { get; }

        public static async Task<OcrReadinessContext> CreateAsync(string engineId = OcrEngineIds.Mock, string? modelPath = null, IRealOcrAdapter? adapter = null, bool applyOnSuccess = false, string? sourcePdfPath = null, int pageCount = 1, Func<MinerUConfiguration, IMinerUClient>? minerUClientFactory = null)
        {
            var database = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(database.ConnectionFactory, clock); await library.CreateLibraryAsync("OCR readiness");
            var item = await new ItemService(database.ConnectionFactory, library, clock).CreateItemAsync("book", "OCR readiness item");
            FileAssetId? fileAssetId = null;
            if (!string.IsNullOrWhiteSpace(sourcePdfPath))
                fileAssetId = (await new FileAssetService(database.ConnectionFactory, library, clock).RegisterFileAsync(sourcePdfPath)).Value.FileAssetId;
            var document = await new DocumentInstanceService(database.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, fileAssetId, DocumentInstanceType.PrimaryScan);
            PageId? firstPageId = null;
            for (var i = 0; i < pageCount; i++)
            {
                var page = await new PageService(database.ConnectionFactory, clock).CreatePageAsync(document.Value.DocumentInstanceId, i, (i + 1).ToString(), null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);
                firstPageId ??= page.Value.PageId;
            }
            var layout = new LayoutTreeService(database.ConnectionFactory, clock);
            var currentRevision = await layout.CreateLayoutRevisionAsync(document.Value.DocumentInstanceId, LayoutRevisionSource.Manual, makeCurrent: true);
            var presets = new OcrPresetService(database.ConnectionFactory, library, clock);
            var preset = await presets.CreatePresetAsync("Readiness", null, engineId, "model", modelPath, "{}", applyOnSuccess);
            var registry = CreateRegistry(adapter);
            var coordinator = new OcrRunCoordinator(database.ConnectionFactory, clock, new MockOcrEngine(), adapterRegistry: registry, minerUResultImporter: new MinerUResultImporter(database.ConnectionFactory, clock), minerUClientFactory: minerUClientFactory);
            var search = new SqliteSearchService(database.ConnectionFactory);
            var evidence = new EvidenceReferenceService(database.ConnectionFactory, clock);
            return new OcrReadinessContext(database, presets, coordinator, new McpReadApi(database.ConnectionFactory, search, evidence), preset.Value.PresetId, document.Value.DocumentInstanceId, firstPageId!.Value, currentRevision.Value.LayoutRevisionId);
        }

        public async Task SaveMinerUCredentialAsync(string token)
        {
            await using var connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            var libraryId = await connection.ExecuteScalarAsync<string>("select library_id from library_metadata limit 1;");
            var now = DateTimeOffset.Parse("2026-06-20T00:00:00Z").ToString("O");
            await connection.ExecuteAsync(
                "insert into provider_credentials (credential_id, library_id, provider_id, display_name, secret_value, status, created_at, updated_at) values (@Id, @LibraryId, @Provider, 'MinerU', @Secret, 'active', @Now, @Now);",
                new { Id = CredentialId.New().ToString(), LibraryId = libraryId, Provider = ProviderIds.MinerU, Secret = token, Now = now });
        }

        public async Task<LayoutRevisionId?> GetCurrentRevisionIdAsync()
        {
            await using var connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            var id = await connection.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id = @DocumentInstanceId and is_current = 1 limit 1;", new { DocumentInstanceId = DocumentInstanceId.ToString() });
            return id is null ? null : LayoutRevisionId.Parse(id);
        }

        public async Task<int> CountAsync(string table)
        {
            await using var connection = Database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>($"select count(1) from {table};");
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class FakeRegionAdapter : IRealOcrAdapter
    {
        public const string EngineIdValue = "fake_region";
        public string EngineId => EngineIdValue;
        public string DisplayName => "Fake Region Adapter";
        public string Kind => OcrAdapterKind.LocalProcess;
        public OcrInputDescriptor? LastInput { get; private set; }
        public OcrEngineCapability GetCapability() => new(EngineId, DisplayName, false, false, false, false, true, false, false, false, false, [OcrInputKinds.RegionImage], "Test adapter for region OCR.");
        public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) => Task.FromResult(new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, presetVersion.ModelPath, OcrEnvironmentStatus.Ready, true, "ready", OcrRequiredAction.None, []));
        public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result> ValidateInputAsync(OcrInputDescriptor input, CancellationToken cancellationToken = default) => Task.FromResult(input.InputKind == OcrInputKinds.RegionImage && input.RegionBBox is not null ? Result.Success() : Result.Failure(AppErrorCodes.ValidationFailed, "region bbox required"));
        public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input, OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
        {
            LastInput = input;
            return Task.FromResult(Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(input.PageId, true, "region text", input.RegionBBox, null, null)));
        }
    }

    private static string CreateMinerUZip(string contentListJson)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"mineru-preset-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("sample_content_list.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contentListJson);
        return zipPath;
    }

    private static IMinerUClient CreateProtocolMinerUClient(MinerUConfiguration config, byte[] zipBytes)
    {
        return new MinerUClient(
            new HttpClient(new MinerUProtocolHandler(request =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v4/file-urls/batch")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"code":0,"data":{"batch_id":"batch-1","file_urls":["https://upload.example.test/file"]},"msg":"ok"}""")
                    };

                if (request.Method == HttpMethod.Put && request.RequestUri!.Host == "upload.example.test")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);

                if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v4/extract-results/batch/batch-1")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"code":0,"data":{"batch_id":"batch-1","extract_result":[{"file_name":"sample.pdf","state":"done","err_msg":"","full_zip_url":"https://cdn.example.test/result.zip"}]},"msg":"ok"}
                        """)
                    };

                if (request.Method == HttpMethod.Get && request.RequestUri!.Host == "cdn.example.test")
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            })),
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
            });
    }

    private sealed class MinerUProtocolHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MinerUProtocolHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
