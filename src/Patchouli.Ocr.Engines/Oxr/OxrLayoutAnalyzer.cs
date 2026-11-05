using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Patchouli.Core.Layout;
using Patchouli.Ocr.Engines.Vision;
using SkiaSharp;

namespace Patchouli.Ocr.Engines.Oxr;

internal sealed record OxrLayoutRegion(
    int Category,
    float Score,
    NormalizedBBox Box,
    int Left,
    int Top,
    int Right,
    int Bottom);

internal static class OxrLayoutAnalyzer
{
    private const int Side = 960;
    private const float MinimumScore = 0.35f;

    public static OxrLayoutRegion[] Detect(SKBitmap source, InferenceSession model)
    {
        int width = source.Width;
        int height = source.Height;
        float ratio = Side / (float)Math.Max(width, height);
        int resizedWidth = Math.Max(1, (int)(width * ratio));
        int resizedHeight = Math.Max(1, (int)(height * ratio));
        using SKBitmap resized = source.Resize(new SKSizeI(resizedWidth, resizedHeight),
                                     new SKSamplingOptions(SKCubicResampler.CatmullRom))
                                 ?? throw new InvalidDataException("Could not resize the OXR layout image.");
        using SKBitmap canvas = new(Side, Side);
        canvas.Erase(new SKColor(114, 114, 114));
        using (SKCanvas drawing = new(canvas))
        {
            drawing.DrawBitmap(resized,
                (Side - resizedWidth) / 2,
                (Side - resizedHeight) / 2);
            drawing.Flush();
        }

        float[] pixels = new float[3 * Side * Side];
        SKColor[] colors = canvas.Pixels;
        for (int index = 0; index < colors.Length; index++)
        {
            pixels[index] = colors[index].Red / 255f;
            pixels[Side * Side + index] = colors[index].Green / 255f;
            pixels[2 * Side * Side + index] = colors[index].Blue / 255f;
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result = model.Run(
        [
            NamedOnnxValue.CreateFromTensor("images",
                new DenseTensor<float>(pixels, [1, 3, Side, Side])),
            NamedOnnxValue.CreateFromTensor("original_sizes",
                new DenseTensor<long>(new long[] { width, height }, [1, 2]))
        ]);
        long[] labels = result.Single(value => value.Name == "labels").AsTensor<long>().ToArray();
        float[] boxes = result.Single(value => value.Name == "boxes").AsTensor<float>().ToArray();
        float[] scores = result.Single(value => value.Name == "scores").AsTensor<float>().ToArray();
        if (labels.Length != scores.Length || boxes.Length != labels.Length * 4)
        {
            throw new InvalidDataException("OXR returned an invalid layout detection shape.");
        }

        List<OxrLayoutRegion> regions = [];
        for (int index = 0; index < labels.Length; index++)
        {
            if (scores[index] < MinimumScore || labels[index] is < 0 or > 11)
            {
                continue;
            }

            int left = Math.Clamp((int)Math.Floor(boxes[index * 4]), 0, width - 1);
            int top = Math.Clamp((int)Math.Floor(boxes[index * 4 + 1]), 0, height - 1);
            int right = Math.Clamp((int)Math.Ceiling(boxes[index * 4 + 2]), left + 1, width);
            int bottom = Math.Clamp((int)Math.Ceiling(boxes[index * 4 + 3]), top + 1, height);
            NormalizedBBox local = VisionImaging.ClampNormalized(new NormalizedBBox(
                left / (double)width, top / (double)height,
                (right - left) / (double)width, (bottom - top) / (double)height));
            regions.Add(new OxrLayoutRegion((int)labels[index], scores[index], local,
                left, top, right, bottom));
        }

        return regions.OrderByDescending(region => region.Score).ToArray();
    }
}
