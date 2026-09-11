using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

/// <summary>
/// Managed port of upstream <c>ch_ppocr_cls</c>. Crops are classified in batches of six
/// and rotated 180 degrees only when the model reports <c>180</c> above the 0.9 threshold.
/// </summary>
internal sealed class RapidOcrClassifier : IDisposable
{
    public const int BatchSize = 6;
    public const int ImageHeight = 48;
    public const int ImageWidth = 192;
    public const double DefaultThreshold = 0.9;
    private readonly string _inputName;
    private readonly InferenceSession _session;
    private readonly double _threshold;

    public RapidOcrClassifier(string modelPath, SessionOptions sessionOptions, double threshold)
    {
        _threshold = threshold;
        _session = new InferenceSession(modelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public IReadOnlyList<RapidOcrImage> Run(IReadOnlyList<RapidOcrImage> images)
    {
        if (images.Count == 0)
        {
            return images;
        }

        RapidOcrImage[] results = images.ToArray();
        int[] order = Enumerable.Range(0, images.Count)
            .OrderBy(index => (double)images[index].Width / images[index].Height)
            .ToArray();
        for (int start = 0; start < images.Count; start += BatchSize)
        {
            int end = Math.Min(images.Count, start + BatchSize);
            DenseTensor<float> batch = CreateBatch(images, order, start, end);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(
                new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, batch) });
            DenseTensor<float> predictions = (DenseTensor<float>)outputs[0].AsTensor<float>();
            for (int index = start; index < end; index++)
            {
                int batchIndex = index - start;
                int labelIndex = predictions[batchIndex, 0] >= predictions[batchIndex, 1] ? 0 : 1;
                double score = predictions[batchIndex, labelIndex];
                if (ShouldRotate(labelIndex, score, _threshold))
                {
                    results[order[index]] = images[order[index]].Rotate180();
                }
            }
        }

        return results;
    }

    internal static bool ShouldRotate(int labelIndex, double score, double threshold)
    {
        return labelIndex == 1 && score > threshold;
    }

    private static DenseTensor<float> CreateBatch(IReadOnlyList<RapidOcrImage> images, IReadOnlyList<int> order,
        int start, int end)
    {
        int count = end - start;
        DenseTensor<float> tensor = new(new[] { count, RapidOcrImage.ChannelCount, ImageHeight, ImageWidth });
        for (int index = start; index < end; index++)
        {
            RapidOcrImage image = images[order[index]];
            int resizedWidth = (int)Math.Ceiling(ImageHeight * ((double)image.Width / image.Height));
            resizedWidth = Math.Clamp(resizedWidth, 1, ImageWidth);
            RapidOcrImage resized = image.ResizeLinear(resizedWidth, ImageHeight);
            int batchIndex = index - start;
            for (int y = 0; y < ImageHeight; y++)
            {
                for (int x = 0; x < resizedWidth; x++)
                {
                    int offset = (y * resizedWidth + x) * RapidOcrImage.ChannelCount;
                    for (int channel = 0; channel < RapidOcrImage.ChannelCount; channel++)
                    {
                        tensor[batchIndex, channel, y, x] =
                            (float)((resized.Pixels[offset + channel] * (1.0 / 255.0) - 0.5) / 0.5);
                    }
                }
            }
        }

        return tensor;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
