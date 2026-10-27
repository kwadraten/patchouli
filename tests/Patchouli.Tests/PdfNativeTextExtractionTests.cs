using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.PdfText;
using Patchouli.Ocr;
using static Patchouli.Tests.PdfTextLayerTestSupport;

namespace Patchouli.Tests;

/// <summary>
/// Runs real PDFium text extraction against the committed three-page PDFium fixture.
/// No rendering, layout models or credentials are involved: the test only exercises the
/// fpdf_text bindings through the existing PDFium scheduler.
/// </summary>
public class PdfNativeTextExtractionTests
{
    private static readonly string RealThreePagePdf = TestFixtures.RealThreePagePdf;

    [Fact]
    public async Task ExtractTextPage_ReturnsGlyphsWithGeometryFontsAndAnchors()
    {
        PdfiumTextPage page = await new PdfiumDocumentEngine()
            .ExtractTextPageAsync(RealThreePagePdf, 0);

        Assert.True(page.RotationDegrees is 0 or 90 or 180 or 270);
        Assert.True(page.MediaBox.Right > page.MediaBox.Left && page.MediaBox.Top > page.MediaBox.Bottom);
        Assert.False(page.EffectiveBox.IsDegenerate);
        Assert.True(page.EffectiveBox.Left >= page.MediaBox.Left - 0.01 &&
                    page.EffectiveBox.Right <= page.MediaBox.Right + 0.01);
        Assert.True(page.PageWidthPoints > 0 && page.PageHeightPoints > 0);
        Assert.NotEmpty(page.Glyphs);
        Assert.Equal(page.Glyphs.Count, page.Glyphs.Select(g => g.Index).Distinct().Count());
        // Native indices are dense in [0, CharCount) — the source anchor domain.
        Assert.Equal(Enumerable.Range(0, page.CharCount).ToArray(),
            page.Glyphs.Select(g => g.Index).OrderBy(i => i).ToArray());
        Assert.Contains(page.Glyphs, g => !string.IsNullOrWhiteSpace(g.Text));
        Assert.Contains(page.Glyphs, g => g.Right > g.Left && g.Top > g.Bottom);
        Assert.Contains(page.Glyphs, g => g.FontSize > 0);
    }

    [Fact]
    public async Task ExtractTextPage_OutOfRange_ThrowsArgument()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PdfiumDocumentEngine().ExtractTextPageAsync(RealThreePagePdf, 99));
    }

    [Fact]
    public async Task SessionOverload_UsesOwnedDocument()
    {
        PdfiumDocumentEngine engine = new();
        PdfiumDocumentSession session = await engine.OpenSessionAsync(RealThreePagePdf);
        try
        {
            PdfiumTextPage page = await session.ExtractTextPageAsync(0);
            Assert.Equal(page.Glyphs.Count,
                (await engine.ExtractTextPageAsync(RealThreePagePdf, 0)).Glyphs.Count);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public void ToDisplayGlyph_Rotation0_MapsPdfSpaceToDisplaySpace()
    {
        // 10×10 page; glyph occupies PDF user-space [2,4]×[1,3] → display x .2, y .7
        // (top-left origin).
        PdfiumTextPage page = RawPage([RawGlyph(0, "A", 2, 4, 1, 3, 2, 1)], 0, 0, 10, 10);
        PdfTextGlyph g = PdfTextCoordinates.ToDisplayGlyph(page, page.Glyphs[0]);
        Assert.Equal(0.2, g.BBox.X, 6);
        Assert.Equal(0.7, g.BBox.Y, 6);
        Assert.Equal(0.2, g.BBox.Width, 6);
        Assert.Equal(0.2, g.BBox.Height, 6);
        Assert.Equal(0, g.NativeIndex);
        Assert.Equal("A", g.Text);
    }

    [Fact]
    public void ToDisplayGlyph_Rotation90_RotatesIntoDisplaySpace()
    {
        PdfiumTextPage page = RawPage([RawGlyph(0, "B", 2, 4, 1, 3, 2, 1)], 0, 0, 10, 10, 90);
        PdfTextGlyph g = PdfTextCoordinates.ToDisplayGlyph(page, page.Glyphs[0]);
        // 90° rotation maps the user box [2,4]×[1,3] to display [.1,.3]×[.2,.4].
        Assert.Equal(0.1, g.BBox.X, 6);
        Assert.Equal(0.2, g.BBox.Y, 6);
        Assert.Equal(0.2, g.BBox.Width, 6);
        Assert.Equal(0.2, g.BBox.Height, 6);
    }

    [Fact]
    public void ToDisplayGlyph_MapsFlagsOrientationAndAnchor()
    {
        PdfiumTextPage page = RawPage([
            RawGlyph(7, "x", 1, 2, 1, 2, 1, 1,
                PdfiumTextGlyphFlags.Generated | PdfiumTextGlyphFlags.NoUnicodeMapping)
        ], 0, 0, 10, 10);

        PdfTextGlyph g = PdfTextCoordinates.ToDisplayGlyph(page, page.Glyphs[0]);
        Assert.Equal(7, g.NativeIndex);
        Assert.True(g.Flags.HasFlag(PdfGlyphFlags.Generated));
        Assert.True(g.Flags.HasFlag(PdfGlyphFlags.NoUnicodeMapping));
        Assert.Equal(PdfGlyphOrientation.Horizontal, g.Orientation);
    }

    [Fact]
    public void ToDisplayGlyph_DegenerateCharBox_StillClampsToPage()
    {
        PdfiumTextPage page = RawPage([RawGlyph(0, "?", 0, 0, 0, 0, 0, 0)], 0, 0, 10, 10);
        PdfTextGlyph g = PdfTextCoordinates.ToDisplayGlyph(page, page.Glyphs[0]);
        Assert.InRange(g.BBox.X, 0, 1);
        Assert.InRange(g.BBox.Y, 0, 1);
        Assert.True(g.BBox is { Width: > 0, Height: > 0 });
    }

    [Fact]
    public void ToDisplayGlyph_CropBox_NormalizesWithinEffectiveBox()
    {
        // Media 0..100 clipped to a 20-wide effective box at x 40..60: a glyph inside the
        // crop normalizes against the crop frame; a glyph fully outside it is dropped.
        PdfiumTextPage page = RawPage([
            RawGlyph(0, "A", 45, 55, 40, 60, 45, 40),
            RawGlyph(1, "x", 5, 15, 40, 60, 5, 40)
        ], 0, 0, 100, 100, bound: new PdfPageBox(40, 0, 60, 100));

        IReadOnlyList<PdfTextGlyph> glyphs = PdfTextCoordinates.ToDisplayGlyphs(page);
        Assert.Single(glyphs);
        Assert.Equal(0, glyphs[0].NativeIndex);
        Assert.Equal(0.25, glyphs[0].BBox.X, 6);
        Assert.Equal(0.5, glyphs[0].BBox.Width, 6);
    }

    [Fact]
    public void ToDisplayAngleDegrees_PageRotationAddsToGlyphAngle()
    {
        Assert.Equal(90, PdfTextCoordinates.ToDisplayAngleDegrees(90, 0));
        Assert.Equal(270, PdfTextCoordinates.ToDisplayAngleDegrees(0, Math.PI / 2));
    }
}
