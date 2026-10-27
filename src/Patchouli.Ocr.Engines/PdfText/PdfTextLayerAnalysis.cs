using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.PdfText;

/// <summary>
/// Provider-neutral knobs shared by the text-layer schemes. Each package maps its attributed
/// settings record onto this record; the algorithms consume the neutral values.
/// </summary>
public sealed record PdfTextLayerParseOptions(
    /// <summary>Inter-line whitespace beyond the expected line gap, as a fraction of the median line pitch, that opens a paragraph/column cut.</summary>
    double LineGapFactor = 0.45,
    /// <summary>Minimum inline gap, as a fraction of the median line block size, that separates layout columns.</summary>
    double ColumnGapFactor = 1.2,
    /// <summary>Detect per-line local frames (RTL lines, rotated notes) under the dominant page frame.</summary>
    bool DetectMixedDirections = true,
    /// <summary>Space insertion threshold, as a fraction of the median glyph pitch beyond the line's own tracking gap.</summary>
    double WordGapFactor = 0.32,
    /// <summary>Fraction of unconvertible glyphs before the whole page's text layer is declared unusable.</summary>
    double MaxUntrustedRatio = 0.5,
    /// <summary>Layout model id for visual-guided detection.</summary>
    string? LayoutModelId = null,
    /// <summary>Resolved layout model path for visual-guided detection.</summary>
    string? LayoutModelPath = null,
    /// <summary>DPI used when a visual scheme renders the page.</summary>
    int LayoutDpi = 200,
    /// <summary>Fraction of a text region's area trusted native glyphs must cover before a low_native_coverage diagnostic is emitted.</summary>
    double MinNativeCoverage = 0.25,
    /// <summary>Whether non-textual regions (figure/table/formula) are requested for OCR.</summary>
    bool RecognizeNonTextRegions = true,
    /// <summary>
    /// Whether a native scheme may request bounded image OCR for unconvertible glyph runs.
    /// False by default: native providers report damaged native text as an explicit failure
    /// rather than silently opting into an image-OCR policy the user never selected. The
    /// visual-guided scheme requests regions by its own declared policy and ignores this flag.
    /// </summary>
    bool SupplementWithOcr = false);

/// <summary>One layout cut produced by a segmentation algorithm, in writing-space terms.</summary>
public sealed record PdfTextLayoutCut(
    /// <summary>"block" for horizontal cuts across the block axis, "inline" for column cuts.</summary>
    string Axis,
    double Position,
    double GapSize);

/// <summary>A gap-tree cutline: the persisted inline gap spanning a run of rows.</summary>
public sealed record PdfTextGapLine(
    double MinU,
    double MaxU,
    int StartRow,
    int EndRow);

/// <summary>
/// One physical text line in writing space: glyphs kept in reading order along with the
/// line's own local frame. The local frame may differ from the page's dominant frame on mixed
/// pages; text assembly uses the local frame so RTL runs and rotated notes stay faithful.
/// <see cref="WritingBox"/> is always expressed in the page's dominant frame so lines stay
/// mutually comparable for layout ordering even when their own frames differ.
/// </summary>
public sealed record PdfTextLineUnit(
    WritingRect WritingBox,
    NormalizedBBox DisplayBox,
    WritingFrame Frame,
    IReadOnlyList<PdfTextGlyph> Glyphs,
    string Text);

/// <summary>A produced paragraph: merged lines plus provenance, ready for a DocumentBoxSeed.</summary>
public sealed record PdfTextParagraph(
    NormalizedBBox DisplayBox,
    WritingFrame Frame,
    IReadOnlyList<PdfTextLineUnit> Lines,
    string Text,
    string? SubType = null);

