namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>One detected quadrilateral and its probability-map score.</summary>
internal sealed record RapidOcrDetBox(RapidOcrPoint[] Points, double Score)
{
    public double X => Points.Min(static point => point.X);

    public double Y => Points.Min(static point => point.Y);

    public double Right => Points.Max(static point => point.X);

    public double Bottom => Points.Max(static point => point.Y);
}

internal sealed record RapidOcrDetOptions(
    float Threshold,
    float BoxThreshold,
    int MaxCandidates,
    double UnclipRatio,
    bool UseDilation,
    int LimitSideLength,
    string LimitType,
    double[] Mean,
    double[] StandardDeviation)
{
    public const int MinimumSide = 3;

    public static RapidOcrDetOptions Default { get; } = new(
        0.3f,
        0.5f,
        1000,
        1.6,
        true,
        736,
        "min",
        [0.5, 0.5, 0.5],
        [0.5, 0.5, 0.5]);
}

/// <summary>
/// Managed port of upstream <c>ch_ppocr_det/utils.py</c>. OpenCV and pyclipper are
/// replaced by in-process rasterization, convex hulls, and a round polygon offset;
/// for the convex quadrilaterals produced by detection this reproduces the upstream
/// boxes without a native computer-vision dependency.
/// </summary>
internal static class DbPostProcess
{
    public static IReadOnlyList<RapidOcrDetBox> Run(float[] probabilityMap, int width, int height, int destinationWidth,
        int destinationHeight, RapidOcrDetOptions options)
    {
        bool[] mask = new bool[width * height];
        for (int index = 0; index < mask.Length; index++)
        {
            mask[index] = probabilityMap[index] > options.Threshold;
        }

        if (options.UseDilation)
        {
            mask = Dilate2x2(mask, width, height);
        }

        List<List<RapidOcrPoint>> regions = FindContourRegions(mask, width, height);
        List<(RapidOcrPoint[] Box, double Score)> boxes = new();
        int candidates = Math.Min(regions.Count, options.MaxCandidates);
        for (int index = 0; index < candidates; index++)
        {
            (RapidOcrPoint[] contourBox, double minSide) = RapidOcrGeometry.GetMiniBoxes(regions[index]);
            if (minSide < RapidOcrDetOptions.MinimumSide)
            {
                continue;
            }

            double score = BoxScoreFast(probabilityMap, width, height, contourBox);
            if (options.BoxThreshold > score)
            {
                continue;
            }

            double area = RapidOcrGeometry.PolygonArea(contourBox);
            double perimeter = RapidOcrGeometry.PolygonPerimeter(contourBox);
            if (perimeter <= 0)
            {
                continue;
            }

            double distance = area * options.UnclipRatio / perimeter;
            RapidOcrPoint[] offset = RapidOcrGeometry.OffsetPolygon(contourBox, distance);
            (RapidOcrPoint[] expandedBox, double expandedSide) = RapidOcrGeometry.GetMiniBoxes(offset);
            if (expandedSide < RapidOcrDetOptions.MinimumSide + 2)
            {
                continue;
            }

            RapidOcrPoint[] scaled = new RapidOcrPoint[4];
            for (int pointIndex = 0; pointIndex < 4; pointIndex++)
            {
                double x = Math.Round(expandedBox[pointIndex].X / width * destinationWidth,
                    MidpointRounding.ToEven);
                double y = Math.Round(expandedBox[pointIndex].Y / height * destinationHeight,
                    MidpointRounding.ToEven);
                scaled[pointIndex] = new RapidOcrPoint(Math.Clamp(x, 0, destinationWidth),
                    Math.Clamp(y, 0, destinationHeight));
            }

            boxes.Add((scaled, score));
        }

        List<RapidOcrDetBox> filtered = FilterDetections(boxes, destinationWidth, destinationHeight);
        return SortBoxes(filtered);
    }

    internal static bool[] Dilate2x2(bool[] mask, int width, int height)
    {
        // cv2.dilate uses the 2x2 kernel anchor (1, 1), so output (x, y) covers
        // source rows y-1..y and columns x-1..x with a constant border of 0.
        bool[] result = new bool[mask.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool value = mask[y * width + x];
                if (!value && x > 0)
                {
                    value = mask[y * width + x - 1];
                }

                if (!value && y > 0)
                {
                    value = mask[(y - 1) * width + x];
                }

                if (!value && x > 0 && y > 0)
                {
                    value = mask[(y - 1) * width + x - 1];
                }

                result[y * width + x] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Equivalent of <c>findContours(RETR_LIST)</c> for area purposes: every connected
    /// foreground region and every enclosed background hole becomes one region whose
    /// convex hull determines its <c>minAreaRect</c>.
    /// </summary>
    internal static List<List<RapidOcrPoint>> FindContourRegions(bool[] mask, int width, int height)
    {
        List<List<RapidOcrPoint>> regions = new();
        bool[] visited = new bool[mask.Length];
        int[] queue = new int[mask.Length];

        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start])
            {
                continue;
            }

            regions.Add(FloodFill(mask, visited, queue, width, height, start, true));
        }

