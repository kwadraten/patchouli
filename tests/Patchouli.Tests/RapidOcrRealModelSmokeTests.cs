using System.Globalization;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines;
using SkiaSharp;

namespace Patchouli.Tests;

/// <summary>
/// Opt-in end-to-end smoke test for the native RapidOCR pipeline behind the bundled
/// <c>ocr.rapidocr</c> extension. It runs only when <c>PATCHOULI_RAPIDOCR_MODELS</c> points at a
/// directory containing the pinned models, and optionally uses the image in
/// <c>PATCHOULI_RAPIDOCR_IMAGE</c> so the output can be compared against upstream RapidOCR on the
/// same input.
/// </summary>
public sealed class RapidOcrRealModelSmokeTests
{
    [Fact]
    public void Native_pipeline_runs_against_the_pinned_models()
    {
        string? modelsDirectory = Environment.GetEnvironmentVariable("PATCHOULI_RAPIDOCR_MODELS");
        if (string.IsNullOrWhiteSpace(modelsDirectory))
        {
            return;
        }

        string? fixedImage = Environment.GetEnvironmentVariable("PATCHOULI_RAPIDOCR_IMAGE");
        byte[] imageBytes = string.IsNullOrWhiteSpace(fixedImage)
            ? CreateTextImage()
            : File.ReadAllBytes(fixedImage);

        Run(modelsDirectory, imageBytes, """{"textScore":0.0}""", "cls");
        Run(modelsDirectory, imageBytes, """{"useCls":false,"textScore":0.0}""", "noclass");
    }

    private static void Run(string modelsDirectory, byte[] imageBytes, string parametersJson, string label)
    {
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model-path"] = modelsDirectory,
            ["parameters-json"] = parametersJson
        }, 1);
        OcrJobInput input = new(new OcrInputId("smoke"), new NormalizedBBox(0, 0, 1, 1), "image/png");
        OcrJobPage page = new(new ExtensionPageIdentity(DocumentInstanceId.New(), PageId.New(),
            DocumentTreeRevisionId.New(), 0), [input]);

        using NativeOcrSession session = new(NativeOcrSession.RapidOcrEngineId, config);
        using OcrInputImage image = new(input.Id, "image/png", imageBytes);
        OcrExtensionBoxCandidate[] candidates = session.Recognize(page, input, image, CancellationToken.None);

        candidates.Should().NotBeEmpty();
        foreach (OcrExtensionBoxCandidate candidate in candidates)
        {
            DocumentBoxSeed seed = candidate.Candidate;
            NormalizedBBox box = seed.BBox;
            string text = (seed.Payload as TextBoxPayload)?.Markdown ?? "";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"NATIVE {label} x={box.X:0.#####} y={box.Y:0.#####} w={box.Width:0.#####} h={box.Height:0.#####} score={seed.Confidence:0.#####} text={text}"));
        }
    }

    private static byte[] CreateTextImage()
    {
        using SKBitmap bitmap = new(900, 400, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.White);
            using SKFont font = new(SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default, 48);
            using SKPaint paint = new() { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawText("Hello 123", 40, 80, SKTextAlign.Left, font, paint);
            canvas.DrawText("你好世界", 40, 160, SKTextAlign.Left, font, paint);
            canvas.DrawText("RapidOCR native", 40, 240, SKTextAlign.Left, font, paint);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
