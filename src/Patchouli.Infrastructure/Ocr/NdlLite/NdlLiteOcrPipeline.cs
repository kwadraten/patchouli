using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>One recognized NDLOCR-Lite text line.</summary>
public sealed record NdlLiteLine(
    string Text,
    NdlLiteBox Box,
    bool IsVertical,
    float Confidence,
    int ClassIndex,
    string ClassName);

/// <summary>Page-level NDLOCR-Lite result: lines in reading order plus the joined text.</summary>
public sealed record NdlLitePageResult(IReadOnlyList<NdlLiteLine> Lines, string Text);

/// <summary>
/// Native NDLOCR-Lite pipeline: DEIM detection, layout/reading-order grouping and
/// the three-tier PARSeq cascade. No Python runtime or process is involved.
/// </summary>
public sealed class NdlLiteOcrPipeline : IDisposable
{
    private readonly NdlLiteDetector _detector;
    private readonly NdlLiteRecognitionCascade _cascade;

    public NdlLiteOcrPipeline(string modelsDirectory)
    {
        string detectorPath = NdlLiteModelFiles.GetDetectorPath(modelsDirectory);
        string classesPath = NdlLiteModelFiles.GetClassesPath(modelsDirectory);
        string charsetPath = NdlLiteModelFiles.GetCharsetPath(modelsDirectory);

        _detector = new NdlLiteDetector(detectorPath, classesPath);
        ClassNames = _detector.ClassNames;

        IReadOnlyList<string> charset = NdlLiteCharsetParser.Parse(File.ReadAllText(charsetPath));
        _cascade = new NdlLiteRecognitionCascade(
            new NdlLiteRecognizer(NdlLiteModelFiles.GetRecognizer30Path(modelsDirectory), charset),
            new NdlLiteRecognizer(NdlLiteModelFiles.GetRecognizer50Path(modelsDirectory), charset),
            new NdlLiteRecognizer(NdlLiteModelFiles.GetRecognizer100Path(modelsDirectory), charset));
    }

    public IReadOnlyList<string> ClassNames { get; }

    public NdlLitePageResult Run(string imagePath)
    {
        using SKBitmap? image = SKBitmap.Decode(imagePath);
        if (image is null)
        {
            throw new InvalidOperationException($"Unable to decode image: {imagePath}");
        }

        return Run(image);
    }

    public NdlLitePageResult Run(SKBitmap image)
    {
        IReadOnlyList<NdlLiteDetection> detections = _detector.Detect(image);
        IReadOnlyList<NdlLiteDetection> ordered = NdlLiteLayoutOrderer.Order(detections);

        List<SKBitmap> crops = new(ordered.Count);
        List<NdlLiteRecognitionInput> inputs = new(ordered.Count);
        try
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                SKBitmap crop = Crop(image, ordered[i].Box);
                crops.Add(crop);
                inputs.Add(new NdlLiteRecognitionInput(crop, ordered[i].PredictedCharCount, i));
            }

            IReadOnlyList<string> texts = _cascade.Recognize(inputs);
            List<NdlLiteLine> lines = new(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                NdlLiteDetection detection = ordered[i];
                lines.Add(new NdlLiteLine(
                    texts[i],
                    detection.Box,
                    detection.Box.Height > detection.Box.Width,
                    detection.Confidence,
                    detection.ClassIndex,
                    detection.ClassName));
            }

            return new NdlLitePageResult(lines, string.Join("\n", lines.Select(static line => line.Text)));
        }
        finally
        {
            foreach (SKBitmap crop in crops)
            {
                crop.Dispose();
            }
        }
    }

    internal static SKBitmap Crop(SKBitmap image, NdlLiteBox box)
    {
        int x0 = Math.Clamp(box.X0, 0, image.Width - 1);
        int y0 = Math.Clamp(box.Y0, 0, image.Height - 1);
        int x1 = Math.Clamp(box.X1, x0 + 1, image.Width);
        int y1 = Math.Clamp(box.Y1, y0 + 1, image.Height);
        SKBitmap crop = new(x1 - x0, y1 - y0, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKCanvas canvas = new(crop);
        canvas.DrawBitmap(image, new SKRect(x0, y0, x1, y1), new SKRect(0, 0, crop.Width, crop.Height));
        return crop;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _cascade.Dispose();
    }
}
