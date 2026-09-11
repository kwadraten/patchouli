using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.NdlLite;
using Patchouli.Ocr;
using SkiaSharp;

namespace Patchouli.Tests;

public sealed class NdlLiteOcrAdapterTests
{
    [Fact]
    public void Adapter_exposes_the_official_ndlocr_lite_engine_identity()
    {
        NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

        adapter.EngineId.Should().Be(OcrEngineIds.NdlLite);
        adapter.EngineId.Should().Be("ndlocr-lite");
        adapter.EngineId.Should().NotBe(OcrEngineIds.NdlKoten);
        adapter.DisplayName.Should().Be("NDLOCR-Lite");
        adapter.Kind.Should().Be(OcrAdapterKind.LocalLibrary);
    }

    [Fact]
    public void Capability_declares_a_local_native_engine_without_credentials()
    {
        NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

        OcrEngineCapability capability = adapter.GetCapability();

        capability.EngineId.Should().Be("ndlocr-lite");
        capability.SupportsLocalModel.Should().BeTrue();
        capability.SupportsRemoteEndpoint.Should().BeFalse();
        capability.RequiresCredential.Should().BeFalse();
        capability.RequiresModelPath.Should().BeTrue();
        capability.SupportsRegionOcr.Should().BeTrue();
        capability.SupportedInputKinds.Should().BeEquivalentTo(
            [OcrInputKinds.PageImage, OcrInputKinds.ImageFile, OcrInputKinds.RegionImage]);
        capability.Notes.Should().Contain("ndl-lab/ndlocr-lite");
    }

    [Fact]
    public async Task CheckEnvironmentAsync_reports_a_missing_model_directory_as_a_rebind_action()
    {
        NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

        OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset(null));

