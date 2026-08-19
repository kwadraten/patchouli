using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public sealed class ParseqRecognizer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly IReadOnlyList<char> _charlist;

    public ParseqRecognizer(string modelPath, IReadOnlyList<char> charlist)
    {
        _charlist = charlist;
        SessionOptions options = new();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;
        options.AppendExecutionProvider_CPU(0);
        _session = new InferenceSession(modelPath, options);
    }

    public string Read(SKBitmap lineImage)
    {
        using SKBitmap prepared = Preprocess(lineImage);
        DenseTensor<float> input = CreateTensor(prepared);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_session.InputMetadata.First().Key, input)
        });

        DenseTensor<float> output = (DenseTensor<float>)outputs.First().AsTensor<float>();
        int length = output.Dimensions[1];
        Span<int> predictions = length <= 256
            ? stackalloc int[length]
            : new int[length];
        for (int t = 0; t < length; t++)
        {
            int bestIndex = 0;
            float bestValue = output[0, t, 0];
            for (int c = 1; c < output.Dimensions[2]; c++)
            {
                float value = output[0, t, c];
                if (value > bestValue)
                {
                    bestValue = value;
                    bestIndex = c;
                }
            }

            predictions[t] = bestIndex;
        }

        char[] buffer = new char[length];
        int written = 0;
        foreach (int token in predictions)
        {
            if (token == 0)
            {
                break;
            }

            int charIndex = token - 1;
            if (charIndex < _charlist.Count)
            {
                buffer[written++] = _charlist[charIndex];
            }
        }

        return new string(buffer, 0, written);
    }

    private static SKBitmap Preprocess(SKBitmap image)
    {
        SKBitmap working = image;
        bool ownsWorking = false;
        if (image.Height > image.Width)
        {
            working = RotateVerticalLineCounterClockwise(image);
            ownsWorking = true;
        }

        const int TargetWidth = 384;
        const int TargetHeight = 32;
        SKBitmap resized = working.Resize(new SKSizeI(TargetWidth, TargetHeight),
                               new SKSamplingOptions(SKCubicResampler.Mitchell))
                           ?? throw new InvalidOperationException("Failed to resize line image for PARSeq.");
        if (ownsWorking)
        {
            working.Dispose();
        }

        return resized;
    }

    internal static SKBitmap RotateVerticalLineCounterClockwise(SKBitmap image)
    {
        SKBitmap rotated = new(image.Height, image.Width, image.ColorType, image.AlphaType);
        using SKCanvas canvas = new(rotated);
        canvas.Translate(0, rotated.Height);
        canvas.RotateDegrees(-90);
        canvas.DrawBitmap(image, 0, 0);
        return rotated;
    }

    private static DenseTensor<float> CreateTensor(SKBitmap image)
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
                tensor[0, 0, y, x] = 2.0f * (b - 0.5f);
                tensor[0, 1, y, x] = 2.0f * (g - 0.5f);
                tensor[0, 2, y, x] = 2.0f * (r - 0.5f);
            }
        }

        return tensor;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
