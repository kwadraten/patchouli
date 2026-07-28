using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Core.Documents;

public sealed record DocumentBoxOverlap(DocumentBox First, DocumentBox Second, NormalizedBBox Intersection);

public static class DocumentBoxOverlapDetector
{
    private const double SignificantOverlapRatio = 0.1;

    // Reports significant overlaps between ordinary sibling boxes. Parent-child nesting is
    // legitimate and never reported; staging normalizes contained boxes into children at import.
    public static IReadOnlyList<DocumentBoxOverlap> Detect(IReadOnlyList<DocumentBox> boxes)
    {
        List<DocumentBoxOverlap> overlaps = [];
        foreach (IGrouping<DocumentBoxId?, DocumentBox> group in boxes.GroupBy(box => box.ParentBoxId))
        {
            DocumentBox[] siblings = group
                .Where(box => box.BoxType != DocumentBoxType.LogicalPage && !box.Suppressed)
                .ToArray();
            for (int first = 0; first < siblings.Length; first++)
            for (int second = first + 1; second < siblings.Length; second++)
            {
                DocumentBox left = siblings[first];
                DocumentBox right = siblings[second];
                if (DocumentBoxType.AllowsOverlap(left.BoxType) || DocumentBoxType.AllowsOverlap(right.BoxType))
                {
                    continue;
                }

                if (HasSignificantIntersection(left.BBox, right.BBox, out NormalizedBBox intersection))
                {
                    overlaps.Add(new DocumentBoxOverlap(left, right, intersection));
                }
            }
        }

        return overlaps;
    }

    private static bool HasSignificantIntersection(
        NormalizedBBox left,
        NormalizedBBox right,
        out NormalizedBBox intersection)
    {
        double x = Math.Max(left.X, right.X);
        double y = Math.Max(left.Y, right.Y);
        double width = Math.Min(left.X + left.Width, right.X + right.Width) - x;
        double height = Math.Min(left.Y + left.Height, right.Y + right.Height) - y;
        if (width <= 0 || height <= 0)
        {
            intersection = default;
            return false;
        }

        double intersectionArea = width * height;
        double smallerArea = Math.Min(left.Width * left.Height, right.Width * right.Height);
        if (intersectionArea / smallerArea < SignificantOverlapRatio)
        {
            intersection = default;
            return false;
        }

        intersection = new NormalizedBBox(x, y, width, height);
        return true;
    }
}
