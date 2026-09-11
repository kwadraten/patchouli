using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>Axis-aligned integer pixel box, origin at the top-left of the page image.</summary>
public readonly record struct NdlLiteBox(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;

    public int CenterX => (X0 + X1) / 2;
    public int CenterY => (Y0 + Y1) / 2;

    public bool IsVertical => Height > Width;

    public bool Contains(int x, int y)
    {
        return x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    }
}

/// <summary>One DEIM detection with its resolved ndl.yaml class.</summary>
public sealed record NdlLiteDetection(
    int ClassIndex,
    string ClassName,
    float Confidence,
    NdlLiteBox Box,
    float PredictedCharCount);

/// <summary>Raw, pre-scaling detector row used by <see cref="NdlLiteDetector.Postprocess"/>.</summary>
internal sealed record NdlLiteRawDetection(
    int ClassId,
    float X1,
    float Y1,
    float X2,
    float Y2,
    float Score,
    float PredictedCharCount);

/// <summary>
/// Native ONNX port of the official <c>deim.py</c> DEIM-S detector used by
/// ndl-lab/ndlocr-lite. Detection is intentionally different from the NDL Koten
/// RTMDet pipeline: the image is padded to a black square, resized with a cubic
/// filter, normalized with ImageNet mean/std, and the model is fed a second
/// <c>input_shape</c> tensor.
/// </summary>
public sealed class NdlLiteDetector : IDisposable
{
    /// <summary>Official <c>--det-conf-threshold</c> default.</summary>
    public const float DefaultConfidenceThreshold = 0.25f;

