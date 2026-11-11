using System.Reflection;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Tests;

/// <summary>
/// Viewport-rotation OCR coverage (worker D): real pixel rotation, normalized-bbox inverse
/// transforms for every quarter turn, and production-path runs proving that the recognizer —
/// a real adapter and a compiled-extension image input alike — receives the rotated page
/// image while stored geometry lands back in the unrotated document basis.
/// </summary>
public sealed class OcrRotationTests
{
    [Theory]
    [InlineData(SourceBBoxCoordinateSystem.Unknown, null, null)]
    [InlineData(SourceBBoxCoordinateSystem.EngineRaw, null, null)]
    [InlineData(SourceBBoxCoordinateSystem.ImagePixels, null, null)]
    [InlineData(SourceBBoxCoordinateSystem.ImagePixels, 100.0, 0.0)]
    public void Rotation_preserves_unresolved_source_coordinate_bases(string coordinateSystem,
        double? basisWidth, double? basisHeight)
    {
        SourceBBox source = new(.1, .2, .3, .4, coordinateSystem, basisWidth, basisHeight);
        OcrEnginePageResult output = new(PageId.New(), true, "text", null, null, null, source);
        MethodInfo transform = typeof(OcrRunEngine).GetMethod("UnrotateEngineResult",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        OcrEnginePageResult transformed = (OcrEnginePageResult)transform.Invoke(null, [output, 90])!;

        transformed.SourceBBox.Should().BeSameAs(source,
            "rotation must preserve the resolver's unknown or missing coordinate-basis diagnostics");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(450, 90)]
    public void Normalize_accepts_only_clockwise_quarter_turns(int input, int expected)
    {
        Result<int> normalized = OcrImageRotation.Normalize(input);
        normalized.IsSuccess.Should().BeTrue();
        normalized.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(45)]
    [InlineData(-45)]
    [InlineData(135)]
    public void Normalize_rejects_non_quarter_turns(int input)
    {
        OcrImageRotation.Normalize(input).ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Theory]
    // Input box (0.1, 0.2, 0.3, 0.4); expected values are the same physical rect
    // re-expressed in the rotated image's own normalized frame.
    [InlineData(0, .1, .2, .3, .4)]
    [InlineData(90, .4, .1, .4, .3)]
    [InlineData(180, .6, .4, .3, .4)]
    [InlineData(270, .2, .6, .4, .3)]
    public void BBox_forward_transform_maps_into_the_image_basis(int degrees, double x, double y,
        double w, double h)
    {
        NormalizedBBox rotated = OcrImageRotation.RotateIntoImageBasis(
            new NormalizedBBox(.1, .2, .3, .4), degrees);
        rotated.X.Should().BeApproximately(x, 1e-9);
        rotated.Y.Should().BeApproximately(y, 1e-9);
        rotated.Width.Should().BeApproximately(w, 1e-9);
        rotated.Height.Should().BeApproximately(h, 1e-9);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void BBox_inverse_transform_round_trips_every_quarter_turn(int degrees)
    {
        NormalizedBBox original = new(.13, .27, .31, .42);
        NormalizedBBox restored = OcrImageRotation.UnrotateFromImageBasis(
            OcrImageRotation.RotateIntoImageBasis(original, degrees), degrees);
        restored.X.Should().BeApproximately(original.X, 1e-9);
        restored.Y.Should().BeApproximately(original.Y, 1e-9);
        restored.Width.Should().BeApproximately(original.Width, 1e-9);
        restored.Height.Should().BeApproximately(original.Height, 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotateToPngBytes_rotates_real_pixels(int degrees)
    {
        using SKBitmap source = CornerBitmap();
        byte[] png = OcrImageRotation.RotateToPngBytes(source, degrees);
        using SKBitmap? rotated = SKBitmap.Decode(png);
        rotated.Should().NotBeNull();
        bool swapped = degrees is 90 or 270;
        rotated!.Width.Should().Be(swapped ? 3 : 4);
        rotated.Height.Should().Be(swapped ? 4 : 3);

        (int X, int Y) red = degrees switch
        {
            90 => (2, 0),
            180 => (3, 2),
            270 => (0, 3),
            _ => (0, 0)
        };
        (int X, int Y) green = degrees switch
        {
            90 => (2, 3),
            180 => (0, 2),
            270 => (0, 0),
            _ => (3, 0)
        };
        (int X, int Y) blue = degrees switch
        {
            90 => (0, 0),
            180 => (3, 0),
            270 => (2, 3),
            _ => (0, 2)
        };
        rotated.GetPixel(red.X, red.Y).Should().Be(SKColors.Red);
        rotated.GetPixel(green.X, green.Y).Should().Be(SKColors.Green);
        rotated.GetPixel(blue.X, blue.Y).Should().Be(SKColors.Blue);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public async Task Rotated_page_reaches_the_adapter_as_rotated_pixels_and_boxes_land_unrotated(
        int rotationDegrees)
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(RecordingAdapter.Id);
        RecordingAdapter adapter = new();
        OcrAdapterRegistry registry = new();
        registry.RegisterAdapter(adapter);
        RotatingPageRenderService renders = new(context.SourceImagePath);
        OcrRunEngine engine = context.CreateEngine(renders, registry);

        Result<OcrRun> run = await engine.RunPresetOnPagesAsync(context.Document.DocumentInstanceId,
            version.PresetId, [context.Pages[0].PageId], rotationDegrees: rotationDegrees);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        run.Value.State.Should().Be(OcrRunState.Completed);
        adapter.Inputs.Should().HaveCount(1);
        OcrInputDescriptor input = adapter.Inputs[0];
        using SKBitmap? recognized = SKBitmap.Decode(input.ImagePath);
        recognized.Should().NotBeNull();
        // The recognizer received the 3x4 rotated page, not the 4x3 document page.
        recognized!.Width.Should().Be(3);
        recognized.Height.Should().Be(4);
        if (rotationDegrees == 90)
        {
            recognized.GetPixel(2, 0).Should().Be(SKColors.Red);
        }
        else
        {
            recognized.GetPixel(0, 3).Should().Be(SKColors.Red);
        }

        NormalizedBBox expected = OcrImageRotation.UnrotateFromImageBasis(RecordingAdapter.Box,
            rotationDegrees);
        IReadOnlyList<DocumentBox> stored = await context.WorkingBoxesAsync(run.Value);
        stored.Should().ContainSingle(box => box.BoxType == DocumentBoxType.Text);
        DocumentBox box = stored.Single(box => box.BoxType == DocumentBoxType.Text);
        box.BBox.X.Should().BeApproximately(expected.X, 1e-9);
        box.BBox.Y.Should().BeApproximately(expected.Y, 1e-9);
        box.BBox.Width.Should().BeApproximately(expected.Width, 1e-9);
        box.BBox.Height.Should().BeApproximately(expected.Height, 1e-9);
    }

    [Fact]
    public async Task Region_run_hands_the_adapter_a_rotated_region_in_the_image_basis()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(RecordingAdapter.Id);
        RecordingAdapter adapter = new();
        OcrAdapterRegistry registry = new();
        registry.RegisterAdapter(adapter);
        RotatingPageRenderService renders = new(context.SourceImagePath);
        OcrRunEngine engine = context.CreateEngine(renders, registry);
        NormalizedBBox region = new(0, .25, 1, .5);

        Result<OcrRun> run = await engine.RunPresetOnRegionAsync(context.Document.DocumentInstanceId,
            version.PresetId, context.Pages[0].PageId, region, rotationDegrees: 90);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        renders.LastRotationDegrees.Should().Be(90);
        OcrInputDescriptor input = adapter.Inputs.Should().ContainSingle().Subject;
        input.InputKind.Should().Be(OcrInputKinds.RegionImage);
        NormalizedBBox expectedRegion = OcrImageRotation.RotateIntoImageBasis(region, 90);
        input.RegionBBox.Should().NotBeNull();
        input.RegionBBox!.Value.X.Should().BeApproximately(expectedRegion.X, 1e-9);
        input.RegionBBox.Value.Y.Should().BeApproximately(expectedRegion.Y, 1e-9);
        input.RegionBBox.Value.Width.Should().BeApproximately(expectedRegion.Width, 1e-9);
        input.RegionBBox.Value.Height.Should().BeApproximately(expectedRegion.Height, 1e-9);
        using SKBitmap? crop = SKBitmap.Decode(input.ImagePath);
        crop.Should().NotBeNull("the adapter's page image is the rotated 3x4 render it crops");
        crop!.Width.Should().Be(3);
        crop.Height.Should().Be(4);
    }

    [Fact]
    public async Task Extension_job_receives_rotated_image_bytes_and_stores_unrotated_boxes()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(RecordingLease.Id);
        RecordingLease lease = new();
        RotatingPageRenderService renders = new(context.SourceImagePath);
        OcrRunEngine engine = context.CreateEngine(renders);

        Result<OcrRun> run = await engine.ExecuteAsync(new OcrRunExecutionRequest(
            context.Document.DocumentInstanceId, version.PresetId, [context.Pages[0].PageId],
            OcrRunExecutionScope.Pages, version.PresetVersionId, engineLease: lease,
            rotationDegrees: 90));

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        // The host-mediated image lease delivered the rotated page pixels to the extension.
        lease.ImageSizes.Should().ContainSingle().Subject.Should().Be((3, 4));
        NormalizedBBox expected = OcrImageRotation.UnrotateFromImageBasis(RecordingLease.Box, 90);
        IReadOnlyList<DocumentBox> stored = await context.WorkingBoxesAsync(run.Value);
        stored.Should().ContainSingle(box => box.BoxType == DocumentBoxType.Text);
        DocumentBox box = stored.Single(box => box.BoxType == DocumentBoxType.Text);
        box.BBox.X.Should().BeApproximately(expected.X, 1e-9);
        box.BBox.Y.Should().BeApproximately(expected.Y, 1e-9);
        box.BBox.Width.Should().BeApproximately(expected.Width, 1e-9);
        box.BBox.Height.Should().BeApproximately(expected.Height, 1e-9);
    }

    [Fact]
    public async Task Extension_region_job_uses_the_rotated_basis_for_its_declared_region()
    {
        await using Context context = await Context.CreateAsync();
        OcrPresetVersion version = await context.CreatePresetVersionAsync(RecordingLease.Id);
        RecordingLease lease = new();
        RotatingPageRenderService renders = new(context.SourceImagePath);
        OcrRunEngine engine = context.CreateEngine(renders,
            engineRuntime: new FakeEngineRuntime(lease));
        NormalizedBBox region = new(.1, .2, .5, .4);

        Result<OcrRegionCandidate> candidate = await engine.RecognizeRegionCandidateAsync(
            context.Document.DocumentInstanceId, version.PresetId, context.Pages[0].PageId, region,
            rotationDegrees: 90);

        candidate.IsSuccess.Should().BeTrue(candidate.ErrorMessage);
        lease.Regions.Should().ContainSingle().Subject
            .Should().Be(OcrImageRotation.RotateIntoImageBasis(region, 90));
        candidate.Value.BBox.Should().Be(region,
            "the ephemeral candidate still describes the unrotated document region");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Persisted_user_rotation_drives_the_input_when_no_rotation_is_requested(
        int userRotation)
    {
        await using Context context = await Context.CreateAsync();
        await context.SetUserRotationAsync(context.Pages[0].PageId, userRotation);
        OcrPresetVersion version = await context.CreatePresetVersionAsync(RecordingAdapter.Id);
        RecordingAdapter adapter = new();
        OcrAdapterRegistry registry = new();
        registry.RegisterAdapter(adapter);
        RotatingPageRenderService renders = new(context.SourceImagePath);
        OcrRunEngine engine = context.CreateEngine(renders, registry);

        // Null rotation: each page runs with its own persisted user rotation.
        Result<OcrRun> run = await engine.RunPresetOnPagesAsync(context.Document.DocumentInstanceId,
            version.PresetId, [context.Pages[0].PageId]);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        renders.LastRotationDegrees.Should().Be(userRotation);
        OcrInputDescriptor input = adapter.Inputs.Should().ContainSingle().Subject;
        using SKBitmap? recognized = SKBitmap.Decode(input.ImagePath);
        recognized.Should().NotBeNull();
        bool swapped = userRotation is 90 or 270;
        recognized!.Width.Should().Be(swapped ? 3 : 4);
        recognized.Height.Should().Be(swapped ? 4 : 3);
        NormalizedBBox expected = OcrImageRotation.UnrotateFromImageBasis(RecordingAdapter.Box,
            userRotation);
        IReadOnlyList<DocumentBox> stored = await context.WorkingBoxesAsync(run.Value);
        DocumentBox box = stored.Single(box => box.BoxType == DocumentBoxType.Text);
        box.BBox.X.Should().BeApproximately(expected.X, 1e-9);
        box.BBox.Y.Should().BeApproximately(expected.Y, 1e-9);
        box.BBox.Width.Should().BeApproximately(expected.Width, 1e-9);
        box.BBox.Height.Should().BeApproximately(expected.Height, 1e-9);
    }

    [Fact]
    public async Task Explicit_rotation_overrides_the_pages_persisted_user_rotation()
    {
        await using Context context = await Context.CreateAsync();
        await context.SetUserRotationAsync(context.Pages[0].PageId, 90);
        OcrPresetVersion version = await context.CreatePresetVersionAsync(RecordingAdapter.Id);
        RecordingAdapter adapter = new();
        OcrAdapterRegistry registry = new();
        registry.RegisterAdapter(adapter);
        RotatingPageRenderService renders = new(context.SourceImagePath);
        OcrRunEngine engine = context.CreateEngine(renders, registry);

        Result<OcrRun> run = await engine.RunPresetOnPagesAsync(context.Document.DocumentInstanceId,
            version.PresetId, [context.Pages[0].PageId], rotationDegrees: 0);

        run.IsSuccess.Should().BeTrue(run.ErrorMessage);
        renders.LastRotationDegrees.Should().Be(0,
            "an explicit rotation is a uniform override; persisted page rotation loses");
        OcrInputDescriptor input = adapter.Inputs.Should().ContainSingle().Subject;
        using SKBitmap? recognized = SKBitmap.Decode(input.ImagePath);
        recognized.Should().NotBeNull();
        recognized!.Width.Should().Be(4);
        recognized.Height.Should().Be(3);
    }

    private static SKBitmap CornerBitmap()
    {
        SKBitmap bitmap = new(4, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(0, 0, SKColors.Red);
        bitmap.SetPixel(3, 0, SKColors.Green);
        bitmap.SetPixel(0, 2, SKColors.Blue);
        bitmap.SetPixel(3, 2, SKColors.Yellow);
        return bitmap;
    }

    /// <summary>
    /// Mirrors <see cref="PageRenderService"/>'s rotated-render contract: the unrotated page PNG
    /// rotates clockwise into a sibling file, and region crops come from the rotated page at the
    /// region's rotated-basis rect.
    /// </summary>
    private sealed class RotatingPageRenderService(string sourcePngPath) : IPageRenderService
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"ocr-rot-{Guid.NewGuid():N}");
        public NormalizedBBox? LastRegion { get; private set; }
        public int LastRotationDegrees { get; private set; }

        public Task<Result<OcrInputDescriptor>> BuildOcrInputFromRenderedPageAsync(
            DocumentInstanceId documentInstanceId, PageId pageId, int dpi = 200,
            CancellationToken cancellationToken = default, int rotationDegrees = 0)
        {
            Directory.CreateDirectory(_root);
            LastRotationDegrees = rotationDegrees;
            string path = sourcePngPath;
            if (rotationDegrees != 0)
            {
                path = Path.Combine(_root, $"page.rot{rotationDegrees}.png");
                OcrImageRotation.RotatePngFile(sourcePngPath, path, rotationDegrees);
            }

            return Task.FromResult(Result<OcrInputDescriptor>.Success(new OcrInputDescriptor(pageId,
                documentInstanceId, OcrInputKinds.PageImage, path, null, null, "available", null)));
        }

        public async Task<Result<string>> RenderRegionPngAsync(DocumentInstanceId documentInstanceId,
            PageId pageId, NormalizedBBox region, int dpi = 200,
            CancellationToken cancellationToken = default, int rotationDegrees = 0)
        {
            LastRegion = region;
            Result<OcrInputDescriptor> page = await BuildOcrInputFromRenderedPageAsync(
                documentInstanceId, pageId, dpi, cancellationToken, rotationDegrees);
            NormalizedBBox imageRegion = rotationDegrees == 0
                ? region
                : OcrImageRotation.RotateIntoImageBasis(region, rotationDegrees);
            string cropPath = Path.Combine(_root,
                $"region-{region.X}-{region.Y}.rot{rotationDegrees}.png");
            try
            {
                RegionImageCrop.CropPngToFile(page.Value.ImagePath!, imageRegion, cropPath);
                return Result<string>.Success(cropPath);
            }
            catch (Exception exception)
            {
                return Result<string>.Failure(AppErrorCodes.InvalidState, exception.Message);
            }
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
            return Task.FromResult(Result.Success());
        }

        public Task<PdfRendererAvailability> GetRendererAvailabilityAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ReleaseDocumentSessionAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>Records the descriptor — including image path and rotated-basis region — it sees.</summary>
    private sealed class RecordingAdapter : IRealOcrAdapter
    {
        public const string Id = "test-rotation-adapter";

        /// <summary>Rotated-basis box the fake recognizer reports for the image it received.</summary>
        public static readonly NormalizedBBox Box = new(.1, .2, .3, .4);

        public List<OcrInputDescriptor> Inputs { get; } = [];
        public string EngineId => Id;
        public string DisplayName => "Rotation test adapter";
        public string Kind => OcrAdapterKind.LocalLibrary;

        public OcrEngineCapability GetCapability()
        {
            return new OcrEngineCapability(EngineId, DisplayName, false, false, true, false, true,
                false, false, false, false, [OcrInputKinds.PageImage, OcrInputKinds.RegionImage], "");
        }

        public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrEnvironmentCheckResult(EngineId, "model", null,
                OcrEnvironmentStatus.Ready, true, "ready", OcrRequiredAction.None, []));
        }

        public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ValidateInputAsync(OcrInputDescriptor input,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input,
            OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            return Task.FromResult(Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(
                input.PageId, true, "recognized", null, null, null,
                TextBoxes: [new OcrEngineTextBox("recognized", Box)])));
        }
    }

