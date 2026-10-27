using System.Security.Cryptography;
using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Ocr;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class OcrManagedModelObservationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        $"ocr-model-observation-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    [Fact]
    public async Task Published_digest_is_never_reported_as_observed_for_an_absent_file()
    {
        ConfigFieldDescriptor field = DirectoryField(new ExtensionManagedFile("model.onnx",
            "https://example.test/model.onnx", 4, Hash([1, 2, 3, 4])));

        OcrModelObservation observed = await OcrModelObserver.ObserveAsync(Definition(), [field],
            Config(), OcrRunExecutionScope.Pages, CancellationToken.None);

        observed.IsComplete.Should().BeFalse();
        observed.Digest.Should().BeNull();
        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty();
    }

    [Fact]
    public async Task Same_size_and_timestamp_change_still_changes_real_content_identity()
    {
        string path = Path.Combine(_root, "model.onnx");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        ConfigFieldDescriptor field = DirectoryField(new ExtensionManagedFile("model.onnx",
            "https://example.test/model.onnx", 4));
        OcrModelObservation before = await OcrModelObserver.ObserveAsync(Definition(), [field], Config(),
            OcrRunExecutionScope.Pages, CancellationToken.None);
        await File.WriteAllBytesAsync(path, [4, 3, 2, 1]);
        File.SetLastWriteTimeUtc(path, timestamp);

        OcrModelObservation after = await OcrModelObserver.ObserveAsync(Definition(), [field], Config(),
            OcrRunExecutionScope.Pages, CancellationToken.None);

        before.IsComplete.Should().BeTrue();
        after.IsComplete.Should().BeTrue();
        after.AssetSet.Should().Be(before.AssetSet);
        after.Digest.Should().NotBe(before.Digest);
    }

    [Fact]
    public async Task Pinned_manifest_mismatch_is_not_a_valid_observation()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "model.onnx"), [4, 3, 2, 1]);
        ConfigFieldDescriptor field = DirectoryField(new ExtensionManagedFile("model.onnx",
            "https://example.test/model.onnx", 4, Hash([1, 2, 3, 4])));

        OcrModelObservation observed = await OcrModelObserver.ObserveAsync(Definition(), [field], Config(),
            OcrRunExecutionScope.Pages, CancellationToken.None);

        observed.IsComplete.Should().BeFalse();
        observed.Digest.Should().BeNull();
    }

    [Fact]
    public async Task Explicit_crop_has_no_observation_dependency_on_unused_full_page_layout()
    {
        ConfigFieldDescriptor field = new("model-path", "Layout model", ConfigValueKind.FilePath, false, "")
        {
            ManagedFiles =
            [
                new ExtensionManagedFile("layout.onnx", "https://example.test/layout.onnx",
                    RequiredScopes: [OcrRunExecutionScope.Document, OcrRunExecutionScope.Pages])
            ]
        };
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
            { ["model-path"] = Path.Combine(_root, "not-downloaded-layout.onnx") }, 1);

        OcrModelObservation crop = await OcrModelObserver.ObserveAsync(Definition(), [field], config,
            OcrRunExecutionScope.Region, CancellationToken.None);
        OcrModelObservation page = await OcrModelObserver.ObserveAsync(Definition(), [field], config,
            OcrRunExecutionScope.Pages, CancellationToken.None);

        crop.Should().Be(OcrModelObservation.Empty);
        page.IsComplete.Should().BeFalse();
        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_optional_model_is_not_part_of_the_required_asset_set()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "det.onnx"), [1, 2, 3, 4]);
        ConfigFieldDescriptor models = DirectoryField(
            new ExtensionManagedFile("det.onnx", "https://example.test/det.onnx", 4),
            new ExtensionManagedFile("cls.onnx", "https://example.test/cls.onnx", 4,
                RequiredWhenKey: "use-cls", RequiredWhenValue: "true"));
        ConfigFieldDescriptor classifier = new("use-cls", "Classifier", ConfigValueKind.Boolean, false, "", "true");
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
            { ["model-path"] = _root, ["use-cls"] = "false" }, 1);

        OcrModelObservation observed = await OcrModelObserver.ObserveAsync(Definition(), [models, classifier], config,
            OcrRunExecutionScope.Pages, CancellationToken.None);

        observed.IsComplete.Should().BeTrue();
        observed.Digest.Should().StartWith("sha256:");
    }

    [Fact]
    public async Task Host_does_not_guess_or_walk_an_undeclared_private_model_directory()
    {
        ExtensionConfigSnapshot config = new(new Dictionary<string, string>
            { ["model-path"] = Path.Combine(_root, "private-unmanaged-directory") }, 1);

        OcrModelObservation observed = await OcrModelObserver.ObserveAsync(Definition(), [], config,
            OcrRunExecutionScope.Pages, CancellationToken.None);

        observed.Should().Be(OcrModelObservation.Empty);
        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty();
    }

    private ExtensionConfigSnapshot Config()
    {
        return new ExtensionConfigSnapshot(new Dictionary<string, string> { ["model-path"] = _root }, 1);
    }

    private static ConfigFieldDescriptor DirectoryField(params ExtensionManagedFile[] files)
    {
        return new ConfigFieldDescriptor("model-path", "Model directory", ConfigValueKind.DirectoryPath, false, "")
            { ManagedFiles = files };
    }

    private static OcrEngineDefinition Definition()
    {
        return new OcrEngineDefinition("test-models", "Test models", OcrAdapterKind.LocalLibrary,
            "fixture", [OcrInputKinds.PageImage]);
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
