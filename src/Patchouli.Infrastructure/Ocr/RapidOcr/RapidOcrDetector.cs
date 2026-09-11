using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>
/// Managed port of upstream <c>ch_ppocr_det</c>: it resizes the image so that the
/// configured side length is met in multiples of 32, normalizes in BGR/CHW order, runs
/// the DB model, and post-processes the probability map into line quadrilaterals.
/// </summary>
internal sealed class RapidOcrDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly RapidOcrDetOptions _options;

    public RapidOcrDetector(string modelPath, SessionOptions sessionOptions, RapidOcrDetOptions options)
    {
        _options = options;
        _session = new InferenceSession(modelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public IReadOnlyList<RapidOcrDetBox> Detect(RapidOcrImage image)
    {
        int limitSideLength = GetLimitSideLength(Math.Max(image.Width, image.Height));
        RapidOcrImage resized = RapidOcrDetPreprocess.Resize(image, limitSideLength, _options.LimitType);
        DenseTensor<float> input = RapidOcrDetPreprocess.CreateTensor(resized, _options.Mean,
            _options.StandardDeviation);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(
            new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) });

        DenseTensor<float> probability = (DenseTensor<float>)outputs[0].AsTensor<float>();
        // The exported DB model emits a single [1, 1, H, W] probability plane. Index it
        // explicitly so a changed export shape cannot silently score the wrong plane.
        if (probability.Dimensions.Length != 4 || probability.Dimensions[0] != 1 ||
            probability.Dimensions[1] != 1)
        {
            throw new InvalidOperationException(
                $"Unexpected RapidOCR detector output shape: [{string.Join(", ", probability.Dimensions.ToArray())}].");
        }

        int height = probability.Dimensions[2];
        int width = probability.Dimensions[3];
        float[] map = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                map[y * width + x] = probability[0, 0, y, x];
            }
        }

        return DbPostProcess.Run(map, width, height, image.Width, image.Height, _options);
    }

    private int GetLimitSideLength(int maximumSide)
    {
        // Upstream TextDetector.get_preprocess only honors the configured side length
        // when limit_type is "min"; the exported configuration always uses "min".
        if (_options.LimitType == "min")
        {
            return _options.LimitSideLength;
        }

        if (maximumSide < 960)
        {
            return 960;
        }

        return maximumSide < 1500 ? 1500 : 2000;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}

internal static class RapidOcrDetPreprocess
{
    private const int SizeMultiple = 32;

    /// <summary>OpenCV-style resize to a multiple of 32 around the configured side length.</summary>
    public static RapidOcrImage Resize(RapidOcrImage image, int limitSideLength, string limitType)
    {
        int height = image.Height;
        int width = image.Width;
        double ratio;
        if (limitType == "max")
        {
            int longest = Math.Max(height, width);
            ratio = longest > limitSideLength ? (double)limitSideLength / longest : 1.0;
        }
        else
        {
            int shortest = Math.Min(height, width);
            ratio = shortest < limitSideLength ? (double)limitSideLength / shortest : 1.0;
        }

        int resizedHeight = RoundToMultiple((int)(height * ratio));
        int resizedWidth = RoundToMultiple((int)(width * ratio));
        if (resizedWidth <= 0 || resizedHeight <= 0)
        {
            throw new InvalidOperationException("The detection resize produced an empty image.");
        }

        return image.ResizeLinear(resizedWidth, resizedHeight);
    }

    internal static int RoundToMultiple(int value)
    {
        return (int)(Math.Round(value / (double)SizeMultiple, MidpointRounding.ToEven) * SizeMultiple);
    }

    public static DenseTensor<float> CreateTensor(RapidOcrImage image, IReadOnlyList<double> mean,
        IReadOnlyList<double> standardDeviation)
    {
        DenseTensor<float> tensor = new(new[] { 1, RapidOcrImage.ChannelCount, image.Height, image.Width });
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int offset = (y * image.Width + x) * RapidOcrImage.ChannelCount;
                for (int channel = 0; channel < RapidOcrImage.ChannelCount; channel++)
                {
                    double value = (image.Pixels[offset + channel] * (1.0 / 255.0) - mean[channel]) /
                                   standardDeviation[channel];
                    tensor[0, channel, y, x] = (float)value;
                }
            }
        }

        return tensor;
    }
}
