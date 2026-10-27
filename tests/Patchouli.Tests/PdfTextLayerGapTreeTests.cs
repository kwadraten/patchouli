using Patchouli.Core.Documents;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.PdfText;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

public class PdfTextLayerGapTreeTests
{
    private static Task<Result<PdfTextLayerResult>> Analyze(
        IReadOnlyList<PdfTextGlyph> glyphs, PdfTextLayerParseOptions? options = null)
    {
        return new GapTreeTextLayerAnalyzer().AnalyzeAsync(Context(glyphs),
            options ?? Options(), CancellationToken.None);
    }

    [Fact]
    public async Task TwoColumnPage_ReadsLeftColumnBeforeRight()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "left", 0.05, 0.20),
            ..TextLine(4, "left", 0.05, 0.215),
            ..TextLine(8, "rite", 0.55, 0.20),
            ..TextLine(12, "rite", 0.55, 0.215)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "left\nleft", "rite\nrite" },
            result.Value.Boxes.Select(TextOf).ToArray());
        // Anchor coverage: every glyph accounted for exactly once, in source order, and the
        // two column boxes are horizontally disjoint.
        Assert.Equal(new[] { 0, 1 }, result.Value.Boxes.Select(b => b.Box.SourceOrder).ToArray());
        Assert.Equal(Enumerable.Range(0, 8).ToArray(), AnchorIndices(result.Value.Boxes[0]));
        Assert.Equal(Enumerable.Range(8, 8).ToArray(), AnchorIndices(result.Value.Boxes[1]));
        Assert.True(
            result.Value.Boxes[0].Box.BBox.X + result.Value.Boxes[0].Box.BBox.Width
            < result.Value.Boxes[1].Box.BBox.X,
            "left column box must lie entirely left of the right column box");
    }

    [Fact]
    public async Task SpanningTitle_PrecedesColumnBody()
    {
        // A full-width title row blocks the column cutline at its level — the case the
        // gap-tree algorithm exists for: the title's row has no column gap, so the title
        // stays a parent node above the two column nodes.
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "TITLE", 0.10, 0.05, advance: 0.10),
            ..TextLine(5, "aaaa", 0.05, 0.20),
            ..TextLine(9, "bbbb", 0.55, 0.20)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        Assert.Equal("TITLE", TextOf(result.Value.Boxes[0]));
        Assert.Equal("aaaa", TextOf(result.Value.Boxes[1]));
        Assert.Equal("bbbb", TextOf(result.Value.Boxes[2]));
        // Anchor coverage: the title keeps ALL five of its glyphs, and every supplied glyph
        // is accounted for exactly once across boxes — no drops, no duplicates.
        Assert.Equal(Enumerable.Range(0, 5).ToArray(), AnchorIndices(result.Value.Boxes[0]));
        Assert.Equal(Enumerable.Range(5, 4).ToArray(), AnchorIndices(result.Value.Boxes[1]));
        Assert.Equal(Enumerable.Range(9, 4).ToArray(), AnchorIndices(result.Value.Boxes[2]));
        PdfTextLayerStatistics? stats = result.Value.Statistics;
        Assert.NotNull(stats);
        Assert.Equal(13, stats.SuppliedGlyphCount);
        Assert.Equal(13, stats.AccountedGlyphCount);
        // The title sits above the body rows and spans across the column gap: its right
        // edge reaches past the left column's right edge toward the right column.
        PdfTextLayerBox title = result.Value.Boxes[0];
        PdfTextLayerBox leftColumn = result.Value.Boxes[1];
        Assert.True(
            title.Box.BBox.Y + title.Box.BBox.Height <= leftColumn.Box.BBox.Y + 1e-6,
            "the title box must sit above the column body");
        Assert.True(
            title.Box.BBox.X + title.Box.BBox.Width > leftColumn.Box.BBox.X + leftColumn.Box.BBox.Width,
            "the title must extend past the left column's right edge toward the right column");
    }

    [Fact]
    public async Task CutlineDiagnostics_ExposeRowSpans()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "left", 0.05, 0.20),
            ..TextLine(4, "rite", 0.55, 0.20)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.CutLine &&
                 d.Message.Contains("vertical cutline"));
    }

    [Fact]
    public async Task RtlLine_KeepsNativeOrder()
    {
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "ש", 0.94, 0.20, 0.012, 0.02),
            Glyph(1, "ל", 0.92, 0.20, 0.012, 0.02),
            Glyph(2, "ו", 0.90, 0.20, 0.012, 0.02)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal("שלו", TextOf(box));
        Assert.Equal(new[] { 0, 1, 2 }, AnchorIndices(box));
    }

    [Fact]
    public async Task MissingNativeText_IsExplicitFailure()
    {
        Result<PdfTextLayerResult> result = await Analyze([]);

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.NativeTextMissing, result.ErrorCode);
    }

    [Fact]
    public async Task SeedsCarryTypedPayloads()
    {
        Result<PdfTextLayerResult> result = await Analyze([..TextLine(0, "x", 0.05, 0.10)]);

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal(DocumentBoxType.Text, box.Box.BoxType);
        Assert.IsType<TextBoxPayload>(box.Box.Payload);
        Assert.Equal(new[] { 0 }, AnchorIndices(box));
    }
}
