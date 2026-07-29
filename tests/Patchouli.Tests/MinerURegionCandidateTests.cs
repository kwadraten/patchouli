using System.IO.Compression;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class MinerURegionCandidateTests
{
    [Fact]
    public async Task MinerU_region_candidate_routes_through_image_upload_instead_of_the_adapter()
    {
        await using Context context = await Context.CreateAsync();
        NormalizedBBox region = new(0.2, 0.3, 0.4, 0.2);

        Result<OcrRegionCandidate> candidate = await context.Coordinator.RecognizeRegionCandidateAsync(
            context.Document.DocumentInstanceId, context.Preset.PresetId, context.Page.PageId, region);

        candidate.IsSuccess.Should().BeTrue(candidate.ErrorMessage);
        candidate.Value.BBox.Should().Be(region);
        candidate.Value.BoxType.Should().Be(DocumentBoxType.Text);
        candidate.Value.Payload.Should().BeOfType<TextBoxPayload>()
            .Which.Markdown.Should().Be("region line one\nregion line two");
        context.Renders.LastRegion.Should().Be(region);
        context.MinerUClient.UploadRequest.Should().NotBeNull();
        context.MinerUClient.UploadRequest!.FileName.Should().EndWith(".png");
        (await context.CountAsync("select count(1) from ocr_runs;")).Should().Be(0);
        (await context.CountAsync("select count(1) from document_tree_revisions;")).Should().Be(0);
    }

    [Fact]
    public async Task MinerU_region_candidate_fails_without_a_credential()
    {
        await using Context context = await Context.CreateAsync(_ =>
            Task.FromResult(Result<string>.Failure("not_configured", "No MinerU token.")));
        NormalizedBBox region = new(0.2, 0.3, 0.4, 0.2);

        Result<OcrRegionCandidate> candidate = await context.Coordinator.RecognizeRegionCandidateAsync(
            context.Document.DocumentInstanceId, context.Preset.PresetId, context.Page.PageId, region);

        candidate.IsFailure.Should().BeTrue();
        candidate.ErrorMessage.Should().Contain("No MinerU token.");
        context.Renders.LastRegion.Should().BeNull();
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;
        private readonly string _rootDirectory;

        private Context(TemporarySqliteDatabase database, string rootDirectory, DocumentInstance document, Page page,
            OcrPreset preset, OcrRunEngine coordinator, FakePageRenderService renders,
            FakeMinerUClient minerUClient)
        {
            _database = database;
            _rootDirectory = rootDirectory;
            Document = document;
            Page = page;
            Preset = preset;
            Coordinator = coordinator;
            Renders = renders;
            MinerUClient = minerUClient;
        }

        public DocumentInstance Document { get; }
        public Page Page { get; }
        public OcrPreset Preset { get; }
        public OcrRunEngine Coordinator { get; }
        public FakePageRenderService Renders { get; }
        public FakeMinerUClient MinerUClient { get; }

        public async Task<int> CountAsync(string sql)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(sql);
        }

        public static async Task<Context> CreateAsync(
            Func<string, Task<Result<string>>>? credentialResolver = null)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            string rootDirectory = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), $"patchouli-mineru-region-{Guid.NewGuid():N}")).FullName;
            string regionPngPath = Path.Combine(rootDirectory, "region.png");
            WriteSolidPng(regionPngPath);
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("MinerU region");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "MinerU region")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page page = (await pages.CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            OcrPreset preset = (await new OcrPresetService(database.ConnectionFactory, libraries, clock)
                .CreatePresetAsync("MinerU region", null, OcrEngineIds.MinerU, "vlm", null, "{}", false)).Value;

            // Registering the real MinerU adapter proves the region path never reaches it:
            // the adapter rejects non-PDF input and fails region recognition.
            OcrAdapterRegistry adapters = new();
            adapters.RegisterAdapter(new MinerUOcrAdapter());
            FakePageRenderService renders = new(regionPngPath);
            FakeMinerUClient minerUClient = new();
            OcrRunEngine coordinator = new(
                database.ConnectionFactory,
                clock,
                credentialResolver is null
                    ? (_, _) => Task.FromResult(Result<string>.Success("token"))
                    : (provider, _) => credentialResolver(provider),
                new MockOcrEngine(),
                adapterRegistry: adapters,
                pageRenderService: renders,
                minerUClientFactory: _ => minerUClient,
                minerUCacheRoot: Path.Combine(rootDirectory, "mineru-cache"));
            return new Context(database, rootDirectory, document, page, preset, coordinator, renders, minerUClient);
        }

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, true);
            }
        }

        private static void WriteSolidPng(string path)
        {
            using SKBitmap bitmap = new(4, 4);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.White);
            canvas.Flush();
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, png.ToArray());
        }
    }

    private sealed class FakePageRenderService : IPageRenderService
    {
        private readonly string _regionPngPath;

        public FakePageRenderService(string regionPngPath)
        {
            _regionPngPath = regionPngPath;
        }

        public NormalizedBBox? LastRegion { get; private set; }

        public Task<Result<string>> RenderRegionPngAsync(DocumentInstanceId documentInstanceId, PageId pageId,
            NormalizedBBox region, int dpi = 200, CancellationToken cancellationToken = default)
        {
            LastRegion = region;
            return Task.FromResult(Result<string>.Success(_regionPngPath));
        }

        public Task<Result<PageRenderResult>> RenderPageAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<string?>> GetCachedRenderPathAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<PdfPagePixelBufferLease>> RenderPreviewAsync(PageRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> ClearRenderCacheForDocumentAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrInputDescriptor>> BuildOcrInputFromRenderedPageAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, int dpi = 200,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<PdfRendererAvailability> GetRendererAvailabilityAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeMinerUClient : IMinerUClient
    {
        public MinerUUploadRequest? UploadRequest { get; private set; }
        public bool IsConfigured => true;

        public Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
            IReadOnlyList<MinerUUploadRequest> files,
            CancellationToken cancellationToken = default)
        {
            UploadRequest = files.Single();
            return Task.FromResult(Result<MinerUUploadBatch>.Success(
                new MinerUUploadBatch("batch-1",
                    [new MinerUFileUploadUrl(UploadRequest.FileName, "https://upload.example.test/file", "file-1")])));
        }

        public Task<Result> UploadFileAsync(string uploadUrl, string localPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<MinerUPollResult>> PollExtractResultAsync(string batchId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                Result<MinerUPollResult>.Success(new MinerUPollResult(batchId, MinerUProviderStatus.Done, null, null)));
        }

        public Task<Result<MinerUDownloadedResult>> WaitForCompletionAndDownloadAsync(
            string batchId,
            string downloadDirectory,
            CancellationToken cancellationToken = default,
            IProgress<OcrTaskStageProgress>? progress = null)
        {
            Directory.CreateDirectory(downloadDirectory);
            string zipPath = Path.Combine(downloadDirectory, $"{Guid.NewGuid():N}.zip");
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("region_content_list.json");
                using StreamWriter writer = new(entry.Open());
                writer.Write("""
                             [{"type":"text","page_idx":0,"text":"region line one","bbox":[0,0,100,20]},{"type":"image","page_idx":0,"bbox":[0,20,100,120]},{"type":"text","page_idx":0,"text":"region line two","bbox":[0,120,100,140]}]
                             """);
            }

            return Task.FromResult(Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done)));
        }
    }
}
