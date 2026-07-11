using System.IO.Compression;
using FluentAssertions;
using Patchouli.Infrastructure.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUZipReaderTests
{
    [Fact]
    public void ReadFileContent_returns_content_by_pattern_match()
    {
        string zipPath = CreateSampleZip();
        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(zipPath);
            string? content = reader.ReadFileContent("_content_list.json");
            content.Should().NotBeNull();
            content.Should().Contain("Sample Document");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void ReadFileContent_returns_null_for_missing_pattern()
    {
        string zipPath = CreateSampleZip();
        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(zipPath);
            reader.ReadFileContent("nonexistent.json").Should().BeNull();
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void HasFile_returns_true_for_matching_pattern()
    {
        string zipPath = CreateSampleZip();
        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(zipPath);
            reader.HasFile(".json").Should().BeTrue();
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public void GetFileNames_returns_all_entries()
    {
        string zipPath = CreateSampleZip();
        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(zipPath);
            reader.GetFileNames().Should().Contain(f => f.EndsWith("_content_list.json"));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static string CreateSampleZip()
    {
        string zipPath = Path.Combine(Path.GetTempPath(), $"mineru-test-{Guid.NewGuid():N}.zip");
        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("sample_content_list.json");
        using StreamWriter writer = new(entry.Open());
        writer.Write(
            """{"pages":[{"page_num":1,"width":595,"height":842,"blocks":[{"type":"text","text":"Sample Document"}]}]}""");
        return zipPath;
    }
}
