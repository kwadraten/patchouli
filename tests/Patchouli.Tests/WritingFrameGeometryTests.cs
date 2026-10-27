using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.PdfText;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

public class WritingFrameGeometryTests
{
    [Fact]
    public void RoundTrip_PreservesBBoxes()
    {
        NormalizedBBox box = new(0.25, 0.10, 0.30, 0.05);
        foreach (WritingDirection direction in Enum.GetValues<WritingDirection>())
        {
            if (direction == WritingDirection.Mixed)
            {
                continue;
            }

            WritingFrame frame = new(direction);
            NormalizedBBox roundTripped =
                WritingFrameGeometry.ToDisplayBBox(frame, WritingFrameGeometry.ToWritingRect(frame, box));
            Assert.Equal(box.X, roundTripped.X, 6);
            Assert.Equal(box.Y, roundTripped.Y, 6);
            Assert.Equal(box.Width, roundTripped.Width, 6);
            Assert.Equal(box.Height, roundTripped.Height, 6);
        }
    }

    [Fact]
    public void HorizontalFrame_LargerDisplayY_IsLaterBlock()
    {
        WritingRect lower = WritingFrameGeometry.ToWritingRect(WritingFrame.HorizontalLtr,
            new NormalizedBBox(0.1, 0.9, 0.1, 0.05));
        WritingRect upper = WritingFrameGeometry.ToWritingRect(WritingFrame.HorizontalLtr,
            new NormalizedBBox(0.1, 0.1, 0.1, 0.05));
        Assert.True(lower.MinV > upper.MinV);
    }

    [Fact]
    public void RtlFrame_FlipsInlineAxisWithoutReversingOrder()
    {
        // Rightmost glyph gets the smallest U — reading order is ascending U.
        WritingRect right = WritingFrameGeometry.ToWritingRect(WritingFrame.HorizontalRtl,
            new NormalizedBBox(0.9, 0.2, 0.02, 0.02));
        WritingRect left = WritingFrameGeometry.ToWritingRect(WritingFrame.HorizontalRtl,
            new NormalizedBBox(0.1, 0.2, 0.02, 0.02));
        Assert.True(right.MinU < left.MinU);
    }

    [Fact]
    public void VerticalRlFrame_XBecomesBlockAxis()
    {
        WritingRect rightColumn = WritingFrameGeometry.ToWritingRect(WritingFrame.VerticalRl,
            new NormalizedBBox(0.7, 0.1, 0.2, 0.5));
        WritingRect leftColumn = WritingFrameGeometry.ToWritingRect(WritingFrame.VerticalRl,
            new NormalizedBBox(0.1, 0.1, 0.2, 0.5));
        // The right column comes earlier in block direction for vertical-rl.
        Assert.True(rightColumn.MinV < leftColumn.MinV);
    }

    [Fact]
    public void DetectDominantFrame_HorizontalVsVertical()
    {
        List<PdfTextGlyph> horizontal =
            [..TextLine(0, "abcd", 0.1, 0.1), ..TextLine(4, "efgh", 0.1, 0.5)];
        Assert.Equal(WritingDirection.HorizontalLtr,
            WritingFrameGeometry.DetectDominantFrame(horizontal).Direction);

        List<PdfTextGlyph> vertical =
            [..VerticalLine(0, "東京都", 0.9, 0.1), ..VerticalLine(3, "大阪府", 0.7, 0.1)];
        Assert.Equal(WritingDirection.VerticalRl,
            WritingFrameGeometry.DetectDominantFrame(vertical).Direction);
    }

    [Fact]
    public void DetectDominantFrame_RtlScriptWins()
    {
        IReadOnlyList<PdfTextGlyph> glyphs =
        [
            Glyph(0, "ש", 0.9, 0.2, 0.01, 0.02), Glyph(1, "ל", 0.88, 0.2, 0.01, 0.02),
            Glyph(2, "ם", 0.86, 0.2, 0.01, 0.02)
        ];

        Assert.Equal(WritingDirection.HorizontalRtl,
            WritingFrameGeometry.DetectDominantFrame(glyphs).Direction);
    }

    [Fact]
    public void DetectLocalFrame_VerticalGlyphMajority_Wins()
    {
        IReadOnlyList<PdfTextGlyph> glyphs =
        [
            Glyph(0, "日", 0.9, 0.2, 0.02, 0.02, 90),
            Glyph(1, "本", 0.9, 0.4, 0.02, 0.02, 90),
            Glyph(2, "x", 0.9, 0.6, 0.02, 0.02)
        ];

        Assert.True(WritingFrameGeometry.DetectLocalFrame(glyphs).Direction is
            WritingDirection.VerticalRl or WritingDirection.VerticalLr);
    }

    [Fact]
    public void DetectLocalFrame_RtlBeatsLtr()
    {
        IReadOnlyList<PdfTextGlyph> glyphs =
        [
            Glyph(0, "ש", 0.90, 0.2, 0.01, 0.02), Glyph(1, "ל", 0.88, 0.2, 0.01, 0.02),
            Glyph(2, "ם", 0.86, 0.2, 0.01, 0.02), Glyph(3, " ", 0.84, 0.2, 0.01, 0.02),
            Glyph(4, "x", 0.82, 0.2, 0.01, 0.02)
        ];

        Assert.Equal(WritingDirection.HorizontalRtl,
            WritingFrameGeometry.DetectLocalFrame(glyphs).Direction);
    }