    /// <summary>
    /// Extension-lease recognizer: materializes every issued input through the host-mediated
    /// image callback (proving the extension sees rotated bytes and the rotated-basis region),
    /// then submits its candidate inside that rotated frame.
    /// </summary>
    private sealed class RecordingLease : IOcrEngineLease
    {
        public const string Id = "test-rotation-extension";

        /// <summary>Rotated-basis box the fake extension submits for the image it received.</summary>
        public static readonly NormalizedBBox Box = new(.15, .25, .35, .45);

        public List<(int Width, int Height)> ImageSizes { get; } = [];
        public List<NormalizedBBox> Regions { get; } = [];

        public OcrEngineDefinition Definition { get; } = new(Id, "Rotation extension",
            OcrAdapterKind.LocalLibrary, "test-model", [OcrInputKinds.ImageFile]);

        public string EngineId { get; } = Id;
        public bool IsBuiltIn => false;
        public string? EngineExtensionId => "ext.rotation";
        public string? EngineFingerprint => "fingerprint";
        public int? EngineConfigurationVersion => 1;
        public string? ModelIdentity => "test-model";
        public string? ModelDigest => null;
        public ExtensionConfigSnapshot? EngineConfig => null;
        public string? TextLayerProviderId => null;
        public string? TextLayerExtensionId => null;
        public string? TextLayerFingerprint => null;
        public int? TextLayerConfigurationVersion => null;
        public ExtensionConfigSnapshot? TextLayerConfig => null;

