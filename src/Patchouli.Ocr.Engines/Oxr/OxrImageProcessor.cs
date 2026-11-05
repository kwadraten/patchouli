using Patchouli.Ocr.Engines.Vision;
using SkiaSharp;

namespace Patchouli.Ocr.Engines.Oxr;

internal static class OxrImageProcessor
{
    private const int Side = 448;
    private const int Patch = 16;
    private const int Stride = 2;
    public const int PatchCount = 784;
    public const int PatchElements = Patch * Patch * 3;

    public static float[] Prepare(ReadOnlySpan<byte> imageBytes)
    {
        using SKBitmap original = NativeImageDecode.DecodeOriented(imageBytes)
                                  ?? throw new InvalidDataException("Could not decode the OXR input image.");
        if (original.Width <= 0 || original.Height <= 0)
        {
            throw new InvalidDataException("The OXR input image has invalid dimensions.");
        }

        int squareSide = Math.Max(original.Width, original.Height);
        using SKBitmap? square = original.Width == original.Height
            ? null
            : new SKBitmap(squareSide, squareSide, SKColorType.Rgba8888, SKAlphaType.Opaque);
        if (square is not null)
        {
            square.Erase(SKColors.White);
            using SKCanvas canvas = new(square);
            canvas.DrawBitmap(original,
                (squareSide - original.Width) / 2,
                (squareSide - original.Height) / 2);
            canvas.Flush();
        }

        SKBitmap input = square ?? original;
        using SKBitmap? resized = input.Width == Side && input.Height == Side
            ? null
            : input.Resize(new SKSizeI(Side, Side), new SKSamplingOptions(SKFilterMode.Linear));
        SKBitmap prepared = resized ?? input;
        if (prepared.Width != Side || prepared.Height != Side)
        {
            throw new InvalidDataException("Could not resize the OXR input image.");
        }

        SKColor[] colors = prepared.Pixels;
        float[] patches = new float[PatchCount * PatchElements];
        int position = 0;
        for (int blockY = 0; blockY < Side / (Patch * Stride); blockY++)
        {
            for (int blockX = 0; blockX < Side / (Patch * Stride); blockX++)
            {
                for (int partY = 0; partY < Stride; partY++)
                {
                    for (int partX = 0; partX < Stride; partX++)
                    {
                        int baseY = blockY * Patch * Stride + partY * Patch;
                        int baseX = blockX * Patch * Stride + partX * Patch;
                        for (int channel = 0; channel < 3; channel++)
                        {
                            for (int y = 0; y < Patch; y++)
                            {
                                for (int x = 0; x < Patch; x++)
                                {
                                    SKColor color = colors[(baseY + y) * Side + baseX + x];
                                    byte component = channel switch
                                    {
                                        0 => color.Red,
                                        1 => color.Green,
                                        _ => color.Blue
                                    };
                                    patches[position++] = component * (2f / 255f) - 1f;
                                }
                            }
                        }
                    }
                }
            }
        }

        return patches;
    }
}
