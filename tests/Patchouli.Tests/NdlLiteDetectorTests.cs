using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;
using Patchouli.Infrastructure.Ocr.NdlLite;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class NdlLiteDetectorTests
{
    private static readonly IReadOnlyList<string> ClassNames =
    [
        "text_block", "line_main", "line_caption", "line_ad", "line_note", "line_note_tochu",
        "block_fig", "block_ad", "block_pillar", "block_folio", "block_rubi", "block_chart",
        "block_eqn", "block_cfm", "block_eng", "block_table", "line_title"
    ];

    [Fact]
    public void Postprocess_maps_one_based_labels_to_zero_based_class_names_and_scales_boxes()
    {
        IReadOnlyList<NdlLiteDetection> detections = NdlLiteDetector.Postprocess(
            [new NdlLiteRawDetection(2, 10.5f, 20.9f, 30.2f, 40.1f, 0.6f, 3.0f)],
            ClassNames, 100, 100, 200, 200, 0.25f);

        NdlLiteDetection detection = detections.Should().ContainSingle().Subject;
        detection.ClassIndex.Should().Be(1);
        detection.ClassName.Should().Be("line_main");
        detection.Confidence.Should().Be(0.6f);
        detection.PredictedCharCount.Should().Be(3.0f);
        detection.Box.Should().Be(new NdlLiteBox(21, 41, 60, 80));
    }

    [Fact]
    public void Postprocess_drops_scores_exactly_on_the_official_strict_threshold()
    {
        // deim.py keeps scores > conf_threshold, so a score equal to 0.25 is dropped.
        IReadOnlyList<NdlLiteDetection> detections = NdlLiteDetector.Postprocess(
            [
                new NdlLiteRawDetection(1, 0, 0, 10, 10, 0.25f, 100.0f),
                new NdlLiteRawDetection(1, 0, 0, 10, 10, 0.2500001f, 100.0f)
            ],
            ClassNames, 100, 100, 100, 100, 0.25f);

        detections.Should().ContainSingle();
    }

    [Fact]
    public void Postprocess_uses_the_model_input_width_for_both_scaling_axes()
    {
        // deim.py scales every ordinate by image_width / input_width, even the Y axis.
        IReadOnlyList<NdlLiteDetection> detections = NdlLiteDetector.Postprocess(
            [new NdlLiteRawDetection(1, 0, 0, 10, 10, 0.9f, 100.0f)],
            ClassNames, 50, 50, 200, 200, 0.25f);

        detections[0].Box.Should().Be(new NdlLiteBox(0, 0, 40, 40));
    }

    [Fact]
    public void Postprocess_clips_boxes_to_the_padded_canvas()
    {
        IReadOnlyList<NdlLiteDetection> detections = NdlLiteDetector.Postprocess(
            [new NdlLiteRawDetection(1, -5, -6, 1000, 1000, 0.9f, 100.0f)],
            ClassNames, 100, 100, 200, 200, 0.25f);

        detections[0].Box.Should().Be(new NdlLiteBox(0, 0, 200, 200));
    }

    [Fact]
    public void Postprocess_drops_label_zero_and_labels_beyond_the_class_list()
    {
        // Upstream deim.py would resolve label 0 as self.classes[-1] (line_title) in
        // Python; the port deliberately drops label < 1 instead of reproducing that
        // quirk, and also guards labels past the end of the class list.
        IReadOnlyList<NdlLiteDetection> detections = NdlLiteDetector.Postprocess(
            [
                new NdlLiteRawDetection(0, 0, 0, 10, 10, 0.9f, 100.0f),
                new NdlLiteRawDetection(99, 0, 0, 10, 10, 0.9f, 100.0f)
            ],
            ClassNames, 100, 100, 100, 100, 0.25f);

        detections.Should().BeEmpty();
    }

    [Fact]
    public void Postprocess_uses_the_default_predicted_character_count_when_no_char_head_is_present()
    {
        IReadOnlyList<NdlLiteDetection> detections = NdlLiteDetector.Postprocess(
            [new NdlLiteRawDetection(1, 0, 0, 10, 10, 0.9f, 100.0f)],
            ClassNames, 100, 100, 100, 100, 0.25f);

        detections[0].PredictedCharCount.Should().Be(100.0f);
    }

    [Fact]
    public void PadToSquare_black_pads_the_shorter_side_like_the_official_preprocess()
    {
        using SKBitmap image = new(2, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
        image.Erase(SKColors.Red);

        using SKBitmap padded = NdlLiteDetector.PadToSquare(image);

        padded.Width.Should().Be(3);
        padded.Height.Should().Be(3);
        padded.GetPixel(0, 0).Should().Be(SKColors.Red);
        padded.GetPixel(1, 2).Should().Be(SKColors.Red);
        padded.GetPixel(2, 0).Should().Be(SKColors.Black);
        padded.GetPixel(2, 2).Should().Be(SKColors.Black);
    }

    [Fact]
    public void CreateTensor_normalizes_in_rgb_order_with_imagenet_statistics()
    {
        using SKBitmap image = new(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        image.SetPixel(0, 0, new SKColor(255, 0, 0));

        DenseTensor<float> tensor = NdlLiteDetector.CreateTensor(image);

        tensor.Dimensions.ToArray().Should().Equal(1, 3, 1, 1);
        tensor[0, 0, 0, 0].Should().BeApproximately((1.0f - 0.485f) / 0.229f, 1e-4f); // R
        tensor[0, 1, 0, 0].Should().BeApproximately((0.0f - 0.456f) / 0.224f, 1e-4f); // G
        tensor[0, 2, 0, 0].Should().BeApproximately((0.0f - 0.406f) / 0.225f, 1e-4f); // B
    }

    [Fact]
    public void NormalizeToBgra8888_converts_other_color_types()
    {
        using SKBitmap input = new(2, 1, SKColorType.Rgb565, SKAlphaType.Opaque);
        input.Erase(SKColors.Red);

        using SKBitmap normalized = NdlLiteDetector.NormalizeToBgra8888(input);

        normalized.ColorType.Should().Be(SKColorType.Bgra8888);
        normalized.GetPixel(0, 0).Red.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Detector_default_confidence_matches_the_official_0_25()
    {
        NdlLiteDetector.DefaultConfidenceThreshold.Should().Be(0.25f);
    }
}
