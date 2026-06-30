using FluentAssertions;
using Patchouli.Infrastructure.Workflows;

namespace Patchouli.Tests;

public sealed class PdfDiscoveryServiceTests
{
    [Fact]
    public async Task ScanDirectoryAsync_returns_only_pdfs()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pdfscan-{Guid.NewGuid():N}")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "doc1.pdf"), "%PDF-1.4");
            File.WriteAllText(Path.Combine(dir, "doc2.pdf"), "%PDF-1.4");
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
            File.WriteAllText(Path.Combine(dir, "valid.pdf"), "%PDF");
            File.WriteAllText(Path.Combine(dir, "bin", "output.pdf"), "%PDF");
            File.WriteAllText(Path.Combine(dir, "obj", "temp.pdf"), "%PDF");

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
    }
}
