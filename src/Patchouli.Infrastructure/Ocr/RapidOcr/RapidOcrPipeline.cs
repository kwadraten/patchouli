using Microsoft.ML.OnnxRuntime;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

internal sealed record RapidOcrPipelineLine(string Text, double Score, RapidOcrPoint[] Points);

internal sealed record RapidOcrPipelineResult(IReadOnlyList<RapidOcrPipelineLine> Lines, string Text)
{
    public static RapidOcrPipelineResult Empty { get; } = new([], string.Empty);
}

/// <summary>
/// Native RapidOCR page pipeline (DB detection, optional PP-LCNet classification, CTC
/// recognition) mirroring upstream <c>python/rapidocr/main.py</c>. All inference runs
/// in-process through ONNX Runtime; no Python interpreter or external process is used.
/// </summary>
internal sealed class RapidOcrPipeline : IDisposable
{
    private readonly RapidOcrClassifier? _classifier;
    private readonly RapidOcrDetector _detector;
    private readonly RapidOcrParameters _parameters;
    private readonly RapidOcrRecognizer _recognizer;

    public RapidOcrPipeline(string modelsDirectory, RapidOcrParameters parameters)
    {
        _parameters = parameters;
        string detectorPath = RapidOcrModelFiles.GetLocalPath(modelsDirectory, RapidOcrModelFiles.Detector);
        string recognizerPath = RapidOcrModelFiles.GetLocalPath(modelsDirectory, RapidOcrModelFiles.Recognizer);
        string classifierPath = RapidOcrModelFiles.GetLocalPath(modelsDirectory, RapidOcrModelFiles.Classifier);

        RapidOcrDetector? detector = null;
        RapidOcrRecognizer? recognizer = null;
        RapidOcrClassifier? classifier = null;
        SessionOptions options = CreateSessionOptions(parameters);
        try
        {
            try
            {
                detector = new RapidOcrDetector(detectorPath, options, RapidOcrDetOptions.Default);
                recognizer = new RapidOcrRecognizer(recognizerPath, options, parameters.RecKeysPath);
                if (parameters.UseCls)
                {
                    classifier = new RapidOcrClassifier(classifierPath, options, RapidOcrClassifier.DefaultThreshold);
                }
            }
            catch
            {
                // Dispose the sessions that were created before the failure so a broken
                // classifier checkpoint cannot leak the detector/recognizer sessions.
                detector?.Dispose();
                recognizer?.Dispose();
                throw;
            }
        }
        finally
        {
            options.Dispose();
        }

        _detector = detector!;
        _recognizer = recognizer!;
        _classifier = classifier;
    }

    internal static SessionOptions CreateSessionOptions(RapidOcrParameters parameters)
    {
        SessionOptions options = new();
        options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL;
        options.EnableCpuMemArena = false;
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        int processorCount = Environment.ProcessorCount;
        if (parameters.IntraOpNumThreads != -1 && parameters.IntraOpNumThreads >= 1 &&
            parameters.IntraOpNumThreads <= processorCount)
        {
            options.IntraOpNumThreads = parameters.IntraOpNumThreads;
        }

        if (parameters.InterOpNumThreads != -1 && parameters.InterOpNumThreads >= 1 &&
            parameters.InterOpNumThreads <= processorCount)
        {
            options.InterOpNumThreads = parameters.InterOpNumThreads;
        }

        return options;
    }

    public RapidOcrPipelineResult Run(RapidOcrImage original)
    {
        double ratioHeight = 1.0;
        double ratioWidth = 1.0;
        RapidOcrImage image = original;
        if (_parameters.UsePreprocessImg)
        {
            image = ResizeWithinBounds(original, _parameters.MinSideLen, _parameters.MaxSideLen, out ratioHeight,
                out ratioWidth);
        }

        int paddingTop = 0;
        if (_parameters.UseVerticalPadding)
        {
            (image, paddingTop) = ApplyVerticalPadding(image, _parameters.WidthHeightRatio, _parameters.MinHeight);
        }

        IReadOnlyList<RapidOcrDetBox> boxes = _detector.Detect(image);
        if (boxes.Count == 0)
        {
            return RapidOcrPipelineResult.Empty;
        }

        List<RapidOcrImage> crops = new(boxes.Count);
        foreach (RapidOcrDetBox box in boxes)
        {
            crops.Add(CropTextRegion(image, box.Points));
        }

        IReadOnlyList<RapidOcrImage> classified = _classifier is null ? crops : _classifier.Run(crops);
        IReadOnlyList<RapidOcrRecognizedLine> recognized = _recognizer.Recognize(classified);

        List<RapidOcrPipelineLine> lines = new(recognized.Count);
        for (int index = 0; index < recognized.Count && index < boxes.Count; index++)
        {
            RapidOcrRecognizedLine line = recognized[index];
            // Upstream drops whitespace-only recognition results before score filtering.
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            if (line.Score < _parameters.TextScore)
            {
                continue;
            }

            RapidOcrPoint[] pagePoints = MapPointsToOriginal(boxes[index].Points, ratioHeight, ratioWidth,
                paddingTop, original.Width, original.Height);
            lines.Add(new RapidOcrPipelineLine(line.Text, line.Score, pagePoints));
        }

        return new RapidOcrPipelineResult(lines, string.Join("\n", lines.Select(static line => line.Text)));
    }

