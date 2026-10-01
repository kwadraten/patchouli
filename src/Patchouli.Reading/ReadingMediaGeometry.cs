using Avalonia;
using Patchouli.Core.Layout;

namespace Patchouli.Reading;

/// <summary>
/// Pure geometry for drawing a media block's normalized page region out of an in-memory page
/// bitmap: the source sub-rect (in image pixels) plus the destination size that preserves the
/// source region's aspect ratio inside the content box. Edge cases are clamped rather than
/// thrown: a degenerate region falls back to the whole image, a region overhanging the page is
/// trimmed to it, and invalid image sizes are treated as a 1×1 placeholder.
/// </summary>
public static class ReadingMediaGeometry
{
    /// <summary>Computes the source sub-rect and the fitted destination size for one media block.
    /// The destination scales uniformly to fill <paramref name="contentWidth"/> and is capped by
    /// <paramref name="maxHeight"/>; it is never stretched and never larger than the cap.</summary>
    public static (Rect Source, Size Destination) ComputeDrawRects(
        Size imageSize, NormalizedBBox region, double contentWidth, double maxHeight)
    {
        double imageWidth = double.IsFinite(imageSize.Width) && imageSize.Width > 0 ? imageSize.Width : 1;
        double imageHeight = double.IsFinite(imageSize.Height) && imageSize.Height > 0 ? imageSize.Height : 1;
        (double x, double y, double width, double height) = ClampRegion(region);
        Rect source = new(x * imageWidth, y * imageHeight, width * imageWidth, height * imageHeight);

        double maxWidth = double.IsFinite(contentWidth) && contentWidth > 0 ? contentWidth : 1;
        double cap = double.IsFinite(maxHeight) && maxHeight > 0 ? maxHeight : double.PositiveInfinity;
        double scale = maxWidth / source.Width;
        if (source.Height * scale > cap)
        {
            scale = cap / source.Height;
        }

        Size destination = new(Math.Max(1, source.Width * scale), Math.Max(1, source.Height * scale));
        return (source, destination);
    }

    /// <summary>Clamps a normalized region into the unit page. A degenerate or non-finite region
    /// selects the whole page; a region overhanging the right/bottom edge is trimmed to it.</summary>
    public static (double X, double Y, double Width, double Height) ClampRegion(NormalizedBBox region)
    {
        double x = double.IsFinite(region.X) && region.X > 0 ? region.X : 0;
        double y = double.IsFinite(region.Y) && region.Y > 0 ? region.Y : 0;
        double width = double.IsFinite(region.Width) ? region.Width : 0;
        double height = double.IsFinite(region.Height) ? region.Height : 0;
        if (width <= 0 || height <= 0)
        {
            return (0, 0, 1, 1);
        }

        x = Math.Min(x, 1);
        y = Math.Min(y, 1);
        width = Math.Min(width, 1 - x);
        height = Math.Min(height, 1 - y);
        return width <= 0 || height <= 0 ? (0, 0, 1, 1) : (x, y, width, height);
    }
}
