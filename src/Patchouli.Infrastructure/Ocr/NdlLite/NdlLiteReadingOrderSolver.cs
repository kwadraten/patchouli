namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Native port of the official <c>reading_order/xy_cut/block_xy_cut.py</c>
/// recursive XY-cut reading-order solver used by ndl-lab/ndlocr-lite.
/// </summary>
/// <remarks>
/// The algorithm is shared with the NDL Koten pipeline, but this port keeps the
/// upstream one-versus-many IoU formula (which adds one pixel to every span) and
/// the upstream coarse-grain rounding, so ranks match the NDLOCR-Lite release the
/// rest of this engine is pinned to.
/// </remarks>
public static class NdlLiteReadingOrderSolver
{
    public static int[] Solve(IReadOnlyList<NdlLiteBox> boxes, double scale = 1.0, double tolerance = 0.25)
    {
        if (boxes.Count == 0)
        {
            return [];
        }

        NdlLiteBox[] normalized = Normalize(boxes, scale, tolerance);
        int[,] table = MakeMeshTable(normalized);
        BlockNode root = new(0, 0, table.GetLength(1), table.GetLength(0), null);
        BlockXyCut(table, root);
        AssignBboxToNode(root, normalized);
        SortNodes(root, normalized);

        int[] ranks = new int[boxes.Count];
        Array.Fill(ranks, -1);
        int rank = 0;
        GetRanking(root, ranks, ref rank);
        return ranks;
    }

    internal static NdlLiteBox[] Normalize(IReadOnlyList<NdlLiteBox> boxes, double scale, double tolerance)
    {
        NdlLiteBox[] result = new NdlLiteBox[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            NdlLiteBox box = boxes[i];
            int x1 = box.X0 < box.X1 ? box.X1 : box.X0;
            int y1 = box.Y0 < box.Y1 ? box.Y1 : box.Y0;
            result[i] = new NdlLiteBox(box.X0, box.Y0, x1, y1);
        }

        if (scale != 1.0)
        {
            int[] widths = new int[result.Length];
            int[] heights = new int[result.Length];
            int[] minimums = new int[result.Length];
            for (int i = 0; i < result.Length; i++)
            {
                widths[i] = result[i].Width;
                heights[i] = result[i].Height;
                minimums[i] = Math.Min(widths[i], heights[i]);
            }

            double median = Median(minimums);
            double lower = median * (1.0 - tolerance);
            double upper = median * (1.0 + tolerance);
            for (int i = 0; i < result.Length; i++)
            {
                int w = widths[i];
                int h = heights[i];
                if (w < h && lower <= w && w < upper)
                {
                    int delta = (int)Math.Floor((scale - 1.0) * w / 2.0);
                    result[i] = new NdlLiteBox(result[i].X0 - delta, result[i].Y0, result[i].X1 + delta,
                        result[i].Y1);
                }
                else if (h < w && lower <= h && h < upper)
                {
                    int delta = (int)Math.Floor((scale - 1.0) * h / 2.0);
                    result[i] = new NdlLiteBox(result[i].X0, result[i].Y0 - delta, result[i].X1,
                        result[i].Y1 + delta);
                }
            }
        }

        int xMin = result.Min(static box => box.X0);
        int yMin = result.Min(static box => box.Y0);
        int xMax = result.Max(static box => box.X1);
        int yMax = result.Max(static box => box.Y1);
        double wPage = Math.Max(1, xMax - xMin);
        double hPage = Math.Max(1, yMax - yMin);
        double grid = 100.0 * Math.Sqrt(result.Length);
        double xGrid = wPage < hPage ? grid : grid * (wPage / hPage);
        double yGrid = hPage < wPage ? grid : grid * (hPage / wPage);

        for (int i = 0; i < result.Length; i++)
        {
            NdlLiteBox box = result[i];
            int nx0 = (int)((box.X0 - xMin) * xGrid / wPage);
            int ny0 = (int)((box.Y0 - yMin) * yGrid / hPage);
            int nx1 = (int)((box.X1 - xMin) * xGrid / wPage);
            int ny1 = (int)((box.Y1 - yMin) * yGrid / hPage);
            result[i] = new NdlLiteBox(
                Math.Max(0, nx0),
                Math.Max(0, ny0),
                Math.Max(0, nx1),
                Math.Max(0, ny1));
        }

        return result;
    }

