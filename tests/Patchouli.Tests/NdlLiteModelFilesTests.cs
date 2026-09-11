using FluentAssertions;
using Patchouli.Infrastructure.Ocr.NdlLite;

namespace Patchouli.Tests;

public sealed class NdlLiteModelFilesTests
{
    /// <summary>
    /// Hard-coded byte lengths and SHA256 values verified by downloading the six
    /// files from the pinned commit. Literals (not values recomputed from the
    /// manifest) so a wrong size, re-pointed tag or LFS pointer is detectable.
    /// </summary>
    private static readonly Dictionary<string, (long Bytes, string Sha256)> ExpectedManifest = new()
    {
        ["model/deim-s-1024x1024.onnx"] = (
            40_256_763L, "c156ce0c4e704bc3bf7e4016d0a87b949cffa8b3724f4b4cc696b8284c3c7373"),
        ["model/parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx"] = (
            36_457_393L, "9e651bae4c1a4d5254da1127e86e82e21ef62d5339b37e62d4a3d3d30831772d"),
        ["model/parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx"] = (
            37_808_553L, "49cea9db4552f19eb05c8ee202fcf74714977749b2f4c9376b127fde41b07a99"),
        ["model/parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx"] = (
            42_588_187L, "06462b0dbd5b0b8508545c8c3d485cf20dbf4ffa652fe145e69c9e7457080602"),
        ["config/ndl.yaml"] = (
            299L, "0c2a6a184dd322375b76f2ce3842f8ac555d53edad0ab63655c013f4c471c5a0"),
        ["config/NDLmoji.yaml"] = (
            42_434L, "f6ad5a2de444b495155866af811cf1a98309dcae3225db802767ea531a2dc529")
    };

    [Fact]
    public void Manifest_pins_the_immutable_ndlocr_lite_commit_and_host()
    {
        NdlLiteModelFiles.UpstreamCommit.Should().Be("6feccb29e33c2467ea48894e66502a7788ae2b2d");
        NdlLiteModelFiles.UpstreamRelease.Should().Be("1.3.1");
        NdlLiteModelFiles.BaseUrl.Should()
            .Be("https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src");
    }

    [Fact]
    public void Manifest_exposes_detector_three_recognizers_and_two_configs()
    {
        NdlLiteModelFiles.Files.Should().HaveCount(6);
        NdlLiteModelFiles.Files.Select(static file => file.RelativePath).Should().OnlyHaveUniqueItems();
        NdlLiteModelFiles.Files.Should().OnlyContain(static file => file.ExpectedBytes > 0);

        NdlLiteModelFiles.Files.Select(static file => file.RelativePath).Should().Contain(
            "model/deim-s-1024x1024.onnx",
            "model/parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx",
            "model/parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx",
            "model/parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx",
            "config/ndl.yaml",
            "config/NDLmoji.yaml");
    }

    [Fact]
    public void Manifest_pins_the_exact_upstream_byte_sizes_and_sha256()
    {
        NdlLiteModelFiles.Files.Should().HaveCount(ExpectedManifest.Count);

        foreach (NdlLiteModelFileEntry entry in NdlLiteModelFiles.Files)
        {
            ExpectedManifest.Should().ContainKey(entry.RelativePath);
            (long bytes, string sha256) = ExpectedManifest[entry.RelativePath];
            entry.ExpectedBytes.Should().Be(bytes, "byte size for {0}", entry.RelativePath);
            entry.Sha256.Should().Be(sha256, "SHA256 for {0}", entry.RelativePath);
            entry.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
        }
    }

    [Fact]
    public void Manifest_is_not_the_ndl_koten_manifest()
    {
        // NDL Koten uses rtmdet-s-1280x1280 and a single 32x384 PARSeq model; a
        // relabelled Koten pipeline must never satisfy this manifest.
        NdlLiteModelFiles.DetectorRelativePath.Should().Be("model/deim-s-1024x1024.onnx");
        NdlLiteModelFiles.Recognizer30RelativePath.Should().NotContain("32x384");
        NdlLiteModelFiles.Recognizer30RelativePath.Should().Contain("24x256");
        NdlLiteModelFiles.GetDetectorPath("/models").Should().NotContain("rtmdet");
    }

    [Fact]
    public void Download_urls_are_built_from_the_pinned_commit()
    {
        foreach (NdlLiteModelFileEntry entry in NdlLiteModelFiles.Files)
        {
            entry.DownloadUrl.Should().StartWith(
                "https://raw.githubusercontent.com/ndl-lab/ndlocr-lite/6feccb29e33c2467ea48894e66502a7788ae2b2d/src/");
            entry.DownloadUrl.Should().EndWith(entry.RelativePath);
            entry.DownloadUrl.Should().NotContain("/1.3.1/");
        }
    }

    [Fact]
    public void IsComplete_is_false_for_missing_or_truncated_files_and_true_when_sizes_match()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            NdlLiteModelFiles.IsComplete(modelsDirectory).Should().BeFalse();

