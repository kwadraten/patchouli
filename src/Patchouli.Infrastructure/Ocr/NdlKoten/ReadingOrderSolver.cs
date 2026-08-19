namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public static class ReadingOrderSolver
{
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
        public List<int> LineIdx { get; } = new();
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

    public static int[] Solve(Box[] boxes, double scale = 1.0, double tolerance = 0.25)
    {
        if (boxes.Length == 0)
        {
            return Array.Empty<int>();
        }

        Box[] normalized = Normalize(boxes, scale, tolerance);
        int[,] table = MakeMeshTable(normalized);
        BlockNode root = new(0, 0, table.GetLength(1), table.GetLength(0), null);
        BlockXyCut(table, root);
        AssignBboxToNode(root, normalized);
        SortNodes(root, normalized);
        int[] ranks = new int[boxes.Length];
        Array.Fill(ranks, -1);
        int rank = 0;
        GetRanking(root, ranks, ref rank);
        return ranks;
    }

    private static Box[] Normalize(Box[] boxes, double scale, double tolerance)
    {
        Box[] result = new Box[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            Box box = boxes[i];
            int x0 = Math.Min(box.X0, box.X1);
            int x1 = Math.Max(box.X0, box.X1);
            int y0 = Math.Min(box.Y0, box.Y1);
            int y1 = Math.Max(box.Y0, box.Y1);
            result[i] = new Box(x0, y0, x1, y1);
        }

        if (scale != 1.0)
        {
            int[] widths = result.Select(static b => b.X1 - b.X0).ToArray();
            int[] heights = result.Select(static b => b.Y1 - b.Y0).ToArray();
            int[] mins = result.Select(static b => Math.Min(b.X1 - b.X0, b.Y1 - b.Y0)).ToArray();
            double median = Median(mins);
            double lower = median * (1.0 - tolerance);
            double upper = median * (1.0 + tolerance);
            for (int i = 0; i < result.Length; i++)
            {
                int w = widths[i];
                int h = heights[i];
                int x0 = result[i].X0;
                int y0 = result[i].Y0;
                int x1 = result[i].X1;
                int y1 = result[i].Y1;
                if (w < h && lower <= w && w < upper)
                {
                    int delta = (int)((scale - 1.0) * w / 2.0);
                    result[i] = new Box(x0 - delta, y0, x1 + delta, y1);
                }
                else if (h < w && lower <= h && h < upper)
                {
                    int delta = (int)((scale - 1.0) * h / 2.0);
                    result[i] = new Box(x0, y0 - delta, x1, y1 + delta);
                }
            }
        }

        int xMin = result.Min(static b => b.X0);
        int yMin = result.Min(static b => b.Y0);
        int xMax = result.Max(static b => b.X1);
        int yMax = result.Max(static b => b.Y1);
        int wPage = Math.Max(1, xMax - xMin);
        int hPage = Math.Max(1, yMax - yMin);
        int num = result.Length;
        double grid = 100.0 * Math.Sqrt(num);
        double xGrid = wPage < hPage ? grid : grid * ((double)wPage / hPage);
        double yGrid = hPage < wPage ? grid : grid * ((double)hPage / wPage);

        for (int i = 0; i < result.Length; i++)
        {
            Box b = result[i];
            int nx0 = (int)((b.X0 - xMin) * xGrid / wPage);
            int ny0 = (int)((b.Y0 - yMin) * yGrid / hPage);
            int nx1 = (int)((b.X1 - xMin) * xGrid / wPage);
            int ny1 = (int)((b.Y1 - yMin) * yGrid / hPage);
            result[i] = new Box(
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
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static int[,] MakeMeshTable(Box[] boxes)
    {
        int xGrid = boxes.Max(static b => b.X1) + 1;
        int yGrid = boxes.Max(static b => b.Y1) + 1;
        int[,] table = new int[yGrid, xGrid];
        foreach (Box box in boxes)
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

    private static (int[] xHist, int[] yHist) CalcHist(int[,] table, int x0, int y0, int x1, int y1)
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

    private static (int start, int end, double score) CalcMinSpan(ReadOnlySpan<int> hist)
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

    private static void AssignBboxToNode(BlockNode root, Box[] boxes)
    {
        List<BlockNode> leaves = new();
        CollectLeaves(root, leaves);
        if (leaves.Count == 0)
        {
            return;
        }

        Box[] leafBoxes = leaves.Select(static l => new Box(l.X0, l.Y0, l.X1, l.Y1)).ToArray();
        for (int i = 0; i < boxes.Length; i++)
        {
            Box box = boxes[i];
            double bestIou = double.MinValue;
            int bestLeaf = 0;
            for (int j = 0; j < leafBoxes.Length; j++)
            {
                double iou = CalcIou(box, leafBoxes[j]);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    bestLeaf = j;
                }
            }

            leaves[bestLeaf].LineIdx.Add(i);
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

    private static double CalcIou(Box a, Box b)
    {
        int x0 = Math.Max(a.X0, b.X0);
        int y0 = Math.Max(a.Y0, b.Y0);
        int x1 = Math.Min(a.X1, b.X1);
        int y1 = Math.Min(a.Y1, b.Y1);
        int interWidth = Math.Max(0, x1 - x0);
        int interHeight = Math.Max(0, y1 - y0);
        double interArea = interWidth * interHeight;
        double areaA = (a.X1 - a.X0) * (a.Y1 - a.Y0);
        double areaB = (b.X1 - b.X0) * (b.Y1 - b.Y0);
        double unionArea = areaA + areaB - interArea;
        return unionArea <= 0 ? 0.0 : interArea / unionArea;
    }

    private static (int numLines, int numVertical) SortNodes(BlockNode node, Box[] boxes)
    {
        if (node.LineIdx.Count > 0)
        {
            int count = node.LineIdx.Count;
            int vertical = 0;
            foreach (int idx in node.LineIdx)
            {
                Box b = boxes[idx];
                if (b.X1 - b.X0 < b.Y1 - b.Y0)
                {
                    vertical++;
                }
            }

            node.NumLines = count;
            node.NumVerticalLines = vertical;
            bool isVertical = node.IsVertical();
            List<int> sorted = new(node.LineIdx);
            if (isVertical)
            {
                sorted.Sort((a, b) =>
                {
                    Box ba = boxes[a];
                    Box bb = boxes[b];
                    int cmp = bb.X0.CompareTo(ba.X0); // descending x
                    if (cmp != 0)
                    {
                        return cmp;
                    }

                    return ba.Y0.CompareTo(bb.Y0); // ascending y
                });
            }
            else
            {
                sorted.Sort((a, b) =>
                {
                    Box ba = boxes[a];
                    Box bb = boxes[b];
                    int cmp = ba.Y0.CompareTo(bb.Y0); // ascending y
                    if (cmp != 0)
                    {
                        return cmp;
                    }

                    return ba.X0.CompareTo(bb.X0); // ascending x
                });
            }

            node.LineIdx.Clear();
            node.LineIdx.AddRange(sorted);
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
        foreach (int idx in node.LineIdx)
        {
            ranks[idx] = rank++;
        }

        foreach (BlockNode child in node.Children)
        {
            GetRanking(child, ranks, ref rank);
        }
    }
}

public readonly record struct Box(int X0, int Y0, int X1, int Y1);