    private static double Median(int[] values)
    {
        if (values.Length == 0)
        {
            return 0.0;
        }

        int[] sorted = (int[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    internal static int[,] MakeMeshTable(IReadOnlyList<NdlLiteBox> normalized)
    {
        int xGrid = normalized.Max(static box => box.X1) + 1;
        int yGrid = normalized.Max(static box => box.Y1) + 1;
        int[,] table = new int[yGrid, xGrid];
        foreach (NdlLiteBox box in normalized)
        {
            for (int y = box.Y0; y < box.Y1 && y < yGrid; y++)
            {
                for (int x = box.X0; x < box.X1 && x < xGrid; x++)
                {
                    table[y, x] = 1;
                }
            }
        }

        return table;
    }

    private static void BlockXyCut(int[,] table, BlockNode node)
    {
        int x0 = node.X0;
        int y0 = node.Y0;
        int x1 = node.X1;
        int y1 = node.Y1;
        if (!(x0 < x1 && y0 < y1))
        {
            return;
        }

        (int[] xHist, int[] yHist) = CalcHist(table, x0, y0, x1, y1);
        (int xBeg, int xEnd, double xVal) = CalcMinSpan(xHist);
        (int yBeg, int yEnd, double yVal) = CalcMinSpan(yHist);
        xBeg += x0;
        xEnd += x0;
        yBeg += y0;
        yEnd += y0;

        if (x0 == xBeg && x1 == xEnd && y0 == yBeg && y1 == yEnd)
        {
            return;
        }

        if (yVal < xVal)
        {
            SplitX(node, table, xBeg, xEnd);
        }
        else if (xVal < yVal)
        {
            SplitY(node, table, yBeg, yEnd);
        }
        else if (xEnd - xBeg < yEnd - yBeg)
        {
            SplitY(node, table, yBeg, yEnd);
        }
        else
        {
            SplitX(node, table, xBeg, xEnd);
        }
    }

    private static (int[] XHist, int[] YHist) CalcHist(int[,] table, int x0, int y0, int x1, int y1)
    {
        int width = x1 - x0;
        int height = y1 - y0;
        int[] xHist = new int[width];
        int[] yHist = new int[height];
        for (int y = y0; y < y1; y++)
        {
            int rowSum = 0;
            for (int x = x0; x < x1; x++)
            {
                if (table[y, x] != 0)
                {
                    xHist[x - x0]++;
                    rowSum++;
                }
            }

            yHist[y - y0] = rowSum;
        }

        return (xHist, yHist);
    }

    internal static (int Start, int End, double Score) CalcMinSpan(ReadOnlySpan<int> hist)
    {
        if (hist.Length == 1)
        {
            return (0, 1, 0.0);
        }

        int minVal = int.MaxValue;
        int maxVal = int.MinValue;
        foreach (int value in hist)
        {
            if (value < minVal)
            {
                minVal = value;
            }

            if (value > maxVal)
            {
                maxVal = value;
            }
        }

        int bestStart = 0;
        int bestEnd = 0;
        int bestLength = -1;
        int i = 0;
        while (i < hist.Length)
        {
            if (hist[i] == minVal)
            {
                int start = i;
                while (i < hist.Length && hist[i] == minVal)
                {
                    i++;
                }

                int length = i - start;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = start;
                    bestEnd = i;
                }
            }
            else
            {
                i++;
            }
        }

        double score = maxVal > 0 ? -(double)minVal / maxVal : 0.0;
        return (bestStart, bestEnd, score);
    }

    private static void Split(BlockNode parent, int[,] table, int? x0 = null, int? y0 = null, int? x1 = null,
        int? y1 = null)
    {
        int nx0 = x0 ?? parent.X0;
        int ny0 = y0 ?? parent.Y0;
        int nx1 = x1 ?? parent.X1;
        int ny1 = y1 ?? parent.Y1;
        if (!(nx0 < nx1 && ny0 < ny1))
        {
            return;
        }

        if (nx0 == parent.X0 && ny0 == parent.Y0 && nx1 == parent.X1 && ny1 == parent.Y1)
        {
            return;
        }

        BlockNode child = new(nx0, ny0, nx1, ny1, parent);
        parent.Children.Add(child);
        BlockXyCut(table, child);
    }

    private static void SplitX(BlockNode parent, int[,] table, int x0, int x1)
    {
        Split(parent, table, x1: x0);
        Split(parent, table, x0, x1: x1);
        Split(parent, table, x1);
    }

    private static void SplitY(BlockNode parent, int[,] table, int y0, int y1)
    {
        Split(parent, table, y1: y0);
        Split(parent, table, y0: y0, y1: y1);
        Split(parent, table, y0: y1);
    }

    private static void AssignBboxToNode(BlockNode root, IReadOnlyList<NdlLiteBox> boxes)
    {
        List<BlockNode> leaves = new();
        CollectLeaves(root, leaves);
        if (leaves.Count == 0)
        {
            return;
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            NdlLiteBox box = boxes[i];
            double bestIou = double.NegativeInfinity;
            int bestLeaf = 0;
            for (int j = 0; j < leaves.Count; j++)
            {
                BlockNode leaf = leaves[j];
                double iou = CalcIou(box, new NdlLiteBox(leaf.X0, leaf.Y0, leaf.X1, leaf.Y1));
                if (double.IsNaN(iou))
                {
                    iou = 0.0;
                }

                if (iou > bestIou)
                {
                    bestIou = iou;
                    bestLeaf = j;
                }
            }

            leaves[bestLeaf].LineIndexes.Add(i);
        }
    }

    private static void CollectLeaves(BlockNode node, List<BlockNode> leaves)
    {
        if (node.Children.Count == 0)
        {
            leaves.Add(node);
            return;
        }

        foreach (BlockNode child in node.Children)
        {
            CollectLeaves(child, leaves);
        }
    }

    /// <summary>
    /// Upstream one-versus-many IoU. Spans are inclusive (<c>+1</c>) and the
    /// denominator is asymmetric, exactly as written in <c>block_xy_cut.py</c>.
    /// </summary>
    internal static double CalcIou(NdlLiteBox box, NdlLiteBox target)
    {
        int overlapX0 = Math.Max(box.X0, target.X0);
        int overlapY0 = Math.Max(box.Y0, target.Y0);
        int overlapX1 = Math.Min(box.X1, target.X1);
        int overlapY1 = Math.Min(box.Y1, target.Y1);
        double intersection = Math.Max(0, overlapX1 - overlapX0 + 1) * (double)Math.Max(0, overlapY1 - overlapY0 + 1);
        double boxArea = (box.X1 - target.X0 + 1) * (double)(box.Y1 - target.Y0 + 1);
        double targetArea = (target.X1 - box.X0 + 1) * (double)(target.Y1 - box.Y0 + 1);
        double union = boxArea + targetArea - intersection;
        return union == 0.0 ? double.NaN : intersection / union;
    }

    private static (int NumLines, int NumVertical) SortNodes(BlockNode node, IReadOnlyList<NdlLiteBox> boxes)
    {
        if (node.LineIndexes.Count > 0)
        {
            int count = node.LineIndexes.Count;
            int vertical = 0;
            foreach (int index in node.LineIndexes)
            {
                NdlLiteBox box = boxes[index];
                if (box.Width < box.Height)
                {
                    vertical++;
                }
            }

            node.NumLines = count;
            node.NumVerticalLines = vertical;
            bool isVertical = node.IsVertical();
            List<int> sorted = isVertical
                ? node.LineIndexes
                    .OrderByDescending(index => boxes[index].X0)
                    .ThenBy(index => boxes[index].Y0)
                    .ToList()
                : node.LineIndexes
                    .OrderBy(index => boxes[index].Y0)
                    .ThenBy(index => boxes[index].X0)
                    .ToList();
            node.LineIndexes.Clear();
            node.LineIndexes.AddRange(sorted);
            return (count, vertical);
        }

        int totalLines = 0;
        int totalVertical = 0;
        foreach (BlockNode child in node.Children)
        {
            (int childLines, int childVertical) = SortNodes(child, boxes);
            totalLines += childLines;
            totalVertical += childVertical;
        }

        node.NumLines = totalLines;
        node.NumVerticalLines = totalVertical;
        if (node.IsXSplit() && node.IsVertical())
        {
            node.Children.Reverse();
        }

        return (totalLines, totalVertical);
    }

    private static void GetRanking(BlockNode node, int[] ranks, ref int rank)
    {
        foreach (int index in node.LineIndexes)
        {
            ranks[index] = rank++;
        }

        foreach (BlockNode child in node.Children)
        {
            GetRanking(child, ranks, ref rank);
        }
    }

    private sealed class BlockNode
    {
        public BlockNode(int x0, int y0, int x1, int y1, BlockNode? parent)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            Parent = parent;
        }

        public int X0 { get; }
        public int Y0 { get; }
        public int X1 { get; }
        public int Y1 { get; }
        public BlockNode? Parent { get; }
        public List<BlockNode> Children { get; } = new();
        public List<int> LineIndexes { get; } = new();
        public int NumLines { get; set; }
        public int NumVerticalLines { get; set; }

        public bool IsXSplit()
        {
            foreach (BlockNode child in Children)
            {
                if (child.Y0 != Y0 || child.Y1 != Y1)
                {
                    return false;
                }
            }

            return true;
        }

        public bool IsVertical()
        {
            return NumLines < NumVerticalLines * 2;
        }
    }
}
