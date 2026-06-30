using System.Text.Json;
using Dapper;
using FluentAssertions;
using LiteratureApp.Core.Bibliography;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Bibliography;
using LiteratureApp.Infrastructure.Documents;
using LiteratureApp.Infrastructure.Evidence;
using LiteratureApp.Infrastructure.Layout;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Mcp;
using LiteratureApp.Infrastructure.Migrations;
using LiteratureApp.Infrastructure.Ocr;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Mcp;
using LiteratureApp.Ocr;
using LiteratureApp.Search;

namespace LiteratureApp.Tests;

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
            .Should().Contain("OCR Preset").And.Contain("MCP cannot read provider secrets");
    }

    [Fact]
    public void Product_startup_registers_mineru_workflow_components()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "LiteratureApp.UI", "AppServices.cs"))
            .Should().Contain("MinerUResultImporter").And.Contain("FirstRunWorkflow");
    }
    [Fact] public void No_new_migration_created_in_step_14A() => Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql").Select(Path.GetFileName).Should().NotContain(name => name!.Contains("14", StringComparison.OrdinalIgnoreCase));

    private static OcrAdapterRegistry CreateRegistry()
    {
        var registry = new OcrAdapterRegistry();
        registry.RegisterAdapter(new MockOcrAdapter());
        registry.RegisterAdapter(new LocalPlaceholderOcrAdapter(new OcrModelPathValidator()));
        return registry;
    }

    private sealed class OcrReadinessContext : IAsyncDisposable
    {
        private OcrReadinessContext(TemporarySqliteDatabase database, OcrPresetService presets, OcrRunCoordinator coordinator, McpReadApi mcp, OcrPresetId presetId, DocumentInstanceId documentInstanceId, PageId pageId)
        { Database = database; Presets = presets; Coordinator = coordinator; Mcp = mcp; PresetId = presetId; DocumentInstanceId = documentInstanceId; PageId = pageId; }
        public TemporarySqliteDatabase Database { get; }
        public OcrPresetService Presets { get; }
        public OcrRunCoordinator Coordinator { get; }
        public McpReadApi Mcp { get; }
        public OcrPresetId PresetId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageId PageId { get; }

        public static async Task<OcrReadinessContext> CreateAsync(string engineId = OcrEngineIds.Mock, string? modelPath = null)
        {
            var database = TemporarySqliteDatabase.Create();
            var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(database.ConnectionFactory, clock); await library.CreateLibraryAsync("OCR readiness");
            var item = await new ItemService(database.ConnectionFactory, library, clock).CreateItemAsync("book", "OCR readiness item");
            var document = await new DocumentInstanceService(database.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var page = await new PageService(database.ConnectionFactory, clock).CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);
            var presets = new OcrPresetService(database.ConnectionFactory, library, clock);
            var preset = await presets.CreatePresetAsync("Readiness", null, engineId, "model", modelPath, "{}", false);
            var registry = CreateRegistry();
            var coordinator = new OcrRunCoordinator(database.ConnectionFactory, clock, new MockOcrEngine(), adapterRegistry: registry);
            var search = new SqliteSearchService(database.ConnectionFactory);
            var evidence = new EvidenceReferenceService(database.ConnectionFactory, clock);
            return new OcrReadinessContext(database, presets, coordinator, new McpReadApi(database.ConnectionFactory, search, evidence), preset.Value.PresetId, document.Value.DocumentInstanceId, page.Value.PageId);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