    [Fact]
    public void DetectDominantFrame_MongolianRange_IsVerticalLr()
    {
        // U+1820 ᠠ is Mongolian — the real range is U+1800–18AF, not U+0800–18AF.
        List<PdfTextGlyph> glyphs =
        [
            Glyph(0, "ᠠ", 0.30, 0.10, 0.02, 0.02, 90),
            Glyph(1, "ᠪ", 0.30, 0.14, 0.02, 0.02, 90),
            Glyph(2, "ᠴ", 0.30, 0.18, 0.02, 0.02, 90)
        ];

        Assert.Equal(WritingDirection.VerticalLr,
            WritingFrameGeometry.DetectDominantFrame(glyphs).Direction);
    }

    [Fact]
    public void DetectDominantFrame_SupplementaryRtlCodePoint_Classifies()
    {
        // U+10800 (Cypriot syllabary) is supplementary-plane RTL — a char-level check
        // cannot see it because it does not fit in 16 bits.
        IReadOnlyList<PdfTextGlyph> glyphs =
        [
            Glyph(0, "𐠀", 0.90, 0.2, 0.02, 0.02), Glyph(1, "𐠁", 0.86, 0.2, 0.02, 0.02),
            Glyph(2, "𐠂", 0.82, 0.2, 0.02, 0.02)
        ];

        Assert.Equal(WritingDirection.HorizontalRtl,
            WritingFrameGeometry.DetectDominantFrame(glyphs).Direction);
    }

    [Fact]
    public void DetectDominantFrame_UprightVerticalGlyphs_UsesPositionsNotAngles()
    {
        // Angle-0 glyphs that stack top→bottom: vertical CJK with unrotated metrics.
        List<PdfTextGlyph> glyphs = [..UprightVerticalLine(0, "日本語東", 0.5, 0.1)];

        Assert.Equal(WritingDirection.VerticalRl,
            WritingFrameGeometry.DetectDominantFrame(glyphs).Direction);
    }

    [Fact]
    public void DetectLocalFrame_AngleWraparound_DoesNotCancel()
    {
        // 359° and 1° average to ~0°/360° via the circular mean — not 180°.
        IReadOnlyList<PdfTextGlyph> glyphs =
        [
            Glyph(0, "a", 0.1, 0.2, 0.01, 0.02, 359),
            Glyph(1, "b", 0.12, 0.2, 0.01, 0.02, 1)
        ];

        WritingFrame frame = WritingFrameGeometry.DetectLocalFrame(glyphs);
        Assert.Equal(0, frame.RotationDegrees, 1);
    }

    [Fact]
    public void ToWritingRect_RotatedFrame_OnNonSquarePage_PreservesCenter()
    {
        // Aspect-corrected rotation about the page center on a 612×792 page: the projected
        // rect is a superset of the true rotated box, but its center maps back exactly —
        // a skewed transform would shift it.
        const double aspect = 612.0 / 792.0;
        WritingFrame frame = new(WritingDirection.HorizontalLtr, 12);
        NormalizedBBox box = new(0.30, 0.40, 0.20, 0.05);
        WritingPoint expected = WritingFrameGeometry.ToWriting(frame,
            box.X + box.Width / 2, box.Y + box.Height / 2, aspect);
        NormalizedBBox roundTripped = WritingFrameGeometry.ToDisplayBBox(frame,
            WritingFrameGeometry.ToWritingRect(frame, box, aspect), aspect);
        Assert.Equal(box.X + box.Width / 2, roundTripped.X + roundTripped.Width / 2, 6);
        Assert.Equal(box.Y + box.Height / 2, roundTripped.Y + roundTripped.Height / 2, 6);

        // Point roundtrip is exact for any aspect — ToDisplay inverts ToWriting.
        (double rx, double ry) = WritingFrameGeometry.ToDisplay(frame, expected.U, expected.V,
            aspect);
        Assert.Equal(box.X + box.Width / 2, rx, 6);
        Assert.Equal(box.Y + box.Height / 2, ry, 6);
    }

    [Fact]
    public void DirectionClassOf_DigitsAreLtr_HebrewIsRtl()
    {
        Assert.Equal(PdfDirectionClass.Ltr,
            WritingFrameGeometry.DirectionClassOf(Glyph(0, "5", 0.1, 0.1, 0.01, 0.02)));
        Assert.Equal(PdfDirectionClass.Rtl,
            WritingFrameGeometry.DirectionClassOf(Glyph(0, "ש", 0.1, 0.1, 0.01, 0.02)));
        Assert.Equal(PdfDirectionClass.Neutral,
            WritingFrameGeometry.DirectionClassOf(Glyph(0, " ", 0.1, 0.1, 0.01, 0.02)));
    }
}
