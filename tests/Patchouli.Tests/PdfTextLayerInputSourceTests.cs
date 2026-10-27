using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.PdfText;
using Patchouli.Infrastructure.Database;
using Patchouli.Ocr;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

/// <summary>
/// Exercises the host text-layer input source against a seeded SQLite file with inert
/// service doubles — no real PDF, PDFium process, layout model or rendering.
/// </summary>
public class PdfTextLayerInputSourceTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly DocumentInstanceId _documentId = DocumentInstanceId.New();
    private readonly PageId _pageId = PageId.New();
    private readonly DocumentTreeRevisionId _revisionId = DocumentTreeRevisionId.New();
    private readonly FileAssetId _fileAssetId = FileAssetId.New();
    private readonly SqliteConnectionFactory _factory;

    public PdfTextLayerInputSourceTests()
    {
        _root = Path.Combine(TestPaths.FromRepositoryRoot(".tmp", "tests", "pdf-text-input"),
            Guid.NewGuid().ToString("N"));
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "library.db"));
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await SeedAsync();
    }

    public Task DisposeAsync()
    {
        // Release pooled SQLite connections before deleting the isolated test directory.
        _factory.ClearPools();
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
        return Task.CompletedTask;
    }

    private PdfTextLayerInputSource CreateSource(
        IFileResolutionService? resolution = null,
        IFileMaterializationService? materialization = null,
        ISourceValidationService? validation = null,
        IPdfTextExtractor? extractor = null)
    {
        return new PdfTextLayerInputSource(_factory,
            resolution ?? FakeFileResolution.Available(Path.Combine(_root, "source.pdf")),
            materialization ?? new FakeMaterialization(),
            validation ?? new FakeSourceValidation(),
            extractor ?? new FakeTextExtractor(RawPage([
                RawGlyph(0, "H", 72, 82, 700, 712, 72, 700)
            ])));
    }

    private async Task SeedAsync()
    {
        await using SqliteConnection connection = _factory.CreateConnection();
        await connection.ExecuteAsync(
            """
            create table pages (page_id text primary key, document_instance_id text,
                page_index integer, rotation integer, coordinate_basis text,
                source_file_hash text, width real, height real,
                basis_width real, basis_height real, renderer_basis_version text);
            create table document_instances (document_instance_id text primary key,
                file_asset_id text);
            create table file_assets (file_asset_id text primary key, status text,
                original_path text, size_bytes integer, mtime_utc text, quick_hash text,
                full_blake3 text);
            """);
        await connection.ExecuteAsync(
            """
            insert into pages values (@page, @doc, 0, 0, 'normalized_page', 'fh',
                null, null, null, null, null);
            insert into file_assets values (@asset, 'available', 'source.pdf', 1024,
                '2026-01-01T00:00:00Z', 'qh', 'fh');
            insert into document_instances values (@doc, @asset);
            """,
            new
            {
                page = _pageId.ToString(), doc = _documentId.ToString(),
                asset = _fileAssetId.ToString()
            });
    }

    private PdfTextLayerContextRequest Request(bool image = false, bool layout = false)
    {
        return new PdfTextLayerContextRequest(_documentId, _pageId, _revisionId, 0,
            new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0),
            IncludePageImage: image, IncludeLayoutDetection: layout);
    }

    [Fact]
    public async Task CreateContext_ProducesNormalizedGlyphsAndIdentity()
    {
        FakeTextExtractor extractor = new(RawPage([
            RawGlyph(0, "H", 72, 82, 700, 712, 72, 700),
            RawGlyph(1, "i", 84, 88, 700, 712, 84, 700)
        ]));
        PdfTextLayerInputSource source = CreateSource(extractor: extractor);

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsSuccess);
        PdfTextLayerContext context = result.Value;
        Assert.Equal(_pageId, context.Page.PageId);
        Assert.Equal(_revisionId, context.Page.RevisionId);
        Assert.Equal(0, context.Page.PageIndex);
        Assert.Equal(2, context.Glyphs.Count);
        Assert.Equal(new[] { 0, 1 }, context.Glyphs.Select(g => g.NativeIndex));
        Assert.All(context.Glyphs, g => Assert.InRange(g.BBox.X, 0, 1));
        Assert.Equal(612, context.Geometry.WidthPoints);
        Assert.Null(context.ReadPageImageAsync);
        Assert.Null(context.DetectLayoutAsync);
        Assert.Single(extractor.Calls);
    }

    [Fact]
    public async Task CreateContext_VisualDelegatesOnlyWhenRequested()
    {
        PdfTextLayerInputSource source = CreateSource();

        Result<PdfTextLayerContext> native = await source.CreateContextAsync(Request());
        Result<PdfTextLayerContext> visual =
            await source.CreateContextAsync(Request(true, true));

        Assert.Null(native.Value.ReadPageImageAsync);
        // Without the optional render/detector services the delegates stay absent —
        // the provider then fails explicitly instead of silently degrading.
        Assert.Null(visual.Value.ReadPageImageAsync);
        Assert.Null(visual.Value.DetectLayoutAsync);
    }

    [Fact]
    public async Task CreateContext_RejectsWrongDocumentInstance()
    {
        PdfTextLayerInputSource source = CreateSource();
        PdfTextLayerContextRequest wrong = Request() with
        {
            DocumentId = DocumentInstanceId.New()
        };

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(wrong);

        Assert.True(result.IsFailure);
        Assert.Contains("document instance", result.ErrorMessage);
    }

    [Fact]
    public async Task CreateContext_ChangedSource_IsExplicitFailure()
    {
        PdfTextLayerInputSource source = CreateSource(
            FakeFileResolution.WithStatus(FileAssetStatus.Changed, "source moved"));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerErrorCodes.SourceChanged, result.ErrorCode);
    }

    [Fact]
    public async Task CreateContext_NonPdfAsset_IsExplicitFailure()
    {
        PdfTextLayerInputSource source = CreateSource(
            FakeFileResolution.Available(Path.Combine(_root, "source.epub")));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerErrorCodes.UnsupportedFile, result.ErrorCode);
    }

    [Fact]
    public async Task CreateContext_StaleValidation_IsExplicitFailure()
    {
        PdfTextLayerInputSource source = CreateSource(
            validation: new FakeSourceValidation(SourceValidationStatus.Changed));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerErrorCodes.SourceChanged, result.ErrorCode);
    }

    [Fact]
    public async Task CreateContext_RotationMismatch_SurfacesDiagnostic()
    {
        FakeTextExtractor extractor = new(RawPage([
            RawGlyph(0, "H", 72, 82, 700, 712, 72, 700)
        ], rotation: 90));
        PdfTextLayerInputSource source = CreateSource(extractor: extractor);

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.ExtractionDiagnostics!,
            d => d.Code == PdfTextLayerDiagnosticCodes.PageRotationMismatch);
    }

    /// <summary>Overwrites the seeded page row's persisted basis columns.</summary>
    private void PersistBasis(double? width, double? height, double? basisWidth,
        double? basisHeight, string? hash = "fh", int rotation = 0)
    {
        using SqliteConnection connection = _factory.CreateConnection();
        connection.Execute(
            """
            update pages set width=@w, height=@h, basis_width=@bw, basis_height=@bh,
                source_file_hash=@hash, rotation=@rot where page_id=@page;
            """,
            new
            {
                w = width, h = height, bw = basisWidth, bh = basisHeight, hash,
                rot = rotation, page = _pageId.ToString()
            });
    }

    [Fact]
    public async Task CreateContext_PersistedBasisConsistent_AdoptsNativeRotation()
    {
        // Persisted basis is the rotated display dims — consistent with a /Rotate 90 page.
        PersistBasis(null, null, 792, 612, rotation: 90);
        PdfTextLayerInputSource source = CreateSource(extractor: new FakeTextExtractor(
            RawPage([RawGlyph(0, "H", 72, 82, 700, 712, 72, 700)], rotation: 90)));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(90, result.Value.Geometry.RotationDegrees);
        Assert.Equal(792, result.Value.Geometry.WidthPoints);
    }

    [Fact]
    public async Task CreateContext_UnrotatedPersistedBasis_ReconcilesToHostBasis()
    {
        // The persisted basis predates /Rotate handling: it matches the un-rotated media
        // box while the source declares 90°. The host's basis is the persisted frame.
        PersistBasis(null, null, 612, 792);
        PdfTextLayerInputSource source = CreateSource(extractor: new FakeTextExtractor(
            RawPage([RawGlyph(0, "H", 72, 82, 700, 712, 72, 700)], rotation: 90)));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Geometry.RotationDegrees);
        Assert.Equal(612, result.Value.Geometry.WidthPoints);
        Assert.Contains(result.Value.ExtractionDiagnostics!,
            d => d.Code == PdfTextLayerDiagnosticCodes.RotationBasisReconciled);
    }

    [Fact]
    public async Task CreateContext_PersistedBasisInconsistent_IsExplicitFailure()
    {
        // A basis matching neither the rotated nor un-rotated page cannot be trusted.
        PersistBasis(null, null, 500, 500);
        PdfTextLayerInputSource source = CreateSource(extractor: new FakeTextExtractor(
            RawPage([RawGlyph(0, "H", 72, 82, 700, 712, 72, 700)])));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.SourceBasisMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task CreateContext_StalePageBasis_IgnoresPersistedBasis()
    {
        // Persisted basis was written against older bytes (different hash) — it is ignored
        // and the native basis stands.
        PersistBasis(null, null, 500, 500, "oldhash");
        PdfTextLayerInputSource source = CreateSource(extractor: new FakeTextExtractor(
            RawPage([RawGlyph(0, "H", 72, 82, 700, 712, 72, 700)])));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.ExtractionDiagnostics!,
            d => d.Code == PdfTextLayerDiagnosticCodes.PageBasisStale);
    }

    [Fact]
    public async Task CreateContext_CropBoxDiffersFromMediaBox_SurfacesDiagnostic()
    {
        // FPDF_GetPageWidth/Height disagree with the media box: a crop-boxed page.
        PdfTextLayerInputSource source = CreateSource(extractor: new FakeTextExtractor(
            RawPage([RawGlyph(0, "H", 72, 82, 700, 712, 72, 700)],
                pageWidth: 500, pageHeight: 600)));

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.ExtractionDiagnostics!,
            d => d.Code == PdfTextLayerDiagnosticCodes.PageBoxMismatch);
    }

    [Fact]
    public async Task CreateContext_CroppedPage_PersistedCropBasis_IsAccepted()
    {
        // A spread-sized media box clipped by a portrait crop box — the geometry that made
        // persisted-basis reconciliation fail on a real book scan. The persisted render
        // basis describes the effective crop frame the renderer produced, so it matches.
        PersistBasis(null, null, 567, 814);
        FakeTextExtractor extractor = new(RawPage([
                RawGlyph(0, "H", 800, 810, 700, 712, 800, 700),
                RawGlyph(1, "x", 10, 20, 700, 712, 10, 700)
            ], 0, 0, 1355, 859,
            bound: new PdfPageBox(765, 23, 1332, 836)));
        PdfTextLayerInputSource source = CreateSource(extractor: extractor);

        Result<PdfTextLayerContext> result = await source.CreateContextAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(567, result.Value.Geometry.WidthPoints);
        Assert.Equal(813, result.Value.Geometry.HeightPoints);
        Assert.Contains(result.Value.ExtractionDiagnostics!,
            d => d.Code == PdfTextLayerDiagnosticCodes.PageBoxMismatch);
        // The visible glyph normalizes inside the crop frame; the clipped one is dropped.
        Assert.Single(result.Value.Glyphs);
        Assert.Equal(35.0 / 567, result.Value.Glyphs[0].BBox.X, 3);
    }
}