        result.EngineId.Should().Be(OcrEngineIds.NdlLite);
        result.ModelId.Should().Be("ndlocr-lite-default");
        result.Status.Should().Be(OcrEnvironmentStatus.MissingModelPath);
        result.IsReady.Should().BeFalse();
        result.RequiredAction.Should().Be(OcrRequiredAction.RebindModelPath);
    }

    [Fact]
    public async Task CheckEnvironmentAsync_reports_incomplete_model_files_before_the_run_fails()
    {
        string modelsDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-ndlocr-lite-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelsDirectory);
        try
        {
            NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

            OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset(modelsDirectory));

            result.EngineId.Should().Be(OcrEngineIds.NdlLite);
            result.IsReady.Should().BeFalse();
            result.Status.Should().Be(OcrEnvironmentStatus.MissingModelPath);
            result.RequiredAction.Should().Be(OcrRequiredAction.RebindModelPath);
            result.Message.Should().Contain("deim-s-1024x1024.onnx");
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public async Task CheckEnvironmentAsync_reports_ready_when_every_manifest_file_is_present()
    {
        string modelsDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-ndlocr-lite-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelsDirectory);
        try
        {
            foreach (NdlLiteModelFileEntry entry in NdlLiteModelFiles.Files)
            {
                string path = NdlLiteModelFiles.GetLocalPath(modelsDirectory, entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, new byte[entry.ExpectedBytes]);
            }

            NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

            OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset(modelsDirectory));

            result.IsReady.Should().BeTrue();
            result.Status.Should().Be(OcrEnvironmentStatus.Ready);
            result.EngineId.Should().Be(OcrEngineIds.NdlLite);
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public async Task CheckEnvironmentAsync_propagates_a_failed_model_path_check_with_engine_identity()
    {
        NdlLiteOcrAdapter adapter = new(new FailingModelPathValidator());

        OcrEnvironmentCheckResult result = await adapter.CheckEnvironmentAsync(Preset("/definitely/missing"));

        result.IsReady.Should().BeFalse();
        result.Status.Should().Be(OcrEnvironmentStatus.ModelPathInaccessible);
        result.EngineId.Should().Be(OcrEngineIds.NdlLite);
        result.ModelId.Should().Be("ndlocr-lite-default");
    }

    [Fact]
    public async Task ValidatePresetAsync_requires_a_model_directory()
    {
        NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

        (await adapter.ValidatePresetAsync(Preset(null))).IsFailure.Should().BeTrue();
        (await adapter.ValidatePresetAsync(Preset("/models"))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateInputAsync_rejects_unsupported_input_kinds_and_missing_images()
    {
        NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

        OcrInputDescriptor unsupported = new(PageId.New(), DocumentInstanceId.New(), OcrInputKinds.PdfPage,
            "/tmp/a.pdf", "/tmp/a.pdf", null, "available", null);
        (await adapter.ValidateInputAsync(unsupported)).IsFailure.Should().BeTrue();

        OcrInputDescriptor missingImage = Input("/definitely/missing.png");
        (await adapter.ValidateInputAsync(missingImage)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task RunPageAsync_reports_a_typed_failure_for_a_corrupt_same_size_model()
    {
        // A corrupt .onnx whose byte length matches the manifest passes the size-only
        // readiness check; the adapter must turn the ONNX failure into a typed
        // InvalidState result instead of letting the queue report worker_crashed.
        string modelsDirectory =
            Path.Combine(Path.GetTempPath(), $"patchouli-ndlocr-lite-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelsDirectory);
        string imagePath = CreatePng();
        try
        {
            foreach (NdlLiteModelFileEntry entry in NdlLiteModelFiles.Files)
            {
                string path = NdlLiteModelFiles.GetLocalPath(modelsDirectory, entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using FileStream stream = File.Create(path);
                stream.SetLength(entry.ExpectedBytes);
            }

            NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());

            Result<OcrEnginePageResult> result = await adapter.RunPageAsync(Input(imagePath), Preset(modelsDirectory));

            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
            result.ErrorMessage.Should().Contain("NDLOCR-Lite");
            result.ErrorMessage.Should().MatchRegex(
                "inference failed|could not run its models|could not read its model files");
            result.ErrorMessage.Should().NotContain("worker_crashed");
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task RunPageAsync_fails_when_the_native_model_files_are_missing()
    {
        string modelsDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-ndlocr-lite-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(modelsDirectory);
        string imagePath = CreatePng();
        try
        {
            NdlLiteOcrAdapter adapter = new(new FakeModelPathValidator());
            OcrInputDescriptor input = Input(imagePath);

            Result<OcrEnginePageResult> result = await adapter.RunPageAsync(input, Preset(modelsDirectory));

            result.IsFailure.Should().BeTrue();
            result.ErrorMessage.Should().Contain("model files are missing");
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
            File.Delete(imagePath);
        }
    }

    private static string CreatePng()
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-ndlocr-lite-{Guid.NewGuid():N}.png");
        using SKBitmap bitmap = new(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.White);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static OcrPresetVersion Preset(string? modelPath)
    {
        return new OcrPresetVersion(OcrPresetVersionId.New(), OcrPresetId.New(), "ndlocr-lite",
            "ndlocr-lite-default", modelPath, "{}", true, DateTimeOffset.UtcNow);
    }

    private static OcrInputDescriptor Input(string imagePath)
    {
        return new OcrInputDescriptor(PageId.New(), DocumentInstanceId.New(), OcrInputKinds.ImageFile, imagePath,
            null, null, "available", null);
    }

    private sealed class FakeModelPathValidator : IOcrModelPathValidator
    {
        public Task<OcrEnvironmentCheckResult> ValidateModelPathAsync(string? modelPath, bool required,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrEnvironmentCheckResult("ndlocr-lite", "ndlocr-lite-default", modelPath,
                OcrEnvironmentStatus.Ready, true, "ready", OcrRequiredAction.None, []));
        }
    }

    private sealed class FailingModelPathValidator : IOcrModelPathValidator
    {
        public Task<OcrEnvironmentCheckResult> ValidateModelPathAsync(string? modelPath, bool required,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrEnvironmentCheckResult("model-path-validator", string.Empty, modelPath,
                OcrEnvironmentStatus.ModelPathInaccessible, false, "inaccessible", OcrRequiredAction.RebindModelPath,
                []));
        }
    }
}
