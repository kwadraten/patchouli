using FluentAssertions;
using Patchouli.Core.Layout;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class RegionImageCropTests
{
    [Fact]
    public void ComputePixelRect_maps_normalized_region_to_pixels()
    {
        RegionPixelRect rect = RegionImageCrop.ComputePixelRect(new NormalizedBBox(0.2, 0.3, 0.4, 0.2), 1000, 2000);

        rect.Should().Be(new RegionPixelRect(200, 600, 400, 400));
    }

    [Fact]
    public void ComputePixelRect_covers_the_full_page_for_a_full_page_region()
    {
        RegionPixelRect rect = RegionImageCrop.ComputePixelRect(new NormalizedBBox(0, 0, 1, 1), 1200, 1600);

        rect.Should().Be(new RegionPixelRect(0, 0, 1200, 1600));
    }

    [Fact]
    public void ComputePixelRect_clamps_to_image_bounds_at_page_edges()
    {
        RegionPixelRect rect = RegionImageCrop.ComputePixelRect(new NormalizedBBox(0.99, 0.99, 0.01, 0.01), 10, 10);

        rect.Should().Be(new RegionPixelRect(9, 9, 1, 1));
    }

    [Fact]
    public void CropPngToFile_writes_the_cropped_region()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"patchouli-crop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "page.png");
        string cropPath = Path.Combine(directory, "crop.png");
        try
        {
            using (SKBitmap bitmap = new(8, 8))
            {
                using SKCanvas canvas = new(bitmap);
                canvas.Clear(SKColors.Red);
                using SKPaint paint = new() { Color = SKColors.Blue };
                canvas.DrawRect(SKRect.Create(4, 4, 4, 4), paint);
                canvas.Flush();
                using SKImage image = SKImage.FromBitmap(bitmap);
                using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(sourcePath, png.ToArray());
            }

            RegionImageCrop.CropPngToFile(sourcePath, new NormalizedBBox(0.5, 0.5, 0.5, 0.5), cropPath);

            using SKBitmap? cropped = SKBitmap.Decode(cropPath);
            cropped.Should().NotBeNull();
            cropped!.Width.Should().Be(4);
            cropped.Height.Should().Be(4);
            cropped.GetPixel(0, 0).Should().Be(SKColors.Blue);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
