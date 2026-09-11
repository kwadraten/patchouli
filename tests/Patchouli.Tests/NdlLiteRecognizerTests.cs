using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;
using Patchouli.Infrastructure.Ocr.NdlLite;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class NdlLiteRecognizerTests
{
    [Theory]
    [InlineData(10, 8, false)] // h == w * 0.8 is not greater than the trigger
    [InlineData(10, 9, true)]
    [InlineData(10, 10, true)]
    [InlineData(100, 20, false)]
    public void ShouldRotateCounterClockwise_matches_the_official_h_greater_than_w_times_0_8_rule(int width,
        int height, bool expected)
    {
        NdlLiteRecognizer.ShouldRotateCounterClockwise(width, height).Should().Be(expected);
    }

    [Fact]
    public void RotateCounterClockwise_matches_cv2_rotate_90_counterclockwise()
    {
        using SKBitmap source = new(2, 3, SKColorType.Bgra8888, SKAlphaType.Premul);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Green);
        source.SetPixel(0, 2, SKColors.Blue);
        source.SetPixel(1, 2, SKColors.Yellow);

        using SKBitmap rotated = NdlLiteRecognizer.RotateCounterClockwise(source);

        rotated.Width.Should().Be(3);
        rotated.Height.Should().Be(2);
        rotated.GetPixel(0, 0).Should().Be(SKColors.Green);
        rotated.GetPixel(2, 0).Should().Be(SKColors.Yellow);
        rotated.GetPixel(0, 1).Should().Be(SKColors.Red);
        rotated.GetPixel(2, 1).Should().Be(SKColors.Blue);
    }

    [Fact]
    public void Preprocess_resizes_a_vertical_crop_to_the_model_input_shape()
    {
        using SKBitmap source = new(30, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        source.Erase(SKColors.White);

        // h (120) > w (30) * 0.8 -> rotated to 120x30, then resized to the model shape.
        using SKBitmap prepared = NdlLiteRecognizer.Preprocess(source, 256, 24);

        prepared.Width.Should().Be(256);
        prepared.Height.Should().Be(24);
    }

    [Fact]
    public void CreateTensor_uses_bgr_channel_order_scaled_to_minus_one_to_one()
    {
        using SKBitmap image = new(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        image.SetPixel(0, 0, new SKColor(255, 0, 0));

        DenseTensor<float> tensor = NdlLiteRecognizer.CreateTensor(image);

        tensor.Dimensions.ToArray().Should().Equal(1, 3, 1, 1);
        tensor[0, 0, 0, 0].Should().BeApproximately(-1.0f, 1e-5f); // B
        tensor[0, 1, 0, 0].Should().BeApproximately(-1.0f, 1e-5f); // G
        tensor[0, 2, 0, 0].Should().BeApproximately(1.0f, 1e-4f); // R
    }

    [Fact]
    public void Decode_stops_at_token_zero_and_maps_remaining_tokens_to_charset_index_minus_one()
    {
        IReadOnlyList<string> charset = [" ", "a", "b", "c"];

        string text = NdlLiteRecognizer.Decode([2, 3, 4, 0, 2], charset);

        text.Should().Be("abc");
    }

    [Fact]
    public void Decode_decodes_the_full_sequence_when_no_stop_token_is_emitted()
    {
        IReadOnlyList<string> charset = [" ", "a", "b"];

        NdlLiteRecognizer.Decode([2, 3], charset).Should().Be("ab");
    }

    [Fact]
    public void Decode_reassembles_an_astral_code_point_without_lone_surrogates()
    {
        IReadOnlyList<string> charset = [" ", "\U0002231E"];

        string text = NdlLiteRecognizer.Decode([2], charset);

        text.Should().Be("\U0002231E");
        text.Length.Should().Be(2);
        char.IsSurrogatePair(text, 0).Should().BeTrue();
    }

    [Fact]
    public void Decode_throws_for_tokens_outside_the_character_set()
    {
        // The constructor invariant guarantees outputClassCount - 1 == charset.Count,
        // so a stray token is a hard model/character-set mismatch, not silently dropped.
        IReadOnlyList<string> charset = [" ", "a"];

        Action decode = () => NdlLiteRecognizer.Decode([2, 99, 2], charset);

        decode.Should().Throw<InvalidOperationException>().WithMessage("*token 99*");
    }

    [Fact]
    public void ValidateCharsetSize_accepts_a_blank_class_plus_one_class_per_code_point()
    {
        NdlLiteRecognizer.ValidateCharsetSize(2, 1);
        NdlLiteRecognizer.ValidateCharsetSize(7142, 7141);
        NdlLiteRecognizer.ValidateCharsetSize(1, 0);
    }

    [Fact]
    public void ValidateCharsetSize_rejects_a_model_and_character_set_from_different_releases()
    {
        // This is the invariant that would have caught the char[]/code-point bug:
        // a 7145-entry (UTF-16 unit) split does not match the 7142-class model.
        Action validate = () => NdlLiteRecognizer.ValidateCharsetSize(7142, 7145);

        validate.Should().Throw<InvalidOperationException>().WithMessage("*7142*7145*");
    }
}
