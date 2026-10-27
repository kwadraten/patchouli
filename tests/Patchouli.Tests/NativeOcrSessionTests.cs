using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Ocr.NdlKoten;
using Patchouli.Infrastructure.Ocr.NdlLite;
using Patchouli.Infrastructure.Ocr.RapidOcr;
using Patchouli.Ocr.Engines;
using SkiaSharp;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Native OCR session facade coverage: declarative metadata, the non-executing readiness probe,
/// configuration resolution (explicit extension keys over legacy flat scalars over
/// parameters-json over upstream defaults), in-memory image decode with EXIF orientation, the
/// raster→region coordinate mapping, lazy pipeline creation and deterministic disposal. No real
/// model weights are required: every test stops before ONNX inference, and quality claims belong
/// to the real-model gates.
/// </summary>
public sealed class NativeOcrSessionTests
{
    private static ExtensionConfigSnapshot Config(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach ((string key, string value) in pairs)
        {
            values[key] = value;
        }

        return new ExtensionConfigSnapshot(values, 7);
    }

    private static (OcrJobPage Page, OcrJobInput Input) Issue(NormalizedBBox region)
    {
        OcrJobInput input = new(new OcrInputId("input-1"), region, "image/png");
        ExtensionPageIdentity pageId = new(DocumentInstanceId.New(), PageId.New(),
            DocumentTreeRevisionId.New(), 0);
        return (new OcrJobPage(pageId, [input]), input);
    }

