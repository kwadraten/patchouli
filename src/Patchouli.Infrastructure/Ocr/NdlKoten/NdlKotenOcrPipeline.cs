using System.Runtime.ExceptionServices;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public sealed class NdlKotenOcrPipeline : IDisposable
{
    // The official ocr.py recognizes line crops with a
    // ThreadPoolExecutor(max_workers=4); keep the same worker cap for parity.
    internal const int RecognitionWorkerCount = 4;

    private readonly RtmdetDetector _detector;
    private readonly ParseqRecognizer _recognizer;

    public NdlKotenOcrPipeline(string modelsDirectory)
    {
        string detectorModel = NdlKotenModelFiles.GetLocalPath(modelsDirectory, NdlKotenModelFiles.Files[0]);
        string recognizerModel = NdlKotenModelFiles.GetLocalPath(modelsDirectory, NdlKotenModelFiles.Files[1]);
        string classesYaml = NdlKotenModelFiles.GetLocalPath(modelsDirectory, NdlKotenModelFiles.Files[2]);
        string charsetYaml = NdlKotenModelFiles.GetLocalPath(modelsDirectory, NdlKotenModelFiles.Files[3]);

        _detector = new RtmdetDetector(detectorModel, classesYaml);
        IReadOnlyList<char> charlist = NdlKotenCharsetParser.Parse(File.ReadAllText(charsetYaml));
        _recognizer = new ParseqRecognizer(recognizerModel, charlist);
    }

    public NdlKotenPageResult Run(string imagePath)
    {
        using SKBitmap? image = SKBitmap.Decode(imagePath);
        if (image is null)
        {
            throw new InvalidOperationException($"Unable to decode image: {imagePath}");
        }

        return Run(image);
    }

    public NdlKotenPageResult Run(SKBitmap image)
    {
        IReadOnlyList<LineDetection> detections = FilterDetections(_detector.Detect(image));
        IReadOnlyList<LineDetection> ordered = OrderDetections(detections);

        SKBitmap[] crops = new SKBitmap[ordered.Count];
        string[] texts = new string[ordered.Count];
        try
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                crops[i] = Crop(image, ordered[i].Box);
            }

            try
            {
                Parallel.For(0, ordered.Count,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(RecognitionWorkerCount, Environment.ProcessorCount)
                    },
                    i => texts[i] = _recognizer.Read(crops[i]));
            }
            catch (AggregateException exception) when (exception.InnerException is not null)
            {
                // Parallel.For wraps worker failures in AggregateException; surface the
                // original recognition failure so callers keep the pre-parallel behavior.
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            }

            List<NdlKotenLine> lines = new(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                LineDetection detection = ordered[i];
                Box box = detection.Box;
                bool isVertical = box.Y1 - box.Y0 > box.X1 - box.X0;
                lines.Add(new NdlKotenLine(texts[i], box, isVertical, detection.Confidence));
            }

            return new NdlKotenPageResult(lines, string.Join("\n", lines.Select(static l => l.Text)));
        }
        finally
        {
            foreach (SKBitmap crop in crops)
            {
                crop?.Dispose();
            }
        }
    }

    internal static IReadOnlyList<LineDetection> FilterDetections(IReadOnlyList<LineDetection> detections)
    {
        bool[] kept = new bool[detections.Count];
        List<int> keptIndices = new();
        foreach (int index in Enumerable.Range(0, detections.Count)
                     .OrderByDescending(i => detections[i].Confidence))
        {
            Box box = detections[index].Box;
            if (box.X1 - box.X0 < 5 || box.Y1 - box.Y0 < 5)
            {
                continue;
            }

            bool duplicate =
                keptIndices.Any(keptIndex => IntersectionOverUnion(detections[keptIndex].Box, box) >= 0.7f);
            if (duplicate)
            {
                continue;
            }

            kept[index] = true;
            keptIndices.Add(index);
        }

        return Enumerable.Range(0, detections.Count)
            .Where(i => kept[i])
            .Select(i => detections[i])
            .ToArray();
    }

    private static float IntersectionOverUnion(Box a, Box b)
    {
        int x0 = Math.Max(a.X0, b.X0);
        int y0 = Math.Max(a.Y0, b.Y0);
        int x1 = Math.Min(a.X1, b.X1);
        int y1 = Math.Min(a.Y1, b.Y1);
        int intersectionWidth = Math.Max(0, x1 - x0);
        int intersectionHeight = Math.Max(0, y1 - y0);
        float intersection = intersectionWidth * intersectionHeight;
        float areaA = (float)(a.X1 - a.X0) * (a.Y1 - a.Y0);
        float areaB = (float)(b.X1 - b.X0) * (b.Y1 - b.Y0);
        return intersection / (areaA + areaB - intersection);
    }

    internal static IReadOnlyList<LineDetection> OrderDetections(IReadOnlyList<LineDetection> detections)
    {
        Box[] boxes = detections.Select(static detection => detection.Box).ToArray();
        int[] ranks = ReadingOrderSolver.Solve(boxes);
        return detections
            .Select((detection, index) => (Detection: detection, Rank: ranks[index]))
            .OrderBy(static item => item.Rank)
            .Select(static item => item.Detection)
            .ToArray();
    }

    private static SKBitmap Crop(SKBitmap image, Box box)
    {
        int x0 = Math.Clamp(box.X0, 0, image.Width - 1);
        int y0 = Math.Clamp(box.Y0, 0, image.Height - 1);
        int x1 = Math.Clamp(box.X1, x0 + 1, image.Width);
        int y1 = Math.Clamp(box.Y1, y0 + 1, image.Height);
        SKBitmap crop = new(x1 - x0, y1 - y0, image.ColorType, image.AlphaType);
        using SKCanvas canvas = new(crop);
        canvas.DrawBitmap(image, new SKRect(x0, y0, x1, y1), new SKRect(0, 0, crop.Width, crop.Height));
        return crop;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
    }
}

public sealed record NdlKotenLine(string Text, Box Box, bool IsVertical, float Confidence);

public sealed record NdlKotenPageResult(IReadOnlyList<NdlKotenLine> Lines, string Text);
