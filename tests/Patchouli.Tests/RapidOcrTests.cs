using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.RapidOcr;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class RapidOcrTests
{
    [Fact]
    public void Parameters_from_empty_json_use_rapidocr_official_defaults()
    {
        RapidOcrParameters parameters = RapidOcrParameters.FromJson("{}");

        parameters.TextScore.Should().Be(0.5);
        parameters.UseDet.Should().BeTrue();
        parameters.UseCls.Should().BeTrue();
        parameters.UseRec.Should().BeTrue();
        parameters.UsePreprocessImg.Should().BeTrue();
        parameters.MinSideLen.Should().Be(30);
        parameters.MaxSideLen.Should().Be(2000);
        parameters.UseVerticalPadding.Should().BeTrue();
        parameters.MinHeight.Should().Be(30);
        parameters.WidthHeightRatio.Should().Be(8);
        parameters.ReturnWordBox.Should().BeFalse();
        parameters.ReturnSingleCharBox.Should().BeFalse();
        parameters.ModelRootDir.Should().BeNull();
        parameters.RecKeysPath.Should().BeNull();
        parameters.IntraOpNumThreads.Should().Be(-1);
        parameters.InterOpNumThreads.Should().Be(-1);
    }

    [Fact]
    public void Parameters_from_null_or_invalid_json_fall_back_to_defaults()
    {
        RapidOcrParameters.FromJson(null).Should().Be(RapidOcrParameters.Default);
        RapidOcrParameters.FromJson("not json").Should().Be(RapidOcrParameters.Default);
    }

    [Fact]
    public void Parameters_override_from_json_and_clamp_to_valid_ranges()
    {
        RapidOcrParameters parameters = RapidOcrParameters.FromJson(
            """
            {"textScore":5,"useDet":false,"returnWordBox":true,"returnSingleCharBox":true,
             "minSideLen":0,"maxSideLen":0,"minHeight":0,"widthHeightRatio":0}
            """);

        parameters.TextScore.Should().Be(1.0);
        parameters.UseDet.Should().BeFalse();
        parameters.ReturnWordBox.Should().BeTrue();
        parameters.ReturnSingleCharBox.Should().BeTrue();
        parameters.MinSideLen.Should().Be(1);
        parameters.MaxSideLen.Should().Be(1);
        parameters.MinHeight.Should().Be(1);
        parameters.WidthHeightRatio.Should().Be(1);
        parameters.GetUnsupportedOptions().Should().Equal("returnWordBox", "returnSingleCharBox");

        // Upstream uses -1 to disable the vertical-padding aspect ratio check.
        RapidOcrParameters.FromJson("""{"widthHeightRatio":-1}""").WidthHeightRatio.Should().Be(-1);
    }

    [Fact]
    public void Model_manifest_pins_upstream_v3_9_2_detector_recognizer_and_classifier()
    {
        RapidOcrModelFiles.BaseUrl.Should()
            .Be("https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2");
        RapidOcrModelFiles.Files.Should().HaveCount(3);

        RapidOcrModelFiles.Detector.FileName.Should().Be("PP-OCRv6_det_small.onnx");
        RapidOcrModelFiles.Detector.DownloadUrl.Should()
            .Be(
                "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx");
        RapidOcrModelFiles.Detector.ExpectedBytes.Should().Be(9_929_594);
        RapidOcrModelFiles.Detector.Sha256.Should()
            .Be("090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f");

        RapidOcrModelFiles.Recognizer.FileName.Should().Be("PP-OCRv6_rec_small.onnx");
        RapidOcrModelFiles.Recognizer.ExpectedBytes.Should().Be(21_234_383);
        RapidOcrModelFiles.Recognizer.Sha256.Should()
            .Be("6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884");

        RapidOcrModelFiles.Classifier.FileName.Should().Be("ch_ppocr_mobile_v2.0_cls_mobile.onnx");
        RapidOcrModelFiles.Classifier.ExpectedBytes.Should().Be(585_532);
        RapidOcrModelFiles.Classifier.Sha256.Should()
            .Be("e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c");
    }

    [Fact]
    public void Model_files_are_reported_missing_until_every_manifest_file_matches_its_size()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            RapidOcrModelFiles.IsComplete(directory).Should().BeFalse();
            RapidOcrModelFiles.GetMissing(directory).Should().HaveCount(3);

            File.WriteAllBytes(RapidOcrModelFiles.GetLocalPath(directory, RapidOcrModelFiles.Detector),
                new byte[RapidOcrModelFiles.Detector.ExpectedBytes]);
            RapidOcrModelFiles.GetMissing(directory).Should().HaveCount(2);
            RapidOcrModelFiles.IsComplete(directory).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Model_readiness_requires_the_classifier_only_when_classification_is_enabled()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(RapidOcrModelFiles.GetLocalPath(directory, RapidOcrModelFiles.Detector),
                new byte[RapidOcrModelFiles.Detector.ExpectedBytes]);
            File.WriteAllBytes(RapidOcrModelFiles.GetLocalPath(directory, RapidOcrModelFiles.Recognizer),
                new byte[RapidOcrModelFiles.Recognizer.ExpectedBytes]);

            RapidOcrModelFiles.GetRequiredFiles(false).Should().HaveCount(2);
            RapidOcrModelFiles.GetMissing(directory, false).Should().BeEmpty();
            RapidOcrModelFiles.IsComplete(directory, false).Should().BeTrue();

            RapidOcrModelFiles.GetRequiredFiles().Should().HaveCount(3);
            RapidOcrModelFiles.GetMissing(directory).Should().ContainSingle()
                .Which.Should().Be(RapidOcrModelFiles.Classifier);
            RapidOcrModelFiles.IsComplete(directory).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Det_resize_rounds_to_multiples_of_32_with_python_bankers_rounding()
    {
        RapidOcrDetPreprocess.RoundToMultiple(2000).Should().Be(1984);
        RapidOcrDetPreprocess.RoundToMultiple(16).Should().Be(0);
        RapidOcrDetPreprocess.RoundToMultiple(80).Should().Be(64);

        // limit_type == "min" upscales the short side to the configured length.
        RapidOcrImage upscaled = RapidOcrDetPreprocess.Resize(new RapidOcrImage(31, 31), 736, "min");
        upscaled.Width.Should().Be(736);
        upscaled.Height.Should().Be(736);

        // A large image keeps ratio 1.0 but is still rounded down to multiples of 32.
        RapidOcrImage rounded = RapidOcrDetPreprocess.Resize(new RapidOcrImage(1000, 2000), 736, "min");
        rounded.Width.Should().Be(992);
        rounded.Height.Should().Be(1984);
    }

    [Fact]
    public void Det_tensor_normalizes_channel_first_with_bgr_mean_and_std()
    {
        RapidOcrImage image = new(1, 1);
        image.Pixels[0] = 0;
        image.Pixels[1] = 128;
        image.Pixels[2] = 255;

        DenseTensor<float> tensor = RapidOcrDetPreprocess.CreateTensor(image, [0.5, 0.5, 0.5], [0.5, 0.5, 0.5]);

        tensor.Dimensions.ToArray().Should().Equal(1, 3, 1, 1);
        tensor[0, 0, 0, 0].Should().BeApproximately(-1.0f, 1e-6f);
        tensor[0, 1, 0, 0].Should().BeApproximately(0.0039215f, 1e-5f);
        tensor[0, 2, 0, 0].Should().BeApproximately(1.0f, 1e-6f);
    }

    [Fact]
    public void Det_postprocess_returns_one_expanded_clockwise_box_for_a_filled_rectangle()
    {
        float[] map = FilledProbabilityMap(100, 100, 40, 30, 19, 9, 0.9f);
        RapidOcrDetOptions options = RapidOcrDetOptions.Default with { UseDilation = false };

        IReadOnlyList<RapidOcrDetBox> boxes = DbPostProcess.Run(map, 100, 100, 100, 100, options);

        RapidOcrDetBox box = boxes.Should().ContainSingle().Subject;
        box.Score.Should().BeApproximately(0.9, 1e-6);
        box.X.Should().BeInRange(34, 36);
        box.Y.Should().BeInRange(24, 26);
        box.Right.Should().BeInRange(63, 65);
        box.Bottom.Should().BeInRange(43, 45);
    }

    [Fact]
    public void Det_postprocess_drops_regions_below_the_box_score_threshold()
    {
        float[] map = FilledProbabilityMap(100, 100, 40, 30, 19, 9, 0.4f);
        RapidOcrDetOptions options = RapidOcrDetOptions.Default with { UseDilation = false };

        DbPostProcess.Run(map, 100, 100, 100, 100, options).Should().BeEmpty();
    }

    [Fact]
    public void Det_postprocess_orders_boxes_by_line_then_horizontal_position()
    {
        float[] map = new float[200 * 200];
        FillRectangle(map, 200, 10, 10, 19, 9, 0.9f);
        FillRectangle(map, 200, 120, 10, 19, 9, 0.9f);
        FillRectangle(map, 200, 10, 60, 19, 9, 0.9f);
        FillRectangle(map, 200, 120, 60, 19, 9, 0.9f);
        RapidOcrDetOptions options = RapidOcrDetOptions.Default with { UseDilation = false };

        IReadOnlyList<RapidOcrDetBox> boxes = DbPostProcess.Run(map, 200, 200, 200, 200, options);

        boxes.Should().HaveCount(4);
        boxes[0].X.Should().BeLessThan(boxes[1].X);
        boxes[1].Y.Should().BeLessThan(boxes[2].Y);
        boxes[2].X.Should().BeLessThan(boxes[3].X);
    }

    [Fact]
    public void Det_dilation_grows_the_mask_by_one_pixel()
    {
        bool[] mask = new bool[5 * 5];
        mask[2 * 5 + 2] = true;

        bool[] dilated = DbPostProcess.Dilate2x2(mask, 5, 5);

        // cv2.dilate uses anchor (1, 1) for a 2x2 kernel, so the pixel spreads to
        // (2,2), (3,2), (2,3) and (3,3).
        dilated.Count(static value => value).Should().Be(4);
        dilated[2 * 5 + 2].Should().BeTrue();
        dilated[3 * 5 + 2].Should().BeTrue();
        dilated[2 * 5 + 3].Should().BeTrue();
        dilated[3 * 5 + 3].Should().BeTrue();
    }

    [Fact]
    public void Geometry_offset_polygon_inflates_a_rectangle_by_the_round_offset_distance()
    {
        RapidOcrPoint[] rectangle =
        [
            new(0, 0), new(10, 0), new(10, 5), new(0, 5)
        ];

        RapidOcrPoint[] offset = RapidOcrGeometry.OffsetPolygon(rectangle, 2);
        (RapidOcrPoint[] _, double width, double height) = RapidOcrGeometry.MinAreaRect(offset);

        width.Should().BeApproximately(14, 0.05);
        height.Should().BeApproximately(9, 0.05);
    }

    [Fact]
    public void Geometry_orders_four_points_clockwise_from_top_left()
    {
        RapidOcrPoint[] ordered = RapidOcrGeometry.OrderPointsClockwise(
        [
            new RapidOcrPoint(50, 40), new RapidOcrPoint(10, 10), new RapidOcrPoint(50, 10), new RapidOcrPoint(10, 40)
        ]);

        ordered.Should().Equal(new RapidOcrPoint(10, 10), new RapidOcrPoint(50, 10), new RapidOcrPoint(50, 40),
            new RapidOcrPoint(10, 40));
    }

    [Fact]
    public void Geometry_min_area_rect_matches_the_axis_aligned_pixel_bounds()
    {
        List<RapidOcrPoint> points = new();
        for (int y = 30; y <= 39; y++)
        {
            for (int x = 40; x <= 59; x++)
            {
                points.Add(new RapidOcrPoint(x, y));
            }
        }

        (RapidOcrPoint[] _, double width, double height) = RapidOcrGeometry.MinAreaRect(points);

        width.Should().BeApproximately(19, 1e-6);
        height.Should().BeApproximately(9, 1e-6);
    }

    [Fact]
    public void Ctc_decode_drops_blanks_and_repeated_tokens_and_rounds_the_mean_score()
    {
        IReadOnlyList<string> characters = ["blank", "a", "b", " "];
        DenseTensor<float> predictions = new(new[] { 1, 6, 4 });
        predictions[0, 0, 0] = 0.9f;
        predictions[0, 1, 1] = 0.8f;
        predictions[0, 2, 1] = 0.7f;
        predictions[0, 3, 0] = 0.95f;
        predictions[0, 4, 2] = 0.6f;
        predictions[0, 5, 3] = 0.55f;

        RapidOcrRecognizedLine line = RapidOcrRecognizer.Decode(predictions, 0, characters);

        line.Text.Should().Be("ab ");
        line.Score.Should().Be(0.65);
    }

    [Fact]
    public void Ctc_decode_returns_an_empty_zero_confidence_line_when_all_tokens_are_blank()
    {
        IReadOnlyList<string> characters = ["blank", "a", "b", " "];
        DenseTensor<float> predictions = new(new[] { 1, 3, 4 });
        predictions[0, 0, 0] = 1.0f;
        predictions[0, 1, 0] = 1.0f;
        predictions[0, 2, 0] = 1.0f;

        RapidOcrRecognizedLine line = RapidOcrRecognizer.Decode(predictions, 0, characters);

        line.Text.Should().BeEmpty();
        line.Score.Should().Be(0);
    }

    [Fact]
    public void Recognizer_character_list_is_blank_then_dictionary_then_trailing_space()
    {
        List<string> characters = RapidOcrRecognizer.AddSpecialTokens(["a", "b"]);

        characters.Should().Equal("blank", "a", "b", " ");
        RapidOcrRecognizer.SplitLines("a\nb\r\nc").Should().Equal("a", "b", "c");
        RapidOcrRecognizer.SplitLines("a\n").Should().Equal("a");
    }

    [Fact]
    public void Recognizer_batch_width_uses_the_max_aspect_ratio_with_the_320_over_48_floor()
    {
        RapidOcrRecognizer.ComputeBatchWidth(RapidOcrRecognizer.ImageWidth / (double)RapidOcrRecognizer.ImageHeight)
            .Should().Be(320);
        RapidOcrRecognizer.ComputeBatchWidth(20).Should().Be(960);
    }

    [Fact]
    public void Classifier_rotates_180_only_above_the_threshold()
    {
        RapidOcrClassifier.ShouldRotate(1, 0.91, 0.9).Should().BeTrue();
        RapidOcrClassifier.ShouldRotate(1, 0.9, 0.9).Should().BeFalse();
        RapidOcrClassifier.ShouldRotate(0, 0.99, 0.9).Should().BeFalse();
    }

    [Fact]
    public void Vertical_padding_matches_the_upstream_formula_for_short_wide_images()
    {
        (RapidOcrImage padded, int paddingTop) =
            RapidOcrPipeline.ApplyVerticalPadding(new RapidOcrImage(100, 10), 8, 30);

        paddingTop.Should().Be(25);
        padded.Height.Should().Be(60);
        padded.Width.Should().Be(100);
    }

    [Fact]
    public void Vertical_padding_leaves_tall_images_untouched()
    {
        RapidOcrImage source = new(100, 100);
        (RapidOcrImage padded, int paddingTop) = RapidOcrPipeline.ApplyVerticalPadding(source, 8, 30);

        paddingTop.Should().Be(0);
        padded.Should().BeSameAs(source);
    }

    [Fact]
    public void Crop_text_region_rotates_tall_lines_counter_clockwise()
    {
        RapidOcrImage source = new(100, 100);
        RapidOcrPoint[] tall =
        [
            new(10, 10), new(30, 10), new(30, 70), new(10, 70)
        ];

        RapidOcrImage cropped = RapidOcrPipeline.CropTextRegion(source, tall);

        cropped.Width.Should().Be(60);
        cropped.Height.Should().Be(20);

        RapidOcrPoint[] wide =
        [
            new(10, 10), new(70, 10), new(70, 30), new(10, 30)
        ];
        RapidOcrImage unrotated = RapidOcrPipeline.CropTextRegion(source, wide);
        unrotated.Width.Should().Be(60);
        unrotated.Height.Should().Be(20);
    }

    [Fact]
    public void Pipeline_maps_detection_points_back_through_padding_then_preprocessing_ratio()
    {
        RapidOcrPoint[] mapped = RapidOcrPipeline.MapPointsToOriginal([new RapidOcrPoint(10, 20)], 2, 3, 5, 100, 100);

        mapped[0].X.Should().Be(30);
        mapped[0].Y.Should().Be(30);

        RapidOcrPoint[] clamped = RapidOcrPipeline.MapPointsToOriginal([new RapidOcrPoint(-4, 500)], 1, 1, 0, 100,
            100);
        clamped[0].X.Should().Be(0);
        clamped[0].Y.Should().Be(100);
    }

    [Fact]
    public void Resize_within_bounds_upscales_images_shorter_than_the_minimum_side()
    {
        RapidOcrImage source = new(20, 10);

        RapidOcrImage resized = RapidOcrPipeline.ResizeWithinBounds(source, 30, 2000, out double ratioHeight,
            out double ratioWidth);

        resized.Width.Should().Be(64);
        resized.Height.Should().Be(32);
        ratioWidth.Should().BeApproximately(20 / 64.0, 1e-9);
        ratioHeight.Should().BeApproximately(10 / 32.0, 1e-9);
    }

    [Fact]
    public void Image_rotate_90_counter_clockwise_maps_source_pixels_to_rot90_positions()
    {
        RapidOcrImage source = new(3, 2);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                source.Pixels[(y * 3 + x) * 3] = (byte)(10 * y + x);
            }
        }

        RapidOcrImage rotated = source.Rotate90CounterClockwise();

        rotated.Width.Should().Be(2);
        rotated.Height.Should().Be(3);
        // rot90 maps src(x, y) to dst(row = width - 1 - x, col = y).
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                int row = 2 - x;
                int column = y;
                rotated.Pixels[(row * 2 + column) * 3].Should().Be((byte)(10 * y + x));
            }
        }
    }

    [Fact]
    public void Image_rotate_180_reverses_both_axes()
    {
        RapidOcrImage source = new(2, 2);
        source.Pixels[0] = 1;
        source.Pixels[3] = 2;
        source.Pixels[6] = 3;
        source.Pixels[9] = 4;

        RapidOcrImage rotated = source.Rotate180();

        rotated.Pixels[0].Should().Be(4);
        rotated.Pixels[3].Should().Be(3);
        rotated.Pixels[6].Should().Be(2);
        rotated.Pixels[9].Should().Be(1);
    }

    [Fact]
    public void Image_composites_fully_transparent_pixels_over_white_background()
    {
        using SKBitmap bitmap = new(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, new SKColor(0, 0, 0, 0));

        RapidOcrImage image = RapidOcrImage.FromBitmap(bitmap, SKEncodedOrigin.TopLeft);

        image.Pixels[0].Should().Be(255);
        image.Pixels[1].Should().Be(255);
        image.Pixels[2].Should().Be(255);
    }

    [Fact]
    public void Image_applies_exif_orientation_before_returning_bgr_pixels()
    {
        RapidOcrImage source = new(2, 1);
        source.Pixels[0] = 10;
        source.Pixels[3] = 20;

        RapidOcrImage oriented = RapidOcrImage.ApplyOrientation(source, SKEncodedOrigin.RightTop);

        oriented.Width.Should().Be(1);
        oriented.Height.Should().Be(2);
        oriented.Pixels[0].Should().Be(10);
        oriented.Pixels[3].Should().Be(20);
    }

    [Fact]
    public async Task Adapter_validates_preset_options_and_model_directory()
    {
        RapidOcrOcrAdapter adapter = new(new OcrModelPathValidator(), Path.GetTempPath());

        (await adapter.ValidatePresetAsync(Preset("""{"useRec":false}"""))).ErrorMessage.Should()
            .Contain("useDet and useRec");
        (await adapter.ValidatePresetAsync(Preset("""{"returnWordBox":true}"""))).ErrorMessage.Should()
            .Contain("returnWordBox");
        (await adapter.ValidatePresetAsync(Preset("{}", null))).ErrorMessage.Should()
            .Contain("model directory");
        (await adapter.ValidatePresetAsync(Preset("{}", Path.GetTempPath()))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Adapter_reports_missing_model_files_as_a_readiness_failure()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            RapidOcrOcrAdapter adapter = new(new OcrModelPathValidator(), directory);

            OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset(modelPath: directory));

            result.IsReady.Should().BeFalse();
            result.Status.Should().Be(OcrEnvironmentStatus.MissingModelPath);
            result.RequiredAction.Should().Be(OcrRequiredAction.RebindModelPath);
            result.Message.Should().Contain("PP-OCRv6_det_small.onnx");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Adapter_is_ready_once_every_pinned_model_file_is_present()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            foreach (RapidOcrModelFile file in RapidOcrModelFiles.Files)
            {
                File.WriteAllBytes(RapidOcrModelFiles.GetLocalPath(directory, file), new byte[file.ExpectedBytes]);
            }

            RapidOcrOcrAdapter adapter = new(new OcrModelPathValidator(), directory);
            OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset(modelPath: directory));

            result.IsReady.Should().BeTrue();
            result.Status.Should().Be(OcrEnvironmentStatus.Ready);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Adapter_is_ready_without_the_classifier_when_classification_is_disabled()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(RapidOcrModelFiles.GetLocalPath(directory, RapidOcrModelFiles.Detector),
                new byte[RapidOcrModelFiles.Detector.ExpectedBytes]);
            File.WriteAllBytes(RapidOcrModelFiles.GetLocalPath(directory, RapidOcrModelFiles.Recognizer),
                new byte[RapidOcrModelFiles.Recognizer.ExpectedBytes]);

            RapidOcrOcrAdapter adapter = new(new OcrModelPathValidator(), directory);
            OcrEnvironmentCheckResult withoutClassifier = await adapter.CheckEnvironmentAsync(
                Preset("""{"useCls":false}""", directory));
            OcrEnvironmentCheckResult withClassifier =
                await adapter.CheckEnvironmentAsync(Preset(modelPath: directory));

            withoutClassifier.IsReady.Should().BeTrue();
            withClassifier.IsReady.Should().BeFalse();
            withClassifier.Message.Should().Contain(RapidOcrModelFiles.Classifier.FileName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Adapter_refuses_to_run_when_the_model_files_are_missing()
    {
        string directory = CreateTemporaryDirectory();
        string imagePath = CreatePng(64, 64);
        try
        {
            RapidOcrOcrAdapter adapter = new(new OcrModelPathValidator(), directory);

            Result<OcrEnginePageResult> result = await adapter.RunPageAsync(
                ImageInput(imagePath, null), Preset(modelPath: directory));

            result.IsFailure.Should().BeTrue();
            result.ErrorMessage.Should().Contain("missing");
        }
        finally
        {
            File.Delete(imagePath);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Adapter_resolves_the_models_directory_from_parameters_before_the_preset()
    {
        RapidOcrOcrAdapter.ResolveModelsDirectory(RapidOcrParameters.Default with { ModelRootDir = "/a" }, "/b")
            .Should().Be("/a");
        RapidOcrOcrAdapter.ResolveModelsDirectory(RapidOcrParameters.Default, "/b").Should().Be("/b");
        RapidOcrOcrAdapter.ResolveModelsDirectory(RapidOcrParameters.Default, null).Should().BeNull();
    }

    private static float[] FilledProbabilityMap(int width, int height, int x0, int y0, int rectangleWidth,
        int rectangleHeight, float value)
    {
        float[] map = new float[width * height];
        FillRectangle(map, width, x0, y0, rectangleWidth, rectangleHeight, value);
        return map;
    }

    private static void FillRectangle(float[] map, int width, int x0, int y0, int rectangleWidth, int rectangleHeight,
        float value)
    {
        for (int y = y0; y <= y0 + rectangleHeight; y++)
        {
            for (int x = x0; x <= x0 + rectangleWidth; x++)
            {
                map[y * width + x] = value;
            }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"patchouli-rapidocr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static OcrPresetVersion Preset(string parametersJson = "{}", string? modelPath = "/models/rapidocr")
    {
        return new OcrPresetVersion(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.RapidOcr,
            OcrModelIds.RapidOcrDefault, modelPath, parametersJson, true, DateTimeOffset.UtcNow);
    }

    private static OcrInputDescriptor ImageInput(string imagePath, NormalizedBBox? region)
    {
        return new OcrInputDescriptor(PageId.New(), DocumentInstanceId.New(),
            region is null ? OcrInputKinds.ImageFile : OcrInputKinds.RegionImage, imagePath, null, region, "available",
            null);
    }

    private static string CreatePng(int width, int height)
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-rapidocr-test-{Guid.NewGuid():N}.png");
        using SKBitmap bitmap = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.White);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }
}
