using FluentAssertions;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Tests;

public sealed class PdfDiscoveryServiceTests
{
    [Fact]
    public async Task ScanDirectoryAsync_returns_only_pdfs()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "doc1.pdf");
            TestFixtures.CopyRealThreePagePdfTo(dir, "doc2.pdf");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a pdf");

            PdfDiscoveryService service = new();
            PdfScanResult result = await service.ScanDirectoryAsync(dir);

            result.Candidates.Should().HaveCount(2);
            result.TotalCount.Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_ignores_bin_and_obj_subdirectories()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            TestFixtures.CopyRealThreePagePdfTo(dir, "valid.pdf");
            TestFixtures.CopyRealThreePagePdfTo(Path.Combine(dir, "bin"), "output.pdf");
            TestFixtures.CopyRealThreePagePdfTo(Path.Combine(dir, "obj"), "temp.pdf");

            PdfDiscoveryService service = new();
            PdfScanResult result = await service.ScanDirectoryAsync(dir);

            result.Candidates.Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_returns_empty_for_nonexistent_directory()
    {
        PdfDiscoveryService service = new();
        PdfScanResult result = await service.ScanDirectoryAsync("X:\\nonexistent\\path");

        result.Candidates.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Failed);
        result.RootStatus.Should().Be(FileSearchRootStatuses.Offline);
    }

    [Fact]
    public async Task ScanDirectoryAsync_matches_pdf_extension_case_insensitively()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "upper.PDF");
            TestFixtures.CopyRealThreePagePdfTo(dir, "mixed.Pdf");

            PdfScanResult result = await new PdfDiscoveryService().ScanDirectoryAsync(dir);

            result.Candidates.Should().HaveCount(2);
            result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Complete);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_preserves_candidates_when_one_directory_fails()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}"))
            .FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "available.pdf");
            string denied = Directory.CreateDirectory(Path.Combine(dir, "denied")).FullName;
            PdfDiscoveryService service = new(new FileSearchRootAccess(new DeniedDirectoryAdapter(denied)));

            PdfScanResult result = await service.ScanDirectoryAsync(dir);

            result.Candidates.Should().ContainSingle();
            result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Partial);
            result.RootStatus.Should().Be(FileSearchRootStatuses.Partial);
            result.SkippedDirectories.Should().ContainSingle(issue => issue.Code == "access_denied");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class DeniedDirectoryAdapter(string deniedPath) : INativeFileAccessAdapter
    {
        private readonly PortableNativeFileAccessAdapter _inner = new();

        public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path,
            CancellationToken cancellationToken)
        {
            return string.Equals(path, deniedPath, StringComparison.OrdinalIgnoreCase)
                ? ValueTask.FromResult(new NativeDirectoryResolution(null, "access_denied", "Test denial."))
                : _inner.ResolveDirectoryAsync(path, cancellationToken);
        }

        public ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path,
            CancellationToken cancellationToken)
        {
            return _inner.MaterializeFileAsync(path, cancellationToken);
        }
    }
}
