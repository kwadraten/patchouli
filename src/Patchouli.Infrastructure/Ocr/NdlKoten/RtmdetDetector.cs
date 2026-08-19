using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public sealed class RtmdetDetector : IDisposable
{
    private readonly InferenceSession _session;

    public RtmdetDetector(string modelPath, string classesYamlPath)
    {
        // Loaded for parity with the official implementation; the detector labels
        // are not used for filtering (see Postprocess).
        _ = NdlKotenClassNames.Parse(File.ReadAllText(classesYamlPath));

        SessionOptions options = new();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;
        options.AppendExecutionProvider_CPU(0);
        _session = new InferenceSession(modelPath, options);
    }

    public float ConfidenceThreshold { get; init; } = 0.1f;

    public IReadOnlyList<LineDetection> Detect(SKBitmap image)
    {
        int inputWidth = _session.InputMetadata.First().Value.Dimensions[3];
        int inputHeight = _session.InputMetadata.First().Value.Dimensions[2];

        using SKBitmap normalized = NormalizeToBgra8888(image);
        using SKBitmap padded = PadToSquare(normalized);
        int paddedSize = Math.Max(padded.Width, padded.Height);
        using SKBitmap resized =
            padded.Resize(new SKSizeI(inputWidth, inputHeight), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Failed to resize image for RTMDet.");

        DenseTensor<float> input = Preprocess(resized);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_session.InputMetadata.First().Key, input)
        });

        return Postprocess(outputs, paddedSize);
    }

    internal static SKBitmap NormalizeToBgra8888(SKBitmap image)
    {
        SKBitmap normalized = new(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(normalized);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(image, 0, 0);
        return normalized;
    }

    private static SKBitmap PadToSquare(SKBitmap image)
    {
        int size = Math.Max(image.Width, image.Height);
        SKBitmap padded = new(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(padded);
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(image, 0, 0);
        return padded;
    }

    private static DenseTensor<float> Preprocess(SKBitmap image)
    {
        int width = image.Width;
        int height = image.Height;
        DenseTensor<float> tensor = new(new[] { 1, 3, height, width });
        ReadOnlySpan<byte> pixels = image.GetPixelSpan();
        int pixelBytes = image.BytesPerPixel;
        const float meanB = 103.53f;
        const float meanG = 116.28f;
        const float meanR = 123.675f;
        const float stdB = 57.375f;
        const float stdG = 57.12f;
        const float stdR = 58.395f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * pixelBytes;
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                tensor[0, 0, y, x] = (b - meanB) / stdB;
                tensor[0, 1, y, x] = (g - meanG) / stdG;
                tensor[0, 2, y, x] = (r - meanR) / stdR;
            }
        }

        return tensor;
    }

    private IReadOnlyList<LineDetection> Postprocess(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, int paddedSize)
    {
        // Expected output shapes: boxes [1, N, 5], class ids [1, N].
        DisposableNamedOnnxValue[] outputArray = outputs.ToArray();
        DenseTensor<float> boxesTensor = (DenseTensor<float>)outputArray[0].AsTensor<float>();
        int inputSize = _session.InputMetadata.First().Value.Dimensions[3];
        int count = boxesTensor.Dimensions[1];

        return Postprocess(
            Enumerable.Range(0, count).Select(i => new RtmdetRawDetection(
                boxesTensor[0, i, 0], boxesTensor[0, i, 1], boxesTensor[0, i, 2], boxesTensor[0, i, 3],
                boxesTensor[0, i, 4], 0)),
            inputSize,
            paddedSize,
            ConfidenceThreshold);
    }

    internal static IReadOnlyList<LineDetection> Postprocess(IEnumerable<RtmdetRawDetection> rawDetections,
        int inputSize, int paddedSize, float confidenceThreshold)
    {
        // The official rtmdet.py postprocess ignores the model's label output and
        // reports every detection above the threshold as class 1 ("line_main").
        // The exported model emits labels=0 ("text_block") for all boxes, so the
        // port must not filter on the label.
        List<LineDetection> detections = new();
        foreach (RtmdetRawDetection raw in rawDetections)
        {
            if (raw.Score < confidenceThreshold)
            {
                continue;
            }

            float x1 = raw.X1 / inputSize * paddedSize;
            float y1 = raw.Y1 / inputSize * paddedSize;
            float x2 = raw.X2 / inputSize * paddedSize;
            float y2 = raw.Y2 / inputSize * paddedSize;
            float deltaH = (y2 - y1) * 0.02f;
            y1 -= deltaH;
            y2 += deltaH;
            detections.Add(new LineDetection(new Box((int)x1, (int)y1, (int)x2, (int)y2), raw.Score, 1));
        }

        return detections;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}

internal sealed record RtmdetRawDetection(float X1, float Y1, float X2, float Y2, float Score, int Label);

public sealed record LineDetection(Box Box, float Confidence, int ClassId);
