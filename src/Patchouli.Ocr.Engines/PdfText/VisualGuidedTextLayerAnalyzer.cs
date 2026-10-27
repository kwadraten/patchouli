using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.PdfText;

/// <summary>
/// Visual-guided scheme: the page image goes through the host-supplied shared layout detector
/// (the provider picks the model id/path from its own settings), detected regions are matched
/// against the native glyph stream, and only bounded missing/untrustworthy/non-textual areas
/// are handed to the image OCR engine. Every trustworthy native glyph is preserved exactly
/// once — either as native text inside its region or as preserved text outside all regions —
/// and is never re-requested for OCR. Non-textual regions are the only replacement semantic:
/// glyphs inside them belong to the figure/table/formula itself and are covered by that
/// bounded request rather than duplicated into a text box.
/// </summary>
public sealed class VisualGuidedTextLayerAnalyzer : IPdfTextLayerAnalyzer
{
    /// <summary>Detector label → document box type, for regions that keep native text.</summary>
    private static readonly IReadOnlyDictionary<string, string> TextualBoxTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = "text",
            ["content"] = "text",
            ["paragraph"] = "text",
            ["paragraph_title"] = "title",
            ["doc_title"] = "title",
            ["title"] = "title",
            ["abstract"] = "text",
            ["reference"] = "ref_text",
            ["references"] = "ref_text",
            ["header"] = "header",
            ["footer"] = "footer",
            ["number"] = "page_number",
            ["footnote"] = "page_footnote",
            ["algorithm"] = "algorithm",
            ["code"] = "code",
            ["aside_text"] = "aside_text",
            ["caption"] = "text",
            ["figure_title"] = "image_caption",
            ["figure_footnote"] = "image_footnote",
            ["table_title"] = "table_caption",
            ["table_footnote"] = "table_footnote",
            ["chart_title"] = "chart_caption",
            ["chart_footnote"] = "chart_footnote",
            ["code_title"] = "code_caption",
            ["code_caption"] = "code_caption"
        };

    /// <summary>Detector labels whose regions are atomic non-textual content for OCR.</summary>
    private static readonly HashSet<string> NonTextualBoxTypes = new(StringComparer.Ordinal)
    {
        "figure", "image", "seal", "table", "chart", "display_formula", "isolate_formula",
        "formula", "formula_number", "inline_formula"
    };

    public async Task<Result<PdfTextLayerResult>> AnalyzeAsync(
        PdfTextLayerContext context,
        PdfTextLayerParseOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.ReadPageImageAsync is null)
        {
            return Result<PdfTextLayerResult>.Failure(PdfTextLayerDiagnosticCodes.PageImageUnavailable,
                "The visual-guided provider requires a page image; the host supplied none.");
        }

        if (context.DetectLayoutAsync is null)
        {
            return Result<PdfTextLayerResult>.Failure(
                PdfTextLayerDiagnosticCodes.LayoutDetectorUnavailable,
                "The visual-guided provider requires the shared layout detector; the host supplied none.");
        }

        if (string.IsNullOrWhiteSpace(options.LayoutModelId) ||
            string.IsNullOrWhiteSpace(options.LayoutModelPath))
        {
            return Result<PdfTextLayerResult>.Failure(PdfTextLayerDiagnosticCodes.LayoutModelMissing,
                "The visual-guided provider requires a configured layout model (model-id and model-path).");
        }

        double aspect = WritingFrameGeometry.AspectOf(context.Geometry);
        IReadOnlyList<PdfTextGlyph> scoped =
            PdfTextGlyphStream.InScope(context.Glyphs, context.Region);

        // A text layer this damaged is unusable as a native basis: region-by-region OCR
        // requests would reconstruct most of the page, which is source corruption being
        // silently converted into adopted partial content. Fail before paying for a render.
        long badCount = scoped.LongCount(PdfTextGlyphStream.IsUntrusted);
        if (scoped.Count > 0 && badCount / (double)scoped.Count > options.MaxUntrustedRatio)
        {
            return Result<PdfTextLayerResult>.Failure(
                PdfTextLayerDiagnosticCodes.NativeTextUnusable,
                $"{badCount / (double)scoped.Count:0.##} of the page's native glyphs are not " +
                "Unicode-convertible; the text layer is untrustworthy.");
        }

        PdfTextPageImage image = await context.ReadPageImageAsync(options.LayoutDpi, cancellationToken);
        Result<IReadOnlyList<PdfTextLayoutRegion>> detected =
            await context.DetectLayoutAsync(image, options.LayoutModelId, options.LayoutModelPath,
                cancellationToken);
        if (detected.IsFailure)
        {
            return Result<PdfTextLayerResult>.Failure(detected.ErrorCode!, detected.ErrorMessage!);
        }

        List<PdfTextLayerDiagnostic> diagnostics = [];
        if (context.ExtractionDiagnostics is { } extraction)
        {
            diagnostics.AddRange(extraction);
        }

        // Drop regions with degenerate or out-of-range geometry — they cannot own glyphs and
        // would produce invalid region requests downstream.
        List<PdfTextLayoutRegion> regions = [];
        List<PdfTextLayoutRegion> invalid = [];
        foreach (PdfTextLayoutRegion candidate in detected.Value)
        {
            (PdfTextGlyphStream.IsValidRegion(candidate.BBox) ? regions : invalid).Add(candidate);
        }

        foreach (PdfTextLayoutRegion bad in invalid)
        {
            diagnostics.Add(new PdfTextLayerDiagnostic(PdfTextLayerDiagnosticCodes.InvalidRegion,
                $"Layout region '{bad.Label}' has degenerate geometry and was skipped.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        // Assign every glyph to the tightest region containing its center; native text outside
        // any region is preserved rather than silently OCR'd or dropped.
        Dictionary<int, List<PdfTextGlyph>> regionGlyphs = [];
        List<PdfTextGlyph> outsideTrusted = [];
        List<PdfTextGlyph> outsideUntrusted = [];
        foreach (PdfTextGlyph glyph in scoped)
        {
            int index = RegionIndexAt(regions, PdfTextGlyphStream.Center(glyph.BBox));
            if (index < 0)
            {
                (PdfTextGlyphStream.IsUntrusted(glyph) ? outsideUntrusted : outsideTrusted)
                    .Add(glyph);
            }
            else
            {
                (regionGlyphs.TryGetValue(index, out List<PdfTextGlyph>? list)
                        ? list
                        : regionGlyphs[index] = [])
                    .Add(glyph);
            }
        }

        List<PdfTextParagraph> paragraphs = [];
        List<PdfTextRequiredRegion> required = [];
        List<(int Order, PdfTextParagraph Paragraph)> ordered = [];
        for (int i = 0; i < regions.Count; i++)
        {
            PdfTextLayoutRegion region = regions[i];
            int order = region.Order ?? i;
            List<PdfTextGlyph> inside = regionGlyphs.TryGetValue(i, out List<PdfTextGlyph>? list)
                ? list
                : [];
            List<PdfTextGlyph> bad = inside.Where(PdfTextGlyphStream.IsUntrusted).ToList();
            List<PdfTextGlyph> good = inside.Except(bad).ToList();
            if (TextualBoxTypes.TryGetValue(region.Label, out string? boxType))
            {
                if (inside.Count == 0)
                {
                    // The layout calls for text here but the native layer has none.
                    required.Add(new PdfTextRequiredRegion(region.BBox,
                        PdfTextOcrReasons.MissingText,
                        $"Region '{region.Label}' (confidence {region.Confidence:0.##}) has no native text."));
                    continue;
                }

                if (good.Count > 0)
                {
                    EmitTextParagraphs(good, boxType, order, ordered, options, diagnostics, region,
                        aspect, bad.Count > 0);
                }

                if (bad.Count > 0)
                {
                    // Broken glyphs inside a text region: request OCR over their tight union
                    // only — the trusted glyphs stay native and are never re-requested.
                    required.Add(BoundedRequest(region, bad,
                        $"Region '{region.Label}' has unconvertible native characters" +
                        (bad.Count * 2 > inside.Count ? " (majority damaged)." : ".")));
                }
            }
            else if (NonTextualBoxTypes.Contains(region.Label))
            {
                if (options.RecognizeNonTextRegions)
                {
                    // Atomic non-textual content: the bounded region request explicitly
                    // replaces any native glyphs inside it — they are part of the figure.
                    required.Add(new PdfTextRequiredRegion(region.BBox,
                        PdfTextOcrReasons.NonTextualContent,
                        $"Region '{region.Label}' is non-textual content handled by the image engine.",
                        inside.Count == 0 ? null : PdfTextGlyphStream.AnchorRuns(inside)));
                }
                else
                {
                    // Recognition disabled: preserve whatever trustworthy native text the
                    // region carries instead of silently dropping it.
                    if (good.Count > 0)
                    {
                        EmitTextParagraphs(good, "text", order, ordered, options, diagnostics,
                            region, aspect, bad.Count > 0);
                    }

                    if (bad.Count > 0)
                    {
                        required.Add(BoundedRequest(region, bad,
                            $"Region '{region.Label}' has unconvertible native characters."));
                    }
                }
            }
            else
            {
                // Unknown label: preserve native text, request only genuinely broken glyphs.
                if (good.Count > 0)
                {
                    EmitTextParagraphs(good, "text", order, ordered, options, diagnostics, region,
                        aspect, bad.Count > 0);
                }

                if (bad.Count > 0)
                {
                    required.Add(BoundedRequest(region, bad,
                        $"Region '{region.Label}' has unconvertible native characters."));
                }
            }
        }

        if (outsideTrusted.Count > 0)
        {
            diagnostics.Add(new PdfTextLayerDiagnostic(
                PdfTextLayerDiagnosticCodes.GlyphsOutsideLayout,
                $"{outsideTrusted.Count} native glyph(s) fell outside every detected layout region; " +
                "they were preserved as native text.",
                PdfTextLayerDiagnosticSeverity.Warning));
            int tail = regions.Count == 0
                ? 0
                : (regions.Select((r, i) => r.Order ?? i).Max() + 1) * 1000;
            IReadOnlyList<PdfTextParagraph> outsideParagraphs = BuildParagraphs(outsideTrusted,
                options, aspect);
            for (int j = 0; j < outsideParagraphs.Count; j++)
            {
                ordered.Add((tail + j, outsideParagraphs[j]));
            }
        }

        paragraphs.AddRange(ordered.OrderBy(item => item.Order)
            .Select(item => item.Paragraph));
        // Unconvertible glyphs outside every region still get a bounded region request.
        List<PdfTextRequiredRegion> allRequired = required
            .Concat(XyCutTextLayerAnalyzer.UntrustedRegions(outsideUntrusted, options, aspect))
            .Select(region => (Region: PdfTextGlyphStream.ClipToScope(context.Region,
                region.Region), region))
            .Where(item => item.Region is not null)
            .Select(item => item.region with { Region = item.Region!.Value })
            .ToList();
        return Result<PdfTextLayerResult>.Success(
            PdfTextResultBuilder.Build(paragraphs, allRequired, diagnostics, scoped.Count));
    }

    /// <summary>
    /// Native text inside a region becomes paragraphs of the label-mapped box type. A
    /// low-coverage diagnostic only fires when the region also carries damaged glyphs —
    /// sparse ordinary text (a one-word heading in a large region) is not a damage signal.
    /// </summary>
    private static void EmitTextParagraphs(
        List<PdfTextGlyph> good,
        string boxType,
        int order,
        List<(int Order, PdfTextParagraph Paragraph)> ordered,
        PdfTextLayerParseOptions options,
        List<PdfTextLayerDiagnostic> diagnostics,
        PdfTextLayoutRegion region,
        double aspect,
        bool hasUntrusted)
    {
        double regionArea = region.BBox.Width * region.BBox.Height;
        if (hasUntrusted && regionArea > 1e-9)
        {
            // Union-style coverage: glyph boxes rarely overlap, but a per-glyph sum can only
            // overstate coverage, never produce a false low — a diagnostic never drops text.
            double coverage = good.Sum(glyph => glyph.BBox.Width * glyph.BBox.Height) / regionArea;
            if (coverage < options.MinNativeCoverage)
            {
                diagnostics.Add(new PdfTextLayerDiagnostic(
                    PdfTextLayerDiagnosticCodes.LowNativeCoverage,
                    $"Region '{region.Label}' has sparse trusted native text (coverage " +
                    $"{coverage:0.##}) alongside damaged glyphs; the trusted glyphs were " +
                    "still preserved as text.",
                    PdfTextLayerDiagnosticSeverity.Warning, region.BBox));
            }
        }

        IReadOnlyList<PdfTextParagraph> regionParagraphs = BuildParagraphs(good, options, aspect);
        for (int j = 0; j < regionParagraphs.Count; j++)
        {
            ordered.Add((order * 1000 + j, regionParagraphs[j] with
            {
                SubType = boxType is "text" ? null : boxType
            }));
        }
    }

    private static IReadOnlyList<PdfTextParagraph> BuildParagraphs(List<PdfTextGlyph> glyphs,
        PdfTextLayerParseOptions options, double aspect)
    {
        WritingFrame frame = WritingFrameGeometry.DetectDominantFrame(glyphs);
        IReadOnlyList<PdfTextLineUnit> lines = PdfTextGlyphStream.AssembleLines(glyphs,
            frame, options.DetectMixedDirections, options.WordGapFactor, aspect);
        return PdfTextGlyphStream.MergeParagraphs(
            lines.OrderBy(line => line.WritingBox.MinV).ThenBy(line => line.WritingBox.MinU)
                .ToArray(), frame, options.LineGapFactor, aspect);
    }

    /// <summary>A bounded OCR request over exactly the union of the damaged glyphs.</summary>
    private static PdfTextRequiredRegion BoundedRequest(
        PdfTextLayoutRegion region,
        List<PdfTextGlyph> bad,
        string detail)
    {
        double x = Math.Clamp(bad.Min(g => g.BBox.X), 0, 1);
        double y = Math.Clamp(bad.Min(g => g.BBox.Y), 0, 1);
        double right = Math.Clamp(bad.Max(g => g.BBox.X + g.BBox.Width), 0, 1);
        double bottom = Math.Clamp(bad.Max(g => g.BBox.Y + g.BBox.Height), 0, 1);
        bool mapError = bad.Any(g => g.Flags.HasFlag(PdfGlyphFlags.UnicodeMapError));
        // The floor must exceed IsValidRegion's 1e-6 threshold or a degenerate union would
        // be silently dropped by scope clipping.
        return new PdfTextRequiredRegion(
            new NormalizedBBox(x, y, Math.Max(right - x, 1e-5), Math.Max(bottom - y, 1e-5)),
            mapError ? PdfTextOcrReasons.UnicodeMapError : PdfTextOcrReasons.UntrustworthyText,
            detail, PdfTextGlyphStream.AnchorRuns(bad));
    }

    /// <summary>The tightest region containing the point, preferring smaller regions on overlap.</summary>
    private static int RegionIndexAt(IReadOnlyList<PdfTextLayoutRegion> regions, (double X, double Y) point)
    {
        int best = -1;
        double bestArea = double.MaxValue;
        for (int i = 0; i < regions.Count; i++)
        {
            NormalizedBBox box = regions[i].BBox;
            if (point.X < box.X || point.X > box.X + box.Width ||
                point.Y < box.Y || point.Y > box.Y + box.Height)
            {
                continue;
            }

            double area = box.Width * box.Height;
            if (area < bestArea)
            {
                best = i;
                bestArea = area;
            }
        }

        return best;
    }
}
