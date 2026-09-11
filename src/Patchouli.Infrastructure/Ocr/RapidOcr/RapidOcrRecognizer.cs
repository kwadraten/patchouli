using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

internal sealed record RapidOcrRecognizedLine(string Text, double Score);

/// <summary>
/// Managed port of upstream <c>ch_ppocr_rec</c>. Line crops are sorted by aspect ratio,
/// recognized in batches of six with a shared <c>max_wh_ratio</c>, and decoded with the
/// embedded CTC dictionary (index 0 is blank, the trailing entry is a space).
/// </summary>
internal sealed class RapidOcrRecognizer : IDisposable
{
    public const int BatchSize = 6;
    public const int ImageHeight = 48;
    public const int ImageWidth = 320;
    private readonly IReadOnlyList<string> _characters;
    private readonly string _inputName;
    private readonly InferenceSession _session;

    public RapidOcrRecognizer(string modelPath, SessionOptions sessionOptions, string? characterPath)
    {
        _session = new InferenceSession(modelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();
        _characters = BuildCharacters(_session, characterPath);
    }

    public IReadOnlyList<RapidOcrRecognizedLine> Recognize(IReadOnlyList<RapidOcrImage> images)
    {
        if (images.Count == 0)
        {
            return [];
        }

        int[] order = Enumerable.Range(0, images.Count)
            .OrderBy(index => (double)images[index].Width / images[index].Height)
            .ToArray();
        RapidOcrRecognizedLine[] results = new RapidOcrRecognizedLine[images.Count];
        for (int start = 0; start < images.Count; start += BatchSize)
        {
            int end = Math.Min(images.Count, start + BatchSize);
            double maxWidthHeightRatio = (double)ImageWidth / ImageHeight;
            for (int index = start; index < end; index++)
            {
                RapidOcrImage image = images[order[index]];
                maxWidthHeightRatio = Math.Max(maxWidthHeightRatio, (double)image.Width / image.Height);
            }

            int batchWidth = (int)(ImageHeight * maxWidthHeightRatio);
            DenseTensor<float> batch = CreateBatch(images, order, start, end, batchWidth);
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(
                new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, batch) });
            DenseTensor<float> predictions = (DenseTensor<float>)outputs[0].AsTensor<float>();
            for (int index = start; index < end; index++)
            {
                results[order[index]] = Decode(predictions, index - start, _characters);
            }
        }

        return results;
    }

    private DenseTensor<float> CreateBatch(IReadOnlyList<RapidOcrImage> images, IReadOnlyList<int> order, int start,
        int end, int batchWidth)
    {
        int count = end - start;
        DenseTensor<float> tensor = new(new[] { count, RapidOcrImage.ChannelCount, ImageHeight, batchWidth });
        for (int index = start; index < end; index++)
        {
            RapidOcrImage image = images[order[index]];
            int resizedWidth = (int)Math.Ceiling(ImageHeight * ((double)image.Width / image.Height));
            resizedWidth = Math.Clamp(resizedWidth, 1, batchWidth);
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

    internal static int ComputeBatchWidth(double maxWidthHeightRatio)
    {
        return (int)(ImageHeight * maxWidthHeightRatio);
    }

    internal static RapidOcrRecognizedLine Decode(DenseTensor<float> predictions, int batchIndex,
        IReadOnlyList<string> characters)
    {
        int steps = predictions.Dimensions[1];
        int classes = predictions.Dimensions[2];
        System.Text.StringBuilder text = new();
        double confidenceSum = 0;
        int selectedCount = 0;
        int previous = -1;
        for (int step = 0; step < steps; step++)
        {
            int best = 0;
            float bestValue = predictions[batchIndex, step, 0];
            for (int candidate = 1; candidate < classes; candidate++)
            {
                float value = predictions[batchIndex, step, candidate];
                if (value > bestValue)
                {
                    bestValue = value;
                    best = candidate;
                }
            }

            if (best != 0 && best != previous)
            {
                if (best < characters.Count)
                {
                    text.Append(characters[best]);
                }

                confidenceSum += Math.Round(bestValue, 5, MidpointRounding.ToEven);
                selectedCount++;
            }

            previous = best;
        }

        return selectedCount == 0
            ? new RapidOcrRecognizedLine(string.Empty, 0)
            : new RapidOcrRecognizedLine(text.ToString(),
                Math.Round(confidenceSum / selectedCount, 5, MidpointRounding.ToEven));
    }

    internal static IReadOnlyList<string> BuildCharacters(InferenceSession session, string? characterPath)
    {
        List<string> characters;
        if (session.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? metadata) &&
            !string.IsNullOrEmpty(metadata))
        {
            characters = SplitLines(metadata);
        }
        else if (!string.IsNullOrWhiteSpace(characterPath) && File.Exists(characterPath))
        {
            characters = File.ReadLines(characterPath)
                .Select(static line => line.TrimEnd('\r', '\n'))
                .ToList();
        }
        else
        {
            throw new InvalidOperationException(
                "The recognition model does not embed a 'character' dictionary and no rec_keys_path was provided.");
        }

        // Upstream inserts the space at the end and the CTC blank at index 0.
        return AddSpecialTokens(characters);
    }

    internal static List<string> AddSpecialTokens(List<string> characters)
    {
        characters.Add(" ");
        characters.Insert(0, "blank");
        return characters;
    }

    internal static List<string> SplitLines(string text)
    {
        List<string> lines = new();
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            bool boundary = current is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029'
                or >= '\u001c' and <= '\u001e';
            if (!boundary)
            {
                continue;
            }

            lines.Add(text[start..index]);
            if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
