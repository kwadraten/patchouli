using System.IO.Compression;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal sealed class MinerUZipReader : IDisposable
{
    private readonly ZipArchive _archive;

    private MinerUZipReader(ZipArchive archive)
    {
        _archive = archive;
    }

    public static MinerUZipReader Open(string zipPath)
    {
        return new MinerUZipReader(ZipFile.OpenRead(zipPath));
    }

    public string? ReadFileContent(string fileNamePattern)
    {
        var entry = _archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(fileNamePattern, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return null;

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    public bool HasFile(string fileNamePattern)
    {
        return _archive.Entries.Any(e =>
            e.Name.EndsWith(fileNamePattern, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> GetFileNames()
    {
        return _archive.Entries.Select(e => e.FullName).ToArray();
    }

    public void Dispose()
    {
        _archive.Dispose();
    }
}