        public Task<Result> CheckReadinessAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public async Task<Result<OcrDomainRunResult>> RunOcrJobAsync(OcrDomainJobRequest request,
            CancellationToken cancellationToken = default)
        {
            List<OcrExtensionCandidate> candidates = [];
            List<OcrPageSettlement> settlements = [];
            foreach (OcrJobPage page in request.Pages)
            {
                List<OcrExtensionBoxCandidate> boxes = [];
                List<OcrInputSettlement> inputs = [];
                foreach (OcrJobInput input in page.Inputs)
                {
                    Regions.Add(input.Region);
                    OcrInputImage image = await request.ReadImage(input, cancellationToken);
                    using SKBitmap? decoded = SKBitmap.Decode(image.Data.Span);
                    ImageSizes.Add((decoded!.Width, decoded.Height));
                    image.Dispose();
                    // The extension answers in the rotated image's own coordinate frame.
                    boxes.Add(new OcrExtensionBoxCandidate(input.Id,
                        new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, Box,
                            new TextBoxPayload("rotated text"))));
                    inputs.Add(new OcrInputSettlement(input.Id, OcrInputSettlementState.Recognized));
                }

                candidates.Add(new OcrExtensionCandidate(page.Page, boxes, inputs));
                settlements.Add(new OcrPageSettlement(page.Page, OcrPageSettlementState.Accepted,
                    AcceptedBoxCount: boxes.Count));
            }

