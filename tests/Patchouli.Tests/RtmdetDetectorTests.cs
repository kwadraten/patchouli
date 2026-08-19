using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class RtmdetDetectorTests
{
    [Fact]
    public void Postprocess_keeps_nonzero_label_above_threshold_as_line_main_and_removes_lower_scores()
    {
        IReadOnlyList<LineDetection> detections = RtmdetDetector.Postprocess(
        [
            new RtmdetRawDetection(100, 200, 300, 400, 0.1f, 42),
            new RtmdetRawDetection(100, 100, 200, 200, 0.099f, 1)
        ], 1000, 2000, 0.1f);

        LineDetection detection = detections.Should().ContainSingle().Subject;
        detection.ClassId.Should().Be(1);
        detection.Confidence.Should().Be(0.1f);
        detection.Box.Should().Be(new Box(200, 392, 600, 808));
    }

    [Fact]
    public void NormalizeToBgra8888_converts_non_bgra_input_before_preprocessing()
    {
        using SKBitmap input = new(2, 1, SKColorType.Rgb565, SKAlphaType.Opaque);
        input.Erase(SKColors.Red);

        using SKBitmap normalized = RtmdetDetector.NormalizeToBgra8888(input);

        normalized.ColorType.Should().Be(SKColorType.Bgra8888);
        normalized.GetPixel(0, 0).Red.Should().BeGreaterThan(200);
    }
}
