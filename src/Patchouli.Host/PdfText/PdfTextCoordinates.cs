using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr;

namespace Patchouli.Host.PdfText;

/// <summary>
/// Maps raw PDFium user-space glyph geometry onto the display page: the page's /Rotate value
/// is applied and the result is normalized to [0,1] with a top-left origin, matching
/// <see cref="CoordinateBasis.NormalizedPage"/>. The mapping is pure math over the effective
/// display box (crop ∩ media — the frame the renderer rasterizes) — no rendering — so it is
/// fully unit-testable with synthetic geometry.
/// </summary>
public static class PdfTextCoordinates
{
    /// <summary>Transforms a user-space point into normalized display-page coordinates.</summary>
    public static (double X, double Y) ToDisplayPoint(PdfiumTextPage page, double userX, double userY)
    {
        PdfPageBox bounds = page.EffectiveBox;
        double width = bounds.Width;
        double height = bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        // Un-rotated display space: origin at the effective box's top-left, y downwards.
        double x0 = userX - bounds.Left;
        double y0 = bounds.Top - userY;
        (double dx, double dy) = page.RotationDegrees switch
        {
            90 => (height - y0, x0),
            180 => (width - x0, height - y0),
            270 => (y0, width - x0),
            _ => (x0, y0)
        };
        double displayWidth = page.RotationDegrees is 90 or 270 ? height : width;
        double displayHeight = page.RotationDegrees is 90 or 270 ? width : height;
        return (dx / displayWidth, dy / displayHeight);
    }

    /// <summary>
    /// The display-space clockwise angle of a glyph's advance direction in degrees
    /// (0 = +x, 90 = +y). PDFium reports the user-space counter-clockwise advance angle in
    /// radians; the display transform y-flips it, then the page rotation is added.
    /// </summary>
    public static double ToDisplayAngleDegrees(int rotationDegrees, double userAngleRadians)
    {
        double degrees = rotationDegrees - userAngleRadians * 180.0 / Math.PI;
        degrees %= 360.0;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        return degrees;
    }

    /// <summary>Classifies a glyph's display-space advance angle for coarse direction tests.</summary>
    public static PdfGlyphOrientation ClassifyOrientation(double displayAngleDegrees)
    {
        double folded = displayAngleDegrees % 180.0;
        if (folded < 0)
        {
            folded += 180.0;
        }

        return folded is < 30.0 or >= 150.0
            ? PdfGlyphOrientation.Horizontal
            : folded is >= 60.0 and <= 120.0
                ? PdfGlyphOrientation.Vertical
                : PdfGlyphOrientation.Rotated;
    }

    /// <summary>
    /// Maps one raw glyph into the provider-neutral normalized form, preserving the native
    /// character index and the original Unicode text exactly.
    /// </summary>
    public static PdfTextGlyph ToDisplayGlyph(PdfiumTextPage page, PdfiumTextGlyph glyph)
    {
        // The char box is axis-aligned in un-rotated user space; after a 90-degree-step
        // rotation it stays axis-aligned in display space, so corner mapping is exact.
        (double x1, double y1) = ToDisplayPoint(page, glyph.Left, glyph.Top);
        (double x2, double y2) = ToDisplayPoint(page, glyph.Right, glyph.Bottom);
        (double x3, double y3) = ToDisplayPoint(page, glyph.Left, glyph.Bottom);
        (double x4, double y4) = ToDisplayPoint(page, glyph.Right, glyph.Top);
        double minX = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
        double maxX = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
        double minY = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
        double maxY = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
        minX = Math.Clamp(minX, 0, 1);
        minY = Math.Clamp(minY, 0, 1);
        maxX = Math.Clamp(maxX, 0, 1);
        maxY = Math.Clamp(maxY, 0, 1);
        NormalizedBBox bbox = new(minX, minY, Math.Max(maxX - minX, 1e-9),
            Math.Max(maxY - minY, 1e-9));
        (double baselineX, double baselineY) = ToDisplayPoint(page, glyph.OriginX, glyph.OriginY);
        double angle = ToDisplayAngleDegrees(page.RotationDegrees, glyph.AngleRadians);
        return new PdfTextGlyph(glyph.Index, glyph.Text, bbox, baselineX, baselineY, angle,
            ClassifyOrientation(angle), glyph.FontName, glyph.FontSize, MapFlags(glyph.Flags));
    }

    /// <summary>
    /// Maps a whole extracted page's glyph stream, preserving native index order. Glyphs whose
    /// char box lies entirely outside the effective display box are dropped: they cover the
    /// clipped-away part of the page no renderer or reader can see, and clamping them would
    /// collapse them into a degenerate sliver of noise at the page edge.
    /// </summary>
    public static IReadOnlyList<PdfTextGlyph> ToDisplayGlyphs(PdfiumTextPage page)
    {
        if (page.Glyphs.Count == 0)
        {
            return Array.Empty<PdfTextGlyph>();
        }

        PdfPageBox bounds = page.EffectiveBox;
        List<PdfTextGlyph> glyphs = new(page.Glyphs.Count);
        for (int i = 0; i < page.Glyphs.Count; i++)
        {
            PdfiumTextGlyph glyph = page.Glyphs[i];
            if (glyph.Right <= bounds.Left || glyph.Left >= bounds.Right ||
                glyph.Top <= bounds.Bottom || glyph.Bottom >= bounds.Top)
            {
                continue;
            }

            glyphs.Add(ToDisplayGlyph(page, glyph));
        }

        return glyphs;
    }

    private static PdfGlyphFlags MapFlags(PdfiumTextGlyphFlags flags)
    {
        PdfGlyphFlags mapped = PdfGlyphFlags.None;
        if (flags.HasFlag(PdfiumTextGlyphFlags.Generated))
        {
            mapped |= PdfGlyphFlags.Generated;
        }

        if (flags.HasFlag(PdfiumTextGlyphFlags.UnicodeMapError))
        {
            mapped |= PdfGlyphFlags.UnicodeMapError;
        }

        if (flags.HasFlag(PdfiumTextGlyphFlags.Hyphen))
        {
            mapped |= PdfGlyphFlags.Hyphen;
        }

        if (flags.HasFlag(PdfiumTextGlyphFlags.NoUnicodeMapping))
        {
            mapped |= PdfGlyphFlags.NoUnicodeMapping;
        }

        if (flags.HasFlag(PdfiumTextGlyphFlags.SurrogateContinuation))
        {
            mapped |= PdfGlyphFlags.SurrogateContinuation;
        }

        return mapped;
    }
}
