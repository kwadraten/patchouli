using System.Globalization;
using System.Text;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.PdfText;

/// <summary>A point in writing space: <c>U</c> is the inline (reading) axis, <c>V</c> the block axis.</summary>
public readonly record struct WritingPoint(double U, double V);

/// <summary>An axis-aligned rect in writing space.</summary>
public readonly record struct WritingRect(double MinU, double MinV, double MaxU, double MaxV)
{
    public double InlineSize => MaxU - MinU;
    public double BlockSize => MaxV - MinV;
    public (double U, double V) Center => ((MinU + MaxU) / 2, (MinV + MaxV) / 2);

    public WritingRect Union(WritingRect other)
    {
        return new WritingRect(Math.Min(MinU, other.MinU), Math.Min(MinV, other.MinV),
            Math.Max(MaxU, other.MaxU), Math.Max(MaxV, other.MaxV));
    }
}

/// <summary>The strong-direction class a character's script implies for run assembly.</summary>
public enum PdfDirectionClass
{
    /// <summary>Spaces, punctuation, marks and other characters without a strong direction.</summary>
    Neutral,

    /// <summary>Left-to-right scripts: Latin, CJK, digits and other LTR text.</summary>
    Ltr,

    /// <summary>Right-to-left scripts: Hebrew, Arabic and other RTL text.</summary>
    Rtl
}

/// <summary>
/// The shared coordinate transform between normalized display space ([0,1], top-left origin,
/// page rotation applied) and the writing space a <see cref="WritingFrame"/> defines. In
/// writing space, +U is always the reading direction and +V is always line progression, so a
/// single geometric algorithm serves LTR, RTL and vertical scripts with no string reversal —
/// ordering is always ascending U/V of projected coordinates. Rotated local frames are
/// un-rotated about the page center in a square (aspect-corrected) space so a rotated note on
/// a non-square page is not skewed by normalization.
/// </summary>
public static class WritingFrameGeometry
{
    /// <summary>Projects a normalized display point into the frame's writing space.</summary>
    public static WritingPoint ToWriting(WritingFrame frame, double x, double y, double aspect = 1.0)
    {
        (double rx, double ry) = Unrotate(frame, x, y, aspect);
        return frame.Direction switch
        {
            WritingDirection.HorizontalRtl => new WritingPoint(-rx, ry),
            WritingDirection.VerticalRl => new WritingPoint(ry, -rx),
            WritingDirection.VerticalLr => new WritingPoint(ry, rx),
            _ => new WritingPoint(rx, ry)
        };
    }

    /// <summary>The inverse of <see cref="ToWriting"/>: writing space back to display space.</summary>
    public static (double X, double Y) ToDisplay(WritingFrame frame, double u, double v,
        double aspect = 1.0)
    {
        (double rx, double ry) = frame.Direction switch
        {
            WritingDirection.HorizontalRtl => (-u, v),
            WritingDirection.VerticalRl => (-v, u),
            WritingDirection.VerticalLr => (v, u),
            _ => (u, v)
        };
        return Rotate(frame, rx, ry, aspect);
    }

    /// <summary>Projects a display-space box to an axis-aligned writing-space rect.</summary>
    public static WritingRect ToWritingRect(WritingFrame frame, NormalizedBBox box,
        double aspect = 1.0)
    {
        WritingPoint a = ToWriting(frame, box.X, box.Y, aspect);
        WritingPoint b = ToWriting(frame, box.X + box.Width, box.Y, aspect);
        WritingPoint c = ToWriting(frame, box.X, box.Y + box.Height, aspect);
        WritingPoint d = ToWriting(frame, box.X + box.Width, box.Y + box.Height, aspect);
        return new WritingRect(
            Math.Min(Math.Min(a.U, b.U), Math.Min(c.U, d.U)),
            Math.Min(Math.Min(a.V, b.V), Math.Min(c.V, d.V)),
            Math.Max(Math.Max(a.U, b.U), Math.Max(c.U, d.U)),
            Math.Max(Math.Max(a.V, b.V), Math.Max(c.V, d.V)));
    }

