using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
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
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class PdfPageRenderingTests
{
    [Fact]
    public async Task RenderPage_uses_file_resolution()
    {
        await using Context c = await Context.CreateAsync();
        (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.Rendered);
    }

    [Fact]
    public async Task RenderPage_missing_source_returns_source_missing()
    {
        await using Context c = await Context.CreateAsync();
        File.Delete(c.PdfPath);
        (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.SourceMissing);
    }

    [Fact]
    public async Task RenderPage_changed_source_returns_source_changed_warning()
    {
        await using Context c = await Context.CreateAsync();
        await File.AppendAllTextAsync(c.PdfPath, "changed");
        Result<PageRenderResult> r = await c.RenderAsync();
        r.Value.Status.Should().Be(PageRenderStatus.SourceChanged);
        r.Value.Warning.Should().Contain("bbox_basis_stale");
    }

    [Fact]
    public async Task RenderPage_conflict_does_not_render()
    {
        await using Context c = await Context.CreateAsync();
        await c.SetAssetStatusAsync(FileAssetStatus.Conflict);
        (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.Conflict);
        c.Renderer.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RenderPage_non_pdf_returns_unsupported_file()
    {
        await using Context c = await Context.CreateAsync(".txt");
        (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.UnsupportedFile);
    }

    [Fact]
    public async Task RenderPage_available_pdf_creates_cache_png_with_fake_renderer()
    {
        await using Context c = await Context.CreateAsync();
        Result<PageRenderResult> r = await c.RenderAsync();
        File.Exists(r.Value.CacheImagePath!).Should().BeTrue();
        Path.GetExtension(r.Value.CacheImagePath!).Should().Be(".png");
    }

    [Fact]
    public async Task RenderPage_second_call_uses_cache()
    {
        await using Context c = await Context.CreateAsync();
        await c.RenderAsync();
        Result<PageRenderResult> r = await c.RenderAsync();
        r.Value.Status.Should().Be(PageRenderStatus.FromCache);
        c.Renderer.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RenderPage_force_rerender_recreates_cache()
    {
        await using Context c = await Context.CreateAsync();
        await c.RenderAsync();
        Result<PageRenderResult> r = await c.RenderAsync(true);
        r.Value.Status.Should().Be(PageRenderStatus.Rendered);
        c.Renderer.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task RenderPage_updates_page_coordinate_basis_metadata()
    {
        await using Context c = await Context.CreateAsync();
        await c.RenderAsync();
        Result<Page> p = await c.Pages.GetPageAsync(c.PageId);
        p.Value.CoordinateBasis.Should().Be(CoordinateBasis.NormalizedPage);
        p.Value.RendererBasisVersion.Should().Be("fake-test-renderer-v1");
        p.Value.SourceFileHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cache_root_is_not_under_sync_root()
    {
        await using Context c = await Context.CreateAsync();
        Path.GetFullPath(c.CacheRoot).Should().NotStartWith(Path.GetFullPath(c.SyncRoot));
    }

    [Fact]
    public async Task BuildOcrInputFromRenderedPage_returns_page_image_descriptor()
    {
        await using Context c = await Context.CreateAsync();
        Result<OcrInputDescriptor> r =
            await c.RenderService.BuildOcrInputFromRenderedPageAsync(c.DocumentInstanceId, c.PageId);
        r.Value.InputKind.Should().Be(OcrInputKinds.PageImage);
        File.Exists(r.Value.ImagePath!).Should().BeTrue();
    }

    [Fact]
    public async Task BuildOcrInput_missing_source_returns_failure()
    {
        await using Context c = await Context.CreateAsync();
        File.Delete(c.PdfPath);
        (await c.RenderService.BuildOcrInputFromRenderedPageAsync(c.DocumentInstanceId, c.PageId)).IsFailure.Should()
            .BeTrue();
    }

    [Fact]
    public async Task Snapshot_publish_does_not_include_render_cache()
    {
        await using Context c = await Context.CreateAsync();
        await c.RenderAsync();
        Result<SnapshotPublishResult> published =
            await new SnapshotPublisher(c.Clock).PublishSnapshotAsync(
                new SnapshotPublishRequest(c.Database.Path, c.SyncRoot, "renderer-test"));
        string syncRoot = c.SyncRoot;
        string cacheRoot = Path.GetFullPath(c.CacheRoot);
        Directory.EnumerateFiles(syncRoot, "*", SearchOption.AllDirectories).Should().NotContain(p =>
            Path.GetFullPath(p) == cacheRoot || p.EndsWith(".png"));
        published.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task MCP_does_not_expose_render_cache_path()
    {
        await using Context c = await Context.CreateAsync();
        Result<PageRenderResult> r = await c.RenderAsync();
        JsonSerializer.Serialize((await c.Mcp.GetDocumentStatusAsync(c.DocumentInstanceId)).Value).Should()
            .NotContain(r.Value.CacheImagePath!);
    }

    [Fact]
    public async Task Evidence_ref_payload_does_not_include_render_cache_path()
    {
        await using Context c = await Context.CreateAsync();
        Result<PageRenderResult> r = await c.RenderAsync();
        EvidenceReference reference = new(c.LibraryId, c.DocumentInstanceId, c.PageId, SearchUnitId.New(),
            "text-revision", "bbox-revision", LayoutRevisionId.New(), null);
        EvidenceReferenceCodec.Encode(reference).Value.Should().NotContain(r.Value.CacheImagePath!);
    }

    [Fact]
    public async Task FakePdfPageRenderer_creates_png()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fake-render-{Guid.NewGuid():N}.png");
        try
        {
            PdfPageRenderOutput output =
                await new FakePdfPageRenderer().RenderPageToPngAsync("ignored.pdf", 0, path, 200);
            File.Exists(path).Should().BeTrue();
            output.RendererBasisVersion.Should().Be("fake-pdf-renderer-v1");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task RealPdfPageRenderer_if_present_handles_failure_as_result_not_exception()
    {
        await using Context c = await Context.CreateAsync();
        c.Renderer.ThrowOnRender = true;
        (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.RenderFailed);
    }

    [Fact]
    public async Task PdfRenderTimeout_is_not_reported_as_success()
    {
        await using Context c = await Context.CreateAsync();
        c.Renderer.ThrowTimeout = true;
        Result<PageRenderResult> r = await c.RenderAsync(true);
        r.Value.Status.Should().Be(PageRenderStatus.RendererTimeout);
        r.Value.CacheImagePath.Should().BeNull();
        r.Value.Warning.Should().Be("PDF renderer timed out.");
    }

    [Fact]
    public void Agent_prd_keeps_render_cache_out_of_mcp_and_snapshots()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot(".agent", "PRD.md")).Should()
            .Contain("MCP never returns cached images or image paths").And.Contain("page_renders");
    }

    [Fact]
    public void Product_ui_uses_pdf_preview_placeholder_not_legacy_batch_ocr_copy()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"))
            .Should().Contain("工作台预览").And.NotContain("batch PDF OCR");
    }

    [Fact]
    public void No_new_business_schema_unless_reported()
    {
        Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql").Select(Path.GetFileName).OrderBy(n => n)
            .Should().Equal(
                "0001_create_schema_migrations.sql",
                "002_create_library_metadata.sql",
                "003_create_bibliographic_core.sql",
                "004_create_file_resolution.sql",
                "005_create_pages_and_layout.sql",
                "006_create_ocr_lifecycle.sql",
                "007_create_search_units_and_fts.sql",
                "008_create_evidence_refs.sql",
                "009_create_provider_credentials.sql",
                "010_expand_item_metadata.sql",
                "011_create_search_profiles.sql",
                "012_hide_ocr_runs.sql",
                "013_create_structured_item_names_and_dates.sql",
                "014_add_table_cell_metadata.sql",
                "015_create_mcp_server_settings.sql",
                "016_create_csl_styles.sql",
                "017_create_item_type_inferences.sql",
                "019_create_blocking_operations.sql",
                "021_create_library_preferences.sql",
                "022_normalize_identifier_schemes.sql",
                "023_add_file_search_root_authorization.sql");
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase db, FixedClock clock, LibraryId libraryId, DocumentInstanceId doc,
            PageId page, FileAssetId asset, string pdf, string cache, string sync, PageRenderService rendererService,
            CountingRenderer renderer, PageService pages, McpReadApi mcp)
        {
            Database = db;
            Clock = clock;
            LibraryId = libraryId;
            DocumentInstanceId = doc;
            PageId = page;
            AssetId = asset;
            PdfPath = pdf;
            CacheRoot = cache;
            SyncRoot = sync;
            RenderService = rendererService;
            Renderer = renderer;
            Pages = pages;
            Mcp = mcp;
        }

        public TemporarySqliteDatabase Database { get; }
        public FixedClock Clock { get; }
        public LibraryId LibraryId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public PageId PageId { get; }
        public FileAssetId AssetId { get; }
        public string PdfPath { get; }
        public string CacheRoot { get; }
        public string SyncRoot { get; }
        public PageRenderService RenderService { get; }
        public CountingRenderer Renderer { get; }
        public PageService Pages { get; }
        public McpReadApi Mcp { get; }

        public static async Task<Context> CreateAsync(string extension = ".pdf")
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"render-{Guid.NewGuid():N}"))
                .FullName;
            string pdf = Path.Combine(dir, "source" + extension);
            File.Copy(TestFixtures.RealThreePagePdf, pdf);
            string cache = Path.Combine(dir, "cache");
            string sync = Path.Combine(dir, "sync");
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            Result<LibraryMetadata> lib = await library.CreateLibraryAsync("Render");
            FileAssetService files = new(db.ConnectionFactory, library, clock);
            Result<FileAsset> asset = await files.RegisterFileAsync(pdf);
            Result<ItemMetadata> item =
                await new ItemService(db.ConnectionFactory, library, clock).CreateItemAsync("book", "Render item");
            Result<DocumentInstance> doc =
                await new DocumentInstanceService(db.ConnectionFactory, clock).AttachDocumentInstanceAsync(
                    item.Value.ItemId, asset.Value.FileAssetId, DocumentInstanceType.PrimaryScan);
            PageService pages = new(db.ConnectionFactory, clock);
            Result<Page> page = await pages.CreatePageAsync(doc.Value.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "initial", null);
            FileResolutionService resolution = new(db.ConnectionFactory, library, clock);
            CountingRenderer renderer = new();
            PageRenderService renderService = new(db.ConnectionFactory, library, resolution, renderer, clock, cache);
            return new Context(db, clock, lib.Value.LibraryId, doc.Value.DocumentInstanceId, page.Value.PageId,
                asset.Value.FileAssetId, pdf, cache, sync, renderService, renderer, pages,
                new McpReadApi(db.ConnectionFactory, new SqliteSearchService(db.ConnectionFactory),
                    new EvidenceReferenceService(db.ConnectionFactory, clock))) { RootDirectory = dir };
        }

        public string RootDirectory { get; private set; } = "";

        public Task<Result<PageRenderResult>> RenderAsync(bool force = false)
        {
            return RenderService.RenderPageAsync(new PageRenderRequest(DocumentInstanceId, PageId, Dpi: 200,
                Purpose: PageRenderPurpose.Ocr, ForceRerender: force));
        }

        public async Task SetAssetStatusAsync(string status)
        {
            await using SqliteConnection c = Database.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await c.ExecuteAsync("update file_assets set status=@Status where file_asset_id=@Id",
                new { Status = status, Id = AssetId.ToString() });
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, true);
            }
        }
    }

    private sealed class CountingRenderer : IPdfPageRenderer
    {
        public int CallCount { get; private set; }
        public bool ThrowOnRender { get; set; }
        public bool ThrowTimeout { get; set; }

        public async Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath, int pageIndex, string outputPath,
            int dpi, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (ThrowTimeout)
            {
                throw new PdfRendererTimeoutException("PDF renderer timed out.");
            }

            if (ThrowOnRender)
            {
                throw new InvalidOperationException("render failed");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, [137, 80, 78, 71]);
            return new PdfPageRenderOutput(1000, 1400, 0, CoordinateBasis.NormalizedPage, 1000, 1400,
                "fake-test-renderer-v1");
        }
    }
}
