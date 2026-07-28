using System.IO.Compression;
using System.Text;
using Dapper;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class MinerUPageRegionRunTests
{
    [Fact]
    public async Task Pages_run_uploads_only_the_requested_pages_and_stages_no_orphan_trees()
    {
        await using Context context = await Context.CreateAsync(3);
        Page[] requested = [context.Pages[0], context.Pages[2]];

        Result<OcrRun> run = await context.Engine.RunPresetOnPagesAsync(
            context.Document.DocumentInstanceId,
            context.Preset.PresetId,
            requested.Select(page => page.PageId).ToArray());

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Completed);
        context.MinerUClient.UploadRequests.Should().HaveCount(2);
        string sourcePdfPath = context.SourcePdfPath;
        context.MinerUClient.UploadRequests.Should().OnlyContain(request =>
            request.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            request.LocalPath != sourcePdfPath);
        context.MinerUClient.UploadedPdfPageCounts.Should().Equal(1, 1);

        IReadOnlyList<OcrPageResult> results = (await context.Engine.ListPageResultsAsync(run.Value.OcrRunId)).Value;
        results.Should().HaveCount(2);
        results.Should().OnlyContain(result =>
            result.State == OcrPageResultState.Succeeded && result.StagingTreeRevisionId != null);
        results.Select(result => result.PageId).Should().BeEquivalentTo(requested.Select(page => page.PageId));
        (await context.CountAsync("select count(1) from document_tree_revisions;")).Should().Be(2);
        foreach (OcrPageResult result in results)
        {
            IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
                result.StagingTreeRevisionId!.Value)).Value;
            boxes.Should().ContainSingle().Which.Payload.Should().Be(new TextBoxPayload("ocr page 0"));
        }
    }

    [Fact]
    public async Task Region_run_uploads_a_png_and_maps_boxes_back_to_page_coordinates()
    {
        await using Context context = await Context.CreateAsync(1);
        context.MinerUClient.ContentListJson = """
                                               [{"type":"text","page_idx":0,"text":"center block","bbox":[500,500,600,600]},{"type":"text","page_idx":0,"text":"corner block","bbox":[0,0,100,100]}]
                                               """;
        NormalizedBBox region = new(0.25, 0.25, 0.5, 0.5);

        Result<OcrRun> run = await context.Engine.RunPresetOnRegionAsync(
            context.Document.DocumentInstanceId, context.Preset.PresetId, context.Pages[0].PageId, region);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Completed);
        context.Renders.LastRegion.Should().Be(region);
        context.MinerUClient.UploadRequests.Should().ContainSingle().Which.FileName.Should().EndWith(".png");
        context.MinerUClient.UploadedPdfPageCounts.Should().BeEmpty();

        OcrPageResult result = (await context.Engine.ListPageResultsAsync(run.Value.OcrRunId)).Value.Single();
        result.State.Should().Be(OcrPageResultState.Succeeded);
        IReadOnlyList<DocumentBox> boxes = (await context.Trees.ListBoxesAsync(
            result.StagingTreeRevisionId!.Value)).Value;
        boxes.Should().HaveCount(2);
        DocumentBox center = boxes.Single(box => ((TextBoxPayload)box.Payload!).Markdown == "center block");
        center.BBox.X.Should().BeApproximately(0.5, 1e-9);
        center.BBox.Y.Should().BeApproximately(0.5, 1e-9);
        center.BBox.Width.Should().BeApproximately(0.05, 1e-9);
        center.BBox.Height.Should().BeApproximately(0.05, 1e-9);
        DocumentBox corner = boxes.Single(box => ((TextBoxPayload)box.Payload!).Markdown == "corner block");
        corner.BBox.X.Should().BeApproximately(0.25, 1e-9);
        corner.BBox.Y.Should().BeApproximately(0.25, 1e-9);
        corner.BBox.Width.Should().BeApproximately(0.05, 1e-9);
        corner.BBox.Height.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public async Task Logical_page_ocr_uploads_one_png_per_region_and_no_pdf()
    {
        await using Context context = await Context.CreateAsync(1);
        context.MinerUClient.ContentListJson = """
                                               [{"type":"text","page_idx":0,"text":"region text","bbox":[100,100,200,200]}]
                                               """;
        DocumentTreeRevision staged = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            context.Pages[0].PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.LogicalPage, null, null,
                    new NormalizedBBox(0, 0, 0.5, 1), null),
                new DocumentBoxSeed(null, null, 1, DocumentBoxType.LogicalPage, null, null,
                    new NormalizedBBox(0.5, 0, 0.5, 1), null)
            ],
            DocumentTreeRevisionSource.Import)).Value;
        DocumentTreeRevision committed = (await context.Trees.AdoptStagingRevisionAsync(staged.TreeRevisionId)).Value;
        IReadOnlyList<DocumentBox> roots = (await context.Trees.ListBoxesAsync(committed.TreeRevisionId)).Value;
        roots.Should().HaveCount(2);
        LogicalPageOcrService service = new(new DirectOcrRunCoordinator(context.Engine), context.Trees);
        LogicalPageOcrTarget[] targets = roots
            .Select((root, index) => new LogicalPageOcrTarget(root.BoxId,
                index == 0 ? new NormalizedBBox(0.1, 0.1, 0.3, 0.3) : new NormalizedBBox(0.6, 0.6, 0.3, 0.3)))
            .ToArray();

        Result<LogicalPageOcrResult> result = await service.RunAsync(
            context.Document.DocumentInstanceId, context.Preset.PresetId, context.Pages[0].PageId, targets);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        context.MinerUClient.UploadRequests.Should().HaveCount(2);
        context.MinerUClient.UploadRequests.Should().OnlyContain(request =>
            request.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        context.MinerUClient.UploadedPdfPageCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Document_run_batches_consecutive_plain_pages_into_one_upload()
    {
        await using Context context = await Context.CreateAsync(3);
        LogicalPageOcrService service = new(new DirectOcrRunCoordinator(context.Engine), context.Trees);
        LogicalDocumentOcrPagePlan[] plans = context.Pages
            .Select(page => new LogicalDocumentOcrPagePlan(page.PageId, []))
            .ToArray();

        Result<LogicalDocumentOcrResult> result = await service.RunDocumentAsync(
            context.Document.DocumentInstanceId, context.Preset.PresetId, plans);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        context.MinerUClient.UploadRequests.Should().ContainSingle().Which.FileName
            .EndsWith(".pdf", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        context.MinerUClient.UploadedPdfPageCounts.Should().Equal(3);
        result.Value.RunIds.Should().HaveCount(1);
        result.Value.StagingTreeRevisionIds.Should().HaveCount(3);
        result.Value.StagingTreeRevisionIds.Should().OnlyHaveUniqueItems();

        IReadOnlyList<OcrPageResult> pageResults =
            (await context.Engine.ListPageResultsAsync(result.Value.RunIds[0])).Value;
        foreach ((LogicalDocumentOcrPagePlan plan, DocumentTreeRevisionId revision) in
                 plans.Zip(result.Value.StagingTreeRevisionIds))
        {
            pageResults.Single(pageResult => pageResult.PageId == plan.PageId)
                .StagingTreeRevisionId.Should().Be(revision);
        }
    }

    [Fact]
    public async Task Document_run_splits_uploads_around_targeted_pages()
    {
        await using Context context = await Context.CreateAsync(4);
        Page targetedPage = context.Pages[2];
        DocumentTreeRevision staged = (await context.Trees.StagePageAsync(
            context.Document.DocumentInstanceId,
            targetedPage.PageId,
            [
                new DocumentBoxSeed(null, null, 0, DocumentBoxType.LogicalPage, null, null,
                    new NormalizedBBox(0, 0, 1, 1), null)
            ],
            DocumentTreeRevisionSource.Import)).Value;
        DocumentTreeRevision committed =
            (await context.Trees.AdoptStagingRevisionAsync(staged.TreeRevisionId)).Value;
        DocumentBox root = (await context.Trees.ListBoxesAsync(committed.TreeRevisionId)).Value.Single();
        LogicalPageOcrService service = new(new DirectOcrRunCoordinator(context.Engine), context.Trees);
        LogicalDocumentOcrPagePlan[] plans = context.Pages
            .Select(page => new LogicalDocumentOcrPagePlan(page.PageId,
                page.PageId == targetedPage.PageId
                    ? [new LogicalPageOcrTarget(root.BoxId, new NormalizedBBox(0.1, 0.1, 0.3, 0.3))]
                    : []))
            .ToArray();

        Result<LogicalDocumentOcrResult> result = await service.RunDocumentAsync(
            context.Document.DocumentInstanceId, context.Preset.PresetId, plans);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        context.MinerUClient.UploadRequests.Should().HaveCount(3);
        context.MinerUClient.UploadRequests.Count(request =>
            request.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        context.MinerUClient.UploadedPdfPageCounts.Should().Equal(2, 1);
        result.Value.RunIds.Should().HaveCount(3);
        result.Value.StagingTreeRevisionIds.Should().HaveCount(4);
        result.Value.StagingTreeRevisionIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Image_structured_parse_skips_blocks_without_a_bbox()
    {
        Page page = NewPage();
        MinerUContentListPage contentPage = new(1, 1000, 1000,
        [
            new MinerUContentBlock("text", null, "no bbox", null, null),
            new MinerUContentBlock("text", [500, 500, 600, 600], "mapped", null, null)
        ]);

        (OcrPageCandidate candidate, IReadOnlyList<OcrDiagnostic> diagnostics) =
            new MinerUDocumentTreeCandidateMapper().MapImagePage(
                contentPage, page, new NormalizedBBox(0.25, 0.25, 0.5, 0.5));

        candidate.Boxes.Should().ContainSingle().Which.Payload.Should().Be(new TextBoxPayload("mapped"));
        candidate.Boxes[0].BBox.X.Should().BeApproximately(0.5, 1e-9);
        candidate.Boxes[0].BBox.Y.Should().BeApproximately(0.5, 1e-9);
        diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "bbox_missing_skipped" && !diagnostic.BlocksAdoption);
    }

    [Fact]
    public void Image_structured_parse_places_a_placeholder_when_every_block_is_unmappable()
    {
        Page page = NewPage();
        NormalizedBBox region = new(0.25, 0.25, 0.5, 0.5);
        MinerUContentListPage contentPage = new(1, 1000, 1000,
            [new MinerUContentBlock("text", null, "no bbox", null, null)]);

        (OcrPageCandidate candidate, _) =
            new MinerUDocumentTreeCandidateMapper().MapImagePage(contentPage, page, region);

        candidate.Boxes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            BoxType = DocumentBoxType.LogicalPage,
            BBox = region,
            Payload = new TextBoxPayload("Blank page (MinerU returned no content).")
        });
    }

    private static Page NewPage()
    {
        return new Page(PageId.New(), DocumentInstanceId.New(), 0, "1", null, null, 0,
            CoordinateBasis.NormalizedPage, null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;
        private readonly string _rootDirectory;

        private Context(TemporarySqliteDatabase database, string rootDirectory, DocumentInstance document,
            Page[] pages, OcrPreset preset, OcrRunEngine engine, DocumentTreeService trees,
            FakePageRenderService renders, RecordingMinerUClient minerUClient, string sourcePdfPath)
        {
            _database = database;
            _rootDirectory = rootDirectory;
            Document = document;
            Pages = pages;
            Preset = preset;
            Engine = engine;
            Trees = trees;
            Renders = renders;
            MinerUClient = minerUClient;
            SourcePdfPath = sourcePdfPath;
        }

        public DocumentInstance Document { get; }
        public Page[] Pages { get; }
        public OcrPreset Preset { get; }
        public OcrRunEngine Engine { get; }
        public DocumentTreeService Trees { get; }
        public FakePageRenderService Renders { get; }
        public RecordingMinerUClient MinerUClient { get; }
        public string SourcePdfPath { get; }

        public async Task<int> CountAsync(string sql)
        {
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                _database.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<int>(sql);
        }

        public static async Task<Context> CreateAsync(int pageCount)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            string rootDirectory = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), $"patchouli-mineru-runs-{Guid.NewGuid():N}")).FullName;
            string sourcePdfPath = Path.Combine(rootDirectory, "source.pdf");
            if (pageCount <= 3)
            {
                File.Copy(TestFixtures.RealThreePagePdf, sourcePdfPath);
            }
            else
            {
                WriteBlankPdf(sourcePdfPath, pageCount);
            }

            string regionPngPath = Path.Combine(rootDirectory, "region.png");
            WriteSolidPng(regionPngPath);
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("MinerU runs");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "MinerU runs")).Value;
            FileAsset file = (await new FileAssetService(database.ConnectionFactory, libraries, clock)
                .RegisterFileAsync(sourcePdfPath)).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, file.FileAssetId, DocumentInstanceType.PrimaryScan)).Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page[] pageList = new Page[pageCount];
            for (int index = 0; index < pageCount; index++)
            {
                pageList[index] = (await pages.CreatePageAsync(document.DocumentInstanceId, index,
                    (index + 1).ToString(), null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test",
                    null)).Value;
            }

            OcrPreset preset = (await new OcrPresetService(database.ConnectionFactory, libraries, clock)
                .CreatePresetAsync("MinerU runs", null, OcrEngineIds.MinerU, "vlm", null, "{}", false)).Value;
            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            OcrDocumentTreeImporter treeImporter = new(trees);
            FakePageRenderService renders = new(regionPngPath);
            RecordingMinerUClient minerUClient = new();
            OcrRunEngine engine = new(
                database.ConnectionFactory,
                clock,
                (_, _) => Task.FromResult(Result<string>.Success("token")),
                new MockOcrEngine(),
                treeImporter: treeImporter,
                pageRenderService: renders,
                minerUResultImporter: new MinerUResultImporter(database.ConnectionFactory, clock, treeImporter),
                minerUClientFactory: _ => minerUClient,
                minerUCacheRoot: Path.Combine(rootDirectory, "mineru-cache"));
            return new Context(database, rootDirectory, document, pageList, preset, engine, trees, renders,
                minerUClient, sourcePdfPath);
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

        private static void WriteBlankPdf(string path, int pageCount)
        {
            using FileStream stream = File.Create(path);
            using SKDocument document = SKDocument.CreatePdf(stream);
            for (int index = 0; index < pageCount; index++)
            {
                using SKCanvas canvas = document.BeginPage(612, 792);
                canvas.Clear(SKColors.White);
                document.EndPage();
            }

            document.Close();
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

    private sealed class RecordingMinerUClient : IMinerUClient
    {
        private readonly Dictionary<string, int> _batchPageCounts = new();

        public List<MinerUUploadRequest> UploadRequests { get; } = new();
        public List<int> UploadedPdfPageCounts { get; } = new();
        public string? ContentListJson { get; set; }
        public bool IsConfigured => true;

        public async Task<Result<MinerUUploadBatch>> RequestUploadUrlsAsync(
            IReadOnlyList<MinerUUploadRequest> files,
            CancellationToken cancellationToken = default)
        {
            MinerUUploadRequest request = files.Single();
            UploadRequests.Add(request);
            string batchId = $"batch-{UploadRequests.Count}";
            int pageCount = 1;
            if (request.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                pageCount = await new PdfiumDocumentEngine().GetPageCountAsync(request.LocalPath, cancellationToken);
                UploadedPdfPageCounts.Add(pageCount);
            }

            _batchPageCounts[batchId] = pageCount;
            return Result<MinerUUploadBatch>.Success(new MinerUUploadBatch(batchId,
                [new MinerUFileUploadUrl(request.FileName, "https://upload.example.test/file", request.DataId)]));
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
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(downloadDirectory);
            string zipPath = Path.Combine(downloadDirectory, $"{batchId}.zip");
            using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            ZipArchiveEntry entry = archive.CreateEntry("result_content_list.json");
            using StreamWriter writer = new(entry.Open());
            writer.Write(ContentListJson ?? BuildContentList(_batchPageCounts[batchId]));
            return Task.FromResult(Result<MinerUDownloadedResult>.Success(
                new MinerUDownloadedResult(batchId, zipPath, MinerUProviderStatus.Done)));
        }

        private static string BuildContentList(int pageCount)
        {
            StringBuilder json = new("[");
            for (int index = 0; index < pageCount; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append(
                    $"{{\"type\":\"text\",\"page_idx\":{index},\"text\":\"ocr page {index}\",\"bbox\":[100,100,200,200]}}");
            }

            return json.Append(']').ToString();
        }
    }

    private sealed class DirectOcrRunCoordinator : IOcrRunCoordinator
    {
        private readonly IOcrRunEngine _engine;

        public DirectOcrRunCoordinator(IOcrRunEngine engine)
        {
            _engine = engine;
        }

        public Task<Result<OcrQueueTask>> QueueDocumentOcrAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, IReadOnlyList<PageId> pageIds, string engineId, string adapterKind,
            string? providerId, string priority, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<OcrRun>> RunPresetOnDocumentAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, CancellationToken cancellationToken = default)
        {
            return _engine.RunPresetOnDocumentAsync(documentInstanceId, presetId, cancellationToken);
        }

        public Task<Result<OcrRun>> RunPresetOnPagesAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, IReadOnlyList<PageId> pageIds, CancellationToken cancellationToken = default)
        {
            return _engine.RunPresetOnPagesAsync(documentInstanceId, presetId, pageIds, cancellationToken);
        }

        public Task<Result<OcrRun>> RunPresetOnRegionAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox,
            CancellationToken cancellationToken = default)
        {
            return _engine.RunPresetOnRegionAsync(documentInstanceId, presetId, pageId, regionBBox,
                cancellationToken);
        }

        public Task<Result<OcrRegionCandidate>> RecognizeRegionCandidateAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, NormalizedBBox regionBBox,
            CancellationToken cancellationToken = default)
        {
            return _engine.RecognizeRegionCandidateAsync(documentInstanceId, presetId, pageId, regionBBox,
                cancellationToken);
        }

        public Task<Result<OcrRun>> RunPresetOnImagePageAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, string imagePath, CancellationToken cancellationToken = default)
        {
            return _engine.RunPresetOnImagePageAsync(documentInstanceId, presetId, pageId, imagePath,
                cancellationToken);
        }

        public Task<Result<OcrRun>> RunPresetOnRenderedPdfPageAsync(DocumentInstanceId documentInstanceId,
            OcrPresetId presetId, PageId pageId, int dpi = 200, CancellationToken cancellationToken = default)
        {
            return _engine.RunPresetOnRenderedPdfPageAsync(documentInstanceId, presetId, pageId, dpi,
                cancellationToken);
        }

        public Task<Result> CancelRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
        {
            return _engine.CancelRunAsync(runId, cancellationToken);
        }

        public Task<Result> UnsetCurrentOcrAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return _engine.UnsetCurrentOcrAsync(documentInstanceId, cancellationToken);
        }

        public Task<Result> HideOcrRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
        {
            return _engine.HideOcrRunAsync(runId, cancellationToken);
        }

        public Task<Result<OcrCandidateAdoption>> AdoptCandidateRunAsync(OcrRunId runId,
            IReadOnlyList<PageId>? selectedPages = null, CancellationToken cancellationToken = default)
        {
            return _engine.AdoptCandidateRunAsync(runId, selectedPages, cancellationToken);
        }

        public Task<Result<OcrRun>> GetRunAsync(OcrRunId runId, CancellationToken cancellationToken = default)
        {
            return _engine.GetRunAsync(runId, cancellationToken);
        }

        public Task<Result<IReadOnlyList<OcrPageResult>>> ListPageResultsAsync(OcrRunId runId,
            CancellationToken cancellationToken = default)
        {
            return _engine.ListPageResultsAsync(runId, cancellationToken);
        }
    }
}
