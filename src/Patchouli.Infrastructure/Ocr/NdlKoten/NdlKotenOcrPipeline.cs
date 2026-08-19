using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public sealed class NdlKotenOcrPipeline : IDisposable
{
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
        IReadOnlyList<LineDetection> detections = _detector.Detect(image);
        IReadOnlyList<LineDetection> ordered = OrderDetections(detections);
        List<NdlKotenLine> lines = new();
        foreach (LineDetection detection in ordered)
        {
            Box box = detection.Box;
            bool isVertical = box.Y1 - box.Y0 > box.X1 - box.X0;
            using SKBitmap crop = Crop(image, box);
            string text = _recognizer.Read(crop);
            lines.Add(new NdlKotenLine(text, box, isVertical, detection.Confidence));
        }

        return new NdlKotenPageResult(lines, string.Join("\n", lines.Select(static l => l.Text)));
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