    /// <summary>
    /// Upstream <c>resize_image_within_bounds</c>. Note that it intentionally reproduces
    /// the upstream ratio bug: if both the max-side reduction and the min-side increase
    /// fire, the earlier reduction ratio is overwritten by the min-side ratio.
    /// </summary>
    internal static RapidOcrImage ResizeWithinBounds(RapidOcrImage image, int minSideLength, int maxSideLength,
        out double ratioHeight, out double ratioWidth)
    {
        ratioHeight = 1.0;
        ratioWidth = 1.0;
        RapidOcrImage current = image;
        int height = current.Height;
        int width = current.Width;
        if (Math.Max(height, width) > maxSideLength)
        {
            current = ResizeSide(current, maxSideLength, true, out ratioHeight, out ratioWidth);
        }

        height = current.Height;
        width = current.Width;
        if (Math.Min(height, width) < minSideLength)
        {
            current = ResizeSide(current, minSideLength, false, out ratioHeight, out ratioWidth);
        }

        return current;
    }

    private static RapidOcrImage ResizeSide(RapidOcrImage image, int limit, bool reduce, out double ratioHeight,
        out double ratioWidth)
    {
        int height = image.Height;
        int width = image.Width;
        double ratio = 1.0;
        if (reduce)
        {
            int longest = Math.Max(height, width);
            if (longest > limit)
            {
                ratio = (double)limit / longest;
            }
        }
        else
        {
            int shortest = Math.Min(height, width);
            if (shortest < limit)
            {
                ratio = (double)limit / shortest;
            }
        }

        int resizedHeight = RapidOcrDetPreprocess.RoundToMultiple((int)(height * ratio));
        int resizedWidth = RapidOcrDetPreprocess.RoundToMultiple((int)(width * ratio));
        if (resizedHeight <= 0 || resizedWidth <= 0)
        {
            throw new InvalidOperationException("The RapidOCR preprocessing resize produced an empty image.");
        }

        RapidOcrImage resized = image.ResizeLinear(resizedWidth, resizedHeight);
        ratioHeight = height / (double)resizedHeight;
        ratioWidth = width / (double)resizedWidth;
        return resized;
    }

    internal static (RapidOcrImage Image, int PaddingTop) ApplyVerticalPadding(RapidOcrImage image,
        int widthHeightRatio, int minHeight)
    {
        int height = image.Height;
        int width = image.Width;
        bool useLimitRatio = widthHeightRatio != -1 && (double)width / height > widthHeightRatio;
        if (height > minHeight && !useLimitRatio)
        {
            return (image, 0);
        }

        int newHeight = Math.Max((int)(width / (double)widthHeightRatio), minHeight) * 2;
        int paddingHeight = (int)(Math.Abs(newHeight - height) / 2.0);
        return (image.PadVertically(paddingHeight, paddingHeight), paddingHeight);
    }

    /// <summary>Upstream <c>get_rotate_crop_image</c>: perspective crop with a CCW rotation for tall lines.</summary>
    internal static RapidOcrImage CropTextRegion(RapidOcrImage image, IReadOnlyList<RapidOcrPoint> points)
    {
        int cropWidth = (int)Math.Max(Distance(points[0], points[1]), Distance(points[2], points[3]));
        int cropHeight = (int)Math.Max(Distance(points[0], points[3]), Distance(points[1], points[2]));
        cropWidth = Math.Max(1, cropWidth);
        cropHeight = Math.Max(1, cropHeight);

        RapidOcrPoint[] destination =
        [
            new(0, 0),
            new(cropWidth, 0),
            new(cropWidth, cropHeight),
            new(0, cropHeight)
        ];
        double[] outputToInput = RapidOcrGeometry.SolvePerspective(destination, points);
        RapidOcrImage warped = RapidOcrGeometry.WarpPerspective(image, outputToInput, cropWidth, cropHeight);
        if (cropHeight / (double)cropWidth >= 1.5)
        {
            return warped.Rotate90CounterClockwise();
        }

        return warped;
    }

    internal static RapidOcrPoint[] MapPointsToOriginal(IReadOnlyList<RapidOcrPoint> points, double ratioHeight,
        double ratioWidth, int paddingTop, int originalWidth, int originalHeight)
    {
        RapidOcrPoint[] mapped = new RapidOcrPoint[points.Count];
        for (int index = 0; index < points.Count; index++)
        {
            double x = Math.Max(0, points[index].X * ratioWidth);
            double y = Math.Max(0, (points[index].Y - paddingTop) * ratioHeight);
            mapped[index] = new RapidOcrPoint(Math.Min(x, originalWidth), Math.Min(y, originalHeight));
        }

        return mapped;
    }

    private static double Distance(RapidOcrPoint a, RapidOcrPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
        _classifier?.Dispose();
    }
}
