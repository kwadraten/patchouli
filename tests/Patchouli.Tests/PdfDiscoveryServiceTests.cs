using FluentAssertions;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Core.Files;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Tests;

public sealed class PdfDiscoveryServiceTests
{
    [Fact]
    public async Task ScanDirectoryAsync_returns_only_pdfs()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}")).FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "doc1.pdf");
            TestFixtures.CopyRealThreePagePdfTo(dir, "doc2.pdf");
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a pdf");

            var service = new PdfDiscoveryService();
            var result = await service.ScanDirectoryAsync(dir);

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
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}")).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            TestFixtures.CopyRealThreePagePdfTo(dir, "valid.pdf");
            TestFixtures.CopyRealThreePagePdfTo(Path.Combine(dir, "bin"), "output.pdf");
            TestFixtures.CopyRealThreePagePdfTo(Path.Combine(dir, "obj"), "temp.pdf");

            var service = new PdfDiscoveryService();
            var result = await service.ScanDirectoryAsync(dir);

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
        var service = new PdfDiscoveryService();
        var result = await service.ScanDirectoryAsync("X:\\nonexistent\\path");

        result.Candidates.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.ScanStatus.Should().Be(FileSearchRootScanStatuses.Failed);
        result.RootStatus.Should().Be(FileSearchRootStatuses.Offline);
    }

    [Fact]
    public async Task ScanDirectoryAsync_matches_pdf_extension_case_insensitively()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}")).FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "upper.PDF");
            TestFixtures.CopyRealThreePagePdfTo(dir, "mixed.Pdf");

            var result = await new PdfDiscoveryService().ScanDirectoryAsync(dir);

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
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}")).FullName;
        try
        {
            TestFixtures.CopyRealThreePagePdfTo(dir, "available.pdf");
            var denied = Directory.CreateDirectory(Path.Combine(dir, "denied")).FullName;
            var service = new PdfDiscoveryService(new FileSearchRootAccess(new DeniedDirectoryAdapter(denied)));

            var result = await service.ScanDirectoryAsync(dir);

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

        public ValueTask<NativeDirectoryResolution> ResolveDirectoryAsync(string path, CancellationToken cancellationToken)
            => string.Equals(path, deniedPath, StringComparison.OrdinalIgnoreCase)
                ? ValueTask.FromResult(new NativeDirectoryResolution(null, "access_denied", "Test denial."))
                : _inner.ResolveDirectoryAsync(path, cancellationToken);

        public ValueTask<NativeFileMaterialization> MaterializeFileAsync(string path, CancellationToken cancellationToken)
            => _inner.MaterializeFileAsync(path, cancellationToken);
    }
}
