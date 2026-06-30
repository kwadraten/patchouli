using System.Text.Json;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.Tests;

/// <summary>
/// Tests for local image OCR infrastructure (ProcessRunner, PngSizeGuard) and
/// OcrRunCoordinator image-page workflow using TesseractCliAdapter as a test double.
/// TesseractCliAdapter is kept as a test-only adapter; it is not registered in product startup.
/// Product-surface tests verify that Tesseract is absent from AppServices and UI.
/// </summary>
public sealed class LocalImageOcrAdapterTests
{
    [Fact] public void SystemProcessRunner_does_not_use_shell_execute() => new SystemProcessRunner().UsesShellExecute.Should().BeFalse();
    [Fact] public async Task FakeProcessRunner_returns_configured_stdout() => (await new FakeProcessRunner(_ => new(0, "recognized", "", false)).RunAsync(new ProcessRunRequest("fake", []))).StandardOutput.Should().Be("recognized");
    [Fact] public async Task ProcessRunner_timeout_returns_timed_out() => (await new FakeProcessRunner(_ => new(-1, "", "", true)).RunAsync(new ProcessRunRequest("fake", []))).TimedOut.Should().BeTrue();

    [Fact]
    public async Task PngSizeGuard_accepts_normal_probe_size()
    {
        var image = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(image, PngHeader(6000, 8000));
            var size = await PngImageSizeGuard.TryReadPngSizeAsync(image);
            size.Should().NotBeNull();
            PngImageSizeGuard.ExceedsLimit(size!).Should().BeFalse();
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task PngSizeGuard_rejects_oversized_image()
    {
        var image = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(image, PngHeader(9001, 1000));
            var size = await PngImageSizeGuard.TryReadPngSizeAsync(image);
            PngImageSizeGuard.ExceedsLimit(size!).Should().BeTrue();
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task RunPresetOnImagePage_creates_ocr_run_and_page_result()
    {
        await using var c = await ImageOcrContext.CreateAsync();
        var r = await c.RunAsync();
        r.IsSuccess.Should().BeTrue(); (await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId)).Value.Single().State.Should().Be(OcrPageResultState.Succeeded);
    }

    [Fact]
    public async Task RunPresetOnImagePage_apply_on_success_true_sets_current_layout()
    {
        await using var c = await ImageOcrContext.CreateAsync(applyOnSuccess: true);
        var r = await c.RunAsync();
        (await c.CurrentRevisionAsync()).Should().Be(r.Value.OutputRevisionId);
    }

    [Fact]
    public async Task RunPresetOnImagePage_apply_on_success_false_keeps_candidate()
    {
        await using var c = await ImageOcrContext.CreateAsync(applyOnSuccess: false);
        var r = await c.RunAsync();
        (await c.CurrentRevisionAsync()).Should().NotBe(r.Value.OutputRevisionId);
    }

    [Fact]
    public async Task RunPresetOnImagePage_missing_image_fails_without_layout_node()
    {
        await using var c = await ImageOcrContext.CreateAsync();
        var r = await c.Coordinator.RunPresetOnImagePageAsync(c.DocumentInstanceId, c.PresetId, c.PageId, "/missing/image.png");
        r.IsFailure.Should().BeTrue(); (await c.LayoutNodeCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunPresetOnImagePage_invalid_parameters_returns_validation_failed()
    {
        await using var c = await ImageOcrContext.CreateAsync(parameters: "{invalid");
        (await c.RunAsync()).ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task MCP_does_not_expose_image_path_after_local_ocr()
    {
        await using var c = await ImageOcrContext.CreateAsync();
        await c.RunAsync();
        JsonSerializer.Serialize((await c.Mcp.GetDocumentStatusAsync(c.DocumentInstanceId)).Value).Should().NotContain(c.ImagePath);
    }

    [Fact]
    public void Product_startup_does_not_register_legacy_local_image_adapter()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "AppServices.cs"))
            .Should().NotContain("RegisterAdapter(new TesseractCliAdapter");
    }

    [Fact]
    public void First_run_ui_mentions_mineru_not_legacy_local_ocr()
    {
        var ui = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "MainWindow.axaml"));
        ui.Should().Contain("MinerU").And.NotContain("Tesseract");
    }

    private static byte[] PngHeader(int width, int height)
    {
        var header = new byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(header, 0);
        header[12] = 73; header[13] = 72; header[14] = 68; header[15] = 82;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), height);
        return header;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRunRequest, ProcessRunResult> _result;
        public FakeProcessRunner(Func<ProcessRunRequest, ProcessRunResult>? result = null) => _result = result ?? (_ => new(0, "", "", false));
        public int Calls { get; private set; }
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(_result(request)); }
    }

    private sealed class ImageOcrContext : IAsyncDisposable
    {
        private ImageOcrContext(TemporarySqliteDatabase database, OcrRunCoordinator coordinator, McpReadApi mcp, OcrPresetId presetId, DocumentInstanceId documentInstanceId, PageId pageId, string imagePath, string executablePath)
        { Database = database; Coordinator = coordinator; Mcp = mcp; PresetId = presetId; DocumentInstanceId = documentInstanceId; PageId = pageId; ImagePath = imagePath; ExecutablePath = executablePath; }
        public TemporarySqliteDatabase Database { get; } public OcrRunCoordinator Coordinator { get; } public McpReadApi Mcp { get; } public OcrPresetId PresetId { get; } public DocumentInstanceId DocumentInstanceId { get; } public PageId PageId { get; } public string ImagePath { get; } public string ExecutablePath { get; }
        public static async Task<ImageOcrContext> CreateAsync(bool applyOnSuccess = false, string parameters = "{}")
        {
            var database = TemporarySqliteDatabase.Create(); var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            var image = Path.GetTempFileName(); var executable = Path.GetTempFileName();
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(database.ConnectionFactory, clock); await library.CreateLibraryAsync("Image OCR");
            var item = await new ItemService(database.ConnectionFactory, library, clock).CreateItemAsync("book", "Image OCR item");
            var document = await new DocumentInstanceService(database.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var page = await new PageService(database.ConnectionFactory, clock).CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);
            await new LayoutTreeService(database.ConnectionFactory, clock).CreateLayoutRevisionAsync(document.Value.DocumentInstanceId, LayoutRevisionSource.Manual, makeCurrent: true);
            var presets = new OcrPresetService(database.ConnectionFactory, library, clock);
            var preset = await presets.CreatePresetAsync("Local Image OCR (test)", null, OcrEngineIds.TesseractCli, OcrModelIds.TesseractDefault, executable, parameters, applyOnSuccess);
            var fake = new FakeProcessRunner(r => r.Arguments.Contains("--version") ? new(0, "v", "", false) : new(0, "local image recognized text", "", false));
            var registry = new OcrAdapterRegistry();
            registry.RegisterAdapter(new TesseractCliAdapter(fake));
            var coordinator = new OcrRunCoordinator(database.ConnectionFactory, clock, adapterRegistry: registry);
            return new ImageOcrContext(database, coordinator, new McpReadApi(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory), new EvidenceReferenceService(database.ConnectionFactory, clock)), preset.Value.PresetId, document.Value.DocumentInstanceId, page.Value.PageId, image, executable);
        }
        public Task<Result<OcrRun>> RunAsync() => Coordinator.RunPresetOnImagePageAsync(DocumentInstanceId, PresetId, PageId, ImagePath);
        public async Task<LayoutRevisionId?> CurrentRevisionAsync() { await using var c=Database.ConnectionFactory.CreateConnection(); await c.OpenAsync(); var id=await c.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id=@Id and is_current=1;",new{Id=DocumentInstanceId.ToString()}); return id is null?null:LayoutRevisionId.Parse(id); }
        public async Task<int> LayoutNodeCountAsync() { await using var c=Database.ConnectionFactory.CreateConnection(); await c.OpenAsync(); return await c.ExecuteScalarAsync<int>("select count(1) from layout_nodes;"); }
        public async ValueTask DisposeAsync() { await Database.DisposeAsync(); File.Delete(ImagePath); File.Delete(ExecutablePath); }
    }
}
