using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Llm;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class LocalMultimodalLayoutTests : IDisposable
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Recognition_is_bounded_and_keeps_reading_order_when_calls_finish_out_of_order(int concurrency)
    {
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Runtime.CompilerServices.StrongBox<int> calls = new();
        StubRecognizer recognizer = new()
        {
            Recognize = async (input, token) =>
            {
                bool first = input.RegionBBox!.Value.X > .5;
                Interlocked.Increment(ref calls.Value);
                (first ? firstStarted : secondStarted).TrySetResult();
                await (first ? releaseFirst : releaseSecond).Task.WaitAsync(token);
                return Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(input.PageId, true,
                    first ? "first" : "second", input.RegionBBox, null, null));
            }
        };
        LayoutAwareMultimodalOcrAdapter adapter = new(recognizer, new StubDetector(),
            () => LlmAppSettings.Default() with { OcrConcurrency = concurrency }, _ => "managed.onnx");
        Task<Result<OcrEnginePageResult>> run = adapter.RunPageAsync(ImageInput(), Preset());
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (concurrency == 1)
            {
                Volatile.Read(ref calls.Value).Should().Be(1);
                releaseFirst.TrySetResult();
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Volatile.Read(ref calls.Value).Should().Be(2);
                releaseSecond.TrySetResult();
            }
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }

        Result<OcrEnginePageResult> result = await run;
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Text.Should().Be("first\nsecond");
        result.Value.TextBoxes!.Select(box => box.BBox.X).Should().Equal(.6, .1);
    }

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        $"patchouli-layout-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Local_layout_uses_llm_order_and_detector_geometry_for_every_region()
    {
        StubRecognizer recognizer = new();
        StubDetector detector = new();
        LayoutAwareMultimodalOcrAdapter
            adapter = new(recognizer, detector, () => LlmAppSettings.Default() with { OcrConcurrency = 1 },
                _ => "managed.onnx");
        OcrInputDescriptor input = ImageInput();

        Result<OcrEnginePageResult> result = await adapter.RunPageAsync(input, Preset());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        detector.Calls.Should().Be(1);
        recognizer.OrderCalls.Should().Be(1);
        recognizer.Inputs.Select(crop => crop.RegionBBox).Should()
            .Equal(detector.Regions[1].BBox, detector.Regions[0].BBox);
        result.Value.TextBoxes!.Select(box => box.BBox).Should()
            .Equal(detector.Regions[1].BBox, detector.Regions[0].BBox);
        result.Value.Text.Should().Be("text-1\ntext-2");
    }

    [Fact]
    public async Task User_drawn_region_bypasses_layout_and_weight_readiness_even_with_local_mode_enabled()
    {
        StubRecognizer recognizer = new();
        StubDetector detector = new() { Validation = Result.Failure("missing", "No weights") };
        LayoutAwareMultimodalOcrAdapter adapter = new(recognizer, detector, LlmAppSettings.Default,
            _ => throw new InvalidOperationException("Region OCR must not resolve layout weights"));
        OcrInputDescriptor input = ImageInput() with
        {
            InputKind = OcrInputKinds.RegionImage, RegionBBox = new NormalizedBBox(.2, .1, .5, .6)
        };

        (await adapter.ValidateRegionPresetAsync(Preset())).IsSuccess.Should().BeTrue();
        Result<OcrEnginePageResult> result = await adapter.RunPageAsync(input, Preset());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        detector.Calls.Should().Be(0);
        recognizer.OrderCalls.Should().Be(0);
        recognizer.EnvironmentChecks.Should().Be(1);
        recognizer.Inputs.Should().Equal(input);
        result.Value.Text.Should().Be("text-1");
    }

    [Fact]
    public async Task Direct_mode_preserves_online_boxes_without_touching_layout_or_ordering()
    {
        StubRecognizer recognizer = new();
        StubDetector detector = new() { Validation = Result.Failure("missing", "No weights") };
        LayoutAwareMultimodalOcrAdapter adapter = new(recognizer, detector,
            () => LlmAppSettings.Default() with { OcrUseLocalLayout = false }, _ => "managed.onnx");
        OcrInputDescriptor input = ImageInput();

        Result<OcrEnginePageResult> result = await adapter.RunPageAsync(input, Preset());

        result.IsSuccess.Should().BeTrue();
        detector.Calls.Should().Be(0);
        recognizer.OrderCalls.Should().Be(0);
        recognizer.Inputs.Should().Equal(input);
        result.Value.TextBoxes![0].BBox.Should().Be(new NormalizedBBox(.9, .9, .1, .1));
    }

    [Fact]
    public async Task Missing_weights_fail_before_any_online_request_and_never_fall_back_to_direct_mode()
    {
        StubRecognizer recognizer = new();
        StubDetector detector = new() { Validation = Result.Failure("missing", "Download weights manually") };
        LayoutAwareMultimodalOcrAdapter
            adapter = new(recognizer, detector, LlmAppSettings.Default, _ => "managed.onnx");

        (await adapter.CheckEnvironmentAsync(Preset())).IsReady.Should().BeFalse();
        Result<OcrEnginePageResult> result = await adapter.RunPageAsync(ImageInput(), Preset());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("missing");
        recognizer.Inputs.Should().BeEmpty();
        recognizer.OrderCalls.Should().Be(0);
        recognizer.EnvironmentChecks.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_reading_order_aborts_before_region_recognition()
    {
        StubRecognizer recognizer = new() { OrderReply = "{\"order\":[1,1]}" };
        LayoutAwareMultimodalOcrAdapter adapter = new(recognizer, new StubDetector(), LlmAppSettings.Default,
            _ => "managed.onnx");

        Result<OcrEnginePageResult> result = await adapter.RunPageAsync(ImageInput(), Preset());

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("阅读顺序");
        recognizer.Inputs.Should().BeEmpty();
    }

    [Fact]
    public async Task No_detected_regions_is_a_blank_page_without_a_fabricated_full_page_region()
    {
        StubRecognizer recognizer = new();
        StubDetector detector = new() { Regions = [] };
        LayoutAwareMultimodalOcrAdapter
            adapter = new(recognizer, detector, LlmAppSettings.Default, _ => "managed.onnx");

        Result<OcrEnginePageResult> result = await adapter.RunPageAsync(ImageInput(), Preset());

        result.IsSuccess.Should().BeTrue();
        result.Value.TextBoxes.Should().BeEmpty();
        recognizer.OrderCalls.Should().Be(0);
        recognizer.Inputs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"order\":[0,0]}")]
    [InlineData("{\"order\":[0]}")]
    [InlineData("{\"order\":[0,2]}")]
    [InlineData("{\"order\":[-1,0]}")]
    [InlineData("{\"order\":[0,1.5]}")]
    [InlineData("{\"order\":[0,\"1\"]}")]
    [InlineData("{\"order\":null}")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Reading_order_must_be_a_complete_permutation(string content)
    {
        MultimodalLayoutReadingOrder.Parse(content, 2).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Fenced_reading_order_is_supported_without_accepting_invalid_ids()
    {
        MultimodalLayoutReadingOrder.Parse("```json\n{\"order\":[1,0]}\n```", 2).Value.Should().Equal(1, 0);
    }

    [Fact]
    public void Yolo_decode_reverses_letterboxing_and_removes_duplicate_regions()
    {
        float[] output = [320, 128, 704, 384, .9f, 1, 320, 128, 704, 384, .7f, 1];
        IReadOnlyList<DocumentLayoutRegion> regions =
            OnnxDocumentLayoutDetector.DecodeBoxes(output, true, 200, 100, 5.12, 0, 256);
        // The first box is clipped against the original image's top after removing padding.
        regions.Should().ContainSingle();
        regions[0].BBox.X.Should().BeApproximately(.3125, 1e-6);
        regions[0].BBox.Y.Should().Be(0);
        regions[0].BBox.Height.Should().BeApproximately(.25, 1e-6);
    }

    [Fact]
    public void Paddle_decode_uses_original_pixel_coordinates_and_the_exported_reading_order()
    {
        float[] output = [22, .9f, 100, 20, 180, 70, 10, 17, .8f, 10, 5, 90, 15, 2];
        IReadOnlyList<DocumentLayoutRegion> regions =
            OnnxDocumentLayoutDetector.DecodeBoxes(output, false, 200, 100, 1, 0, 0);
        regions.Select(region => region.Order).Should().Equal(2, 10);
        regions[1].BBox.X.Should().BeApproximately(.5, 1e-6);
        regions[1].BBox.Y.Should().BeApproximately(.2, 1e-6);
        regions[1].BBox.Width.Should().BeApproximately(.4, 1e-6);
        regions[1].BBox.Height.Should().BeApproximately(.5, 1e-6);
    }

    [Fact]
    public void Model_validation_requires_a_user_supplied_onnx_file()
    {
        using OnnxDocumentLayoutDetector detector = new();
        detector.ValidateModel(OcrLayoutModelIds.PpDocLayoutV3, "").IsFailure.Should().BeTrue();
        detector.ValidateModel(OcrLayoutModelIds.DocLayoutYolo, Path.Combine(_root, "missing.onnx"))
            .IsFailure.Should().BeTrue();
    }

    private OcrInputDescriptor ImageInput()
    {
        string path = Path.Combine(_root, "page.png");
        using SKBitmap bitmap = new(200, 100);
        bitmap.Erase(SKColors.White);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return new OcrInputDescriptor(PageId.New(), DocumentInstanceId.New(), OcrInputKinds.PageImage,
            path, null, null, "available", null);
    }

    private static OcrPresetVersion Preset()
    {
        return new OcrPresetVersion(OcrPresetVersionId.New(), OcrPresetId.New(), OcrEngineIds.MultimodalLlm,
            "vision", null, "{}", false, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private sealed class StubDetector : IDocumentLayoutDetector
    {
        public int Calls { get; private set; }
        public (int Width, int Height) ImageSize { get; private set; }
        public Result Validation { get; init; } = Result.Success();

        public IReadOnlyList<DocumentLayoutRegion> Regions { get; init; } =
        [
            new("text", new NormalizedBBox(.1, .1, .3, .2), .95),
            new("text", new NormalizedBBox(.6, .1, .3, .2), .9)
        ];

        public Result ValidateModel(string modelId, string modelPath)
        {
            return Validation;
        }

        public Task<Result<IReadOnlyList<DocumentLayoutRegion>>> DetectAsync(byte[] imageBytes, string modelId,
            string modelPath, CancellationToken cancellationToken = default)
        {
            Calls++;
            using SKBitmap image = SKBitmap.Decode(imageBytes) ??
                                   throw new InvalidOperationException("Invalid test image");
            ImageSize = (image.Width, image.Height);
            return Task.FromResult(Result<IReadOnlyList<DocumentLayoutRegion>>.Success(Regions));
        }
    }

    private sealed class StubRecognizer : IRealOcrAdapter, IMultimodalLayoutOrderResolver
    {
        public string EngineId => OcrEngineIds.MultimodalLlm;
        public string DisplayName => "Test vision";
        public string Kind => OcrAdapterKind.CloudApi;
        public List<OcrInputDescriptor> Inputs { get; } = [];
        public int OrderCalls { get; private set; }
        public int EnvironmentChecks { get; private set; }
        public (int Width, int Height) OrderImageSize { get; private set; }
        public string OrderReply { get; init; } = "{\"order\":[1,0]}";

        public OcrEngineCapability GetCapability()
        {
            return new MultimodalLlmOcrAdapter().GetCapability();
        }

        public Task<OcrEnvironmentCheckResult> CheckEnvironmentAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            EnvironmentChecks++;
            return Task.FromResult(new OcrEnvironmentCheckResult(EngineId, presetVersion.ModelId, null,
                OcrEnvironmentStatus.Ready, true, "Ready", OcrRequiredAction.None, []));
        }

        public Task<Result> ValidatePresetAsync(OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ValidateInputAsync(OcrInputDescriptor input,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Func<OcrInputDescriptor, CancellationToken, Task<Result<OcrEnginePageResult>>>? Recognize { get; init; }

        public Task<Result<OcrEnginePageResult>> RunPageAsync(OcrInputDescriptor input,
            OcrPresetVersion presetVersion, CancellationToken cancellationToken = default)
        {
            if (Recognize is not null)
            {
                return Recognize(input, cancellationToken);
            }

            Inputs.Add(input);
            string text = $"text-{Inputs.Count}";
            return Task.FromResult(Result<OcrEnginePageResult>.Success(new OcrEnginePageResult(input.PageId,
                true, text, null, null, null,
                TextBoxes: [new OcrEngineTextBox(text, new NormalizedBBox(.9, .9, .1, .1))])));
        }

        public Task<Result<IReadOnlyList<int>>> ResolveReadingOrderAsync(byte[] imageBytes,
            IReadOnlyList<DocumentLayoutRegion> regions, OcrPresetVersion presetVersion,
            CancellationToken cancellationToken = default)
        {
            OrderCalls++;
            using SKBitmap bitmap = SKBitmap.Decode(imageBytes) ??
                                    throw new InvalidOperationException("Invalid test image");
            OrderImageSize = (bitmap.Width, bitmap.Height);
            return Task.FromResult(MultimodalLayoutReadingOrder.Parse(OrderReply, regions.Count));
        }
    }
}
