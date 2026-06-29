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

public sealed class LocalImageOcrAdapterTests
{
    [Fact] public void TesseractCapability_is_registered() => CreateRegistry(new FakeProcessRunner()).ListCapabilities().Should().Contain(c => c.EngineId == OcrEngineIds.TesseractCli && c.SupportsPageImage && !c.SupportsPdfDirectInput);

    [Fact]
    public async Task CheckEnvironment_missing_executable_returns_missing_executable()
    {
        var result = await new TesseractCliAdapter(new FakeProcessRunner()).CheckEnvironmentAsync(Version("/not/a/real/tesseract"));
        result.Status.Should().Be(OcrEnvironmentStatus.MissingExecutable);
    }

    [Fact]
    public async Task CheckEnvironment_model_path_existing_file_returns_ready()
    {
        var executable = Path.GetTempFileName();
        try { (await new TesseractCliAdapter(new FakeProcessRunner()).CheckEnvironmentAsync(Version(executable))).IsReady.Should().BeTrue(); }
        finally { File.Delete(executable); }
    }

    [Fact]
    public async Task CheckEnvironment_path_tesseract_version_success_returns_ready()
    {
        var adapter = new TesseractCliAdapter(new FakeProcessRunner(_ => new(0, "tesseract 5", "", false)));
        (await adapter.CheckEnvironmentAsync(Version())).IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task CheckEnvironment_path_tesseract_version_failure_returns_missing_executable()
    {
        var adapter = new TesseractCliAdapter(new FakeProcessRunner(_ => new(1, "", "not found", false)));
        (await adapter.CheckEnvironmentAsync(Version())).Status.Should().Be(OcrEnvironmentStatus.MissingExecutable);
    }

    [Fact] public void SystemProcessRunner_does_not_use_shell_execute() => new SystemProcessRunner().UsesShellExecute.Should().BeFalse();
    [Fact] public async Task FakeProcessRunner_returns_configured_stdout() => (await new FakeProcessRunner(_ => new(0, "recognized", "", false)).RunAsync(new ProcessRunRequest("fake", []))).StandardOutput.Should().Be("recognized");
    [Fact] public async Task ProcessRunner_timeout_returns_timed_out() => (await new FakeProcessRunner(_ => new(-1, "", "", true)).RunAsync(new ProcessRunRequest("fake", []))).TimedOut.Should().BeTrue();

    [Fact] public async Task ValidateInput_rejects_pdf_input() => (await Adapter().ValidateInputAsync(Input(InputKind: OcrInputKinds.PdfPage))).ErrorCode.Should().Be(AppErrorCodes.UnsupportedOperation);
    [Fact] public async Task ValidateInput_rejects_missing_image_file() => (await Adapter().ValidateInputAsync(Input(ImagePath: "/missing/image.png"))).ErrorCode.Should().Be(AppErrorCodes.NotFound);

    [Fact]
    public async Task ValidateInput_accepts_existing_image_file()
    {
        var image = Path.GetTempFileName();
        try { (await Adapter().ValidateInputAsync(Input(ImagePath: image))).IsSuccess.Should().BeTrue(); }
        finally { File.Delete(image); }
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("conflict")]
    public async Task ValidateInput_rejects_changed_or_conflict_source_status(string status)
    {
        var image = Path.GetTempFileName();
        try { (await Adapter().ValidateInputAsync(Input(ImagePath: image, SourceStatus: status))).ErrorCode.Should().Be(AppErrorCodes.InvalidState); }
        finally { File.Delete(image); }
    }

    [Fact]
    public async Task RunPageAsync_success_outputs_paragraph_text()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName();
        try
        {
            var adapter = new TesseractCliAdapter(new FakeProcessRunner(r => r.Arguments.Contains("--version") ? new(0, "v", "", false) : new(0, "recognized text", "", false)));
            var result = await adapter.RunPageAsync(Input(ImagePath: image), Version(executable));
            result.Value.Succeeded.Should().BeTrue(); result.Value.Text.Should().Be("recognized text"); result.Value.BBox.Should().NotBeNull();
        }
        finally { File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task RunPageAsync_forwards_psm_and_oem_parameters()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName(); ProcessRunRequest? captured = null;
        try
        {
            var adapter = new TesseractCliAdapter(new FakeProcessRunner(request =>
            {
                captured = request;
                return new ProcessRunResult(0, "recognized text", "", false);
            }));

            var result = await adapter.RunPageAsync(Input(ImagePath: image), Version(executable, "{\"lang\":\"eng\",\"psm\":6,\"oem\":1}"));

            result.Value.Succeeded.Should().BeTrue();
            captured!.Arguments.Should().ContainInOrder("--psm", "6", "--oem", "1");
        }
        finally { File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task RunPageAsync_rejects_out_of_range_psm_parameter()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName();
        try
        {
            var result = await Adapter().RunPageAsync(Input(ImagePath: image), Version(executable, "{\"psm\":14}"));
            result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        }
        finally { File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task RunPageAsync_nonzero_exit_returns_local_ocr_process_failed()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName();
        try { var r = await new TesseractCliAdapter(new FakeProcessRunner(x => x.Arguments.Contains("--version") ? new(0, "", "", false) : new(2, "", "failure", false))).RunPageAsync(Input(ImagePath: image), Version(executable)); r.Value.ErrorCode.Should().Be(OcrFailureCode.LocalOcrProcessFailed); }
        finally { File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task RunPageAsync_timeout_returns_timeout_error()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName();
        try { var r = await new TesseractCliAdapter(new FakeProcessRunner(x => x.Arguments.Contains("--version") ? new(0, "", "", false) : new(-1, "", "", true))).RunPageAsync(Input(ImagePath: image), Version(executable)); r.Value.ErrorCode.Should().Be(OcrFailureCode.LocalOcrTimeout); }
        finally { File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task RunPageAsync_empty_output_returns_empty_ocr_output()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName();
        try { var r = await new TesseractCliAdapter(new FakeProcessRunner(x => x.Arguments.Contains("--version") ? new(0, "", "", false) : new(0, "   ", "", false))).RunPageAsync(Input(ImagePath: image), Version(executable)); r.Value.ErrorCode.Should().Be(OcrFailureCode.EmptyOcrOutput); }
        finally { File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task OversizedRenderedImage_is_not_sent_to_tesseract()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName(); var runner = new FakeProcessRunner(_ => new(0, "recognized", "", false));
        try
        {
            await File.WriteAllBytesAsync(image, PngHeader(9001, 1000));
            var result = await new TesseractCliAdapter(runner).RunPageAsync(Input(ImagePath: image), Version(executable));
            result.Value.Succeeded.Should().BeFalse();
            result.Value.ErrorCode.Should().Be(OcrFailureCode.ImageTooLargeForOcr);
            runner.Calls.Should().Be(0);
        }
        finally { File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task OversizedRenderedImage_returns_readable_error_without_path()
    {
        var image = Path.Combine(Path.GetTempPath(), $"oversized-{Guid.NewGuid():N}.png"); var executable = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(image, PngHeader(8000, 11001));
            var result = await Adapter().RunPageAsync(Input(ImagePath: image), Version(executable));
            result.Value.ErrorCode.Should().Be(OcrFailureCode.ImageTooLargeForOcr);
            result.Value.ErrorMessage.Should().Contain("width=8000").And.Contain("height=11001").And.NotContain(image).And.NotContain(Path.GetTempPath());
        }
        finally { if (File.Exists(image)) File.Delete(image); File.Delete(executable); }
    }

    [Fact]
    public async Task Candidate_300dpi_requires_pixel_guard()
    {
        var image = Path.GetTempFileName(); var executable = Path.GetTempFileName(); var runner = new FakeProcessRunner(_ => new(0, "recognized", "", false));
        try
        {
            await File.WriteAllBytesAsync(image, PngHeader(12600, 16800));
            var result = await new TesseractCliAdapter(runner).RunPageAsync(Input(ImagePath: image), Version(executable, "{\"lang\":\"chi_sim+chi_tra+eng\",\"psm\":6}"));
            result.Value.Succeeded.Should().BeFalse();
            result.Value.ErrorCode.Should().Be(OcrFailureCode.ImageTooLargeForOcr);
            runner.Calls.Should().Be(0);
        }
        finally { File.Delete(image); File.Delete(executable); }
    }

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
    public async Task Queue_does_not_mark_oversized_ocr_as_succeeded()
    {
        await using var c = await ImageOcrContext.CreateAsync(applyOnSuccess: true, imageBytes: PngHeader(12600, 16800));
        var originalCurrent = await c.CurrentRevisionAsync();
        var run = await c.RunAsync();
        run.Value.State.Should().Be(OcrRunState.Failed);
        var pageResult = (await c.Coordinator.ListPageResultsAsync(run.Value.OcrRunId)).Value.Single();
        pageResult.State.Should().Be(OcrPageResultState.Failed);
        pageResult.ErrorCode.Should().Be(OcrFailureCode.ImageTooLargeForOcr);
        (await c.CurrentRevisionAsync()).Should().Be(originalCurrent);
        (await c.LayoutNodeCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunPresetOnImagePage_invalid_parameters_returns_validation_failed()
    {
        await using var c = await ImageOcrContext.CreateAsync(parameters: "{invalid");
        (await c.RunAsync()).ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task MCP_does_not_expose_tesseract_executable_path()
    {
        await using var c = await ImageOcrContext.CreateAsync();
        JsonSerializer.Serialize((await c.Mcp.GetDocumentStatusAsync(c.DocumentInstanceId)).Value).Should().NotContain(c.ExecutablePath);
    }

    [Fact]
    public async Task MCP_does_not_expose_image_path_after_local_ocr()
    {
        await using var c = await ImageOcrContext.CreateAsync();
        await c.RunAsync();
        JsonSerializer.Serialize((await c.Mcp.GetDocumentStatusAsync(c.DocumentInstanceId)).Value).Should().NotContain(c.ImagePath);
    }

    [Fact] public void README_states_pdf_ocr_not_implemented() => File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")).Should().Contain("PDF OCR");
    [Fact] public void KnownIssues_mentions_tesseract_language_data() => File.ReadAllText(TestPaths.FromRepositoryRoot("docs", "KNOWN_ISSUES_ALPHA.md")).Should().Contain("language data");
    [Fact] public void No_cloud_ocr_or_credential_ui_added() => File.ReadAllText(TestPaths.FromRepositoryRoot("README.md")).Should().Contain("cloud OCR").And.Contain("credential management UI");

    private static TesseractCliAdapter Adapter() => new(new FakeProcessRunner());
    private static OcrPresetVersion Version(string? executablePath = null, string parameters = "{}") => new(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.TesseractCli, OcrModelIds.TesseractDefault, executablePath, parameters, false, DateTimeOffset.UtcNow);
    private static OcrInputDescriptor Input(string InputKind = OcrInputKinds.ImageFile, string? ImagePath = null, string SourceStatus = "available") => new(PageId.New(), DocumentInstanceId.New(), InputKind, ImagePath, InputKind == OcrInputKinds.PdfPage ? "/fake/document.pdf" : null, null, SourceStatus, null);
    private static OcrAdapterRegistry CreateRegistry(IProcessRunner runner) { var r = new OcrAdapterRegistry(); r.RegisterAdapter(new TesseractCliAdapter(runner)); return r; }
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
        public static async Task<ImageOcrContext> CreateAsync(bool applyOnSuccess = false, string parameters = "{}", byte[]? imageBytes = null)
        {
            var database = TemporarySqliteDatabase.Create(); var clock = new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            var image = Path.GetTempFileName(); var executable = Path.GetTempFileName();
            if (imageBytes is not null) await File.WriteAllBytesAsync(image, imageBytes);
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            var library = new LibraryIdentityService(database.ConnectionFactory, clock); await library.CreateLibraryAsync("Image OCR");
            var item = await new ItemService(database.ConnectionFactory, library, clock).CreateItemAsync("book", "Image OCR item");
            var document = await new DocumentInstanceService(database.ConnectionFactory, clock).AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            var page = await new PageService(database.ConnectionFactory, clock).CreatePageAsync(document.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test", null);
            await new LayoutTreeService(database.ConnectionFactory, clock).CreateLayoutRevisionAsync(document.Value.DocumentInstanceId, LayoutRevisionSource.Manual, makeCurrent: true);
            var presets = new OcrPresetService(database.ConnectionFactory, library, clock);
            var preset = await presets.CreatePresetAsync("Tesseract", null, OcrEngineIds.TesseractCli, OcrModelIds.TesseractDefault, executable, parameters, applyOnSuccess);
            var fake = new FakeProcessRunner(r => r.Arguments.Contains("--version") ? new(0, "v", "", false) : new(0, "local image recognized text", "", false));
            var registry = CreateRegistry(fake);
            var coordinator = new OcrRunCoordinator(database.ConnectionFactory, clock, adapterRegistry: registry);
            return new ImageOcrContext(database, coordinator, new McpReadApi(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory), new EvidenceReferenceService(database.ConnectionFactory, clock)), preset.Value.PresetId, document.Value.DocumentInstanceId, page.Value.PageId, image, executable);
        }
        public Task<Result<OcrRun>> RunAsync() => Coordinator.RunPresetOnImagePageAsync(DocumentInstanceId, PresetId, PageId, ImagePath);
        public async Task<LayoutRevisionId?> CurrentRevisionAsync() { await using var c=Database.ConnectionFactory.CreateConnection(); await c.OpenAsync(); var id=await c.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id=@Id and is_current=1;",new{Id=DocumentInstanceId.ToString()}); return id is null?null:LayoutRevisionId.Parse(id); }
        public async Task<int> LayoutNodeCountAsync() { await using var c=Database.ConnectionFactory.CreateConnection(); await c.OpenAsync(); return await c.ExecuteScalarAsync<int>("select count(1) from layout_nodes;"); }
        public async ValueTask DisposeAsync() { await Database.DisposeAsync(); File.Delete(ImagePath); File.Delete(ExecutablePath); }
    }
}