            return Result<OcrDomainRunResult>.Success(new OcrDomainRunResult(candidates, settlements));
        }

        public Task<Result<PdfTextLayerResult>> ParseTextLayerAsync(DocumentInstanceId documentId,
            PageId pageId, int pageIndex, DocumentTreeRevisionId revisionId, NormalizedBBox? region,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<PdfTextLayerResult>.Failure(
                AppErrorCodes.UnsupportedOperation, "No text-layer provider is pinned."));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEngineRuntime(IOcrEngineLease lease) : IOcrEngineRuntime
    {
        public IReadOnlyList<OcrEngineDefinition> ListDefinitions()
        {
            return [lease.Definition];
        }

        public Task<Result<OcrEngineDefinition>> GetDefinitionAsync(string engineId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<OcrEngineDefinition>.Success(lease.Definition));
        }

        public Task<Result<IOcrEngineLease>> AcquireAsync(OcrEngineLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IOcrEngineLease>.Success(lease));
        }
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;
        private readonly string _rootDirectory;
        private readonly FixedClock _clock;
        private readonly DocumentTreeService _trees;

        private Context(TemporarySqliteDatabase database, string rootDirectory,
            DocumentInstance document, IReadOnlyList<Page> pages, FixedClock clock,
            DocumentTreeService trees, IOcrPresetService presets)
        {
            _database = database;
            _rootDirectory = rootDirectory;
            _clock = clock;
            Document = document;
            Pages = pages;
            _trees = trees;
            Presets = presets;
            SourceImagePath = Path.Combine(rootDirectory, "page.png");
            using SKBitmap bitmap = CornerBitmap();
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(SourceImagePath, png.ToArray());
        }

        public DocumentInstance Document { get; }
        public IReadOnlyList<Page> Pages { get; private set; }
        public IOcrPresetService Presets { get; }
        public string SourceImagePath { get; }

        public async Task<Page> SetUserRotationAsync(PageId pageId, int degrees)
        {
            Infrastructure.Layout.PageService pages = new(_database.ConnectionFactory, _clock);
            Result<Page> updated = await pages.UpdateUserRotationAsync(pageId, degrees);
            updated.IsSuccess.Should().BeTrue(updated.ErrorMessage);
            Pages = Pages.Select(page => page.PageId == pageId ? updated.Value : page).ToArray();
            return updated.Value;
        }

        public OcrRunEngine CreateEngine(IPageRenderService renders,
            IOcrAdapterRegistry? adapters = null, IOcrEngineRuntime? engineRuntime = null)
        {
            return new OcrRunEngine(_database.ConnectionFactory,
                new FixedClock(DateTimeOffset.Parse("2026-07-13T00:00:00Z")), new StubOcrEngine(),
                treeImporter: new OcrDocumentTreeImporter(_trees), adapterRegistry: adapters,
                pageRenderService: renders, trees: _trees, engineRuntime: engineRuntime);
        }

        public async Task<OcrPresetVersion> CreatePresetVersionAsync(string engineId)
        {
            OcrPreset preset = (await Presets.CreatePresetAsync("Rotation preset", null, engineId,
                "test-model", null, "{}", true)).Value;
            return (await Presets.GetCurrentVersionAsync(preset.PresetId)).Value;
        }

        public async Task<IReadOnlyList<DocumentBox>> WorkingBoxesAsync(OcrRun run)
        {
            OcrRunEngine engine = CreateEngine(new RotatingPageRenderService(SourceImagePath));
            Result<IReadOnlyList<OcrPageResult>> results =
                await engine.ListPageResultsAsync(run.OcrRunId);
            results.IsSuccess.Should().BeTrue(results.ErrorMessage);
            results.Value.Should().OnlyContain(result => result.State == OcrPageResultState.Succeeded);
            return (await _trees.ListBoxesAsync(results.Value[0].WorkingTreeRevisionId!.Value)).Value;
        }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory)
                .RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("OCR rotation");
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "OCR rotation")).Value;
            DocumentInstance document = (await new DocumentInstanceService(
                        database.ConnectionFactory, clock)
                    .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan))
                .Value;
            Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
            Page[] created = new Page[1];
            created[0] = (await pages.CreatePageAsync(document.DocumentInstanceId, 0,
                "1", null, null, 0, CoordinateBasis.NormalizedPage, null,
                null, "test", null)).Value;
            string root = Path.Combine(Path.GetTempPath(), $"ocr-rot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            return new Context(database, root, document, created, clock, trees,
                new OcrPresetService(database.ConnectionFactory, libraries, clock));
        }

        public async ValueTask DisposeAsync()
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, true);
            }

            await _database.DisposeAsync();
        }
    }

    private sealed class StubOcrEngine : IOcrEngine
    {
        public string EngineId => "stub-engine";

        public Task<OcrEnginePageResult> RunPageAsync(Page page, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
