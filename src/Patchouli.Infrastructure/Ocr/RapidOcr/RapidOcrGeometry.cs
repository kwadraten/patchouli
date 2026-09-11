namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>2D point in the pixel space of a <see cref="RapidOcrImage" />.</summary>
internal readonly record struct RapidOcrPoint(double X, double Y);

/// <summary>
/// Managed geometry primitives used by the DB detector. They replace the OpenCV and
/// pyclipper calls in upstream RapidOCR while keeping the same results for the convex
/// quadrilaterals produced by detection.
/// </summary>
internal static class RapidOcrGeometry
{
    /// <summary>OpenCV <c>INTER_CUBIC</c> uses a cubic coefficient of -0.75.</summary>
    private const double CubicCoefficient = -0.75;

    public static IReadOnlyList<RapidOcrPoint> ConvexHull(IReadOnlyList<RapidOcrPoint> points)
    {
        if (points.Count <= 2)
        {
            return points.Distinct().ToArray();
        }

        RapidOcrPoint[] sorted = points.Distinct()
            .OrderBy(static p => p.X)
            .ThenBy(static p => p.Y)
            .ToArray();
        if (sorted.Length <= 2)
        {
            return sorted;
        }

        List<RapidOcrPoint> lower = new();
        foreach (RapidOcrPoint point in sorted)
        {
            while (lower.Count >= 2 &&
                   Cross(lower[^2], lower[^1], point) <= 0)
            {
                lower.RemoveAt(lower.Count - 1);
            }

            lower.Add(point);
        }

        List<RapidOcrPoint> upper = new();
        for (int index = sorted.Length - 1; index >= 0; index--)
        {
            RapidOcrPoint point = sorted[index];
            while (upper.Count >= 2 &&
                   Cross(upper[^2], upper[^1], point) <= 0)
            {
                upper.RemoveAt(upper.Count - 1);
            }

            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>Convex hull plus the minimum-area enclosing rectangle (OpenCV <c>minAreaRect</c>).</summary>
    public static (RapidOcrPoint[] Corners, double Width, double Height) MinAreaRect(
        IReadOnlyList<RapidOcrPoint> points)
    {
        IReadOnlyList<RapidOcrPoint> hull = ConvexHull(points);
        if (hull.Count == 0)
        {
            return ([], 0, 0);
        }

        if (hull.Count == 1)
        {
            RapidOcrPoint only = hull[0];
            return ([only, only, only, only], 0, 0);
        }

        double bestArea = double.MaxValue;
        double bestWidth = 0;
        double bestHeight = 0;
        RapidOcrPoint[] bestCorners = [];
        for (int index = 0; index < hull.Count; index++)
        {
            RapidOcrPoint start = hull[index];
            RapidOcrPoint end = hull[(index + 1) % hull.Count];
            double edgeX = end.X - start.X;
            double edgeY = end.Y - start.Y;
            double length = Math.Sqrt(edgeX * edgeX + edgeY * edgeY);
            if (length <= double.Epsilon)
            {
                continue;
            }

            double unitX = edgeX / length;
            double unitY = edgeY / length;
            double minU = double.MaxValue;
            double maxU = double.MinValue;
            double minV = double.MaxValue;
            double maxV = double.MinValue;
            foreach (RapidOcrPoint point in hull)
            {
                double u = point.X * unitX + point.Y * unitY;
                double v = -point.X * unitY + point.Y * unitX;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }

            double width = maxU - minU;
            double height = maxV - minV;
            double area = width * height;
            if (area >= bestArea)
            {
                continue;
            }

            bestArea = area;
            bestWidth = width;
            bestHeight = height;
            double centerU = (minU + maxU) / 2;
            double centerV = (minV + maxV) / 2;
            double centerX = centerU * unitX - centerV * unitY;
            double centerY = centerU * unitY + centerV * unitX;
            double halfWidth = width / 2;
            double halfHeight = height / 2;

            RapidOcrPoint Corner(double widthOffset, double heightOffset)
            {
                return new RapidOcrPoint(
                    centerX + widthOffset * unitX - heightOffset * unitY,
                    centerY + widthOffset * unitY + heightOffset * unitX);
            }

            bestCorners =
            [
                Corner(-halfWidth, -halfHeight),
                Corner(halfWidth, -halfHeight),
                Corner(halfWidth, halfHeight),
                Corner(-halfWidth, halfHeight)
            ];
        }

        if (bestCorners.Length == 0)
        {
            // All hull points collapse to a single location.
            RapidOcrPoint only = hull[0];
            return ([only, only, only, only], 0, 0);
        }

        return (bestCorners, bestWidth, bestHeight);
    }

    /// <summary>
    /// Reproduces upstream <c>get_mini_boxes</c>: sort the rectangle corners by x and
    /// reorder them into top-left, top-right, bottom-right, bottom-left.
    /// </summary>
    public static (RapidOcrPoint[] Box, double MinSide) GetMiniBoxes(IReadOnlyList<RapidOcrPoint> contour)
    {
        (RapidOcrPoint[] corners, double width, double height) = MinAreaRect(contour);
        RapidOcrPoint[] points = corners.OrderBy(static p => p.X).ToArray();
        int leftTop;
        int leftBottom;
        if (points[1].Y > points[0].Y)
        {
            leftTop = 0;
            leftBottom = 1;
        }
        else
        {
            leftTop = 1;
            leftBottom = 0;
        }

        int rightTop;
        int rightBottom;
        if (points[3].Y > points[2].Y)
        {
            rightTop = 2;
            rightBottom = 3;
        }
        else
        {
            rightTop = 3;
            rightBottom = 2;
        }

        return ([points[leftTop], points[rightTop], points[rightBottom], points[leftBottom]],
            Math.Min(width, height));
    }

    /// <summary>Reorders four points into top-left, top-right, bottom-right, bottom-left.</summary>
    public static RapidOcrPoint[] OrderPointsClockwise(IReadOnlyList<RapidOcrPoint> points)
    {
        RapidOcrPoint[] sorted = points.OrderBy(static p => p.X).ToArray();
        RapidOcrPoint[] left = sorted.Take(2).OrderBy(static p => p.Y).ToArray();
        RapidOcrPoint[] right = sorted.Skip(2).Take(2).OrderBy(static p => p.Y).ToArray();
        return [left[0], right[0], right[1], left[1]];
    }

    public static double PolygonArea(IReadOnlyList<RapidOcrPoint> points)
    {
        double sum = 0;
        for (int index = 0; index < points.Count; index++)
        {
            RapidOcrPoint current = points[index];
            RapidOcrPoint next = points[(index + 1) % points.Count];
            sum += current.X * next.Y - next.X * current.Y;
        }

        return Math.Abs(sum) / 2;
    }

    public static double PolygonPerimeter(IReadOnlyList<RapidOcrPoint> points)
    {
        double sum = 0;
        for (int index = 0; index < points.Count; index++)
        {
            RapidOcrPoint current = points[index];
            RapidOcrPoint next = points[(index + 1) % points.Count];
            double dx = next.X - current.X;
            double dy = next.Y - current.Y;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }

        return sum;
    }

    /// <summary>
    /// pyclipper round offset of a convex polygon. The Minkowski sum with a disk is
    /// sampled with a fine polygon; because detection always offsets a convex
    /// quadrilateral, the convex hull of the sampled support points is the offset shape.
    /// </summary>
    public static RapidOcrPoint[] OffsetPolygon(IReadOnlyList<RapidOcrPoint> polygon, double distance,
        int arcSamples = 64)
    {
        if (distance <= 0 || polygon.Count == 0)
        {
            return polygon.ToArray();
        }

        List<RapidOcrPoint> candidates = new(polygon.Count * arcSamples);
        for (int sample = 0; sample < arcSamples; sample++)
        {
            double angle = 2 * Math.PI * sample / arcSamples;
            double offsetX = Math.Cos(angle) * distance;
            double offsetY = Math.Sin(angle) * distance;
            foreach (RapidOcrPoint point in polygon)
            {
                candidates.Add(new RapidOcrPoint(point.X + offsetX, point.Y + offsetY));
            }
        }

        return ConvexHull(candidates).ToArray();
    }

    public static bool PointInPolygon(IReadOnlyList<RapidOcrPoint> polygon, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            RapidOcrPoint current = polygon[i];
            RapidOcrPoint previous = polygon[j];
            bool crosses = current.Y > y != previous.Y > y &&
                           x < (previous.X - current.X) * (y - current.Y) /
                           (previous.Y - current.Y) + current.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Solves the projective transform that maps the four <paramref name="from" /> points
    /// onto the four <paramref name="to" /> points (OpenCV <c>getPerspectiveTransform</c>).
    /// </summary>
    public static double[] SolvePerspective(IReadOnlyList<RapidOcrPoint> from, IReadOnlyList<RapidOcrPoint> to)
    {
        double[,] matrix = new double[8, 9];
        for (int index = 0; index < 4; index++)
        {
            double x = from[index].X;
            double y = from[index].Y;
            double targetX = to[index].X;
            double targetY = to[index].Y;
            int row = index * 2;
            matrix[row, 0] = x;
            matrix[row, 1] = y;
            matrix[row, 2] = 1;
            matrix[row, 6] = -x * targetX;
            matrix[row, 7] = -y * targetX;
            matrix[row, 8] = targetX;
            matrix[row + 1, 3] = x;
            matrix[row + 1, 4] = y;
            matrix[row + 1, 5] = 1;
            matrix[row + 1, 6] = -x * targetY;
            matrix[row + 1, 7] = -y * targetY;
            matrix[row + 1, 8] = targetY;
        }

        for (int column = 0; column < 8; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 8; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (pivot != column)
            {
                for (int k = column; k < 9; k++)
                {
                    (matrix[column, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[column, k]);
                }
            }

            double divisor = matrix[column, column];
            if (Math.Abs(divisor) < 1e-12)
            {
                continue;
            }

            for (int k = column; k < 9; k++)
            {
                matrix[column, k] /= divisor;
            }

            for (int row = 0; row < 8; row++)
            {
                if (row == column)
                {
                    continue;
                }

                double factor = matrix[row, column];
                if (factor == 0)
                {
                    continue;
                }

                for (int k = column; k < 9; k++)
                {
                    matrix[row, k] -= factor * matrix[column, k];
                }
            }
        }

        double[] result = new double[9];
        for (int index = 0; index < 8; index++)
        {
            result[index] = matrix[index, 8];
        }

        result[8] = 1;
        return result;
    }

    /// <summary>
    /// Warps <paramref name="source" /> into a <paramref name="width" /> by
    /// <paramref name="height" /> image using the supplied output-to-input mapping,
    /// with OpenCV <c>INTER_CUBIC</c> sampling and <c>BORDER_REPLICATE</c>.
    /// </summary>
    public static RapidOcrImage WarpPerspective(RapidOcrImage source, double[] outputToInput, int width, int height)
    {
        RapidOcrImage destination = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double denominator = outputToInput[6] * x + outputToInput[7] * y + outputToInput[8];
                if (Math.Abs(denominator) < 1e-12)
                {
                    continue;
                }

                double sourceX = (outputToInput[0] * x + outputToInput[1] * y + outputToInput[2]) / denominator;
                double sourceY = (outputToInput[3] * x + outputToInput[4] * y + outputToInput[5]) / denominator;
                int target = (y * width + x) * RapidOcrImage.ChannelCount;
                for (int channel = 0; channel < RapidOcrImage.ChannelCount; channel++)
                {
                    destination.Pixels[target + channel] =
                        (byte)Math.Clamp((int)Math.Round(BicubicSample(source, channel, sourceX, sourceY)), 0, 255);
                }
            }
        }

        return destination;
    }

    private static double BicubicSample(RapidOcrImage source, int channel, double x, double y)
    {
        int baseX = (int)Math.Floor(x);
        int baseY = (int)Math.Floor(y);
        double fractionX = x - baseX;
        double fractionY = y - baseY;
        double sum = 0;
        for (int offsetY = -1; offsetY <= 2; offsetY++)
        {
            double weightY = CubicWeight(fractionY - offsetY);
            if (weightY == 0)
            {
                continue;
            }

            int sampleY = Math.Clamp(baseY + offsetY, 0, source.Height - 1);
            for (int offsetX = -1; offsetX <= 2; offsetX++)
            {
                double weightX = CubicWeight(fractionX - offsetX);
                if (weightX == 0)
                {
                    continue;
                }

                int sampleX = Math.Clamp(baseX + offsetX, 0, source.Width - 1);
                int index = (sampleY * source.Width + sampleX) * RapidOcrImage.ChannelCount + channel;
                sum += source.Pixels[index] * weightX * weightY;
            }
        }

        return sum;
    }

    private static double CubicWeight(double value)
    {
        double x = Math.Abs(value);
        if (x <= 1)
        {
            return ((CubicCoefficient + 2) * x - (CubicCoefficient + 3)) * x * x + 1;
        }

        if (x < 2)
        {
            return ((CubicCoefficient * x - 5 * CubicCoefficient) * x + 8 * CubicCoefficient) * x -
                   4 * CubicCoefficient;
        }

        return 0;
    }

    private static double Cross(RapidOcrPoint origin, RapidOcrPoint a, RapidOcrPoint b)
    {
        return (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
    }
}
