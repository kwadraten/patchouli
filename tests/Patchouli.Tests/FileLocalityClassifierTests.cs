using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Tests;

public sealed class FileLocalityClassifierTests
{
    [Fact]
    public void OrderForImport_puts_local_before_cloud_ready_before_unready()
    {
        PdfCandidate[] input =
        [
            new(@"C:\cloud\b.pdf", "b.pdf", 1, null, null, "discovered", FileLocalityReadiness.CloudReady, true),
            new(@"C:\local\a.pdf", "a.pdf", 1, null, null, "discovered", FileLocalityReadiness.LocalReady, false),
            new(@"C:\cloud\c.pdf", "c.pdf", 1, null, null, "discovered", FileLocalityReadiness.CloudUnready, true),
            new(@"C:\local\z.pdf", "z.pdf", 1, null, null, "discovered", FileLocalityReadiness.LocalReady, false)
        ];

        string[] ordered = FileLocalityClassifier
            .OrderForImport(input, static c => c.Readiness, static c => c.FileName)
            .Select(c => c.FileName)
            .ToArray();

        ordered.Should().Equal("a.pdf", "z.pdf", "b.pdf", "c.pdf");
    }

    [Fact]
    public void Assess_local_temp_file_is_local_ready()
    {
        string path = Path.Combine(Path.GetTempPath(), $"local-ready-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46]); // %PDF
            FileLocalityAssessment assessment = FileLocalityClassifier.Assess(path);
            assessment.Readiness.Should().Be(FileLocalityReadiness.LocalReady);
            assessment.IsCloudPath.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Assess_does_not_use_path_names_to_decide_cloud()
    {
        // Ordinary local content under a folder named like a sync product stays local_ready.
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"OneDrive-fake-{Guid.NewGuid():N}", "WPSDrive")).FullName;
        string path = Path.Combine(root, "book.pdf");
        try
        {
            File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46]);
            FileLocalityAssessment assessment = FileLocalityClassifier.Assess(path);
            assessment.Readiness.Should().Be(FileLocalityReadiness.LocalReady);
            assessment.IsCloudPath.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(Directory.GetParent(root)!.FullName, true);
        }
    }

    [Fact]
    public void Import_priority_matches_local_then_cloud_ready_then_unready()
    {
        FileLocalityClassifier.ImportPriority(FileLocalityReadiness.LocalReady).Should()
            .BeLessThan(FileLocalityClassifier.ImportPriority(FileLocalityReadiness.CloudReady));
        FileLocalityClassifier.ImportPriority(FileLocalityReadiness.CloudReady).Should()
            .BeLessThan(FileLocalityClassifier.ImportPriority(FileLocalityReadiness.CloudUnready));
    }

    [Fact]
    public async Task ScanPdfAsync_marks_ordinary_files_local_ready_in_discovery_order_by_name_within_tier()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"loc-scan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(root, "b-local.pdf");
            TestFixtures.CopyRealThreePagePdfTo(root, "a-local.pdf");

            FileSearchRootAccess access = new();
            ResolvedFileSearchRoot resolved = new(root, root, root, FileSearchRootAuthorizationKinds.None);
            FileSearchRootScanResult scan = await access.ScanPdfAsync(resolved);

            scan.Candidates.Should().HaveCount(2);
            scan.Candidates.Should().OnlyContain(c => c.Readiness == FileLocalityReadiness.LocalReady);
            scan.Candidates.Should().OnlyContain(c => c.IsCloudPath == false);
            // Within the same readiness tier, sort is by file name.
            scan.Candidates.Select(c => c.FileName).Should().Equal("a-local.pdf", "b-local.pdf");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Scan_retains_cloud_placeholders_and_materializes_them_only_when_requested()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"loc-cloud-{Guid.NewGuid():N}"))
            .FullName;
        string localPath = TestFixtures.CopyRealThreePagePdfTo(root, "a-local.pdf");
        string cloudPath = TestFixtures.CopyRealThreePagePdfTo(root, "b-cloud.pdf");
        bool cloudHydrated = false;
        TrackingHydrationAdapter adapter = new(() => cloudHydrated = true);
        try
        {
            FileLocalityAssessment Assess(string path)
            {
                if (!string.Equals(path, cloudPath, StringComparison.OrdinalIgnoreCase))
                {
                    return new FileLocalityAssessment(FileLocalityReadiness.LocalReady, false);
                }

                return cloudHydrated
                    ? new FileLocalityAssessment(FileLocalityReadiness.CloudReady, true)
                    : new FileLocalityAssessment(
                        FileLocalityReadiness.CloudUnready,
                        true,
                        FileLocalityCodes.CloudNotDownloaded,
                        "Test cloud placeholder.");
            }

            FileSearchRootAccess access = new(adapter, localityClassifier: Assess);
            ResolvedFileSearchRoot resolved = new(root, root, root, FileSearchRootAuthorizationKinds.None);

            FileSearchRootScanResult scan = await access.ScanPdfAsync(resolved);

            scan.Candidates.Select(static candidate => candidate.Path).Should().Equal(localPath, cloudPath);
            scan.Candidates.Select(static candidate => candidate.Readiness).Should()
                .Equal(FileLocalityReadiness.LocalReady, FileLocalityReadiness.CloudUnready);
            scan.ScanStatus.Should().Be(FileSearchRootScanStatuses.Complete);
            scan.SkippedFiles.Should().BeEmpty();
            adapter.MaterializedPaths.Should().BeEmpty();

            Result materialized = await access.EnsureAvailableAsync(cloudPath);

            materialized.IsSuccess.Should().BeTrue(materialized.ErrorMessage);
            adapter.MaterializedPaths.Should().Equal(cloudPath);
            access.Assess(cloudPath).Readiness.Should().Be(FileLocalityReadiness.CloudReady);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class TrackingHydrationAdapter(Action hydrate) : INativeFileAccessAdapter
    {
        private readonly PortableNativeFileAccessAdapter _inner = new();

        public List<string> MaterializedPaths { get; } = [];

        public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            return _inner.ResolveDirectoryAsync(path, cancellationToken);
        }

        public ValueTask<NativeFileMaterialization> MaterializeFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFileName(path), "b-cloud.pdf", StringComparison.OrdinalIgnoreCase))
            {
                MaterializedPaths.Add(path);
                hydrate();
            }

            return ValueTask.FromResult(new NativeFileMaterialization(true));
        }
    }
}
