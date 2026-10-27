using System.Globalization;
using Patchouli.Core.Layout;
using SkiaSharp;

namespace Patchouli.Ocr.Engines.Vision;

/// <summary>
/// In-memory imaging helpers for the multimodal engine: normalize→pixel conversion identical to
/// the shared region crop helper, PNG re-encoding for provider payloads, and the numbered-region
/// overlay used by the reading-order request. Nothing touches the filesystem.
/// </summary>
internal static class VisionImaging
{
    public static bool IsFullPage(NormalizedBBox region)
    {
        return region is { X: <= 0, Y: <= 0 } && region.X + region.Width >= 1 && region.Y + region.Height >= 1;
    }

    public readonly record struct PixelRect(int Left, int Top, int Width, int Height);

    /// <summary>Normalized→pixel rect; epsilon keeps floating-point noise off the pixel edges.</summary>
    public static PixelRect ComputePixelRect(NormalizedBBox region, int imageWidth, int imageHeight)
    {
        const double epsilon = 1e-6;
        int left = Math.Clamp((int)Math.Floor(region.X * imageWidth + epsilon), 0, imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(region.Y * imageHeight + epsilon), 0, imageHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * imageWidth - epsilon),
            left + 1, imageWidth);
        int bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * imageHeight - epsilon),
            top + 1, imageHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>Crops a normalized region out of a decoded bitmap and re-encodes it as PNG.</summary>
    public static byte[] CropToPngBytes(SKBitmap source, NormalizedBBox region)
    {
        ArgumentNullException.ThrowIfNull(source);
        using SKBitmap crop = CropBitmap(source, region);
        using SKImage image = SKImage.FromBitmap(crop);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>Crops a normalized region out of a decoded bitmap; the caller owns the result.</summary>
    public static SKBitmap CropBitmap(SKBitmap source, NormalizedBBox region)
    {
        ArgumentNullException.ThrowIfNull(source);
        PixelRect rect = ComputePixelRect(region, source.Width, source.Height);
        SKBitmap crop = new(rect.Width, rect.Height, source.ColorType, source.AlphaType);
        using SKCanvas canvas = new(crop);
        canvas.DrawBitmap(source,
            SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height),
            SKRect.Create(0, 0, rect.Width, rect.Height));
        canvas.Flush();
        return crop;
    }

    /// <summary>Re-encodes a decoded bitmap as PNG bytes for a provider payload.</summary>
    public static byte[] EncodePng(SKBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using SKImage image = SKImage.FromBitmap(source);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>
    /// Draws the numbered region rectangles the ordering prompt refers to: red borders and a
    /// white-backed index label at each region's top-left corner. Returns a new PNG payload.
    /// </summary>
    public static byte[] MarkRegionIds(SKBitmap image, IReadOnlyList<VisionLayoutRegion> regions)
    {
        using SKCanvas canvas = new(image);
        using SKPaint border = new() { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        using SKPaint background = new() { Color = SKColors.White };
        using SKPaint text = new() { Color = SKColors.Red, IsAntialias = true };
        float size = Math.Max(18, Math.Min(image.Width, image.Height) / 55f);
        using SKFont font = new(SKTypeface.Default, size);
        for (int index = 0; index < regions.Count; index++)
        {
            NormalizedBBox b = regions[index].BBox;
            float x = (float)(b.X * image.Width), y = (float)(b.Y * image.Height);
            canvas.DrawRect(SKRect.Create(x, y, (float)(b.Width * image.Width),
                (float)(b.Height * image.Height)), border);
            string label = index.ToString(CultureInfo.InvariantCulture);
            canvas.DrawRect(SKRect.Create(x, y, font.MeasureText(label) + 4, size + 4), background);
            canvas.DrawText(label, x + 2, y + size, SKTextAlign.Left, font, text);
        }

        canvas.Flush();
        using SKImage marked = SKImage.FromBitmap(image);
        using SKData data = marked.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Maps a box already expressed in normalized crop-local coordinates (0..1 of the region)
    /// into the input region's page-space baseline.
    /// </summary>
    public static NormalizedBBox RegionBoxNormalized(NormalizedBBox region, NormalizedBBox local)
    {
        return ClampNormalized(new NormalizedBBox(
            region.X + local.X * region.Width,
            region.Y + local.Y * region.Height,
            local.Width * region.Width,
            local.Height * region.Height));
    }

    public static NormalizedBBox ClampNormalized(NormalizedBBox bbox)
    {
        double x = Math.Clamp(bbox.X, 0.0, 1.0);
        double y = Math.Clamp(bbox.Y, 0.0, 1.0);
        double width = Math.Clamp(bbox.Width, 0.0, 1.0 - x);
        double height = Math.Clamp(bbox.Height, 0.0, 1.0 - y);
        // A degenerate box still needs positive extent so it passes host validation.
        return new NormalizedBBox(x, y, Math.Max(width, 1e-6), Math.Max(height, 1e-6));
    }
}
