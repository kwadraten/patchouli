using FluentAssertions;
using Patchouli.Core.Files;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Tests;

public sealed class FileFingerprintServiceTests
{
    [Fact]
    public async Task GetFileMetadata_computes_full_blake3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"patchouli-fingerprint-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "abc");

        try
        {
            FileFingerprintService service = new();

            Result<FileFingerprint> result = await service.GetFileMetadataAsync(path);

            result.IsSuccess.Should().BeTrue();
            result.Value.FullBlake3.Should().Be("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85");
            result.Value.QuickHash.Should().HaveLength(64);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