    private const int FallbackInputSize = 1024;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] InvStd = [1.0f / 0.229f, 1.0f / 0.224f, 1.0f / 0.225f];

    private readonly InferenceSession _session;
    private readonly string[] _inputNames;

    public NdlLiteDetector(string modelPath, string classesYamlPath)
    {
        ClassNames = NdlLiteClassNames.Parse(File.ReadAllText(classesYamlPath));

        SessionOptions options = new();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.AppendExecutionProvider_CPU(0);
        _session = new InferenceSession(modelPath, options);
        _inputNames = _session.InputMetadata.Keys.ToArray();
    }

    public IReadOnlyList<string> ClassNames { get; }

    public float ConfidenceThreshold { get; init; } = DefaultConfidenceThreshold;

    public IReadOnlyList<NdlLiteDetection> Detect(SKBitmap image)
    {
        (int inputHeight, int inputWidth) = ResolveInputSize();

        using SKBitmap normalized = NormalizeToBgra8888(image);
        using SKBitmap padded = PadToSquare(normalized);
        int paddedSize = padded.Width;
        using SKBitmap resized =
            padded.Resize(new SKSizeI(inputWidth, inputHeight), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("Failed to resize the page image for the DEIM detector.");

        DenseTensor<float> tensor = CreateTensor(resized);
        List<NamedOnnxValue> inputs = new(_inputNames.Length)
        {
            NamedOnnxValue.CreateFromTensor(_inputNames[0], tensor)
        };
        if (_inputNames.Length > 1)
        {
            DenseTensor<long> inputShape = new(new[] { 1, 2 });
            inputShape[0, 0] = inputHeight;
            inputShape[0, 1] = inputWidth;
            inputs.Add(NamedOnnxValue.CreateFromTensor(_inputNames[1], inputShape));
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputs);
        IReadOnlyList<NdlLiteRawDetection> raw = ReadRawDetections(outputs);
        return Postprocess(raw, ClassNames, inputWidth, inputHeight, paddedSize, paddedSize, ConfidenceThreshold);
    }

    private (int Height, int Width) ResolveInputSize()
    {
        NodeMetadata metadata = _session.InputMetadata[_inputNames[0]];
        if (metadata.Dimensions.Length >= 4 && metadata.Dimensions[2] > 0 && metadata.Dimensions[3] > 0)
        {
            return (metadata.Dimensions[2], metadata.Dimensions[3]);
        }

        return (FallbackInputSize, FallbackInputSize);
    }

    internal static SKBitmap NormalizeToBgra8888(SKBitmap image)
    {
        if (image.ColorType == SKColorType.Bgra8888)
        {
            return image.Copy();
        }

        SKBitmap normalized = new(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(normalized);
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(image, 0, 0);
        return normalized;
    }

    /// <summary>
    /// Pads the image to a black square the same way <c>deim.py</c> zero-fills a
    /// square <c>uint8</c> buffer before the model resize.
    /// </summary>
    internal static SKBitmap PadToSquare(SKBitmap image)
    {
        int size = Math.Max(image.Width, image.Height);
        SKBitmap padded = new(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(padded);
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(image, 0, 0);
        return padded;
    }

    internal static DenseTensor<float> CreateTensor(SKBitmap image)
    {
        int width = image.Width;
        int height = image.Height;
        DenseTensor<float> tensor = new(new[] { 1, 3, height, width });
        ReadOnlySpan<byte> pixels = image.GetPixelSpan();
        int pixelBytes = image.BytesPerPixel;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * pixelBytes;
                float b = pixels[offset] / 255.0f;
                float g = pixels[offset + 1] / 255.0f;
                float r = pixels[offset + 2] / 255.0f;
                tensor[0, 0, y, x] = (r - Mean[0]) * InvStd[0];
                tensor[0, 1, y, x] = (g - Mean[1]) * InvStd[1];
                tensor[0, 2, y, x] = (b - Mean[2]) * InvStd[2];
            }
        }

        return tensor;
    }

    /// <summary>
    /// Applies the official <c>deim.py</c> postprocess: keep scores strictly above
    /// the confidence threshold, scale with the published factors (both axes divide
    /// by the model input width), truncate toward zero, clip to the padded canvas,
    /// and resolve the one-based label to a zero-based class index.
    /// </summary>
    internal static IReadOnlyList<NdlLiteDetection> Postprocess(IReadOnlyList<NdlLiteRawDetection> rawDetections,
        IReadOnlyList<string> classNames, int inputWidth, int inputHeight, int imageWidth, int imageHeight,
        float confidenceThreshold)
    {
        List<NdlLiteDetection> detections = new(rawDetections.Count);
        float scaleX = inputWidth > 0 ? (float)imageWidth / inputWidth : 1.0f;
        float scaleY = inputWidth > 0 ? (float)imageHeight / inputWidth : 1.0f;

        foreach (NdlLiteRawDetection raw in rawDetections)
        {
            if (raw.Score <= confidenceThreshold)
            {
                continue;
            }

            // Upstream deim.py resolves the class with self.classes[int(label) - 1].
            // A label of 0 would wrap to Python's classes[-1] (line_title); the port
            // drops label < 1 explicitly rather than reproducing that quirk, and
            // guards labels beyond the class list defensively.
            if (raw.ClassId < 1)
            {
                continue;
            }

            int classIndex = raw.ClassId - 1;
            if (classIndex >= classNames.Count)
            {
                continue;
            }

            int x0 = Clamp((int)(raw.X1 * scaleX), imageWidth);
            int y0 = Clamp((int)(raw.Y1 * scaleY), imageHeight);
            int x1 = Clamp((int)(raw.X2 * scaleX), imageWidth);
            int y1 = Clamp((int)(raw.Y2 * scaleY), imageHeight);
            detections.Add(new NdlLiteDetection(
                classIndex,
                classNames[classIndex],
                raw.Score,
                new NdlLiteBox(x0, y0, x1, y1),
                raw.PredictedCharCount));
        }

        return detections;
    }

    private static int Clamp(int value, int limit)
    {
        return Math.Clamp(value, 0, limit);
    }

    private static IReadOnlyList<NdlLiteRawDetection> ReadRawDetections(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        if (outputs.Count is not (3 or 4))
        {
            throw new InvalidOperationException(
                $"The DEIM model must expose three or four outputs but exposed {outputs.Count}.");
        }

        DisposableNamedOnnxValue[] array = outputs.ToArray();
        float[] classIds = ReadFloats(array[0]);
        float[] boxes = ReadFloats(array[1]);
        float[] scores = ReadFloats(array[2]);
        float[]? charCounts = array.Length >= 4 ? ReadFloats(array[3]) : null;
        int count = scores.Length;

        List<NdlLiteRawDetection> raw = new(count);
        for (int i = 0; i < count; i++)
        {
            int boxOffset = i * 4;
            if (boxOffset + 3 >= boxes.Length || i >= classIds.Length)
            {
                break;
            }

            float charCount = charCounts is not null && i < charCounts.Length ? charCounts[i] : 100.0f;
            raw.Add(new NdlLiteRawDetection(
                (int)classIds[i],
                boxes[boxOffset],
                boxes[boxOffset + 1],
                boxes[boxOffset + 2],
                boxes[boxOffset + 3],
                scores[i],
                charCount));
        }

        return raw;
    }

    private static float[] ReadFloats(DisposableNamedOnnxValue value)
    {
        return value.ElementType switch
        {
            TensorElementType.Float => value.AsEnumerable<float>().ToArray(),
            TensorElementType.Double => ToFloatArray(value.AsEnumerable<double>().ToArray()),
            TensorElementType.Int64 => ToFloatArray(value.AsEnumerable<long>().ToArray()),
            TensorElementType.Int32 => ToFloatArray(value.AsEnumerable<int>().ToArray()),
            _ => throw new InvalidOperationException(
                $"Unsupported DEIM output element type '{value.ElementType}'.")
        };
    }

    private static float[] ToFloatArray(ReadOnlySpan<double> values)
    {
        float[] result = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = (float)values[i];
        }

        return result;
    }

    private static float[] ToFloatArray(ReadOnlySpan<long> values)
    {
        float[] result = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = values[i];
        }

        return result;
    }

    private static float[] ToFloatArray(ReadOnlySpan<int> values)
    {
        float[] result = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = values[i];
        }

        return result;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