        bool[] backgroundVisited = new bool[mask.Length];
        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] || backgroundVisited[start])
            {
                continue;
            }

            List<RapidOcrPoint> hole = FloodFill(mask, backgroundVisited, queue, width, height, start, false);
            if (!TouchesBorder(hole, width, height))
            {
                regions.Add(hole);
            }
        }

        regions.Sort(static (left, right) =>
        {
            (double leftY, double leftX) = TopMost(left);
            (double rightY, double rightX) = TopMost(right);
            int byY = leftY.CompareTo(rightY);
            return byY != 0 ? byY : leftX.CompareTo(rightX);
        });
        return regions;
    }

    private static (double Y, double X) TopMost(List<RapidOcrPoint> region)
    {
        double y = double.MaxValue;
        double x = double.MaxValue;
        foreach (RapidOcrPoint point in region)
        {
            if (point.Y < y || (point.Y == y && point.X < x))
            {
                y = point.Y;
                x = point.X;
            }
        }

        return (y, x);
    }

    private static bool TouchesBorder(List<RapidOcrPoint> region, int width, int height)
    {
        foreach (RapidOcrPoint point in region)
        {
            if (point.X <= 0 || point.Y <= 0 || point.X >= width - 1 || point.Y >= height - 1)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly int[][] ForegroundOffsets =
    [
        [-1, -1], [0, -1], [1, -1],
        [-1, 0], [1, 0],
        [-1, 1], [0, 1], [1, 1]
    ];

    private static readonly int[][] BackgroundOffsets = [[0, -1], [-1, 0], [1, 0], [0, 1]];

    private static List<RapidOcrPoint> FloodFill(bool[] mask, bool[] visited, int[] queue, int width, int height,
        int start, bool foreground)
    {
        List<RapidOcrPoint> region = new();
        int head = 0;
        int tail = 0;
        queue[tail++] = start;
        visited[start] = true;
        // Contours use 8-connectivity for foreground pixels and 4-connectivity for
        // background holes, matching OpenCV's border following.
        int[][] offsets = foreground ? ForegroundOffsets : BackgroundOffsets;

        while (head < tail)
        {
            int index = queue[head++];
            int x = index % width;
            int y = index / width;
            region.Add(new RapidOcrPoint(x, y));
            foreach (int[] offset in offsets)
            {
                int nextX = x + offset[0];
                int nextY = y + offset[1];
                if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height)
                {
                    continue;
                }

                int next = nextY * width + nextX;
                if (visited[next] || mask[next] != foreground)
                {
                    continue;
                }

                visited[next] = true;
                if (tail >= queue.Length)
                {
                    continue;
                }

                queue[tail++] = next;
            }
        }

        return region;
    }

    internal static double BoxScoreFast(float[] probabilityMap, int width, int height,
        IReadOnlyList<RapidOcrPoint> box)
    {
        double minX = box.Min(static point => point.X);
        double maxX = box.Max(static point => point.X);
        double minY = box.Min(static point => point.Y);
        double maxY = box.Max(static point => point.Y);
        int xmin = Math.Clamp((int)Math.Floor(minX), 0, width - 1);
        int xmax = Math.Clamp((int)Math.Ceiling(maxX), 0, width - 1);
        int ymin = Math.Clamp((int)Math.Floor(minY), 0, height - 1);
        int ymax = Math.Clamp((int)Math.Ceiling(maxY), 0, height - 1);

        RapidOcrPoint[] shifted = new RapidOcrPoint[box.Count];
        for (int index = 0; index < box.Count; index++)
        {
            // cv2.fillPoly truncates the float corners toward zero via astype(int32).
            shifted[index] = new RapidOcrPoint(Math.Truncate(box[index].X) - xmin,
                Math.Truncate(box[index].Y) - ymin);
        }

        double sum = 0;
        long count = 0;
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                if (!RapidOcrGeometry.PointInPolygon(shifted, x - xmin, y - ymin))
                {
                    continue;
                }

                sum += probabilityMap[y * width + x];
                count++;
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    internal static List<RapidOcrDetBox> FilterDetections(List<(RapidOcrPoint[] Box, double Score)> boxes,
        int imageWidth, int imageHeight)
    {
        List<RapidOcrDetBox> filtered = new();
        foreach ((RapidOcrPoint[] box, double score) in boxes)
        {
            RapidOcrPoint[] clockwise = RapidOcrGeometry.OrderPointsClockwise(box);
            RapidOcrPoint[] clipped = new RapidOcrPoint[4];
            for (int index = 0; index < 4; index++)
            {
                clipped[index] = new RapidOcrPoint(
                    Math.Clamp((int)clockwise[index].X, 0, imageWidth - 1),
                    Math.Clamp((int)clockwise[index].Y, 0, imageHeight - 1));
            }

            double rectWidth = (int)Distance(clipped[0], clipped[1]);
            double rectHeight = (int)Distance(clipped[0], clipped[3]);
            if (rectWidth <= 3 || rectHeight <= 3)
            {
                continue;
            }

            filtered.Add(new RapidOcrDetBox(clipped, score));
        }

        return filtered;
    }

    /// <summary>Upstream <c>TextDetector.sorted_boxes</c>: group by y then order by x.</summary>
    internal static List<RapidOcrDetBox> SortBoxes(IReadOnlyList<RapidOcrDetBox> boxes)
    {
        if (boxes.Count == 0)
        {
            return [];
        }

        List<RapidOcrDetBox> ySorted = boxes.OrderBy(static box => box.Points[0].Y).ToList();
        List<(RapidOcrDetBox Box, int Line)> withLines = new(ySorted.Count);
        int line = 0;
        double previousY = ySorted[0].Points[0].Y;
        for (int index = 0; index < ySorted.Count; index++)
        {
            double y = ySorted[index].Points[0].Y;
            if (index > 0 && y - previousY >= 10)
            {
                line++;
            }

            previousY = y;
            withLines.Add((ySorted[index], line));
        }

        return withLines
            .OrderBy(static item => item.Line)
            .ThenBy(static item => item.Box.Points[0].X)
            .Select(static item => item.Box)
            .ToList();
    }

    private static double Distance(RapidOcrPoint a, RapidOcrPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