            foreach (NdlLiteModelFileEntry entry in NdlLiteModelFiles.Files)
            {
                string path = NdlLiteModelFiles.GetLocalPath(modelsDirectory, entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[entry.ExpectedBytes]);
            }

            NdlLiteModelFiles.IsComplete(modelsDirectory).Should().BeTrue();
            NdlLiteModelFiles.GetInstalledByteCount(modelsDirectory)
                .Should().Be(NdlLiteModelFiles.Files.Sum(static file => file.ExpectedBytes));

            NdlLiteModelFileEntry first = NdlLiteModelFiles.Files[0];
            File.WriteAllBytes(NdlLiteModelFiles.GetLocalPath(modelsDirectory, first), [1, 2, 3]);
            NdlLiteModelFiles.IsComplete(modelsDirectory).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public void GetMissing_reports_absent_and_size_mismatched_entries()
    {
        string modelsDirectory = CreateModelsDirectory();
        try
        {
            NdlLiteModelFiles.GetMissing(modelsDirectory).Should().HaveCount(NdlLiteModelFiles.Files.Count);

            NdlLiteModelFileEntry first = NdlLiteModelFiles.Files[0];
            string firstPath = NdlLiteModelFiles.GetLocalPath(modelsDirectory, first);
            Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
            File.WriteAllBytes(firstPath, new byte[first.ExpectedBytes]);

            IReadOnlyList<NdlLiteModelFileEntry> missing = NdlLiteModelFiles.GetMissing(modelsDirectory);
            missing.Should().HaveCount(NdlLiteModelFiles.Files.Count - 1);
            missing.Should().NotContain(first);

            File.WriteAllBytes(firstPath, new byte[first.ExpectedBytes - 1]);
            NdlLiteModelFiles.GetMissing(modelsDirectory).Should().Contain(first);
        }
        finally
        {
            Directory.Delete(modelsDirectory, true);
        }
    }

    [Fact]
    public void Local_paths_follow_upstream_layout()
    {
        string modelsDirectory = Path.Combine("root", "ndlocr-lite");

        NdlLiteModelFiles.GetDetectorPath(modelsDirectory)
            .Should().Be(Path.Combine(modelsDirectory, "model", "deim-s-1024x1024.onnx"));
        NdlLiteModelFiles.GetClassesPath(modelsDirectory)
            .Should().Be(Path.Combine(modelsDirectory, "config", "ndl.yaml"));
        NdlLiteModelFiles.GetCharsetPath(modelsDirectory)
            .Should().Be(Path.Combine(modelsDirectory, "config", "NDLmoji.yaml"));
    }

    [Fact]
    public void Convenience_paths_stay_in_sync_with_the_manifest()
    {
        string modelsDirectory = Path.Combine("root", "ndlocr-lite");

        NdlLiteModelFiles.GetDetectorPath(modelsDirectory)
            .Should().Be(GetManifestPath(modelsDirectory, NdlLiteModelFiles.DetectorRelativePath));
        NdlLiteModelFiles.GetRecognizer30Path(modelsDirectory)
            .Should().Be(GetManifestPath(modelsDirectory, NdlLiteModelFiles.Recognizer30RelativePath));
        NdlLiteModelFiles.GetRecognizer50Path(modelsDirectory)
            .Should().Be(GetManifestPath(modelsDirectory, NdlLiteModelFiles.Recognizer50RelativePath));
        NdlLiteModelFiles.GetRecognizer100Path(modelsDirectory)
            .Should().Be(GetManifestPath(modelsDirectory, NdlLiteModelFiles.Recognizer100RelativePath));
        NdlLiteModelFiles.GetClassesPath(modelsDirectory)
            .Should().Be(GetManifestPath(modelsDirectory, NdlLiteModelFiles.ClassesRelativePath));
        NdlLiteModelFiles.GetCharsetPath(modelsDirectory)
            .Should().Be(GetManifestPath(modelsDirectory, NdlLiteModelFiles.CharsetRelativePath));
    }

    [Fact]
    public void Attribution_identifies_the_upstream_project_and_license()
    {
        NdlLiteModelFiles.LicenseName.Should().Be("Creative Commons Attribution 4.0 International (CC-BY-4.0)");
        NdlLiteModelFiles.Attribution.Should().Contain("ndl-lab/ndlocr-lite");
        NdlLiteModelFiles.Attribution.Should().Contain(NdlLiteModelFiles.UpstreamCommit);
    }

    private static string GetManifestPath(string modelsDirectory, string relativePath)
    {
        NdlLiteModelFileEntry entry = NdlLiteModelFiles.Files
            .Single(file => file.RelativePath == relativePath);
        return NdlLiteModelFiles.GetLocalPath(modelsDirectory, entry);
    }

    private static string CreateModelsDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"patchouli-ndlocr-lite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
