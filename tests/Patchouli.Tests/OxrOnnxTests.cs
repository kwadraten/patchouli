using FluentAssertions;
using Microsoft.ML.OnnxRuntime;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Extensions.Contracts;
using Patchouli.Ocr.Engines.Oxr;
using SkiaSharp;
using Xunit;

namespace Patchouli.Tests;

public sealed class OxrOnnxTests
{
    [Fact]
    public void Definition_requires_a_local_offline_model()
    {
        OcrEngineDefinition definition = OxrOnnxSession.GetDefinition();
        definition.EngineId.Should().Be("oxr");
        definition.AdapterKind.Should().Be("local_library");
        definition.RequiresModelPath.Should().BeTrue();
        OxrOnnxSession.CheckReadiness(Config("")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Tokenizer_decodes_the_original_byte_level_vocabulary()
    {
        string directory = TestPaths.FromRepositoryRoot(".tmp", "tests", "oxr-tokenizer");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path,
                """{"model":{"vocab":{"H":0,"E":1,"L":2,"O":3,"\u0120":4,"C":5,"R":6}}}""");
            OxrTokenizer tokenizer = new(path);
            tokenizer.Decode([0, 1, 2, 2, 3, 4, 3, 5, 6]).Should().Be("HELLO OCR");
            Action invalid = () => tokenizer.Decode([-1]);
            invalid.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Image_processor_orders_vision_patches_by_two_by_two_blocks()
    {
        using SKBitmap bitmap = new(448, 448);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(16, 0, SKColors.Black);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        float[] patches = OxrImageProcessor.Prepare(png.ToArray());
        patches.Should().HaveCount(784 * 768);
        patches[0].Should().BeApproximately(1f, 1e-5f);
        patches[768].Should().BeApproximately(-1f, 1e-5f);
        patches[2 * 768].Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void Opt_in_live_model_recognizes_an_image_without_network()
    {
        string? directory = Environment.GetEnvironmentVariable("PATCHOULI_OXR_TEST_MODEL_PATH");
        string? samplePath = Environment.GetEnvironmentVariable("PATCHOULI_OXR_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(samplePath))
        {
            return;
        }

        using OxrOnnxSession session = new(Config(directory));
        OcrJobInput input = new(new OcrInputId("oxr-test"),
            new NormalizedBBox(0, 0, 1, 1), "image/png");
        using OcrInputImage image = new(input.Id, input.ContentType, File.ReadAllBytes(samplePath));
        OcrExtensionBoxCandidate[] boxes = session.Recognize(input, image, CancellationToken.None);
        boxes.Should().ContainSingle();
        boxes[0].Candidate.Payload.Should().BeOfType<TextBoxPayload>()
            .Which.Markdown.Should().Contain("HELLO OCR 123");
    }

    [Fact]
    public void Opt_in_layout_model_detects_a_real_text_region()
    {
        string? modelPath = Environment.GetEnvironmentVariable("PATCHOULI_OXR_TEST_LAYOUT_PATH");
        string? samplePath = Environment.GetEnvironmentVariable("PATCHOULI_OXR_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(samplePath))
        {
            return;
        }

        using SKBitmap image = SKBitmap.Decode(File.ReadAllBytes(samplePath));
        using SessionOptions options = new()
        {
            IntraOpNumThreads = 4,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL
        };
        using InferenceSession model = new(modelPath, options);
        OxrLayoutRegion[] regions = OxrLayoutAnalyzer.Detect(image, model);
        regions.Should().Contain(region =>
            region.Category == 3 && region.Score > 0.7f &&
            Math.Abs(region.Left - 21) <= 4 && Math.Abs(region.Top - 118) <= 4 &&
            Math.Abs(region.Right - 410) <= 4 && Math.Abs(region.Bottom - 176) <= 4);
    }

    private static ExtensionConfigSnapshot Config(string directory)
    {
        return new ExtensionConfigSnapshot(new Dictionary<string, string>
        {
            ["model-path"] = directory,
            ["max-new-tokens"] = "64"
        }, 1);
    }
}