    /// <summary>Projects a writing-space rect back to a display-space box.</summary>
    public static NormalizedBBox ToDisplayBBox(WritingFrame frame, WritingRect rect,
        double aspect = 1.0)
    {
        (double x1, double y1) = ToDisplay(frame, rect.MinU, rect.MinV, aspect);
        (double x2, double y2) = ToDisplay(frame, rect.MaxU, rect.MinV, aspect);
        (double x3, double y3) = ToDisplay(frame, rect.MinU, rect.MaxV, aspect);
        (double x4, double y4) = ToDisplay(frame, rect.MaxU, rect.MaxV, aspect);
        double minX = Math.Clamp(Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)), 0, 1);
        double minY = Math.Clamp(Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)), 0, 1);
        double maxX = Math.Clamp(Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)), 0, 1);
        double maxY = Math.Clamp(Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)), 0, 1);
        return new NormalizedBBox(minX, minY, Math.Max(maxX - minX, 1e-9), Math.Max(maxY - minY, 1e-9));
    }

    /// <summary>The display-page aspect ratio (width ÷ height in points) used for rotated frames.</summary>
    public static double AspectOf(PdfTextPageGeometry geometry)
    {
        return geometry.WidthPoints > 0 && geometry.HeightPoints > 0
            ? geometry.WidthPoints / geometry.HeightPoints
            : 1.0;
    }

    /// <summary>
    /// The strong-direction class of a Unicode scalar, by scalar value — supplementary-plane
    /// RTL scripts classify correctly where BMP-only checks cannot see them. Digits classify
    /// LTR (the BiDi "weak" classes are approximated by treating numbers as LTR runs), and
    /// combining marks, punctuation and spaces stay neutral so they attach to an adjacent run
    /// rather than splitting into singleton runs.
    /// </summary>
    public static PdfDirectionClass DirectionClassOf(Rune rune)
    {
        int value = rune.Value;
        if (IsRtlCodePoint(value))
        {
            return PdfDirectionClass.Rtl;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category switch
        {
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber => PdfDirectionClass.Ltr,
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter => PdfDirectionClass.Ltr,
            _ => PdfDirectionClass.Neutral
        };
    }

    /// <summary>The class of a whole glyph: the first strong scalar decides; all-neutral → Neutral.</summary>
    public static PdfDirectionClass DirectionClassOf(PdfTextGlyph glyph)
    {
        foreach (Rune rune in glyph.Text.EnumerateRunes())
        {
            PdfDirectionClass cls = DirectionClassOf(rune);
            if (cls != PdfDirectionClass.Neutral)
            {
                return cls;
            }
        }

        return PdfDirectionClass.Neutral;
    }

    /// <summary>Whether the scalar belongs to a right-to-left script (BMP + supplementary planes).</summary>
    public static bool IsRtlCodePoint(int value)
    {
        return value is
            >= 0x0590 and <= 0x05FF or // Hebrew
            >= 0x0600 and <= 0x06FF or // Arabic
            >= 0x0700 and <= 0x074F or // Syriac
            >= 0x0750 and <= 0x077F or // Arabic Supplement
            >= 0x0780 and <= 0x07BF or // Thaana
            >= 0x07C0 and <= 0x07FF or // NKo
            >= 0x0800 and <= 0x083F or // Samaritan
            >= 0x0840 and <= 0x085F or // Mandaic
            >= 0x0860 and <= 0x086F or // Syriac Supplement
            >= 0x0870 and <= 0x089F or // Arabic Extended-B
            >= 0x08A0 and <= 0x08FF or // Arabic Extended-A
            >= 0xFB1D and <= 0xFB4F or // Hebrew Presentation Forms
            >= 0xFB50 and <= 0xFDFF or // Arabic Presentation Forms-A
            >= 0xFE70 and <= 0xFEFF or // Arabic Presentation Forms-B
            >= 0x10800 and <= 0x10FFF or // Historic RTL scripts (Aramaic, Phoenician, …)
            >= 0x1E800 and <= 0x1E8DF; // Mende Kikakui
    }

    /// <summary>Whether the scalar belongs to the Mongolian vertical-LR script range.</summary>
    public static bool IsMongolian(Rune rune)
    {
        return rune.Value is >= 0x1800 and <= 0x18AF or >= 0x11660 and <= 0x1167F;
    }

    /// <summary>BMP convenience wrapper around <see cref="IsRtlCodePoint"/>.</summary>
    public static bool IsRtlScript(char c)
    {
        return IsRtlCodePoint(c);
    }

    /// <summary>How the baseline step between two consecutively emitted glyphs moves.</summary>
    public enum GlyphStep
    {
        /// <summary>Too far apart (or coincident) to be same-line neighbours.</summary>
        NotAdjacent,

        /// <summary>Adjacent but neither decisively sideways nor downward.</summary>
        Indecisive,

        /// <summary>Advances sideways — the pair shares a horizontal line.</summary>
        Horizontal,

        /// <summary>Stacks top→bottom — the pair shares a vertical column.</summary>
        Vertical
    }

    /// <summary>
    /// Classifies the baseline step of a consecutive native-index pair: the shared metric
    /// behind <see cref="AdjacencyVotes"/> and per-chain orientation splitting. Pairs too far
    /// apart to be same-line neighbours report <see cref="GlyphStep.NotAdjacent"/>; stacking
    /// pairs (|Δy| ≫ |Δx|) report vertical, sideways advances horizontal.
    /// </summary>
    public static GlyphStep StepClass(PdfTextGlyph previous, PdfTextGlyph next)
    {
        double dx = next.BaselineX - previous.BaselineX;
        double dy = next.BaselineY - previous.BaselineY;
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (ax > 0.08 || ay > 0.08 || (ax < 0.001 && ay < 0.001))
        {
            return GlyphStep.NotAdjacent;
        }

        if (dy > 0.004 && ay > ax * 2)
        {
            return GlyphStep.Vertical;
        }

        return ax > 0.004 && ax > ay * 2 ? GlyphStep.Horizontal : GlyphStep.Indecisive;
    }

    /// <summary>
    /// Estimates the glyphs' spatial progression from their own positions: consecutive
    /// native-index pairs that sit close enough to be same-line neighbours vote "vertical"
    /// when they stack top→bottom (|Δy| ≫ |Δx|) and "horizontal" when they advance sideways.
    /// This catches upright-glyph vertical scripts (e.g. Japanese rendered with unrotated
    /// metrics) whose character angles all read 0° — the count is positional, not angle-based.
    /// </summary>
    public static (int Horizontal, int Vertical) AdjacencyVotes(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        int horizontal = 0, vertical = 0;
        IReadOnlyList<PdfTextGlyph> ordered = glyphs.OrderBy(glyph => glyph.NativeIndex).ToArray();
        for (int i = 1; i < ordered.Count; i++)
        {
            switch (StepClass(ordered[i - 1], ordered[i]))
            {
                case GlyphStep.Vertical:
                    vertical++;
                    break;
                case GlyphStep.Horizontal:
                    horizontal++;
                    break;
            }
        }

        return (horizontal, vertical);
    }

    /// <summary>
    /// Detects the dominant writing frame for a glyph stream: horizontal vs vertical from the
    /// glyphs' display angles <em>and</em> their positional progression, RTL vs LTR from the
    /// script of the characters, and vertical-rl vs vertical-lr for the rare left-progressing
    /// vertical scripts. A stream carrying material weight in both orientations reports
    /// <see cref="WritingDirection.Mixed"/> — callers then rely on local per-line frames.
    /// </summary>
    public static WritingFrame DetectDominantFrame(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        int horizontal = 0, vertical = 0;
        int rtlChars = 0, mongolianChars = 0, letters = 0;
        foreach (PdfTextGlyph glyph in glyphs)
        {
            if (glyph.Orientation == PdfGlyphOrientation.Vertical)
            {
                vertical++;
            }
            else if (glyph.Orientation == PdfGlyphOrientation.Horizontal)
            {
                horizontal++;
            }

            foreach (Rune rune in glyph.Text.EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is not (UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber))
                {
                    continue;
                }

                letters++;
                if (IsRtlCodePoint(rune.Value))
                {
                    rtlChars++;
                }
                else if (IsMongolian(rune))
                {
                    // Mongolian (U+1800–18AF) and Mongolian Supplement (U+11660–1167F).
                    mongolianChars++;
                }
            }
        }

        // Positional dominance: when the glyphs visibly stack top-to-bottom far more than
        // they advance sideways, the stream is vertical regardless of uninformative glyph
        // metrics — upright vertical CJK reports angle 0 and would otherwise vote itself
        // horizontal on every glyph.
        (int adjHorizontal, int adjVertical) = AdjacencyVotes(glyphs);
        if (adjVertical >= 2 && adjVertical > adjHorizontal * 2)
        {
            return mongolianChars * 10 >= Math.Max(1, letters)
                ? WritingFrame.VerticalLr
                : WritingFrame.VerticalRl;
        }

        horizontal += adjHorizontal;
        vertical += adjVertical;

        int oriented = horizontal + vertical;
        if (oriented == 0)
        {
            return rtlChars * 4 >= Math.Max(1, letters)
                ? WritingFrame.HorizontalRtl
                : WritingFrame.HorizontalLtr;
        }

        if (vertical * 5 >= oriented * 2 && horizontal * 5 >= oriented * 2)
        {
            return WritingFrame.MixedFrame;
        }

        if (vertical * 5 >= oriented * 3)
        {
            return mongolianChars * 10 >= Math.Max(1, letters)
                ? WritingFrame.VerticalLr
                : WritingFrame.VerticalRl;
        }

        return rtlChars * 4 >= Math.Max(1, letters)
            ? WritingFrame.HorizontalRtl
            : WritingFrame.HorizontalLtr;
    }

    /// <summary>
    /// Detects the local frame of a single line/cluster. Unlike the page-level detector this
    /// keeps the line's actual rotation: a rotated side-note on a horizontal page yields a
    /// horizontal frame with a nonzero <see cref="WritingFrame.RotationDegrees"/>. The mean
    /// advance angle is circular (Σsin/Σcos) so wrap-around values like 359°/1° or +90°/−90°
    /// average to the true direction instead of cancelling to a bogus 0°/180°.
    /// </summary>
    public static WritingFrame DetectLocalFrame(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        if (glyphs.Count == 0)
        {
            return WritingFrame.HorizontalLtr;
        }

        int vertical = 0, horizontal = 0;
        int rtlChars = 0, letters = 0;
        double sinSum = 0, cosSum = 0;
        foreach (PdfTextGlyph glyph in glyphs)
        {
            switch (glyph.Orientation)
            {
                case PdfGlyphOrientation.Vertical:
                    vertical++;
                    break;
                case PdfGlyphOrientation.Horizontal:
                    horizontal++;
                    break;
            }

            double radians = glyph.AngleDegrees * Math.PI / 180;
            sinSum += Math.Sin(radians);
            cosSum += Math.Cos(radians);
            foreach (Rune rune in glyph.Text.EnumerateRunes())
            {
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber)
                {
                    letters++;
                    if (IsRtlCodePoint(rune.Value))
                    {
                        rtlChars++;
                    }
                }
            }
        }

        // Circular mean of the advance angles; a degenerate resultant (angles cancelling)
        // falls back to 0° rather than inventing a direction.
        double meanAngle = Math.Abs(sinSum) + Math.Abs(cosSum) < 1e-9
            ? 0
            : Math.Atan2(sinSum, cosSum) * 180 / Math.PI;
        (int adjHorizontal, int adjVertical) = AdjacencyVotes(glyphs);
        horizontal += adjHorizontal;
        vertical += adjVertical;

        bool isRtl = rtlChars * 4 >= Math.Max(1, letters);
        if (adjVertical >= 2 && adjVertical > adjHorizontal)
        {
            // Positionally-detected upright vertical: the glyph angles are uninformative, so
            // the frame's deviation is the column's actual baseline tilt, not a glyph mean.
            return new WritingFrame(WritingDirection.VerticalRl,
                NormalizeAngle(BaselineTiltDegrees(glyphs)));
        }

        if (vertical > horizontal)
        {
            // Vertical lines read top→bottom; page rotation already applied, so the frame's
            // rotation is the deviation from a pure +90° inline axis.
            double deviation = NormalizeAngle(meanAngle - 90);
            return new WritingFrame(WritingDirection.VerticalRl, deviation);
        }

        double rotation = NormalizeAngle(meanAngle);
        if (Math.Abs(rotation) <= 30)
        {
            rotation = 0;
        }
        else if (Math.Abs(Math.Abs(rotation) - 180) <= 30)
        {
            rotation = 180;
        }

        return new WritingFrame(
            isRtl ? WritingDirection.HorizontalRtl : WritingDirection.HorizontalLtr, rotation);
    }

    /// <summary>
    /// The column tilt of a positionally-vertical run: the mean sideways lean of same-line
    /// neighbour baselines, in degrees — 0 for a perfectly upright column.
    /// </summary>
    private static double BaselineTiltDegrees(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        double dxSum = 0, dySum = 0;
        IReadOnlyList<PdfTextGlyph> ordered = glyphs.OrderBy(glyph => glyph.NativeIndex).ToArray();
        for (int i = 1; i < ordered.Count; i++)
        {
            double dx = ordered[i].BaselineX - ordered[i - 1].BaselineX;
            double dy = ordered[i].BaselineY - ordered[i - 1].BaselineY;
            double ax = Math.Abs(dx), ay = Math.Abs(dy);
            if (ax > 0.08 || ay > 0.08 || (ax < 0.001 && ay < 0.001) ||
                !(dy > 0.004 && ay > ax * 2))
            {
                continue;
            }

            dxSum += dx;
            dySum += dy;
        }

        return dySum > 0 ? Math.Atan2(dxSum, dySum) * 180 / Math.PI : 0;
    }

    /// <summary>Median of a value column — the estimators all key off typical glyph scale.</summary>
    public static double Median(IReadOnlyList<double> values, double fallback = 0)
    {
        if (values.Count == 0)
        {
            return fallback;
        }

        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static double NormalizeAngle(double degrees)
    {
        degrees %= 360;
        if (degrees > 180)
        {
            degrees -= 360;
        }
        else if (degrees <= -180)
        {
            degrees += 360;
        }

        return degrees;
    }

    /// <summary>
    /// Rotates display coordinates into the frame's un-rotated space. The rotation is applied
    /// about the page centre in a square space (x scaled by the page aspect) so a rotated
    /// frame on a non-square page is not skewed by normalization — a physical rotation is not
    /// a pure rotation of the unit square.
    /// </summary>
    private static (double X, double Y) Unrotate(WritingFrame frame, double x, double y,
        double aspect)
    {
        if (frame.RotationDegrees == 0)
        {
            return (x, y);
        }

        double radians = frame.RotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double px = (x - 0.5) * aspect, py = y - 0.5;
        return ((px * cos + py * sin) / aspect + 0.5, -px * sin + py * cos + 0.5);
    }

    private static (double X, double Y) Rotate(WritingFrame frame, double x, double y,
        double aspect)
    {
        if (frame.RotationDegrees == 0)
        {
            return (x, y);
        }

        double radians = frame.RotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double px = (x - 0.5) * aspect, py = y - 0.5;
        return ((px * cos - py * sin) / aspect + 0.5, px * sin + py * cos + 0.5);
    }
}
