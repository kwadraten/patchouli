using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.PdfText;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

public class PdfTextLayerXyCutTests
{
    private static Task<Result<PdfTextLayerResult>> Analyze(
        IReadOnlyList<PdfTextGlyph> glyphs, PdfTextLayerParseOptions? options = null)
    {
        return new XyCutTextLayerAnalyzer().AnalyzeAsync(Context(glyphs),
            options ?? Options(), CancellationToken.None);
    }

    [Fact]
    public async Task TwoColumnPage_ReadsLeftColumnBeforeRight()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "left", 0.05, 0.20),
            ..TextLine(4, "left", 0.05, 0.24),
            ..TextLine(8, "rite", 0.55, 0.20),
            ..TextLine(12, "rite", 0.55, 0.24)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Boxes.Count);
        Assert.Equal(new[] { "left left", "rite rite" },
            result.Value.Boxes.Select(b => TextOf(b)!.Replace('\n', ' ')).ToArray());
        // Independent of the joined text: each box must group the two lines of ONE column,
        // keep source order, and cover exactly that column's native anchors.
        PdfTextLayerBox left = result.Value.Boxes[0];
        PdfTextLayerBox right = result.Value.Boxes[1];
        Assert.Equal(new[] { 0, 1 }, result.Value.Boxes.Select(b => b.Box.SourceOrder).ToArray());
        Assert.True(left.Box.BBox.X + left.Box.BBox.Width < right.Box.BBox.X,
            "left column box must lie entirely left of the right column box");
        Assert.Equal(Enumerable.Range(0, 8).ToArray(), AnchorIndices(left));
        Assert.Equal(Enumerable.Range(8, 8).ToArray(), AnchorIndices(right));
    }

    [Fact]
    public async Task ParagraphSplit_ProducesBlocksInBlockOrder()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "one", 0.05, 0.10),
            ..TextLine(3, "one", 0.05, 0.14),
            ..TextLine(6, "two", 0.05, 0.50),
            ..TextLine(9, "two", 0.05, 0.54)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Boxes.Count);
        Assert.Equal("one\none", TextOf(result.Value.Boxes[0]));
        Assert.Equal("two\ntwo", TextOf(result.Value.Boxes[1]));
        Assert.Equal(new[] { 0, 1 }, result.Value.Boxes.Select(b => b.Box.SourceOrder).ToArray());
        // Independent of text: the split must fall on the real paragraph break — the two
        // boxes are vertically disjoint and cover their own anchor runs exactly once.
        Assert.Equal(Enumerable.Range(0, 6).ToArray(), AnchorIndices(result.Value.Boxes[0]));
        Assert.Equal(Enumerable.Range(6, 6).ToArray(), AnchorIndices(result.Value.Boxes[1]));
        Assert.True(
            result.Value.Boxes[0].Box.BBox.Y + result.Value.Boxes[0].Box.BBox.Height
            <= result.Value.Boxes[1].Box.BBox.Y + 1e-6,
            "paragraph boxes must be vertically disjoint and ordered top to bottom");
    }

    [Fact]
    public async Task RtlGlyphs_KeepNativeCharacterOrder_AndAnchors()
    {
        // Hebrew line: index 0 is the rightmost glyph (logical-first character).
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "ש", 0.94, 0.20, 0.012, 0.02),
            Glyph(1, "ל", 0.92, 0.20, 0.012, 0.02),
            Glyph(2, "ו", 0.90, 0.20, 0.012, 0.02),
            Glyph(3, "ם", 0.88, 0.20, 0.012, 0.02)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal("שלום", TextOf(box));
        Assert.Equal(new[] { 0, 1, 2, 3 }, AnchorIndices(box));
        Assert.Contains(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.WritingFrameDetected &&
                 d.Message.Contains("Rtl"));
    }

    [Fact]
    public async Task VerticalCjkPage_UsesVerticalFrame()
    {
        List<PdfTextGlyph> glyphs = [..VerticalLine(0, "東京都", 0.90, 0.10)];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        Assert.Equal("東京都", TextOf(Assert.Single(result.Value.Boxes)));
        Assert.Contains(result.Value.Diagnostics,
            d => d.Code == PdfTextLayerDiagnosticCodes.WritingFrameDetected &&
                 d.Message.Contains("Vertical"));
    }

    [Fact]
    public async Task MissingNativeText_IsExplicitFailure_NotWholePageOcr()
    {
        Result<PdfTextLayerResult> result = await Analyze([]);

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.NativeTextMissing, result.ErrorCode);
    }

    [Fact]
    public async Task UnusableGlyphLayer_IsExplicitFailure()
    {
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "", 0.1, 0.2, 0.01, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(1, "�", 0.2, 0.2, 0.01, 0.02, flags: PdfGlyphFlags.UnicodeMapError)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.NativeTextMissing, result.ErrorCode);
    }

    [Fact]
    public async Task UntrustedGlyphs_DefaultIsExplicitFailure_NotSilentPartialContent()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "good", 0.05, 0.10),
            Glyph(4, "", 0.05, 0.60, 0.012, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(5, "", 0.065, 0.60, 0.012, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.NativeTextUnusable, result.ErrorCode);
    }

    [Fact]
    public async Task UntrustedGlyphs_WithSupplement_SpawnBoundedRequiredRegions()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "good", 0.05, 0.10),
            Glyph(4, "", 0.05, 0.60, 0.012, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(5, "", 0.065, 0.60, 0.012, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs, Options(supplement: true));

        Assert.True(result.IsSuccess);
        PdfTextRequiredRegion region = Assert.Single(result.Value.RequiredOcrRegions);
        Assert.Equal(PdfTextOcrReasons.UntrustworthyText, region.Reason);
        Assert.InRange(region.Region.Y, 0.55, 0.65);
        Assert.Equal(new[] { 4, 5 }, AnchorIndices(region));
        Assert.Single(result.Value.Boxes);
    }

    [Fact]
    public async Task UnicodeMapErrorGlyphs_WithSupplement_RequestOcrWithMapErrorReason()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "ok", 0.05, 0.10),
            Glyph(2, "x", 0.05, 0.60, 0.012, 0.02, flags: PdfGlyphFlags.UnicodeMapError)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs, Options(supplement: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(PdfTextOcrReasons.UnicodeMapError,
            Assert.Single(result.Value.RequiredOcrRegions).Reason);
    }

    [Fact]
    public async Task MostlyUntrustedPage_StaysExplicitFailure_EvenWithSupplement()
    {
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "a", 0.05, 0.10, 0.012, 0.02),
            Glyph(1, "", 0.1, 0.10, 0.012, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(2, "", 0.2, 0.10, 0.012, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping),
            Glyph(3, "", 0.3, 0.10, 0.012, 0.02, flags: PdfGlyphFlags.NoUnicodeMapping)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs, Options(supplement: true));

        Assert.True(result.IsFailure);
        Assert.Equal(PdfTextLayerDiagnosticCodes.NativeTextUnusable, result.ErrorCode);
    }

    [Fact]
    public async Task RtlLineWithDigits_KeepsLogicalRunOrder_NotGlyphReversed()
    {
        // Logical order "אבג 123" stored right-to-left for Hebrew, left-to-right for digits:
        // geometric sorting alone would corrupt the digit run into "321".
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "א", 0.94, 0.20, 0.012, 0.02),
            Glyph(1, "ב", 0.92, 0.20, 0.012, 0.02),
            Glyph(2, "ג", 0.90, 0.20, 0.012, 0.02),
            Glyph(3, " ", 0.88, 0.20, 0.012, 0.02),
            Glyph(4, "1", 0.80, 0.20, 0.012, 0.02),
            Glyph(5, "2", 0.82, 0.20, 0.012, 0.02),
            Glyph(6, "3", 0.84, 0.20, 0.012, 0.02)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal("אבג 123", TextOf(box));
        Assert.Equal(Enumerable.Range(0, 7).ToArray(), AnchorIndices(box));
    }

    [Fact]
    public async Task UprightVerticalRun_BandsInVerticalFrame_NotHorizontalFragments()
    {
        // A long upright-glyph Japanese column (angle 0) crossing two horizontal lines: the
        // column's glyphs stack top→bottom and must form one vertical line. Under the old
        // global V-band sweep it merged both horizontal rows into one band.
        const string columnText = "日本語のたてがきでかいてみましたよ";
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "ab", 0.10, 0.20),
            ..TextLine(2, "cd", 0.10, 0.50),
            ..UprightVerticalLine(4, columnText, 0.50, 0.18)
        ];

        Result<PdfTextLayerResult> result = await Analyze(glyphs);

        Assert.True(result.IsSuccess);
        // The unrelated horizontal lines stayed separate, and the column stayed whole.
        Assert.DoesNotContain(result.Value.Boxes,
            b => TextOf(b)!.Contains("ab") && TextOf(b)!.Contains("cd"));
        PdfTextLayerBox column = Assert.Single(
            result.Value.Boxes.Where(b => TextOf(b) == columnText).ToList());
        // Independent of text: the column's box is tall and narrow, placed at the column's
        // x position, and covers exactly its native anchors once.
        Assert.True(column.Box.BBox.Height > column.Box.BBox.Width * 3,
            "an upright vertical column must produce a tall, narrow box");
        Assert.Equal(Enumerable.Range(4, columnText.Length).ToArray(), AnchorIndices(column));
        Assert.Contains(result.Value.Boxes, b => TextOf(b) == "ab");
        Assert.Contains(result.Value.Boxes, b => TextOf(b) == "cd");
    }

    [Fact]
    public async Task ContextRegion_FiltersGlyphScope()
    {
        List<PdfTextGlyph> glyphs =
        [
            ..TextLine(0, "in", 0.05, 0.10),
            ..TextLine(2, "out", 0.80, 0.80)
        ];

        Result<PdfTextLayerResult> result = await new XyCutTextLayerAnalyzer().AnalyzeAsync(
            Context(glyphs, new NormalizedBBox(0.0, 0.0, 0.5, 0.5)),
            Options(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal("in", TextOf(box));
        Assert.Equal(new[] { 0, 1 }, AnchorIndices(box));
        Assert.Empty(result.Value.RequiredOcrRegions);
    }

    [Fact]
    public async Task SeedsCarryTypedPayloadsAndSourceOrder()
    {
        Result<PdfTextLayerResult> result = await Analyze([..TextLine(0, "abc def", 0.05, 0.10)]);

        Assert.True(result.IsSuccess);
        PdfTextLayerBox box = Assert.Single(result.Value.Boxes);
        Assert.Equal(DocumentBoxType.Text, box.Box.BoxType);
        Assert.IsType<TextBoxPayload>(box.Box.Payload);
        Assert.Equal("abc def", TextOf(box));
        Assert.Equal(Enumerable.Range(0, 7).ToArray(), AnchorIndices(box));
        Assert.True(box.Box.BBox is { X: > 0, Width: > 0 });
    }
}
