using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Native ONNX port of the official <c>parseq.py</c> line recognizer used by
/// ndl-lab/ndlocr-lite. Unlike the NDL Koten recognizer the input geometry comes
/// from the model itself and a crop is rotated counter-clockwise only when its
/// height exceeds 80% of its width.
/// </summary>
public sealed class NdlLiteRecognizer : IDisposable
{
    /// <summary>Official <c>if h &gt; w * 0.8</c> vertical-rotation trigger.</summary>
    public const double VerticalRotationAspect = 0.8;

    /// <summary>Scale applied before the <c>[-1, 1]</c> shift, matching <c>/127.5 - 1</c>.</summary>
    public const float NormalizationScale = 1.0f / 127.5f;

    private readonly InferenceSession _session;
    private readonly IReadOnlyList<string> _charset;

    public NdlLiteRecognizer(string modelPath, IReadOnlyList<string> charset)
    {
        _charset = charset;

        SessionOptions options = new();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.AppendExecutionProvider_CPU(0);
        options.IntraOpNumThreads = 1;
        options.InterOpNumThreads = 1;
        _session = new InferenceSession(modelPath, options);

        NodeMetadata input = _session.InputMetadata.Values.First();
        if (input.Dimensions.Length < 4 || input.Dimensions[2] <= 0 || input.Dimensions[3] <= 0)
        {
            throw new InvalidOperationException(
                "The PARSeq model must declare a static NCHW input shape to drive preprocessing.");
        }

        InputHeight = input.Dimensions[2];
        InputWidth = input.Dimensions[3];

        NodeMetadata output = _session.OutputMetadata.Values.First();
        if (output.Dimensions.Length >= 3 && output.Dimensions[2] > 0)
        {
            ValidateCharsetSize(output.Dimensions[2], charset.Count);
        }
    }

    public int InputHeight { get; }

    public int InputWidth { get; }

    public IReadOnlyList<string> Charset => _charset;

    /// <summary>
    /// The recognizer emits a blank/stop class plus one class per character-set
    /// entry, so the model's output class dimension must be exactly
    /// <c>charset.Count + 1</c>. A mismatch means the wrong NDLmoji.yaml is paired
    /// with the model (or a UTF-16/character-splitting bug shifted the vocabulary)
    /// and would silently garble every decoded line.
    /// </summary>
    internal static void ValidateCharsetSize(int outputClassCount, int charsetCount)
    {
        if (outputClassCount - 1 != charsetCount)
        {
            throw new InvalidOperationException(
                $"The PARSeq model has {outputClassCount} output classes but the character set has {charsetCount} entries; " +
                "expected outputClassCount - 1 == charsetCount. Check that the model and NDLmoji.yaml belong to the same release.");
        }
    }

    public string Read(SKBitmap lineImage)
    {
        using SKBitmap prepared = Preprocess(lineImage, InputWidth, InputHeight);
        DenseTensor<float> tensor = CreateTensor(prepared);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(
            new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), tensor)
            });

        DenseTensor<float> output = (DenseTensor<float>)outputs.First().AsTensor<float>();
        return Decode(ArgMax(output), _charset);
    }

    /// <summary>
    /// Rotates a vertical crop counter-clockwise, then resizes it to the model's
    /// <c>width x height</c> with a linear filter.
    /// </summary>
    internal static SKBitmap Preprocess(SKBitmap image, int inputWidth, int inputHeight)
    {
        SKBitmap working = image;
        bool ownsWorking = false;
        if (ShouldRotateCounterClockwise(image.Width, image.Height))
        {
            working = RotateCounterClockwise(image);
            ownsWorking = true;
        }

        SKBitmap resized = working
                               .Resize(new SKSizeI(inputWidth, inputHeight),
                                   new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
                           ?? throw new InvalidOperationException("Failed to resize the line image for PARSeq.");
        if (ownsWorking)
        {
            working.Dispose();
        }

        return resized;
    }

    /// <summary>Official <c>if h &gt; w * 0.8</c> vertical-rotation trigger.</summary>
    internal static bool ShouldRotateCounterClockwise(int width, int height)
    {
        return height > width * VerticalRotationAspect;
    }

    /// <summary>
    /// Rotates 90 degrees counter-clockwise, matching <c>cv2.ROTATE_90_COUNTERCLOCKWISE</c>.
    /// </summary>
    internal static SKBitmap RotateCounterClockwise(SKBitmap image)
    {
        SKBitmap rotated = new(image.Height, image.Width, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(rotated);
        canvas.Translate(0, rotated.Height);
        canvas.RotateDegrees(-90);
        canvas.DrawBitmap(image, 0, 0);
        return rotated;
    }

    /// <summary>Builds a CHW <c>float</c> tensor in BGR order scaled to <c>[-1, 1]</c>.</summary>
    internal static DenseTensor<float> CreateTensor(SKBitmap image)
    {
        using SKBitmap bgra = image.ColorType == SKColorType.Bgra8888
            ? image.Copy()
            : NormalizeToBgra8888(image);
        int width = bgra.Width;
        int height = bgra.Height;
        DenseTensor<float> tensor = new(new[] { 1, 3, height, width });
        ReadOnlySpan<byte> pixels = bgra.GetPixelSpan();
        int pixelBytes = bgra.BytesPerPixel;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * pixelBytes;
                tensor[0, 0, y, x] = pixels[offset] * NormalizationScale - 1.0f;
                tensor[0, 1, y, x] = pixels[offset + 1] * NormalizationScale - 1.0f;
                tensor[0, 2, y, x] = pixels[offset + 2] * NormalizationScale - 1.0f;
            }
        }

        return tensor;
    }

    private static SKBitmap NormalizeToBgra8888(SKBitmap image)
    {
        SKBitmap normalized = new(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(normalized);
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(image, 0, 0);
        return normalized;
    }

    private static int[] ArgMax(DenseTensor<float> output)
    {
        int length = output.Dimensions[1];
        int classes = output.Dimensions[2];
        ReadOnlySpan<float> logits = output.Buffer.Span;
        int[] indices = new int[length];
        for (int t = 0; t < length; t++)
        {
            ReadOnlySpan<float> step = logits.Slice(t * classes, classes);
            int bestIndex = 0;
            float bestValue = step[0];
            for (int c = 1; c < classes; c++)
            {
                if (step[c] > bestValue)
                {
                    bestValue = step[c];
                    bestIndex = c;
                }
            }

            indices[t] = bestIndex;
        }

        return indices;
    }

    /// <summary>
    /// Decodes arg-max indices: token 0 stops the sequence and token <c>i</c> maps
    /// to character-set entry <c>i - 1</c>. Because <see cref="ValidateCharsetSize"/>
    /// guarantees the model and character set agree, an out-of-range token is a hard
    /// error rather than something to silently swallow.
    /// </summary>
    internal static string Decode(IReadOnlyList<int> indices, IReadOnlyList<string> charset)
    {
        StringBuilder builder = new(indices.Count);
        foreach (int token in indices)
        {
            if (token == 0)
            {
                break;
            }

            int charIndex = token - 1;
            if (charIndex < 0 || charIndex >= charset.Count)
            {
                throw new InvalidOperationException(
                    $"PARSeq emitted token {token} which is outside the {charset.Count}-entry character set.");
            }

            builder.Append(charset[charIndex]);
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
