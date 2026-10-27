using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.PdfText;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

public class PdfTextLayerVisualGuidedTests
{
    private static PdfTextLayerContext VisualContext(
        IReadOnlyList<PdfTextGlyph> glyphs,
        IReadOnlyList<PdfTextLayoutRegion> regions)
    {
        return Context(glyphs,
            readPageImage: (_, _) => ValueTask.FromResult(StubImage()),
            detectLayout: (_, _, _, _) => ValueTask.FromResult(
                Result<IReadOnlyList<PdfTextLayoutRegion>>.Success(regions)));
    }

    private static Task<Result<PdfTextLayerResult>> Analyze(
        PdfTextLayerContext context, PdfTextLayerParseOptions? options = null)
    {
        return new VisualGuidedTextLayerAnalyzer().AnalyzeAsync(context,
            options ?? Options(minCoverage: 0.01), CancellationToken.None);
    }

    [Fact]
    public async Task NativeTextInsideTextRegion_IsPreservedNotOcrRequested()
    {
        List<PdfTextGlyph> glyphs = [..TextLine(0, "native", 0.05, 0.10)];
        NormalizedBBox region = new(0.0, 0.05, 0.40, 0.12);

        Result<PdfTextLayerResult> result = await Analyze(VisualContext(glyphs, [Region(region)]));

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal("native", TextOf(box));
        Assert.Empty(result.Value.RequiredOcrRegions);
    }

    [Fact]
    public async Task EmptyTextRegion_RequestsRegionOcr_Explicitly()
    {
        NormalizedBBox region = new(0.0, 0.05, 0.40, 0.12);

        Result<PdfTextLayerResult> result = await Analyze(VisualContext([], [Region(region)]));

        Assert.True(result.IsSuccess);
        PdfTextRequiredRegion requested = Assert.Single(result.Value.RequiredOcrRegions);
        Assert.Equal(PdfTextOcrReasons.MissingText, requested.Reason);
        Assert.Equal(region, requested.Region);
        Assert.Empty(result.Value.Boxes);
    }

    [Fact]
    public async Task NonTextRegion_RequestsOcr_WhenConfigured()
    {
        NormalizedBBox figure = new(0.1, 0.1, 0.3, 0.3);

        Result<PdfTextLayerResult> result =
            await Analyze(VisualContext([], [Region(figure, "figure")]));

        Assert.True(result.IsSuccess);
        Assert.Equal(PdfTextOcrReasons.NonTextualContent,
            Assert.Single(result.Value.RequiredOcrRegions).Reason);
    }