    private static byte[] PngBytes(int width = 8, int height = 6)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.White);
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static string SparseModelDir(IReadOnlyList<(string RelativePath, long Bytes)> files)
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "patchouli-ocr-models-" + Guid.NewGuid().ToString("N"));
        foreach ((string relative, long bytes) in files)
        {
            string path = Path.Combine(directory,
                relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(bytes);
            }
        }

        return directory;
    }

    private static string SparseRapidModelDir(bool includeClassifier)
    {
        return SparseModelDir(RapidOcrModelFiles.GetRequiredFiles(includeClassifier)
            .Select(file => (file.FileName, file.ExpectedBytes)).ToArray());
    }

    // ── Declarative metadata ────────────────────────────────────────────────

    [Theory]
    [InlineData(NativeOcrSession.RapidOcrEngineId, "rapidocr-default")]
    [InlineData(NativeOcrSession.NdlOcrLiteEngineId, "ndlocr-lite-default")]
    [InlineData(NativeOcrSession.NdlKotenLiteEngineId, "ndl-koten-default")]
    public void GetDefinition_returns_metadata_without_a_session(string engineId, string modelId)
    {
        OcrEngineDefinition definition = NativeOcrSession.GetDefinition(engineId);

        definition.EngineId.Should().Be(engineId);
        definition.DisplayName.Should().NotBeNullOrWhiteSpace();
        definition.AdapterKind.Should().Be(Ocr.OcrAdapterKind.LocalLibrary);
        definition.DefaultModelId.Should().Be(modelId);
        definition.RequiresModelPath.Should().BeTrue();
        definition.RequiresCredential.Should().BeFalse();
        definition.SupportsVerticalText.Should().BeTrue();
        definition.SupportedInputKinds.Should().BeEquivalentTo(
        [
            NativeOcrInputKinds.PageImage, NativeOcrInputKinds.ImageFile,
            NativeOcrInputKinds.RegionImage
        ]);
        definition.ModelDirectoryKind.Should().NotBeNullOrWhiteSpace();
        definition.Notes.Should().NotBeNullOrWhiteSpace();
        // Typed engine options must NOT live in DefaultConfig: schema defaults copied into every
        // resolved snapshot would silently override a configured legacy parameters-json value.
        definition.DefaultConfig.Should().BeNull();
    }

    [Fact]
    public void GetDefinition_rejects_an_unknown_engine_id()
    {
        Action act = () => NativeOcrSession.GetDefinition("mineru");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Mirrored_constants_stay_equal_to_the_domain_contracts()
    {
        NativeOcrInputKinds.PageImage.Should().Be(Ocr.OcrInputKinds.PageImage);
        NativeOcrInputKinds.PdfPage.Should().Be(Ocr.OcrInputKinds.PdfPage);
        NativeOcrInputKinds.ImageFile.Should().Be(Ocr.OcrInputKinds.ImageFile);
        NativeOcrInputKinds.RegionImage.Should().Be(Ocr.OcrInputKinds.RegionImage);
        NativeOcrSession.RapidOcrEngineId.Should().Be(Ocr.OcrEngineIds.RapidOcr);
        NativeOcrSession.NdlOcrLiteEngineId.Should().Be(Ocr.OcrEngineIds.NdlLite);
        NativeOcrSession.NdlKotenLiteEngineId.Should().Be(Ocr.OcrEngineIds.NdlKoten);
    }

    // ── Readiness: resolves config and manifest, never a session ────────────

    [Fact]
    public void CheckReadiness_reports_a_missing_model_path_actionably()
    {
        Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("model-id", "rapidocr-default")));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        result.ErrorMessage.Should().Contain("model").And.Contain("Local Files");
    }

    [Fact]
    public void CheckReadiness_reports_a_missing_directory()
    {
        string missing = Path.Combine(Path.GetTempPath(), "patchouli-ocr-none-" + Guid.NewGuid().ToString("N"));
        Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.NdlOcrLiteEngineId,
            Config(("model-path", missing)));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        result.ErrorMessage.Should().Contain(missing);
    }

    [Fact]
    public void CheckReadiness_lists_missing_manifest_files()
    {
        string directory = Directory.CreateTempSubdirectory("patchouli-ocr-partial-").FullName;
        try
        {
            Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.NdlKotenLiteEngineId,
                Config(("model-path", directory)));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
            result.ErrorMessage.Should().Contain(
                Path.GetFileName(NdlKotenModelFiles.Files[0].RelativePath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CheckReadiness_succeeds_when_the_manifest_files_exist()
    {
        string directory = SparseModelDir(NdlKotenModelFiles.Files
            .Select(entry => (entry.RelativePath, entry.ExpectedBytes)).ToArray());
        try
        {
            NativeOcrSession.CheckReadiness(NativeOcrSession.NdlKotenLiteEngineId,
                Config(("model-path", directory))).IsSuccess.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CheckReadiness_rejects_an_unknown_engine_without_throwing()
    {
        Result result = NativeOcrSession.CheckReadiness("not-an-engine", Config());
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);
    }

    // ── Configuration resolution: legacy keys, explicit overrides, strictness ─

    [Fact]
    public void Legacy_flat_useCls_false_skips_the_optional_classifier()
    {
        // A migrated preset carries flat legacy scalars (useCls) and the re-serialized
        // parameters-json, never the typed kebab keys — the resolved config must honor it.
        string directory = SparseRapidModelDir(false);
        try
        {
            Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
                Config(("model-path", directory), ("useCls", "false"),
                    ("parameters-json", "{\"useCls\": false}")));
            result.IsSuccess.Should().BeTrue(
                "legacy useCls=false means the classifier is legitimately absent: " + result.ErrorMessage);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Legacy_parameters_json_alone_skips_the_optional_classifier()
    {
        string directory = SparseRapidModelDir(false);
        try
        {
            Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
                Config(("model-path", directory),
                    ("parameters-json", "{\"useCls\": false}")));
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void An_explicit_typed_key_overrides_the_legacy_parameters_json()
    {
        string directory = SparseRapidModelDir(false);
        try
        {
            // parameters-json says useCls=false but the explicit extension value says
            // use-cls=true: the classifier is required and its absence must fail readiness.
            Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
                Config(("model-path", directory), ("use-cls", "true"),
                    ("parameters-json", "{\"useCls\": false}")));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
            result.ErrorMessage.Should().Contain(RapidOcrModelFiles.ClassifierFileName);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Frozen_preset_model_path_wins_and_legacy_model_dirs_remain_fallbacks()
    {
        string presetPath = Path.Combine(Path.GetTempPath(), "patchouli-preset-" + Guid.NewGuid().ToString("N"));
        string legacyFlat = Path.Combine(Path.GetTempPath(), "patchouli-flat-" + Guid.NewGuid().ToString("N"));
        string legacyJson = Path.Combine(Path.GetTempPath(), "patchouli-json-" + Guid.NewGuid().ToString("N"));

        // Legacy advanced JSON cannot redirect a preset after its canonical model path was pinned.
        Result pinnedWins = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("model-path", presetPath),
                ("parameters-json", "{\"modelRootDir\": \"" + legacyJson.Replace('\\', '/') + "\"}")));
        pinnedWins.ErrorMessage.Should().Contain(presetPath);
        pinnedWins.ErrorMessage.Should().NotContain(legacyJson);
        Result jsonFallback = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("parameters-json", "{\"modelRootDir\": \"" + legacyJson.Replace('\\', '/') + "\"}")));
        jsonFallback.ErrorMessage!.Replace('\\', '/').Should().Contain(legacyJson.Replace('\\', '/'));

        // The pinned preset path still outranks a bare legacy modelPath member.
        Result presetWins = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("model-path", presetPath), ("modelPath", legacyFlat)));
        presetWins.ErrorMessage.Should().Contain(presetPath);
        presetWins.ErrorMessage.Should().NotContain(legacyFlat);

        // With no preset path the legacy flat scalar supplies the directory.
        Result flatUsed = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("modelPath", legacyFlat)));
        flatUsed.ErrorMessage.Should().Contain(legacyFlat);
    }

    [Fact]
    public void Two_presets_on_one_engine_resolve_independent_configs()
    {
        string dirA = Path.Combine(Path.GetTempPath(), "patchouli-a-" + Guid.NewGuid().ToString("N"));
        string dirB = Path.Combine(Path.GetTempPath(), "patchouli-b-" + Guid.NewGuid().ToString("N"));

        Result first = NativeOcrSession.CheckReadiness(NativeOcrSession.NdlOcrLiteEngineId,
            Config(("model-path", dirA)));
        Result second = NativeOcrSession.CheckReadiness(NativeOcrSession.NdlOcrLiteEngineId,
            Config(("model-path", dirB), ("parameters-json",
                "{\"modelPath\": \"" + dirA.Replace('\\', '/') + "\"}")));

        first.ErrorMessage.Should().Contain(dirA);
        // model-path outranks the legacy parameters-json modelPath for the second preset.
        second.ErrorMessage.Should().Contain(dirB);
        second.ErrorMessage.Should().NotContain(dirA);
    }

    [Theory]
    [InlineData("use-cls", "maybe")]
    [InlineData("use-det", "yes")]
    [InlineData("text-score", "high")]
    [InlineData("min-side-len", "12.5")]
    [InlineData("useCls", "not-a-bool")]
    public void Malformed_explicit_values_fail_instead_of_falling_back(string key, string value)
    {
        Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("model-path", Path.GetTempPath()), (key, value)));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain(value);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[\"useCls\", false]")]
    [InlineData("42")]
    public void Malformed_parameters_json_fails_instead_of_executing_defaults(string payload)
    {
        Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("model-path", Path.GetTempPath()), ("parameters-json", payload)));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("parameters-json");
    }

    [Fact]
    public void CheckReadiness_rejects_invalid_engine_parameters()
    {
        Result result = NativeOcrSession.CheckReadiness(NativeOcrSession.RapidOcrEngineId,
            Config(("model-path", Path.GetTempPath()), ("use-det", "false")));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("useDet");
    }

    // ── Session construction and lifetime ───────────────────────────────────

    [Fact]
    public void Construction_resolves_configuration_but_never_opens_a_model()
    {
        string absent = Path.Combine(Path.GetTempPath(), "patchouli-ocr-lazy-" + Guid.NewGuid().ToString("N"));
        using NativeOcrSession session = new(NativeOcrSession.NdlOcrLiteEngineId,
            Config(("model-path", absent)));
        // Construction succeeded without touching the (missing) model directory.
    }

    [Fact]
    public void Construction_rejects_invalid_parameters()
    {
        Action act = () => _ = new NativeOcrSession(NativeOcrSession.RapidOcrEngineId,
            Config(("model-path", Path.GetTempPath()), ("use-rec", "false")));
        act.Should().Throw<InvalidOperationException>().WithMessage("*useRec*");
    }

    [Fact]
    public void Construction_rejects_a_corrupt_parameters_json_envelope()
    {
        Action act = () => _ = new NativeOcrSession(NativeOcrSession.NdlKotenLiteEngineId,
            Config(("model-path", Path.GetTempPath()), ("parameters-json", "!!not-json!!")));
        act.Should().Throw<InvalidOperationException>().WithMessage("*parameters-json*");
    }

    [Fact]
    public void Dispose_is_idempotent_and_blocks_further_recognition()
    {
        NativeOcrSession session = new(NativeOcrSession.NdlKotenLiteEngineId,
            Config(("model-path", Path.GetTempPath())));
        session.Dispose();
        session.Dispose();

        (OcrJobPage page, OcrJobInput input) = Issue(new NormalizedBBox(0, 0, 1, 1));
        using OcrInputImage image = new(input.Id, "image/png", PngBytes());
        NativeOcrSession disposedSession = session;
        OcrInputImage capturedImage = image;
        Action act = () => disposedSession.Recognize(page, input, capturedImage, CancellationToken.None);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Recognize_rejects_undecodable_image_bytes_before_touching_models()
    {
        using NativeOcrSession session = new(NativeOcrSession.RapidOcrEngineId,
            Config(("model-path", Path.Combine(Path.GetTempPath(), "no-such-dir"))));
        (OcrJobPage page, OcrJobInput input) = Issue(new NormalizedBBox(0, 0, 1, 1));
        using OcrInputImage image = new(input.Id, "image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00 });

        NativeOcrSession capturedSession = session;
        OcrInputImage capturedImage = image;
        Action act = () => capturedSession.Recognize(page, input, capturedImage, CancellationToken.None);
        act.Should().Throw<InvalidOperationException>().WithMessage("*decode*");
    }

    [Fact]
    public void Recognize_fails_actionably_when_weights_are_missing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "patchouli-ocr-noweights-" + Guid.NewGuid().ToString("N"));
        using NativeOcrSession session = new(NativeOcrSession.NdlOcrLiteEngineId,
            Config(("model-path", missing)));
        (OcrJobPage page, OcrJobInput input) = Issue(new NormalizedBBox(0, 0, 1, 1));
        using OcrInputImage image = new(input.Id, "image/png", PngBytes());

        NativeOcrSession capturedSession = session;
        OcrInputImage capturedImage = image;
        Action act = () => capturedSession.Recognize(page, input, capturedImage, CancellationToken.None);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*" + missing + "*Local Files*");
    }

    // ── In-memory decode + EXIF orientation ─────────────────────────────────

    [Fact]
    public void DecodeOriented_reads_png_bytes_without_a_file()
    {
        using SKBitmap? decoded = NativeImageDecode.DecodeOriented(PngBytes(24, 18));
        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(24);
        decoded.Height.Should().Be(18);
    }

    [Fact]
    public void ApplyOrigin_rotates_90_degrees_clockwise()
    {
        using SKBitmap source = new(2, 3, SKColorType.Bgra8888, SKAlphaType.Opaque);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 0, SKColors.Green);
        source.SetPixel(0, 2, SKColors.Blue);
        source.SetPixel(1, 2, SKColors.Yellow);

        using SKBitmap rotated = NativeImageDecode.ApplyOrigin(source, SKEncodedOrigin.RightTop);
        rotated.Width.Should().Be(3);
        rotated.Height.Should().Be(2);
        rotated.GetPixel(0, 0).Should().Be(SKColors.Blue);
        rotated.GetPixel(2, 0).Should().Be(SKColors.Red);
        rotated.GetPixel(0, 1).Should().Be(SKColors.Yellow);
        rotated.GetPixel(2, 1).Should().Be(SKColors.Green);
    }

    [Fact]
    public void ApplyOrigin_rotates_180_degrees()
    {
        using SKBitmap source = new(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        source.SetPixel(0, 0, SKColors.Red);
        source.SetPixel(1, 1, SKColors.Green);

        using SKBitmap rotated = NativeImageDecode.ApplyOrigin(source, SKEncodedOrigin.BottomRight);
        rotated.GetPixel(1, 1).Should().Be(SKColors.Red);
        rotated.GetPixel(0, 0).Should().Be(SKColors.Green);
    }

    // ── Raster → region mapping (the input image already covers its region) ──

    [Fact]
    public void A_detection_spanning_the_supplied_raster_maps_to_the_whole_region()
    {
        // The issued image bytes already cover input.Region. A line covering the entire
        // 200x100 raster must land on exactly the region bounds — any re-crop by the region
        // (the original defect) would produce a different, shrunken mapping.
        NormalizedBBox region = new(0.5, 0.5, 0.5, 0.5);
        NormalizedBBox box = NativeOcrSession.RegionBox(region, 0, 0, 200, 100, 200, 100);

        box.X.Should().BeApproximately(0.5, 1e-9);
        box.Y.Should().BeApproximately(0.5, 1e-9);
        box.Width.Should().BeApproximately(0.5, 1e-9);
        box.Height.Should().BeApproximately(0.5, 1e-9);
        box.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_raster_relative_detection_maps_linearly_into_the_region()
    {
        // Raster pixel (10, 10) of a 200x100 supplied image is 5%/10% into the raster, so it
        // lands 5%/10% into the region — with the double-crop defect the same pixel would have
        // been treated as a crop-local coordinate and mapped deeper into the region.
        NormalizedBBox region = new(0.5, 0.5, 0.5, 0.5);
        NormalizedBBox box = NativeOcrSession.RegionBox(region, 10, 10, 20, 20, 200, 100);

        box.X.Should().BeApproximately(0.5 + 0.05 * 0.5, 1e-9);
        box.Y.Should().BeApproximately(0.5 + 0.10 * 0.5, 1e-9);
        box.Width.Should().BeApproximately(0.10 * 0.5, 1e-9);
        box.Height.Should().BeApproximately(0.20 * 0.5, 1e-9);
        box.Validate().IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_full_page_region_is_an_identity_mapping()
    {
        NormalizedBBox region = new(0, 0, 1, 1);
        NormalizedBBox box = NativeOcrSession.RegionBox(region, 40, 20, 60, 30, 800, 400);

        box.X.Should().BeApproximately(0.05, 1e-9);
        box.Y.Should().BeApproximately(0.05, 1e-9);
        box.Width.Should().BeApproximately(0.075, 1e-9);
        box.Height.Should().BeApproximately(0.075, 1e-9);
    }

    [Fact]
    public void ClampNormalized_keeps_out_of_range_boxes_valid()
    {
        NormalizedBBox box = NativeOcrSession.ClampNormalized(0.95, -0.2, 0.3, 0.0);
        box.X.Should().BeLessThan(1);
        box.Y.Should().Be(0);
        box.Width.Should().BeGreaterThan(0);
        box.Height.Should().BeGreaterThan(0);
        (box.X + box.Width).Should().BeLessThanOrEqualTo(1.0 + 1e-9);
        box.Validate().IsSuccess.Should().BeTrue();
    }
}
