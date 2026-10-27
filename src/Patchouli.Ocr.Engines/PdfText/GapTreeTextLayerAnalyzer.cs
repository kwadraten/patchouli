using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr.Engines.PdfText;

// This file is a direction-aware port of the GapTree sort algorithm to the shared
// WritingFrame coordinate model.
//
//   Upstream: "【间隙·树·排序算法】 GapTree_Sort_Algorithm"
//   Author: hiroi-sora — https://github.com/hiroi-sora/GapTree_Sort_Algorithm
//   Upstream commit: 281987caf36a9f5a7b40e8fcb690a7634502cab4 (gap_tree.py)
//
// MIT License
//
// Copyright (c) 2024 hiroi-sora
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// The port preserves the upstream pipeline exactly — row grouping, per-row gap tracking
// with interval intersection into vertical cutlines, the layout tree with lowest-parent
// selection, and preorder traversal for reading order. The only adaptation is the space
// the geometry runs in: every block coordinate is projected through the page's dominant
// WritingFrame first (x ↔ inline U, y ↔ block V), so the identical algorithm orders RTL,
// vertical-rl, vertical-lr and rotated pages without ever reversing strings or arrays.
// Cutlines and row gaps remain diagnosable artifacts, matching upstream's debug surface.

/// <summary>
/// GapTree sort: group line units into rows along the block axis, track each row's inline
/// gaps and intersect them across consecutive rows into vertical cutlines, build the layout
/// tree of gap-delimited regions, then preorder-traverse it into reading order. Unlike the
/// XY Cut provider this never splits by projection thresholds — a column boundary exists
/// only where a gap survives across a run of rows.
/// </summary>
public sealed class GapTreeTextLayerAnalyzer : IPdfTextLayerAnalyzer
{
    private const double Tolerance = 0.0001;

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
                "The page has no usable native text layer; the native GapTree provider does not fall back to OCR."));
        }

        if (untrusted.Count > 0)
        {
            // Damaged native text is an explicit failure for a selected native scheme unless
            // the caller explicitly opted into bounded OCR supplementation.
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

        List<PdfTextParagraph> paragraphs = [];
        if (lines.Count > 0)
        {
            // 1. Wrap line units and find the page's inline edges (upstream _get_units).
            List<Unit> units = lines.Select(line => new Unit(line.WritingBox, line)).ToList();
            units.Sort((a, b) => a.Box.MinV.CompareTo(b.Box.MinV));
            double pageLeft = units.Min(u => u.Box.MinU) - 1;
            double pageRight = units.Max(u => u.Box.MaxU) + 1;

            // 2. Rows and vertical cutlines (upstream _get_cuts_rows).
            (List<CutLine> cuts, List<List<Unit>> rows) = CutsAndRows(units, pageLeft, pageRight);
            foreach (CutLine cut in cuts)
            {
                diagnostics.Add(new PdfTextLayerDiagnostic(PdfTextLayerDiagnosticCodes.CutLine,
                    $"vertical cutline u∈[{cut.Left:0.####},{cut.Right:0.####}] rows {cut.StartRow}–{cut.EndRow}."));
            }

            // 3. Layout tree (upstream _get_layout_tree) and preorder traversal.
            TreeNode root = LayoutTree(cuts, rows);
            List<TreeNode> nodes = Preorder(root);
            foreach (TreeNode node in nodes)
            {
                if (node.Units.Count == 0)
                {
                    continue;
                }

                // Upstream emits the node's blocks as the grouped text unit; glyph-level
                // lines inside a node still merge into paragraphs by the shared gap rule.
                IReadOnlyList<PdfTextLineUnit> ordered = node.Units
                    .Select(unit => unit.Line)
                    .OrderBy(line => line.WritingBox.MinV)
                    .ThenBy(line => line.WritingBox.MinU)
                    .ToArray();
                paragraphs.AddRange(PdfTextGlyphStream.MergeParagraphs(ordered, frame,
                    options.LineGapFactor, aspect));
            }
        }

        if (untrusted.Count > 0)
        {
            diagnostics.Add(new PdfTextLayerDiagnostic(PdfTextLayerDiagnosticCodes.UntrustedGlyphs,
                $"{untrusted.Count} unconvertible native glyph(s) were declared as bounded OCR regions.",
                PdfTextLayerDiagnosticSeverity.Warning));
        }

        IReadOnlyList<PdfTextRequiredRegion> required =
            XyCutTextLayerAnalyzer.UntrustedRegions(untrusted, options, aspect)
                .Select(region => (Region: PdfTextGlyphStream.ClipToScope(context.Region,
                    region.Region), region))
                .Where(item => item.Region is not null)
                .Select(item => item.region with { Region = item.Region!.Value })
                .ToList();
        return Task.FromResult(Result<PdfTextLayerResult>.Success(
            PdfTextResultBuilder.Build(paragraphs, required, diagnostics, scoped.Count)));
    }

    /// <summary>An upstream "unit": a writing-space box plus the line it wraps.</summary>
    private sealed record Unit(WritingRect Box, PdfTextLineUnit Line);

    /// <summary>A completed vertical cutline: inline gap edges plus the row span it covers.</summary>
    private sealed record CutLine(double Left, double Right, int StartRow, int EndRow);

    private sealed class TreeNode
    {
        public double XLeft;
        public double XRight;
        public int RTop;
        public int RBottom;
        public List<Unit> Units { get; } = [];
        public List<TreeNode> Children { get; } = [];
    }

    /// <summary>
    /// Direct port of upstream _get_cuts_rows: rows are bands of units whose block extents
    /// touch; each row's inline gaps are intersected with the gap set carried over from the
    /// previous row. Gaps that stop intersecting complete into cutlines.
    /// </summary>
    private static (List<CutLine>, List<List<Unit>>) CutsAndRows(
        List<Unit> units, double pageLeft, double pageRight)
    {
        List<List<Unit>> rows = [];
        List<CutLine> completedCuts = [];
        //考察中的间隙: (left, right, startRow)
        List<(double Left, double Right, int StartRow)> gaps = [];
        int rowIndex = 0, unitIndex = 0;
        while (unitIndex < units.Count)
        {
            Unit unit = units[unitIndex];
            double unitBottom = unit.Box.MaxV;
            List<Unit> row = [unit];
            for (int i = unitIndex + 1; i < units.Count; i++)
            {
                Unit next = units[i];
                if (next.Box.MinV > unitBottom)
                {
                    break;
                }

                row.Add(next);
                unitIndex = i;
            }

            row.Sort((a, b) => a.Box.MinU != b.Box.MinU
                ? a.Box.MinU.CompareTo(b.Box.MinU)
                : a.Box.MaxU.CompareTo(b.Box.MaxU));

            // Current row's inline gaps, measured against the carried page edges.
            List<(double Left, double Right, int StartRow)> rowGaps = [];
            double searchStart = pageLeft;
            foreach (Unit item in row)
            {
                if (item.Box.MinU > searchStart)
                {
                    rowGaps.Add((searchStart, item.Box.MinU, rowIndex));
                }

                if (item.Box.MaxU > searchStart)
                {
                    searchStart = item.Box.MaxU;
                }
            }

            rowGaps.Add((searchStart, pageRight, rowIndex));

            // upstream update_gaps: intersect carried gaps with the row's gaps; non-
            // intersecting carry-overs are emitted as completed cutlines.
            bool[] keepGap = new bool[gaps.Count];
            bool[] isNewRowGap = Enumerable.Repeat(true, rowGaps.Count).ToArray();
            List<(double Left, double Right, int StartRow)> newGaps = [];
            for (int i1 = 0; i1 < gaps.Count; i1++)
            {
                (double l1, double r1, int startRow) = gaps[i1];
                for (int i2 = 0; i2 < rowGaps.Count; i2++)
                {
                    (double l2, double r2, _) = rowGaps[i2];
                    double interL = Math.Max(l1, l2);
                    double interR = Math.Min(r1, r2);
                    if (interL <= interR)
                    {
                        newGaps.Add((interL, interR, startRow));
                        keepGap[i1] = true;
                        isNewRowGap[i2] = false;
                    }
                }
            }

            for (int i2 = 0; i2 < rowGaps.Count; i2++)
            {
                if (isNewRowGap[i2])
                {
                    newGaps.Add(rowGaps[i2]);
                }
            }

            for (int i1 = 0; i1 < gaps.Count; i1++)
            {
                if (!keepGap[i1])
                {
                    completedCuts.Add(new CutLine(gaps[i1].Left, gaps[i1].Right,
                        gaps[i1].StartRow, rowIndex - 1));
                }
            }

            gaps = newGaps;
            rows.Add(row);
            unitIndex++;
            rowIndex++;
        }

        int lastRow = rows.Count - 1;
        foreach ((double left, double right, int startRow) in gaps)
        {
            completedCuts.Add(new CutLine(left, right, startRow, lastRow));
        }

        completedCuts.Sort((a, b) => a.Left.CompareTo(b.Left));
        return (completedCuts, rows);
    }

    /// <summary>
    /// Direct port of upstream _get_layout_tree: a node is a gap-delimited region kept open
    /// while its left/right edges keep meeting the same gap on consecutive rows; when an edge
    /// breaks, the node completes and attaches to the lowest completed node whose inline
    /// projection contains its right edge — the parent relation upstream defines.
    /// </summary>
    private static TreeNode LayoutTree(List<CutLine> cuts, List<List<Unit>> rows)
    {
        // Per-row gap list expanded from the cutlines (upstream rows_gaps).
        List<(double Left, double Right)>[] rowGaps = rows
            .Select(_ => new List<(double, double)>()).ToArray();
        foreach (CutLine cut in cuts)
        {
            for (int r = cut.StartRow; r <= cut.EndRow && r < rowGaps.Length; r++)
            {
                rowGaps[r].Add((cut.Left, cut.Right));
            }
        }

        TreeNode root = new()
        {
            XLeft = cuts[0].Left - 1,
            XRight = cuts[^1].Right + 1,
            RTop = -1,
            RBottom = -1
        };
        List<TreeNode> completedNodes = [root];
        List<TreeNode> openNodes = [];

        void Complete(TreeNode node)
        {
            // Upstream nudges the right edge left by 2 user-space units so the probe point
            // sits just inside the node; in normalized space a small fraction plays the role.
            double nodeRight = node.XRight - 0.002;
            List<TreeNode> candidates = [];
            double lowestBottom = -2;
            foreach (TreeNode candidate in completedNodes)
            {
                if (nodeRight < candidate.XLeft || nodeRight > candidate.XRight + Tolerance)
                {
                    continue;
                }

                if (candidate.RBottom >= node.RTop)
                {
                    continue;
                }

                if (candidate.RBottom > lowestBottom)
                {
                    lowestBottom = candidate.RBottom;
                    candidates = [candidate];
                }
                else if (candidate.RBottom == lowestBottom)
                {
                    candidates.Add(candidate);
                }
            }

            TreeNode parent = candidates.OrderByDescending(n => n.XRight).First();
            parent.Children.Add(node);
            completedNodes.Add(node);
        }

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            List<Unit> row = rows[rowIndex];
            List<(double Left, double Right)> gaps = rowGaps[rowIndex];
            int unitIndex = 0, gapIndex = 0;

            // Close open nodes whose edges no longer survive this row's gaps.
            List<TreeNode> stillOpen = [];
            foreach (TreeNode node in openNodes)
            {
                bool leftContinues = false, rightContinues = false, broken = false;
                foreach ((double left, double right) in gaps)
                {
                    if (right == node.XLeft)
                    {
                        leftContinues = true;
                    }

                    if (left == node.XRight)
                    {
                        rightContinues = true;
                    }

                    if ((node.XLeft < left && left < node.XRight) ||
                        (node.XLeft < right && right < node.XRight))
                    {
                        broken = true;
                        break;
                    }
                }

                if (!leftContinues || !rightContinues)
                {
                    broken = true;
                }

                if (broken)
                {
                    Complete(node);
                }
                else
                {
                    node.RBottom = rowIndex;
                    stillOpen.Add(node);
                }
            }

            openNodes = stillOpen;

            // Walk units left→right across the row's gap intervals (upstream's u_i/g_i sweep).
            while (unitIndex < row.Count)
            {
                Unit unit = row[unitIndex];
                double intervalLeft = gaps[gapIndex].Right;
                double intervalRight = gaps[gapIndex + 1].Left;
                if (unit.Box.MinU + Tolerance > intervalRight)
                {
                    gapIndex++;
                    continue;
                }

                TreeNode? existing = openNodes.FirstOrDefault(node =>
                    node.XLeft == intervalLeft && node.XRight == intervalRight);
                if (existing is not null)
                {
                    existing.Units.Add(unit);
                    unitIndex++;
                    continue;
                }

                openNodes.Add(new TreeNode
                {
                    XLeft = intervalLeft,
                    XRight = intervalRight,
                    RTop = rowIndex,
                    RBottom = rowIndex,
                    Units = { unit }
                });
                unitIndex++;
            }
        }

        foreach (TreeNode node in openNodes)
        {
            Complete(node);
        }

        foreach (TreeNode node in completedNodes)
        {
            node.Children.Sort((a, b) => a.XLeft.CompareTo(b.XLeft));
            node.Units.Sort((a, b) => a.Box.MinV.CompareTo(b.Box.MinV));
        }

        return root;
    }

    /// <summary>Preorder traversal: node before children, children in inline (reading) order.</summary>
    private static List<TreeNode> Preorder(TreeNode root)
    {
        List<TreeNode> nodes = [];
        Stack<TreeNode> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            TreeNode node = stack.Pop();
            nodes.Add(node);
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return nodes;
    }
}