    [Fact]
    public async Task UntrustedGlyphsInsideRegion_ShrinkRequestToTightArea()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "text", 0.10, 0.10),
            Glyph(4, "", 0.30, 0.30, 0.02, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(5, "", 0.34, 0.30, 0.02, 0.02, flags: PdfGlyphFlags.UnicodeMapError)
        ];
        NormalizedBBox region = new(0.0, 0.0, 0.9, 0.9);

        Result<PdfTextLayerResult> result = await Analyze(VisualContext(glyphs, [Region(region)]),
            Options(minCoverage: 0));

        Assert.True(result.IsSuccess);
        PdfTextRequiredRegion requested = Assert.Single(result.Value.RequiredOcrRegions);
        Assert.Equal(PdfTextOcrReasons.UnicodeMapError, requested.Reason);
        // The request is tight around the bad glyphs, not the whole region.
        Assert.True(requested.Region.Width < 0.2);
        Assert.True(requested.Region.Height < 0.2);
        Assert.Equal(new[] { 4, 5 }, AnchorIndices(requested));
        // The trusted glyphs in the same region stayed native.
        Assert.Single(result.Value.Boxes);
    }

    [Fact]
    public async Task TrustedTextOutsideRegions_IsNotDropped()
    {
        List<PdfTextGlyph> glyphs = [..TextLine(0, "stray", 0.80, 0.80)];
        NormalizedBBox region = new(0.0, 0.0, 0.30, 0.10);

        Result<PdfTextLayerResult> result = await Analyze(VisualContext(glyphs, [Region(region)]));

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.Boxes, b => TextOf(b) == "stray");
        Assert.Contains(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.GlyphsOutsideLayout);
    }

    [Fact]
    public async Task MissingPageImageDelegate_IsExplicitFailure()
    {
        Result<PdfTextLayerResult> result =
            await Analyze(Context([..TextLine(0, "x", 0.1, 0.1)]));

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.PageImageUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task MissingLayoutDetector_IsExplicitFailure()
    {
        Result<PdfTextLayerResult> result = await Analyze(Context([..TextLine(0, "x", 0.1, 0.1)],
            readPageImage: (_, _) => ValueTask.FromResult(StubImage())));

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.LayoutDetectorUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task MissingModelId_IsExplicitFailure()
    {
        Result<PdfTextLayerResult> result =
            await Analyze(VisualContext([], []), Options(modelId: null));

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.LayoutModelMissing, result.ErrorCode);
    }

    [Fact]
    public async Task DetectorFailure_PropagatesAsExplicitFailure()
    {
        PdfTextLayerContext context = Context([..TextLine(0, "x", 0.1, 0.1)],
            readPageImage: (_, _) => ValueTask.FromResult(StubImage()),
            detectLayout: (_, _, _, _) => ValueTask.FromResult(
                Result<IReadOnlyList<PdfTextLayoutRegion>>.Failure(
                    PdfTextLayerDiagnosticCodes.LayoutDetectionFailed, "detector exploded")));

        Result<PdfTextLayerResult> result = await Analyze(context);

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.LayoutDetectionFailed, result.ErrorCode);
        Assert.Contains("detector exploded", result.ErrorMessage);
    }

    [Fact]
    public async Task Statistics_AccountEverySuppliedGlyph_Once()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "in", 0.05, 0.05),
            ..TextLine(2, "out", 0.85, 0.85)
        ];

        Result<PdfTextLayerResult> result = await Analyze(
            VisualContext(glyphs, [Region(new NormalizedBBox(0.0, 0.0, 0.4, 0.2))]));

        Assert.True(result.IsSuccess);
        PdfTextLayerStatistics? stats = result.Value.Statistics;
        Assert.NotNull(stats);
        Assert.Equal(5, stats.SuppliedGlyphCount);
        Assert.Equal(5, stats.AccountedGlyphCount);
        Assert.DoesNotContain(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.UnaccountedGlyphs);
    }

    [Fact]
    public async Task LowCoverageTextRegion_StillPreservesTrustedGlyphs()
    {
        // A region with sparse trusted text AND a damaged glyph: coverage stays a warning,
        // the trusted glyph stays native, and only the bad glyph is requested.
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "x", 0.05, 0.05, 0.012, 0.015),
            Glyph(1, "", 0.5, 0.5, 0.012, 0.015, flags: PdfGlyphFlags.NoUnicodeMapping)
        ];
        NormalizedBBox region = new(0.0, 0.0, 0.9, 0.9);

        Result<PdfTextLayerResult> result = await Analyze(VisualContext(glyphs, [Region(region)]));

        Assert.True(result.IsSuccess);
        Assert.Equal("x", TextOf(Assert.Single(result.Value.Boxes)));
        PdfTextRequiredRegion request = Assert.Single(result.Value.RequiredOcrRegions);
        Assert.Equal(new[] { 1 }, AnchorIndices(request));
        Assert.Contains(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.LowNativeCoverage);
    }

    [Fact]
    public async Task SparseCleanTextRegion_IsNotFlaggedLowCoverage()
    {
        // A single-word region with zero damaged glyphs is ordinary sparse text, not damage.
        List<PdfTextGlyph> glyphs = [Glyph(0, "x", 0.05, 0.05, 0.012, 0.015)];
        NormalizedBBox region = new(0.0, 0.0, 0.9, 0.9);

        Result<PdfTextLayerResult> result = await Analyze(VisualContext(glyphs, [Region(region)]));

        Assert.True(result.IsSuccess);
        Assert.Equal("x", TextOf(Assert.Single(result.Value.Boxes)));
        Assert.DoesNotContain(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.LowNativeCoverage);
    }

    [Fact]
    public async Task MostlyDamagedPage_IsExplicitFailure_NotRegionSpray()
    {
        // MaxUntrustedRatio: a page this broken fails explicitly rather than spraying
        // OCR requests over most of the page.
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "x", 0.05, 0.05, 0.012, 0.015),
            Glyph(1, "", 0.2, 0.2, 0.012, 0.015, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(2, "", 0.4, 0.4, 0.012, 0.015, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(3, "", 0.6, 0.6, 0.012, 0.015, flags: PdfGlyphFlags.NoUnicodeMapping)
        ];
        NormalizedBBox region = new(0.0, 0.0, 0.9, 0.9);

        Result<PdfTextLayerResult> result = await Analyze(VisualContext(glyphs, [Region(region)]));

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.NativeTextUnusable, result.ErrorCode);
    }

    [Fact]
    public async Task NonTextRegion_WhenRecognitionDisabled_PreservesNativeText()
    {
        // recognize_nontext off must not silently drop native glyphs inside a figure.
        List<PdfTextGlyph> glyphs = [..TextLine(0, "cap", 0.15, 0.15)];
        NormalizedBBox figure = new(0.1, 0.1, 0.3, 0.3);

        Result<PdfTextLayerResult> result = await Analyze(
            VisualContext(glyphs, [Region(figure, "figure")]),
            Options(minCoverage: 0, recognizeNontext: false));

        Assert.True(result.IsSuccess);
        Assert.Equal("cap", TextOf(Assert.Single(result.Value.Boxes)));
        Assert.Empty(result.Value.RequiredOcrRegions);
    }

    [Fact]
    public async Task CaptionLabel_IsTextual_NotImageOcr()
    {
        // figure_title is a caption — text regions, not atomic figure content. The caption
        // glyphs sit INSIDE the detected caption region (centre membership).
        List<PdfTextGlyph> glyphs = [..TextLine(0, "fig", 0.15, 0.12)];
        NormalizedBBox caption = new(0.1, 0.1, 0.3, 0.05);

        Result<PdfTextLayerResult> result = await Analyze(
            VisualContext(glyphs, [Region(caption, "figure_title")]));

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal("fig", TextOf(box));
        // The caption keeps its typed subtype rather than degrading to a generic text box.
        Assert.Equal(DocumentBoxType.ImageCaption, box.Box.BoxType);
        Assert.Equal(new[] { 0, 1, 2 }, AnchorIndices(box));
        Assert.Empty(result.Value.RequiredOcrRegions);
    }

    [Fact]
    public async Task InvalidRegion_IsSkippedWithDiagnostic()
    {
        List<PdfTextGlyph> glyphs = [..TextLine(0, "ok", 0.15, 0.15)];
        PdfTextLayoutRegion broken = new("text",
            new NormalizedBBox(0.1, 0.1, 0.0, 0.0), 0.9, 0);
        NormalizedBBox valid = new(0.0, 0.0, 0.5, 0.3);

        Result<PdfTextLayerResult> result = await Analyze(
            VisualContext(glyphs, [broken, Region(valid)]));

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.InvalidRegion);
        Assert.Equal("ok", TextOf(Assert.Single(result.Value.Boxes)));
    }

    [Fact]
    public async Task OverlappingRegions_TightestRegionOwnsTheGlyph()
    {
        // A glyph inside two overlapping regions belongs to the smaller one — the larger
        // region's own text keeps it from becoming a missing-text request.
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "in", 0.15, 0.15),
            ..TextLine(2, "big", 0.60, 0.60)
        ];
        PdfTextLayoutRegion big = new("text", new NormalizedBBox(0.0, 0.0, 0.9, 0.9), 0.9, 0);
        PdfTextLayoutRegion tight = new("text", new NormalizedBBox(0.1, 0.1, 0.3, 0.1), 0.9, 1);

        Result<PdfTextLayerResult> result = await Analyze(
            VisualContext(glyphs, [big, tight]));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.RequiredOcrRegions);
        Assert.Contains(result.Value.Boxes, b => TextOf(b) == "in");
        Assert.Contains(result.Value.Boxes, b => TextOf(b) == "big");
    }

    [Fact]
    public async Task ZeroDetectedRegions_PreservesOutsideGlyphs()
    {
        List<PdfTextGlyph> glyphs = [..TextLine(0, "all", 0.3, 0.3)];

        Result<PdfTextLayerResult> result = await Analyze(VisualContext(glyphs, []));

        Assert.True(result.IsSuccess);
        Assert.Equal("all", TextOf(Assert.Single(result.Value.Boxes)));
        Assert.Empty(result.Value.RequiredOcrRegions);
    }

    [Fact]
    public async Task UntrustedOutsideRegion_RequestsBoundedArea()
    {
        // One trusted line inside the detected text region keeps the page's untrusted
        // ratio below the failure threshold; the damaged glyph sits outside every region
        // and must get a bounded request in its own area, not a whole-page fallback.
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "ok", 0.05, 0.05),
            Glyph(2, "", 0.7, 0.7, 0.02, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping)
        ];

        Result<PdfTextLayerResult> result = await Analyze(
            VisualContext(glyphs, [Region(new NormalizedBBox(0.0, 0.0, 0.3, 0.3))]));

        Assert.True(result.IsSuccess);
        PdfTextRequiredRegion requested = Assert.Single(result.Value.RequiredOcrRegions);
        Assert.Equal(PdfTextOcrReasons.UntrustworthyText, requested.Reason);
        Assert.True(requested.Region.Width < 0.1);
        Assert.True(requested.Region.X >= 0.6,
            "the request must be scoped to the glyph's area outside the detected region");
        Assert.Equal(new[] { 2 }, AnchorIndices(requested));
        // The trusted glyphs are preserved exactly once as native text.
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal("ok", TextOf(box));
        Assert.Equal(new[] { 0, 1 }, AnchorIndices(box));
        Assert.Equal(3, result.Value.Statistics?.AccountedGlyphCount);
    }
}
