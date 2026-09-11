using System.Globalization;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.RapidOcr;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Tests;

/// <summary>
/// Opt-in end-to-end smoke test for the native RapidOCR pipeline. It runs only when
/// <c>PATCHOULI_RAPIDOCR_MODELS</c> points at a directory containing the pinned models,
/// and optionally uses the image in <c>PATCHOULI_RAPIDOCR_IMAGE</c> so the output can be
/// compared against upstream RapidOCR on the same input.
/// </summary>
public sealed class RapidOcrRealModelSmokeTests
{
    [Fact]
    public async Task Native_pipeline_runs_against_the_pinned_models()
    {
        string? modelsDirectory = Environment.GetEnvironmentVariable("PATCHOULI_RAPIDOCR_MODELS");
        if (string.IsNullOrWhiteSpace(modelsDirectory))
        {
            return;
        }

        string? fixedImage = Environment.GetEnvironmentVariable("PATCHOULI_RAPIDOCR_IMAGE");
        string imagePath = string.IsNullOrWhiteSpace(fixedImage)
            ? Path.Combine(Path.GetTempPath(), $"patchouli-rapidocr-smoke-{Guid.NewGuid():N}.png")
            : fixedImage;
        try
        {
            if (string.IsNullOrWhiteSpace(fixedImage))
            {
                CreateTextImage(imagePath);
            }

            await RunAsync(modelsDirectory, imagePath, """{"textScore":0.0}""", "cls");
            await RunAsync(modelsDirectory, imagePath, """{"useCls":false,"textScore":0.0}""", "noclass");
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(fixedImage) && File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }
        }
    }

    private static async Task RunAsync(string modelsDirectory, string imagePath, string parametersJson, string label)
    {
        RapidOcrOcrAdapter adapter = new(new OcrModelPathValidator(), modelsDirectory);
        OcrPresetVersion preset = new(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.RapidOcr,
            OcrModelIds.RapidOcrDefault, modelsDirectory, parametersJson, true, DateTimeOffset.UtcNow);
        OcrInputDescriptor input = new(PageId.New(), DocumentInstanceId.New(), OcrInputKinds.ImageFile, imagePath,
            null, null, "available", null);

        Result<OcrEnginePageResult> result = await adapter.RunPageAsync(input, preset);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        OcrEnginePageResult page = result.Value!;
        page.TextBoxes.Should().NotBeEmpty();
        foreach (OcrEngineTextBox box in page.TextBoxes)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"NATIVE {label} x={box.BBox.X:0.#####} y={box.BBox.Y:0.#####} w={box.BBox.Width:0.#####} h={box.BBox.Height:0.#####} score={box.Confidence:0.#####} text={box.Text}"));
        }
    }

    private static void CreateTextImage(string path)
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
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }
}
