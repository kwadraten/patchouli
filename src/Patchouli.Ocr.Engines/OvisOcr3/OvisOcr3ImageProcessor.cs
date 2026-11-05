using SkiaSharp;

namespace Patchouli.Ocr.Engines.OvisOcr3;

internal sealed record OvisOcr3Image(
    float[] Pixels,
    float[] PositionEmbeddings,
    long[] VisionPositionIds,
    int GridHeight,
    int GridWidth)
{
    public int ImageTokenCount => GridHeight * GridWidth / 4;
}

internal sealed class OvisOcr3ImageProcessor
{
    private const int Patch = 16;
    private const int Merge = 2;
    private const int Side = 48;
    private const int EmbeddingSize = 768;
    private readonly float[] _positionWeights;

    public OvisOcr3ImageProcessor(string positionWeightsPath)
    {
        byte[] bytes = File.ReadAllBytes(positionWeightsPath);
        if (bytes.Length != Side * Side * EmbeddingSize * 2)
        {
            throw new InvalidDataException("The OvisOCR3 visual position embeddings have an invalid size.");
        }

        _positionWeights = new float[Side * Side * EmbeddingSize];
        for (int index = 0; index < _positionWeights.Length; index++)
        {
            ushort value = BitConverter.ToUInt16(bytes, index * 2);
            _positionWeights[index] = BitConverter.Int32BitsToSingle(value << 16);
        }
    }

    public OvisOcr3Image Prepare(ReadOnlySpan<byte> imageBytes)
    {
        using SKBitmap original = NativeImageDecode.DecodeOriented(imageBytes)
                                  ?? throw new InvalidDataException("Could not decode the OvisOCR3 input image.");
        if (original.Width <= 0 || original.Height <= 0 ||
            Math.Max(original.Width, original.Height) / (double)Math.Min(original.Width, original.Height) > 200)
        {
            throw new InvalidDataException("The OvisOCR3 input image has invalid dimensions.");
        }

        (int height, int width) = SmartResize(original.Height, original.Width);
        using SKBitmap? resized = original.Width == width && original.Height == height
            ? null
            : original.Resize(new SKSizeI(width, height), new SKSamplingOptions(SKCubicResampler.CatmullRom));
        SKBitmap prepared = resized ?? original;
        if (prepared.Width != width || prepared.Height != height)
        {
            throw new InvalidDataException("Could not resize the OvisOCR3 input image.");
        }

        SKColor[] colors = prepared.Pixels;
        int gridHeight = height / Patch;
        int gridWidth = width / Patch;
        int patchCount = gridHeight * gridWidth;
        float[] pixels = new float[patchCount * 1536];
        float[] positionEmbeddings = new float[patchCount * EmbeddingSize];
        long[] visionPositionIds = new long[patchCount * 2];
        int patchIndex = 0;
        for (int blockY = 0; blockY < gridHeight / Merge; blockY++)
        {
            for (int blockX = 0; blockX < gridWidth / Merge; blockX++)
            {
                for (int partY = 0; partY < Merge; partY++)
                {
                    for (int partX = 0; partX < Merge; partX++)
                    {
                        int row = blockY * Merge + partY;
                        int column = blockX * Merge + partX;
                        visionPositionIds[patchIndex * 2] = row;
                        visionPositionIds[patchIndex * 2 + 1] = column;
                        FillPatch(colors, width, row, column, pixels.AsSpan(patchIndex * 1536, 1536));
                        FillPosition(row, column, gridHeight, gridWidth,
                            positionEmbeddings.AsSpan(patchIndex * EmbeddingSize, EmbeddingSize));
                        patchIndex++;
                    }
                }
            }
        }

        return new OvisOcr3Image(pixels, positionEmbeddings, visionPositionIds, gridHeight, gridWidth);
    }

    private static (int Height, int Width) SmartResize(int height, int width)
    {
        const int factor = Patch * Merge;
        const int minPixels = 448 * 448;
        const int maxPixels = 1024 * 1024;
        int h = (int)Math.Round(height / (double)factor, MidpointRounding.ToEven) * factor;
        int w = (int)Math.Round(width / (double)factor, MidpointRounding.ToEven) * factor;
        if ((long)h * w > maxPixels)
        {
            double beta = Math.Sqrt((double)height * width / maxPixels);
            h = Math.Max(factor, (int)Math.Floor(height / beta / factor) * factor);
            w = Math.Max(factor, (int)Math.Floor(width / beta / factor) * factor);
        }
        else if ((long)h * w < minPixels)
        {
            double beta = Math.Sqrt(minPixels / ((double)height * width));
            h = (int)Math.Ceiling(height * beta / factor) * factor;
            w = (int)Math.Ceiling(width * beta / factor) * factor;
        }

        return (h, w);
    }

    private static void FillPatch(SKColor[] colors, int width, int row, int column, Span<float> output)
    {
        int index = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            for (int time = 0; time < 2; time++)
            {
                for (int y = 0; y < Patch; y++)
                {
                    for (int x = 0; x < Patch; x++)
                    {
                        SKColor pixel = colors[(row * Patch + y) * width + column * Patch + x];
                        byte component = channel switch
                        {
                            0 => pixel.Red,
                            1 => pixel.Green,
                            _ => pixel.Blue
                        };
                        output[index++] = component / 127.5f - 1f;
                    }
                }
            }
        }
    }

    private void FillPosition(int row, int column, int height, int width, Span<float> output)
    {
        float y = row * (Side - 1f) / Math.Max(1, height - 1);
        float x = column * (Side - 1f) / Math.Max(1, width - 1);
        int y0 = (int)MathF.Floor(y);
        int x0 = (int)MathF.Floor(x);
        int y1 = Math.Min(y0 + 1, Side - 1);
        int x1 = Math.Min(x0 + 1, Side - 1);
        float fy = y - y0;
        float fx = x - x0;
        int upperLeft = (y0 * Side + x0) * EmbeddingSize;
        int upperRight = (y0 * Side + x1) * EmbeddingSize;
        int lowerLeft = (y1 * Side + x0) * EmbeddingSize;
        int lowerRight = (y1 * Side + x1) * EmbeddingSize;
        for (int index = 0; index < EmbeddingSize; index++)
        {
            output[index] =
                _positionWeights[upperLeft + index] * (1f - fy) * (1f - fx) +
                _positionWeights[upperRight + index] * (1f - fy) * fx +
                _positionWeights[lowerLeft + index] * fy * (1f - fx) +
                _positionWeights[lowerRight + index] * fy * fx;
        }
    }
}