/// <summary>The segmentation contract every provider scheme implements.</summary>
public interface IPdfTextLayerAnalyzer
{
    Task<Result<PdfTextLayerResult>> AnalyzeAsync(
        PdfTextLayerContext context,
        PdfTextLayerParseOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Shared glyph-stream conditioning and line assembly used by every scheme.</summary>
public static class PdfTextGlyphStream
{
    /// <summary>
    /// A glyph the selected image engine must replace: Unicode-map errors, unconvertible
    /// characters, or empty text that is not a structural surrogate continuation.
    /// </summary>
    public static bool IsUntrusted(PdfTextGlyph glyph)
    {
        return glyph.Flags.HasFlag(PdfGlyphFlags.UnicodeMapError) ||
               glyph.Flags.HasFlag(PdfGlyphFlags.NoUnicodeMapping) ||
               (glyph.Text.Length == 0 && !glyph.Flags.HasFlag(PdfGlyphFlags.SurrogateContinuation));
    }

    /// <summary>
    /// Splits the glyph stream into trustworthy glyphs and untrustworthy runs. What the
    /// provider does with the untrusted half is scheme policy: native schemes fail by
    /// default (or emit bounded region requests when supplementation is explicitly enabled),
    /// and the visual-guided scheme always emits bounded region requests.
    /// </summary>
    public static (IReadOnlyList<PdfTextGlyph> Trusted, IReadOnlyList<PdfTextGlyph> Untrusted)
        Split(IReadOnlyList<PdfTextGlyph> glyphs)
    {
        List<PdfTextGlyph> trusted = [];
        List<PdfTextGlyph> untrusted = [];
        foreach (PdfTextGlyph glyph in glyphs)
        {
            (IsUntrusted(glyph) ? untrusted : trusted).Add(glyph);
        }

        return (trusted, untrusted);
    }

    /// <summary>
    /// Restricts a stream to glyphs whose box centre falls inside a declared context region;
    /// a null region leaves the stream unchanged.
    /// </summary>
    public static IReadOnlyList<PdfTextGlyph> InScope(IReadOnlyList<PdfTextGlyph> glyphs,
        NormalizedBBox? region)
    {
        if (region is null)
        {
            return glyphs;
        }

        return glyphs.Where(glyph => Contains(region.Value, Center(glyph.BBox))).ToArray();
    }

    /// <summary>Clips a produced region to the declared scope; null/empty result means outside.</summary>
    public static NormalizedBBox? ClipToScope(NormalizedBBox? scope, NormalizedBBox box)
    {
        if (scope is null)
        {
            return IsValidRegion(box) ? box : null;
        }

        NormalizedBBox bounds = scope.Value;
        double x = Math.Max(bounds.X, box.X);
        double y = Math.Max(bounds.Y, box.Y);
        double right = Math.Min(bounds.X + bounds.Width, box.X + box.Width);
        double bottom = Math.Min(bounds.Y + bounds.Height, box.Y + box.Height);
        if (right - x <= 1e-6 || bottom - y <= 1e-6)
        {
            return null;
        }

        return new NormalizedBBox(x, y, right - x, bottom - y);
    }

    /// <summary>A region is usable only when it has positive size inside the normalized page.</summary>
    public static bool IsValidRegion(NormalizedBBox box)
    {
        return box.Width > 1e-6 && box.Height > 1e-6 &&
               box.X is >= -1e-6 and <= 1.0 && box.Y is >= -1e-6 and <= 1.0;
    }

    /// <summary>The point where a glyph is considered to sit for region assignment.</summary>
    public static (double X, double Y) Center(NormalizedBBox box)
    {
        return (box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    private static bool Contains(NormalizedBBox box, (double X, double Y) point)
    {
        return point.X >= box.X - 1e-6 && point.X <= box.X + box.Width + 1e-6 &&
               point.Y >= box.Y - 1e-6 && point.Y <= box.Y + box.Height + 1e-6;
    }

    /// <summary>
    /// Groups glyphs into physical lines. The stream is first partitioned into contiguous
    /// orientation runs — vertical text never bands together with horizontal neighbours, so a
    /// vertical side-note crossing two horizontal lines cannot merge them. Each run is then
    /// banded in its own frame's writing space: sort by the block axis and merge glyphs whose
    /// block extents overlap, then split bands at column-scale inline gaps.
    /// </summary>
    public static IReadOnlyList<PdfTextLineUnit> AssembleLines(
        IReadOnlyList<PdfTextGlyph> glyphs,
        WritingFrame pageFrame,
        bool detectLocalFrames,
        double wordGapFactor,
        double aspect = 1.0)
    {
        if (glyphs.Count == 0)
        {
            return Array.Empty<PdfTextLineUnit>();
        }

        List<PdfTextLineUnit> lines = [];
        foreach ((List<PdfTextGlyph> run, bool runVertical) in OrientationRuns(glyphs))
        {
            // A horizontal run on a vertical-dominant page still bands horizontally —
            // inheriting the vertical page frame would fragment each line into
            // one-glyph columns.
            WritingFrame runFrame = runVertical || IsVertical(pageFrame.Direction)
                ? WritingFrameGeometry.DetectDominantFrame(run)
                : pageFrame;
            if (!IsVertical(runFrame.Direction) && runVertical)
            {
                runFrame = WritingFrame.VerticalRl;
            }

            List<(PdfTextGlyph Glyph, WritingRect Rect)> projected = new(run.Count);
            foreach (PdfTextGlyph glyph in run)
            {
                projected.Add((glyph,
                    WritingFrameGeometry.ToWritingRect(runFrame, glyph.BBox, aspect)));
            }

            projected.Sort((a, b) => a.Rect.MinV.CompareTo(b.Rect.MinV));
            List<(PdfTextGlyph Glyph, WritingRect Rect)> band = [];
            double bandBottom = double.NegativeInfinity;
            foreach ((PdfTextGlyph glyph, WritingRect rect) in projected)
            {
                // A glyph joins the band only when it genuinely overlaps the accumulated
                // block extent — lines whose boxes merely touch (interline gap ≈ 0) are
                // separate lines and must never be glued together by a tolerance.
                double minOverlap = Math.Max(rect.BlockSize * 0.2, 0.0002);
                if (band.Count > 0 && rect.MinV >= bandBottom - minOverlap)
                {
                    lines.AddRange(BuildBandLines(band, runFrame, pageFrame, detectLocalFrames,
                        wordGapFactor, aspect));
                    band = [];
                    bandBottom = double.NegativeInfinity;
                }

                band.Add((glyph, rect));
                bandBottom = Math.Max(bandBottom, rect.MaxV);
            }

            if (band.Count > 0)
            {
                lines.AddRange(BuildBandLines(band, runFrame, pageFrame, detectLocalFrames,
                    wordGapFactor, aspect));
            }
        }

        return lines;
    }

    /// <summary>
    /// Partitions the stream into contiguous orientation runs using each glyph's own
    /// orientation plus positional evidence: inside a nominally-horizontal run, a glyph
    /// whose adjacent baseline steps visibly stack top→bottom belongs to upright-glyph
    /// vertical text (e.g. Japanese with unrotated metrics). The run splits wherever the
    /// local evidence flips, so a column crossing horizontal rows reclassifies alone —
    /// whole-run voting would let a long column drag unrelated horizontal lines into its
    /// vertical frame and fragment them into one-glyph columns.
    /// </summary>
    private static List<(List<PdfTextGlyph> Glyphs, bool Vertical)> OrientationRuns(
        IReadOnlyList<PdfTextGlyph> glyphs)
    {
        List<(List<PdfTextGlyph>, bool)> runs = [];
        List<PdfTextGlyph> current = [];
        bool currentVertical = false;
        foreach (PdfTextGlyph glyph in glyphs)
        {
            bool vertical = glyph.Orientation == PdfGlyphOrientation.Vertical;
            if (current.Count == 0)
            {
                currentVertical = vertical;
                current.Add(glyph);
                continue;
            }

            if (vertical != currentVertical)
            {
                runs.Add((current, currentVertical));
                current = [];
                currentVertical = vertical;
            }

            current.Add(glyph);
        }

        if (current.Count > 0)
        {
            runs.Add((current, currentVertical));
        }

        // Positional reclassification: each glyph in an all-horizontal-orientation run is
        // judged by its own adjacent steps, and the run is re-segmented wherever the
        // judgement changes.
        List<(List<PdfTextGlyph>, bool)> refined = [];
        foreach ((List<PdfTextGlyph> run, bool vertical) in runs)
        {
            if (vertical || run.Count < 2 ||
                run.Any(g => g.Orientation != PdfGlyphOrientation.Horizontal))
            {
                refined.Add((run, vertical));
                continue;
            }

            List<PdfTextGlyph> ordered = run.OrderBy(g => g.NativeIndex).ToList();
            List<PdfTextGlyph> segment = [ordered[0]];
            bool segmentVertical = !IsGenerated(ordered[0]) && StepsVoteVertical(ordered, 0);
            for (int i = 1; i < ordered.Count; i++)
            {
                // Synthesized whitespace/return glyphs inherit the segment they sit in —
                // they carry no direction evidence of their own.
                bool glyphVertical = IsGenerated(ordered[i])
                    ? segmentVertical
                    : StepsVoteVertical(ordered, i);
                if (glyphVertical != segmentVertical)
                {
                    refined.Add((segment, segmentVertical));
                    segment = [];
                    segmentVertical = glyphVertical;
                }

                segment.Add(ordered[i]);
            }

            refined.Add((segment, segmentVertical));
        }

        return refined;
    }

    /// <summary>
    /// A glyph's positional orientation vote: the baseline steps linking it to its previous
    /// and next stream neighbours are classified; the glyph counts as upright-vertical when
    /// stacking evidence dominates (same 2:1 rule the run-level vote used).
    /// </summary>
    private static bool IsGenerated(PdfTextGlyph glyph)
    {
        return glyph.Flags.HasFlag(PdfGlyphFlags.Generated);
    }

    private static bool StepsVoteVertical(IReadOnlyList<PdfTextGlyph> run, int index)
    {
        int horizontal = 0, vertical = 0;
        // The glyph's own neighbours are the nearest non-generated stream entries:
        // PDFium-synthesized whitespace glyphs may sit between every real pair and would
        // report edge-position steps that erase the true stacking evidence.
        int previous = index - 1;
        while (previous >= 0 && IsGenerated(run[previous]))
        {
            previous--;
        }

        int next = index + 1;
        while (next < run.Count && IsGenerated(run[next]))
        {
            next++;
        }

        if (previous >= 0)
        {
            switch (WritingFrameGeometry.StepClass(run[previous], run[index]))
            {
                case WritingFrameGeometry.GlyphStep.Vertical:
                    vertical++;
                    break;
                case WritingFrameGeometry.GlyphStep.Horizontal:
                    horizontal++;
                    break;
            }
        }

        if (next < run.Count)
        {
            switch (WritingFrameGeometry.StepClass(run[index], run[next]))
            {
                case WritingFrameGeometry.GlyphStep.Vertical:
                    vertical++;
                    break;
                case WritingFrameGeometry.GlyphStep.Horizontal:
                    horizontal++;
                    break;
            }
        }

        return vertical > 0 && vertical >= horizontal * 2;
    }

    private static bool IsVertical(WritingDirection direction)
    {
        return direction is WritingDirection.VerticalRl or WritingDirection.VerticalLr;
    }

    /// <summary>
    /// Splits one block-axis band into separate lines at large inline gaps — a column gap must
    /// produce two line units or no downstream algorithm can ever separate the columns. The
    /// split threshold is self-calibrating: a gap must exceed the band's own tracking
    /// baseline (the median gap and median pitch of the OTHER adjacent pairs, so the pair
    /// under test never calibrates itself) by more than two median glyph pitches. Wide
    /// tracking — a spaced-out TITLE — therefore stays one line, while a several-pitch
    /// valley is a column boundary. Word gaps stay inside a line (they are handled by text
    /// assembly). A lone two-glyph pair has no tracking reference, so it falls back to the
    /// absolute multiple of the median glyph advance.
    /// </summary>
    private static List<PdfTextLineUnit> BuildBandLines(
        List<(PdfTextGlyph Glyph, WritingRect Rect)> band,
        WritingFrame bandFrame,
        WritingFrame pageFrame,
        bool detectLocalFrames,
        double wordGapFactor,
        double aspect)
    {
        List<(PdfTextGlyph Glyph, WritingRect Rect)> sorted =
            band.OrderBy(item => item.Rect.MinU).ThenBy(item => item.Glyph.NativeIndex).ToList();
        double medianAdvance = WritingFrameGeometry.Median(
            sorted.Select(item => item.Rect.InlineSize).Where(size => size > 0).ToArray(), 0.01);
        double[] signedGaps = new double[sorted.Count - 1];
        double[] pitches = new double[sorted.Count - 1];
        for (int i = 1; i < sorted.Count; i++)
        {
            signedGaps[i - 1] = sorted[i].Rect.MinU - sorted[i - 1].Rect.MaxU;
            pitches[i - 1] = Math.Abs(sorted[i].Rect.MinU - sorted[i - 1].Rect.MinU);
        }

        List<PdfTextLineUnit> lines = [];
        List<(PdfTextGlyph Glyph, WritingRect Rect)> segment = [sorted[0]];
        double segmentMaxU = sorted[0].Rect.MaxU;
        int pairIndex = 0;
        foreach ((PdfTextGlyph Glyph, WritingRect Rect) item in sorted.Skip(1))
        {
            double naturalGap = signedGaps.Length > 1
                ? MedianExcluding(signedGaps, pairIndex, 0)
                : 0;
            double medianPitch = pitches.Length > 1
                ? MedianExcluding(pitches, pairIndex, medianAdvance)
                : medianAdvance;
            double splitThreshold = Math.Max(
                Math.Max(naturalGap + medianPitch * 2.0, medianAdvance * 4), 0.008);
            if (item.Rect.MinU - segmentMaxU > splitThreshold)
            {
                lines.Add(BuildLine(segment, bandFrame, pageFrame, detectLocalFrames,
                    wordGapFactor, aspect));
                segment = [];
                segmentMaxU = item.Rect.MaxU;
            }

            segment.Add(item);
            segmentMaxU = Math.Max(segmentMaxU, item.Rect.MaxU);
            pairIndex++;
        }

        lines.Add(BuildLine(segment, bandFrame, pageFrame, detectLocalFrames, wordGapFactor,
            aspect));
        return lines;
    }

    private static PdfTextLineUnit BuildLine(
        List<(PdfTextGlyph Glyph, WritingRect Rect)> band,
        WritingFrame bandFrame,
        WritingFrame pageFrame,
        bool detectLocalFrames,
        double wordGapFactor,
        double aspect)
    {
        List<PdfTextGlyph> glyphs = band.Select(item => item.Glyph).ToList();
        // The unit's WritingBox is expressed in the page's dominant frame so lines from
        // differently-oriented runs stay mutually comparable downstream.
        WritingRect box = glyphs
            .Select(glyph => WritingFrameGeometry.ToWritingRect(pageFrame, glyph.BBox, aspect))
            .Aggregate((a, b) => a.Union(b));
        WritingFrame frame = detectLocalFrames
            ? WritingFrameGeometry.DetectLocalFrame(glyphs)
            : bandFrame;
        if (!IsVertical(frame.Direction) && IsVertical(bandFrame.Direction))
        {
            // Upright vertical glyphs report angle 0 — a horizontal local detection on a
            // vertically-banded run is positional evidence being overridden by angle alone.
            frame = bandFrame;
        }

        // Geometry order for the glyph list is the local frame's inline axis; the TEXT is
        // assembled by direction-aware runs so RTL runs keep their native logical order
        // instead of being geometrically reversed.
        List<(PdfTextGlyph Glyph, WritingRect Rect)> projected = glyphs
            .Select(glyph => (glyph, WritingFrameGeometry.ToWritingRect(frame, glyph.BBox, aspect)))
            .ToList();
        List<PdfTextGlyph> ordered = projected
            .OrderBy(item => item.Item2.MinU)
            .ThenBy(item => item.Glyph.NativeIndex)
            .Select(item => item.Glyph)
            .ToList();
        return new PdfTextLineUnit(box, WritingFrameGeometry.ToDisplayBBox(pageFrame, box, aspect),
            frame, ordered,
            AssembleText(projected, frame, wordGapFactor));
    }

    /// <summary>
    /// Joins a line's text by direction runs: the stream is segmented into maximal
    /// strong-direction runs in NATIVE index order, runs are ordered along the line's inline
    /// axis, and inside each run glyphs emit in the run's own reading direction — ascending
    /// inline position when the run's direction agrees with the line frame, descending when
    /// it opposes. This is the visual-order → logical-order reconstruction: an RTL Hebrew run
    /// inside an LTR line emits right-to-left — "אבג" stays "אבג" — and a run stored at its
    /// visual slot is repaired by its own geometry rather than by reversing the character
    /// array. Neutral runs (spaces, punctuation, combining marks) attach to the preceding
    /// strong run — an honest BiDi approximation, not the full UBA: without shaping data,
    /// mirrored brackets and neutrals at embedding-level boundaries keep the adjacent
    /// run's direction.
    /// </summary>
    private static string AssembleText(
        List<(PdfTextGlyph Glyph, WritingRect Rect)> projected,
        WritingFrame frame,
        double wordGapFactor)
    {
        if (projected.Count == 0)
        {
            return string.Empty;
        }

        // Segment into direction runs along the native index order — the logical order the
        // source actually stored. Neutral glyphs attach to the preceding run — they never
        // open a run of their own unless the line starts neutral.
        List<(PdfDirectionClass Class, List<(PdfTextGlyph Glyph, WritingRect Rect)> Glyphs)> runs = [];
        foreach ((PdfTextGlyph glyph, WritingRect rect) in projected
                     .OrderBy(item => item.Glyph.NativeIndex))
        {
            PdfDirectionClass cls = WritingFrameGeometry.DirectionClassOf(glyph);
            if (cls == PdfDirectionClass.Neutral && runs.Count > 0)
            {
                runs[^1].Glyphs.Add((glyph, rect));
                continue;
            }

            if (runs.Count == 0 || runs[^1].Class != cls)
            {
                runs.Add((cls, [(glyph, rect)]));
                continue;
            }

            runs[^1].Glyphs.Add((glyph, rect));
        }

        // Order runs by their position along the line's inline axis. Inside each run the
        // emission follows the run's own reading direction — ascending inline position
        // when the run's strong direction agrees with the line frame's, descending when
        // they oppose. Stream order cannot be trusted to be logical: producers may store
        // a shaped RTL cluster at its visual slot (a ة index ahead of مكتب), so the
        // run's geometry, not the native index, decides emission order. Geometric
        // ordering is identical to native order for well-formed input and additionally
        // repairs split or visual-order runs.
        bool rtlFrame = frame.Direction == WritingDirection.HorizontalRtl;
        bool horizontalFrame = frame.Direction is WritingDirection.HorizontalLtr
            or WritingDirection.HorizontalRtl;
        List<(PdfDirectionClass Class, List<(PdfTextGlyph Glyph, WritingRect Rect)> Glyphs,
            double MinU, double MaxU)> spans = runs
            .Select(run => (run.Class, run.Glyphs,
                MinU: run.Glyphs.Min(item => item.Rect.MinU),
                MaxU: run.Glyphs.Max(item => item.Rect.MaxU)))
            .OrderBy(run => run.MinU)
            .ToList();
        List<((PdfTextGlyph Glyph, WritingRect Rect) Item, int Run)> ordered = [];
        for (int r = 0; r < spans.Count; r++)
        {
            (PdfDirectionClass cls, List<(PdfTextGlyph Glyph, WritingRect Rect)> glyphs, _, _) =
                spans[r];
            bool flipped = horizontalFrame && cls != PdfDirectionClass.Neutral &&
                           cls == PdfDirectionClass.Rtl != rtlFrame;
            IEnumerable<(PdfTextGlyph Glyph, WritingRect Rect)> emitted = flipped
                ? glyphs.OrderByDescending(item => item.Rect.MinU)
                    .ThenBy(item => item.Glyph.NativeIndex)
                : glyphs.OrderBy(item => item.Rect.MinU)
                    .ThenBy(item => item.Glyph.NativeIndex);
            ordered.AddRange(emitted.Select(item => (item, r)));
        }

        // Word-gap estimation is calibrated against the line's own tracking so direction
        // never leaks into it: the reference is the median signed ink gap and median pitch
        // of the OTHER adjacent pairs (the pair under test cannot calibrate itself), and a
        // space is inserted only when the physical gap exceeds that baseline by more than
        // the configured fraction of a glyph pitch. Inside a run the gap is measured along
        // the emission direction (descending for a flipped run); at a run boundary it is
        // the two runs' nearest-edge gap. Glyphs inside a strong-direction run therefore
        // concatenate without spaces, while a real word break still exceeds the baseline.
        // A lone pair has no tracking reference and falls back to the absolute multiple of
        // the median glyph advance.
        double medianAdvance = WritingFrameGeometry.Median(
            ordered.Select(item => item.Item.Rect.InlineSize).Where(size => size > 0)
                .ToArray(), 0.01);
        double[] signedGaps = new double[ordered.Count - 1];
        double[] pitches = new double[ordered.Count - 1];
        for (int i = 1; i < ordered.Count; i++)
        {
            ((PdfTextGlyph Glyph, WritingRect Rect) item, int run) = ordered[i];
            ((PdfTextGlyph Glyph, WritingRect Rect) previous, int previousRun) = ordered[i - 1];
            signedGaps[i - 1] = previousRun == run
                ? Math.Max(item.Rect.MinU - previous.Rect.MaxU,
                    previous.Rect.MinU - item.Rect.MaxU)
                : Math.Max(0, spans[run].MinU - spans[previousRun].MaxU);
            pitches[i - 1] = Math.Abs(item.Rect.MinU - previous.Rect.MinU);
        }

        System.Text.StringBuilder text = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            ((PdfTextGlyph Glyph, WritingRect Rect) item, _) = ordered[i];
            if (i > 0)
            {
                double naturalGap = signedGaps.Length > 1
                    ? MedianExcluding(signedGaps, i - 1, 0)
                    : 0;
                double medianPitch = pitches.Length > 1
                    ? MedianExcluding(pitches, i - 1, medianAdvance)
                    : medianAdvance;
                double spaceThreshold = Math.Max(
                    naturalGap + medianPitch * wordGapFactor, 0.0008);
                string previous = ordered[i - 1].Item.Glyph.Text;
                if (signedGaps[i - 1] > spaceThreshold && item.Glyph.Text.Length > 0 &&
                    !char.IsWhiteSpace(item.Glyph.Text[0]) &&
                    (previous.Length == 0 || !char.IsWhiteSpace(previous[^1])))
                {
                    text.Append(' ');
                }
            }

            text.Append(item.Glyph.Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// The block-axis gap that separates paragraphs or columns: the expected line gap —
    /// the median line pitch (line-to-line spacing) minus the median line block size —
    /// plus the configured fraction of that pitch. Using pitch rather than raw block size
    /// keeps single-spaced lines merged even when their boxes merely touch, while a true
    /// paragraph jump still exceeds the threshold. Pitch samples of zero come from rows
    /// sharing a block position (two columns on one line) and are excluded; beyond roughly
    /// four median line heights whitespace is unconditionally a block boundary, so the cap
    /// keeps a degenerate pitch median — e.g. cross-column distances leaking onto the block
    /// axis — from inflating the threshold past real paragraph breaks.
    /// </summary>
    public static double ParagraphGapThreshold(
        IReadOnlyList<PdfTextLineUnit> lines, double lineGapFactor)
    {
        double medianBlock = WritingFrameGeometry.Median(
            lines.Select(line => line.WritingBox.BlockSize).Where(size => size > 0).ToArray(),
            0.01);
        double[] orderedMinV = lines.OrderBy(line => line.WritingBox.MinV)
            .Select(line => line.WritingBox.MinV).ToArray();
        double[] pitches = new double[orderedMinV.Length - 1];
        for (int i = 1; i < orderedMinV.Length; i++)
        {
            pitches[i - 1] = orderedMinV[i] - orderedMinV[i - 1];
        }

        double medianPitch = WritingFrameGeometry.Median(
            pitches.Where(pitch => pitch > 1e-9).ToArray(), medianBlock);
        double expectedGap = Math.Max(medianPitch - medianBlock, 0.0);
        return Math.Min(expectedGap + Math.Max(medianPitch * lineGapFactor, 0.002),
            Math.Max(medianBlock * 4, 0.0025));
    }

    /// <summary>
    /// Median of a small sample excluding one element — the pair under test never calibrates
    /// its own significance reference; an empty remainder yields <paramref name="fallback"/>.
    /// </summary>
    private static double MedianExcluding(IReadOnlyList<double> values, int excludeIndex,
        double fallback)
    {
        List<double> rest = new(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            if (i != excludeIndex)
            {
                rest.Add(values[i]);
            }
        }

        return WritingFrameGeometry.Median(rest, fallback);
    }

    /// <summary>
    /// Merges consecutive same-region lines into paragraphs: a block gap larger than the
    /// pitch-aware <see cref="ParagraphGapThreshold"/> starts a new paragraph. Lines keep
    /// their own local frames; the paragraph inherits the dominant one.
    /// </summary>
    public static IReadOnlyList<PdfTextParagraph> MergeParagraphs(
        IReadOnlyList<PdfTextLineUnit> orderedLines,
        WritingFrame frame,
        double lineGapFactor,
        double aspect = 1.0)
    {
        if (orderedLines.Count == 0)
        {
            return Array.Empty<PdfTextParagraph>();
        }

        double paragraphGap = ParagraphGapThreshold(orderedLines, lineGapFactor);
        List<PdfTextParagraph> paragraphs = [];
        List<PdfTextLineUnit> current = [orderedLines[0]];
        for (int i = 1; i < orderedLines.Count; i++)
        {
            double gap = orderedLines[i].WritingBox.MinV - current[^1].WritingBox.MaxV;
            if (gap > paragraphGap)
            {
                paragraphs.Add(BuildParagraph(current, frame, aspect));
                current = [];
            }

            current.Add(orderedLines[i]);
        }

        paragraphs.Add(BuildParagraph(current, frame, aspect));
        return paragraphs;
    }

    private static PdfTextParagraph BuildParagraph(IReadOnlyList<PdfTextLineUnit> lines,
        WritingFrame frame, double aspect)
    {
        WritingRect box = lines.Select(line => line.WritingBox).Aggregate((a, b) => a.Union(b));
        return new PdfTextParagraph(WritingFrameGeometry.ToDisplayBBox(frame, box, aspect), frame,
            lines, string.Join('\n', lines.Select(line => line.Text)));
    }

    /// <summary>
    /// Compresses the glyphs of a produced block into contiguous native-index runs — the
    /// original-char anchors — without touching their reading order.
    /// </summary>
    public static IReadOnlyList<PdfTextAnchor> AnchorRuns(IEnumerable<PdfTextGlyph> glyphs)
    {
        List<int> indices = glyphs.Select(glyph => glyph.NativeIndex).Distinct().OrderBy(i => i).ToList();
        if (indices.Count == 0)
        {
            return Array.Empty<PdfTextAnchor>();
        }

        List<PdfTextAnchor> anchors = [new(indices[0], 1)];
        for (int i = 1; i < indices.Count; i++)
        {
            PdfTextAnchor last = anchors[^1];
            if (indices[i] == last.StartCharIndex + last.CharCount)
            {
                anchors[^1] = last with { CharCount = last.CharCount + 1 };
            }
            else
            {
                anchors.Add(new PdfTextAnchor(indices[i], 1));
            }
        }

        return anchors;
    }
}
