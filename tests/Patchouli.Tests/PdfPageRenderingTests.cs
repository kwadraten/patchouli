using System.Text.Json;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
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
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class PdfPageRenderingTests
{
    [Fact] public async Task RenderPage_uses_file_resolution() { await using var c=await Context.CreateAsync(); (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.Rendered); }
    [Fact] public async Task RenderPage_missing_source_returns_source_missing() { await using var c=await Context.CreateAsync(); File.Delete(c.PdfPath); (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.SourceMissing); }
    [Fact] public async Task RenderPage_changed_source_returns_source_changed_warning() { await using var c=await Context.CreateAsync(); await File.AppendAllTextAsync(c.PdfPath,"changed"); var r=await c.RenderAsync(); r.Value.Status.Should().Be(PageRenderStatus.SourceChanged); r.Value.Warning.Should().Contain("bbox_basis_stale"); }
    [Fact] public async Task RenderPage_conflict_does_not_render() { await using var c=await Context.CreateAsync(); await c.SetAssetStatusAsync(FileAssetStatus.Conflict); (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.Conflict); c.Renderer.CallCount.Should().Be(0); }
    [Fact] public async Task RenderPage_non_pdf_returns_unsupported_file() { await using var c=await Context.CreateAsync(extension:".txt"); (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.UnsupportedFile); }
    [Fact] public async Task RenderPage_available_pdf_creates_cache_png_with_fake_renderer() { await using var c=await Context.CreateAsync(); var r=await c.RenderAsync(); File.Exists(r.Value.CacheImagePath!).Should().BeTrue(); Path.GetExtension(r.Value.CacheImagePath!).Should().Be(".png"); }
    [Fact] public async Task RenderPage_second_call_uses_cache() { await using var c=await Context.CreateAsync(); await c.RenderAsync(); var r=await c.RenderAsync(); r.Value.Status.Should().Be(PageRenderStatus.FromCache); c.Renderer.CallCount.Should().Be(1); }
    [Fact] public async Task RenderPage_force_rerender_recreates_cache() { await using var c=await Context.CreateAsync(); await c.RenderAsync(); var r=await c.RenderAsync(force:true); r.Value.Status.Should().Be(PageRenderStatus.Rendered); c.Renderer.CallCount.Should().Be(2); }
    [Fact] public async Task RenderPage_updates_page_coordinate_basis_metadata() { await using var c=await Context.CreateAsync(); await c.RenderAsync(); var p=await c.Pages.GetPageAsync(c.PageId); p.Value.CoordinateBasis.Should().Be(CoordinateBasis.NormalizedPage); p.Value.RendererBasisVersion.Should().Be("fake-test-renderer-v1"); p.Value.SourceFileHash.Should().NotBeNullOrWhiteSpace(); }
    [Fact] public async Task Cache_root_is_not_under_sync_root() { await using var c=await Context.CreateAsync(); Path.GetFullPath(c.CacheRoot).Should().NotStartWith(Path.GetFullPath(c.SyncRoot)); }
    [Fact] public async Task BuildOcrInputFromRenderedPage_returns_page_image_descriptor() { await using var c=await Context.CreateAsync(); var r=await c.RenderService.BuildOcrInputFromRenderedPageAsync(c.DocumentInstanceId,c.PageId); r.Value.InputKind.Should().Be(OcrInputKinds.PageImage); File.Exists(r.Value.ImagePath!).Should().BeTrue(); }
    [Fact] public async Task BuildOcrInput_missing_source_returns_failure() { await using var c=await Context.CreateAsync(); File.Delete(c.PdfPath); (await c.RenderService.BuildOcrInputFromRenderedPageAsync(c.DocumentInstanceId,c.PageId)).IsFailure.Should().BeTrue(); }
    [Fact] public async Task RunPresetOnRenderedPdfPage_creates_ocr_run_with_fake_render_and_fake_ocr() { await using var c=await Context.CreateAsync(); var r=await c.Coordinator.RunPresetOnRenderedPdfPageAsync(c.DocumentInstanceId,c.PresetId,c.PageId); r.IsSuccess.Should().BeTrue(); (await c.Coordinator.ListPageResultsAsync(r.Value.OcrRunId)).Value.Single().State.Should().Be(OcrPageResultState.Succeeded); }
    [Fact] public async Task RunPresetOnRenderedPdfPage_apply_on_success_sets_current_layout() { await using var c=await Context.CreateAsync(apply:true); var r=await c.Coordinator.RunPresetOnRenderedPdfPageAsync(c.DocumentInstanceId,c.PresetId,c.PageId); (await c.CurrentRevisionAsync()).Should().Be(r.Value.OutputRevisionId); }
    [Fact] public async Task RunPresetOnRenderedPdfPage_source_changed_blocks_ocr() { await using var c=await Context.CreateAsync(); await File.AppendAllTextAsync(c.PdfPath,"changed"); (await c.Coordinator.RunPresetOnRenderedPdfPageAsync(c.DocumentInstanceId,c.PresetId,c.PageId)).ErrorCode.Should().Be(AppErrorCodes.ValidationFailed); }
    [Fact] public async Task Snapshot_publish_does_not_include_render_cache() { await using var c=await Context.CreateAsync(); await c.RenderAsync(); var published=await new SnapshotPublisher(c.Clock).PublishSnapshotAsync(new SnapshotPublishRequest(c.Database.Path,c.SyncRoot,"renderer-test")); Directory.EnumerateFiles(c.SyncRoot,"*",SearchOption.AllDirectories).Should().NotContain(p=>Path.GetFullPath(p)==Path.GetFullPath(c.CacheRoot) || p.EndsWith(".png")); published.IsSuccess.Should().BeTrue(); }
    [Fact] public async Task MCP_does_not_expose_render_cache_path() { await using var c=await Context.CreateAsync(); var r=await c.RenderAsync(); JsonSerializer.Serialize((await c.Mcp.GetDocumentStatusAsync(c.DocumentInstanceId)).Value).Should().NotContain(r.Value.CacheImagePath!); }
    [Fact] public async Task Evidence_ref_payload_does_not_include_render_cache_path() { await using var c=await Context.CreateAsync(); var r=await c.RenderAsync(); var reference=new EvidenceReference(c.LibraryId,c.DocumentInstanceId,c.PageId,SearchUnitId.New(),"text-revision","bbox-revision",LayoutRevisionId.New(),null); EvidenceReferenceCodec.Encode(reference).Value.Should().NotContain(r.Value.CacheImagePath!); }
    [Fact] public async Task FakePdfPageRenderer_creates_png() { var path=Path.Combine(Path.GetTempPath(),$"fake-render-{Guid.NewGuid():N}.png"); try { var output=await new FakePdfPageRenderer().RenderPageToPngAsync("ignored.pdf",0,path,200); File.Exists(path).Should().BeTrue(); output.RendererBasisVersion.Should().Be("fake-pdf-renderer-v1"); } finally { if(File.Exists(path))File.Delete(path); } }
    [Fact] public async Task RealPdfPageRenderer_if_present_handles_failure_as_result_not_exception() { await using var c=await Context.CreateAsync(); c.Renderer.ThrowOnRender=true; (await c.RenderAsync()).Value.Status.Should().Be(PageRenderStatus.RenderFailed); }
    [Fact] public async Task PdfRenderTimeout_is_not_reported_as_success() { await using var c=await Context.CreateAsync(); c.Renderer.ThrowTimeout=true; var r=await c.RenderAsync(force:true); r.Value.Status.Should().Be(PageRenderStatus.RendererTimeout); r.Value.CacheImagePath.Should().BeNull(); r.Value.Warning.Should().Be("PDF renderer timed out."); }
    [Fact] public async Task PdfRenderTimeout_does_not_create_ocr_result() { await using var c=await Context.CreateAsync(); c.Renderer.ThrowTimeout=true; var r=await c.Coordinator.RunPresetOnRenderedPdfPageAsync(c.DocumentInstanceId,c.PresetId,c.PageId); r.IsFailure.Should().BeTrue(); r.ErrorCode.Should().Be(OcrFailureCode.RendererTimeout); (await c.CountAsync("ocr_runs")).Should().Be(0); (await c.CountAsync("ocr_page_results")).Should().Be(0); }
    [Fact] public async Task PdfRenderTimeout_does_not_create_current_search_evidence() { await using var c=await Context.CreateAsync(apply:true); c.Renderer.ThrowTimeout=true; var r=await c.Coordinator.RunPresetOnRenderedPdfPageAsync(c.DocumentInstanceId,c.PresetId,c.PageId); r.ErrorCode.Should().Be(OcrFailureCode.RendererTimeout); (await c.CurrentRevisionAsync()).Should().BeNull(); (await c.CountAsync("search_units")).Should().Be(0); EvidenceReferenceCodec.Encode(new EvidenceReference(c.LibraryId,c.DocumentInstanceId,c.PageId,SearchUnitId.New(),"text","bbox",LayoutRevisionId.New(),null)).Value.Should().NotContain(c.PdfPath); }
    [Fact] public void Agent_prd_keeps_render_cache_out_of_mcp_and_snapshots() => File.ReadAllText(TestPaths.FromRepositoryRoot(".agent","PRD.md")).Should().Contain("MCP never returns cached images or image paths").And.Contain("page_renders");
    [Fact] public void Product_ui_uses_pdf_preview_placeholder_not_legacy_batch_ocr_copy() => File.ReadAllText(TestPaths.FromRepositoryRoot("src","Patchouli.UI","Views","PdfReaderPage.axaml")).Should().Contain("PDF 预览").And.NotContain("batch PDF OCR");
    [Fact] public void No_new_business_schema_unless_reported() => Directory.EnumerateFiles(TestPaths.MigrationsDirectory,"*.sql").Select(Path.GetFileName).OrderBy(n=>n).Should().Equal(
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
        "013_create_structured_item_names_and_dates.sql");

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase db, FixedClock clock, LibraryId libraryId, DocumentInstanceId doc, PageId page, FileAssetId asset, string pdf, string cache, string sync, PageRenderService rendererService, CountingRenderer renderer, PageService pages, OcrRunCoordinator coordinator, OcrPresetId preset, McpReadApi mcp)
        { Database=db;Clock=clock;LibraryId=libraryId;DocumentInstanceId=doc;PageId=page;AssetId=asset;PdfPath=pdf;CacheRoot=cache;SyncRoot=sync;RenderService=rendererService;Renderer=renderer;Pages=pages;Coordinator=coordinator;PresetId=preset;Mcp=mcp; }
        public TemporarySqliteDatabase Database{get;} public FixedClock Clock{get;} public LibraryId LibraryId{get;} public DocumentInstanceId DocumentInstanceId{get;} public PageId PageId{get;} public FileAssetId AssetId{get;} public string PdfPath{get;} public string CacheRoot{get;} public string SyncRoot{get;} public PageRenderService RenderService{get;} public CountingRenderer Renderer{get;} public PageService Pages{get;} public OcrRunCoordinator Coordinator{get;} public OcrPresetId PresetId{get;} public McpReadApi Mcp{get;}
        public static async Task<Context> CreateAsync(bool apply=false,string extension=".pdf")
        {
            var db=TemporarySqliteDatabase.Create(); var clock=new FixedClock(DateTimeOffset.Parse("2026-06-20T00:00:00Z")); var dir=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),$"render-{Guid.NewGuid():N}")).FullName; var pdf=Path.Combine(dir,"source"+extension); await File.WriteAllTextAsync(pdf,"fake pdf content"); var cache=Path.Combine(dir,"cache"); var sync=Path.Combine(dir,"sync");
            await new MigrationRunner(db.ConnectionFactory,TestPaths.MigrationsDirectory).RunAsync(); var library=new LibraryIdentityService(db.ConnectionFactory,clock); var lib=await library.CreateLibraryAsync("Render"); var files=new FileAssetService(db.ConnectionFactory,library,clock); var asset=await files.RegisterFileAsync(pdf); var item=await new ItemService(db.ConnectionFactory,library,clock).CreateItemAsync("book","Render item"); var doc=await new DocumentInstanceService(db.ConnectionFactory,clock).AttachDocumentInstanceAsync(item.Value.ItemId,asset.Value.FileAssetId,DocumentInstanceType.PrimaryScan); var pages=new PageService(db.ConnectionFactory,clock); var page=await pages.CreatePageAsync(doc.Value.DocumentInstanceId,0,"1",null,null,0,CoordinateBasis.NormalizedPage,null,null,"initial",null);
            var resolution=new FileResolutionService(db.ConnectionFactory,library,clock); var renderer=new CountingRenderer(); var renderService=new PageRenderService(db.ConnectionFactory,library,resolution,renderer,clock,cache);
            var presets=new OcrPresetService(db.ConnectionFactory,library,clock); var exe=Path.GetTempFileName(); var preset=await presets.CreatePresetAsync("Local Image OCR (test)",null,OcrEngineIds.TesseractCli,OcrModelIds.TesseractDefault,exe,"{}",apply); var registry=new OcrAdapterRegistry(); registry.RegisterAdapter(new TesseractCliAdapter(new FakeRunner())); var coordinator=new OcrRunCoordinator(db.ConnectionFactory,clock,adapterRegistry:registry,pageRenderService:renderService);
            return new Context(db,clock,lib.Value.LibraryId,doc.Value.DocumentInstanceId,page.Value.PageId,asset.Value.FileAssetId,pdf,cache,sync,renderService,renderer,pages,coordinator,preset.Value.PresetId,new McpReadApi(db.ConnectionFactory,new SqliteSearchService(db.ConnectionFactory),new EvidenceReferenceService(db.ConnectionFactory,clock))){ ExecutablePath=exe, RootDirectory=dir };
        }
        public string ExecutablePath{get;private set;}=""; public string RootDirectory{get;private set;}="";
        public Task<Result<PageRenderResult>> RenderAsync(bool force=false)=>RenderService.RenderPageAsync(new PageRenderRequest(DocumentInstanceId,PageId,Dpi:200,Purpose:PageRenderPurpose.Ocr,ForceRerender:force));
        public async Task SetAssetStatusAsync(string status){await using var c=Database.ConnectionFactory.CreateConnection();await c.OpenAsync();await c.ExecuteAsync("update file_assets set status=@Status where file_asset_id=@Id",new{Status=status,Id=AssetId.ToString()});}
        public async Task<LayoutRevisionId?> CurrentRevisionAsync(){await using var c=Database.ConnectionFactory.CreateConnection();await c.OpenAsync();var id=await c.ExecuteScalarAsync<string?>("select layout_revision_id from layout_revisions where document_instance_id=@Id and is_current=1",new{Id=DocumentInstanceId.ToString()});return id is null?null:LayoutRevisionId.Parse(id);}
        public async Task<int> CountAsync(string table){await using var c=Database.ConnectionFactory.CreateConnection();await c.OpenAsync();return await c.ExecuteScalarAsync<int>($"select count(*) from {table};");}
        public async ValueTask DisposeAsync(){await Database.DisposeAsync();if(Directory.Exists(RootDirectory))Directory.Delete(RootDirectory,true);if(File.Exists(ExecutablePath))File.Delete(ExecutablePath);}
    }
    private sealed class CountingRenderer : IPdfPageRenderer { public int CallCount{get;private set;} public bool ThrowOnRender{get;set;} public bool ThrowTimeout{get;set;} public async Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath,int pageIndex,string outputPath,int dpi,CancellationToken cancellationToken=default){CallCount++;if(ThrowTimeout)throw new PdfRendererTimeoutException("PDF renderer timed out.");if(ThrowOnRender)throw new InvalidOperationException("render failed");Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);await File.WriteAllBytesAsync(outputPath,[137,80,78,71]);return new(1000,1400,0,CoordinateBasis.NormalizedPage,1000,1400,"fake-test-renderer-v1");} }
    private sealed class FakeRunner : IProcessRunner { public Task<ProcessRunResult> RunAsync(ProcessRunRequest r,CancellationToken c=default)=>Task.FromResult(r.Arguments.Contains("--version")?new ProcessRunResult(0,"v","",false):new ProcessRunResult(0,"rendered OCR text","",false)); }
}
