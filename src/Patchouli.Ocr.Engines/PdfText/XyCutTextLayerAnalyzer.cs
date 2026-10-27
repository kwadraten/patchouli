using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.PdfText;

/// <summary>
/// Recursive XY Cut over the native glyph stream in writing space: alternately project the
/// region's line extents onto the block axis and the inline axis, cut at projection valleys
/// deeper than the configured gap factors, and recurse until every leaf holds one reading
/// column. Cut order follows the frame — block progression first, then inline position — so
/// LTR, RTL and vertical pages all emit a reading-ordered leaf list without ever touching
/// string order. Missing or unusable native text is an explicit provider failure, never a
/// whole-page OCR request.
/// </summary>
public sealed class XyCutTextLayerAnalyzer : IPdfTextLayerAnalyzer
{
    private const int MaxDepth = 12;

    public Task<Result<PdfTextLayerResult>> AnalyzeAsync(
        PdfTextLayerContext context,
        PdfTextLayerParseOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PdfTextGlyph> scoped = PdfTextGlyphStream.InScope(context.Glyphs,
            context.Region);
        (IReadOnlyList<PdfTextGlyph> trusted, IReadOnlyList<PdfTextGlyph> untrusted) =
            PdfTextGlyphStream.Split(scoped);
        if (trusted.Count == 0)
        {
            return Task.FromResult(Result<PdfTextLayerResult>.Failure(
                PdfTextLayerDiagnosticCodes.NativeTextMissing,
                "The page has no usable native text layer; the native XY Cut provider does not fall back to OCR."));
        }

        if (untrusted.Count > 0)
        {
            // Damaged native text is an explicit failure for a selected native scheme unless
            // the caller explicitly opted into bounded OCR supplementation — never silently
            // auto-adopted partial content.
            double untrustedShare = untrusted.Count / (double)(trusted.Count + untrusted.Count);
            if (!options.SupplementWithOcr)
            {
                return Task.FromResult(Result<PdfTextLayerResult>.Failure(
                    PdfTextLayerDiagnosticCodes.NativeTextUnusable,
                    $"{untrusted.Count} native glyph(s) are not Unicode-convertible; enable " +
                    "'ocr-untrusted' to request bounded image OCR for them."));
            }

            if (untrustedShare > options.MaxUntrustedRatio)
            {
                return Task.FromResult(Result<PdfTextLayerResult>.Failure(
                    PdfTextLayerDiagnosticCodes.NativeTextUnusable,
                    $"{untrustedShare:0.##} of the page's native glyphs are not " +
                    "Unicode-convertible; the text layer is untrustworthy."));
            }
        }

        double aspect = WritingFrameGeometry.AspectOf(context.Geometry);
        WritingFrame frame = WritingFrameGeometry.DetectDominantFrame(trusted);
        IReadOnlyList<PdfTextLineUnit> lines = PdfTextGlyphStream.AssembleLines(trusted, frame,
            options.DetectMixedDirections, options.WordGapFactor, aspect);
        List<PdfTextLayerDiagnostic> diagnostics =
        [
            new(PdfTextLayerDiagnosticCodes.WritingFrameDetected,
                $"Dominant writing frame: {frame.Direction}{(frame.RotationDegrees != 0 ? $" ({frame.RotationDegrees:0.#}°)" : "")}.")
        ];
        if (context.ExtractionDiagnostics is { } extraction)
        {
            diagnostics.AddRange(extraction);
        }

        List<PdfTextLayoutCut> cuts = [];
        List<IReadOnlyList<PdfTextLineUnit>> leaves = [];
        Segment(lines, 0, cuts, leaves, options);

        foreach (PdfTextLayoutCut cut in cuts)
        {
            diagnostics.Add(new PdfTextLayerDiagnostic(PdfTextLayerDiagnosticCodes.CutLine,
                $"{cut.Axis} cut at {cut.Position:0.####} (gap {cut.GapSize:0.####})."));
        }

        List<PdfTextParagraph> paragraphs = [];
        foreach (IReadOnlyList<PdfTextLineUnit> leaf in leaves)
        {
            IReadOnlyList<PdfTextLineUnit> ordered = leaf
                .OrderBy(line => line.WritingBox.MinV)
                .ThenBy(line => line.WritingBox.MinU)
                .ToArray();
            paragraphs.AddRange(PdfTextGlyphStream.MergeParagraphs(ordered, frame,
                options.LineGapFactor, aspect));
        }

        if (untrusted.Count > 0)
        {
            diagnostics.Add(new PdfTextLayerDiagnostic(PdfTextLayerDiagnosticCodes.UntrustedGlyphs,
                $"{untrusted.Count} unconvertible native glyph(s) were declared as bounded OCR regions.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        IReadOnlyList<PdfTextRequiredRegion> required = UntrustedRegions(untrusted, options,
                aspect)
            .Select(region => (Region: PdfTextGlyphStream.ClipToScope(context.Region,
                region.Region), region))
            .Where(item => item.Region is not null)
            .Select(item => item.region with { Region = item.Region!.Value })
            .ToList();
        return Task.FromResult(Result<PdfTextLayerResult>.Success(
            PdfTextResultBuilder.Build(paragraphs, required, diagnostics, scoped.Count)));
    }

    /// <summary>
    /// The recursive core: at every level find the widest projection valley on each axis and
    /// cut at whichever is relatively deepest — a column boundary is a huge inline valley, a
    /// paragraph break a modest block valley. Recursion repeats the comparison inside each
    /// partition, so cut order always follows whitespace significance rather than a fixed
    /// axis alternation. A region that admits no significant valley is a leaf.
    /// </summary>
    private static void Segment(
        IReadOnlyList<PdfTextLineUnit> lines,
        int depth,
        List<PdfTextLayoutCut> cuts,
        List<IReadOnlyList<PdfTextLineUnit>> leaves,
        PdfTextLayerParseOptions options)
    {
        if (lines.Count <= 1 || depth >= MaxDepth)
        {
            leaves.Add(lines);
            return;
        }

        double medianBlock = WritingFrameGeometry.Median(
            lines.Select(line => line.WritingBox.BlockSize).Where(size => size > 0).ToArray(), 0.01);
        // The block valley is significant only beyond the expected line gap — the same
        // pitch-aware rule paragraph merging uses, so a normal interline gap inside a
        // column never reads as a paragraph/column boundary.
        double blockThreshold = PdfTextGlyphStream.ParagraphGapThreshold(lines,
            options.LineGapFactor);
        double inlineThreshold = Math.Max(medianBlock * options.ColumnGapFactor, 0.01);

        (double blockGap, int blockIndex) = WidestValley(lines, true);
        (double inlineGap, int inlineIndex) = WidestValley(lines, false);
        bool blockCut = blockGap > blockThreshold;
        bool inlineCut = inlineGap > inlineThreshold;
        if (!blockCut && !inlineCut)
        {
            leaves.Add(lines);
            return;
        }

        // Pick the axis whose valley is relatively largest against its own threshold.
        bool useBlock = blockCut &&
                        (!inlineCut || blockGap / blockThreshold >= inlineGap / inlineThreshold);
        IReadOnlyList<PdfTextLineUnit> ordered = useBlock
            ? lines.OrderBy(line => line.WritingBox.MinV).ToArray()
            : lines.OrderBy(line => line.WritingBox.MinU).ToArray();
        int split = useBlock ? blockIndex : inlineIndex;
        double gap = useBlock ? blockGap : inlineGap;
        cuts.Add(new PdfTextLayoutCut(useBlock ? "block" : "inline",
            (useBlock ? ordered[split].WritingBox.MinV : ordered[split].WritingBox.MinU) - gap / 2,
            gap));
        Segment(ordered.Take(split).ToArray(), depth + 1, cuts, leaves, options);
        Segment(ordered.Skip(split).ToArray(), depth + 1, cuts, leaves, options);
    }

    /// <summary>
    /// The widest valley in the axis projection: sort line extents along the axis and find
    /// the adjacent pair with the largest gap between a line's leading edge and the deepest
    /// extent seen so far — the classic profile-valley measurement, so a slightly indented
    /// continuation never registers as a valley.
    /// </summary>
    private static (double Gap, int Index) WidestValley(
        IReadOnlyList<PdfTextLineUnit> lines, bool block)
    {
        PdfTextLineUnit[] ordered = block
            ? lines.OrderBy(line => line.WritingBox.MinV).ToArray()
            : lines.OrderBy(line => line.WritingBox.MinU).ToArray();
        double bestGap = 0;
        int bestIndex = -1;
        double deepest = block ? ordered[0].WritingBox.MaxV : ordered[0].WritingBox.MaxU;
        for (int i = 1; i < ordered.Length; i++)
        {
            double leading = block ? ordered[i].WritingBox.MinV : ordered[i].WritingBox.MinU;
            double gap = leading - deepest;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestIndex = i;
            }

            deepest = Math.Max(deepest,
                block ? ordered[i].WritingBox.MaxV : ordered[i].WritingBox.MaxU);
        }

        return (bestGap, bestIndex);
    }

    /// <summary>
    /// Bounded OCR requests for genuinely unusable glyph runs — the explicit-region path, not
    /// a fallback. Runs are grouped by spatial adjacency so one damaged block does not merge
    /// the whole page into a single request.
    /// </summary>
    internal static IReadOnlyList<PdfTextRequiredRegion> UntrustedRegions(
        IReadOnlyList<PdfTextGlyph> untrusted,
        PdfTextLayerParseOptions options,
        double aspect = 1.0)
    {
        if (untrusted.Count == 0)
        {
            return Array.Empty<PdfTextRequiredRegion>();
        }

        WritingFrame frame = WritingFrameGeometry.DetectDominantFrame(untrusted);
        List<(PdfTextGlyph Glyph, WritingRect Rect)> projected = untrusted
            .Select(glyph => (glyph,
                WritingFrameGeometry.ToWritingRect(frame, glyph.BBox, aspect)))
            .OrderBy(item => item.Item2.MinV)
            .ThenBy(item => item.Item2.MinU)
            .ToList();
        List<List<(PdfTextGlyph Glyph, WritingRect Rect)>> runs = [[projected[0]]];
        foreach ((PdfTextGlyph glyph, WritingRect rect) in projected.Skip(1))
        {
            List<(PdfTextGlyph Glyph, WritingRect Rect)> last = runs[^1];
            double gap = rect.MinV - last.Max(item => item.Item2.MaxV);
            if (gap > 0.02)
            {
                runs.Add([]);
            }

            runs[^1].Add((glyph, rect));
        }

        List<PdfTextRequiredRegion> regions = [];
        foreach (List<(PdfTextGlyph Glyph, WritingRect Rect)> run in runs)
        {
            WritingRect box = run.Select(item => item.Rect).Aggregate((a, b) => a.Union(b));
            bool mapError = run.Any(item => item.Glyph.Flags.HasFlag(PdfGlyphFlags.UnicodeMapError));
            NormalizedBBox display = WritingFrameGeometry.ToDisplayBBox(frame, box, aspect);
            // A union of zero-area glyph boxes must still exceed the region-validity floor or
            // scope clipping would silently drop the request and strand the anchors.
            regions.Add(new PdfTextRequiredRegion(
                new NormalizedBBox(display.X, display.Y, Math.Max(display.Width, 1e-5),
                    Math.Max(display.Height, 1e-5)),
                mapError ? PdfTextOcrReasons.UnicodeMapError : PdfTextOcrReasons.UntrustworthyText,
                mapError
                    ? "Native characters carry Unicode-map errors."
                    : "Native characters are not Unicode-convertible.",
                PdfTextGlyphStream.AnchorRuns(run.Select(item => item.Glyph))));
        }

        return regions;
    }
}
