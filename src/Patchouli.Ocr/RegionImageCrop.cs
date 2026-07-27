using Patchouli.Core.Layout;
using SkiaSharp;

namespace Patchouli.Ocr;

public readonly record struct RegionPixelRect(int Left, int Top, int Width, int Height);

/// <summary>
/// Crops a normalized page region out of a rendered full-page PNG for region OCR.
/// </summary>
public static class RegionImageCrop
{
    public static RegionPixelRect ComputePixelRect(NormalizedBBox region, int imageWidth, int imageHeight)
    {
        // The epsilon keeps floating-point noise (e.g. 0.2 + 0.4 = 0.6000000000000001)
        // from shifting the pixel edges by one.
        const double epsilon = 1e-6;
        int left = Math.Clamp((int)Math.Floor(region.X * imageWidth + epsilon), 0, imageWidth - 1);
        int top = Math.Clamp((int)Math.Floor(region.Y * imageHeight + epsilon), 0, imageHeight - 1);
        int right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * imageWidth - epsilon), left + 1,
            imageWidth);
        int bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * imageHeight - epsilon), top + 1,
            imageHeight);
        return new RegionPixelRect(left, top, right - left, bottom - top);
    }

    public static void CropPngToFile(string sourcePngPath, NormalizedBBox region, string outputPath)
    {
        using SKBitmap? source = SKBitmap.Decode(sourcePngPath);
        if (source is null)
        {
            throw new InvalidOperationException($"Could not decode PNG '{sourcePngPath}'.");
        }

        RegionPixelRect rect = ComputePixelRect(region, source.Width, source.Height);
        using SKBitmap crop = new(rect.Width, rect.Height, source.ColorType, source.AlphaType);
        using SKCanvas canvas = new(crop);
        canvas.DrawBitmap(source,
            SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height),
            SKRect.Create(0, 0, rect.Width, rect.Height));
        canvas.Flush();
        using SKImage image = SKImage.FromBitmap(crop);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, png.ToArray());
    }
}
