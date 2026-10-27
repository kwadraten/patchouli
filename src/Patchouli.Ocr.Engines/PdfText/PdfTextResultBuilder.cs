using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.PdfText;

/// <summary>
/// Turns ordered paragraphs plus explicit region requests into the typed
/// <see cref="PdfTextLayerResult"/>: <see cref="DocumentBoxSeed"/> candidates in reading order
/// with original-char anchors, and a coverage audit so no native glyph is silently dropped or
/// counted twice.
/// </summary>
public static class PdfTextResultBuilder
{
    public static PdfTextLayerResult Build(
        IReadOnlyList<PdfTextParagraph> paragraphs,
        IReadOnlyList<PdfTextRequiredRegion> requiredRegions,
        IReadOnlyList<PdfTextLayerDiagnostic> diagnostics,
        int suppliedGlyphCount)
    {
        List<PdfTextLayerBox> boxes = new(paragraphs.Count);
        List<PdfTextLayerDiagnostic> allDiagnostics = new(diagnostics);
        HashSet<int> accounted = [];
        HashSet<int> inBoxes = [];
        for (int i = 0; i < paragraphs.Count; i++)
        {
            PdfTextParagraph paragraph = paragraphs[i];
            NormalizedBBox bbox = Clamp(paragraph.DisplayBox);
            // Paragraph subtypes carry layout labels (title/ref_text/header/...): they are
            // document box types, not DocumentBoxSeed subtypes, which the model reserves
            // for Code/List/Image specializations.
            string boxType = paragraph.SubType is { Length: > 0 } label &&
                             DocumentBoxType.IsKnown(label)
                ? label
                : DocumentBoxType.Text;
            DocumentBoxPayload payload = boxType switch
            {
                DocumentBoxType.Equation => new EquationBoxPayload(paragraph.Text, true),
                DocumentBoxType.Code => new CodeBoxPayload(paragraph.Text),
                _ => new TextBoxPayload(paragraph.Text)
            };
            DocumentBoxSeed seed = new(null, null, i, boxType, null,
                bbox, payload);
            IReadOnlyList<PdfTextAnchor> anchors = PdfTextGlyphStream.AnchorRuns(
                paragraph.Lines.SelectMany(line => line.Glyphs));
            foreach (PdfTextAnchor anchor in anchors)
            {
                for (int j = anchor.StartCharIndex; j < anchor.StartCharIndex + anchor.CharCount; j++)
                {
                    accounted.Add(j);
                    inBoxes.Add(j);
                }
            }

            boxes.Add(new PdfTextLayerBox(seed, anchors));
        }

        // Ownership is exactly-once: a glyph already anchored by a produced box must never
        // also be covered by an OCR region request — the conflict is surfaced, not merged.
        int duplicated = 0;
        foreach (PdfTextRequiredRegion region in requiredRegions)
        {
            if (region.Anchors is null)
            {
                continue;
            }

            foreach (PdfTextAnchor anchor in region.Anchors)
            {
                for (int j = anchor.StartCharIndex; j < anchor.StartCharIndex + anchor.CharCount; j++)
                {
                    if (inBoxes.Contains(j))
                    {
                        duplicated++;
                    }
                    else
                    {
                        accounted.Add(j);
                    }
                }
            }
        }

        if (duplicated > 0)
        {
            allDiagnostics.Add(new PdfTextLayerDiagnostic(
                PdfTextLayerDiagnosticCodes.DuplicateGlyphOwnership,
                $"{duplicated} supplied glyph(s) were claimed by both a produced box and an " +
                "OCR region request; the box wins and the region anchor is reported.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        int unaccounted = suppliedGlyphCount - accounted.Count;
        if (unaccounted > 0)
        {
            allDiagnostics.Add(new PdfTextLayerDiagnostic(
                PdfTextLayerDiagnosticCodes.UnaccountedGlyphs,
                $"{unaccounted} supplied glyph(s) did not land in any produced box or required region.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        return new PdfTextLayerResult(boxes, requiredRegions, allDiagnostics,
            new PdfTextLayerStatistics(suppliedGlyphCount, accounted.Count, boxes.Count));
    }

    /// <summary>Clamps a computed box into the validated normalized-page domain.</summary>
    private static NormalizedBBox Clamp(NormalizedBBox box)
    {
        double x = Math.Clamp(box.X, 0, 1);
        double y = Math.Clamp(box.Y, 0, 1);
        double right = Math.Clamp(box.X + box.Width, 0, 1);
        double bottom = Math.Clamp(box.Y + box.Height, 0, 1);
        return new NormalizedBBox(x, y, Math.Max(right - x, 1e-9), Math.Max(bottom - y, 1e-9));
    }
}
