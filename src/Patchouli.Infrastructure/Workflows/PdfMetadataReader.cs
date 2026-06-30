using Patchouli.Core.Import;
using Patchouli.Ocr;
using System.Text;
using System.Text.RegularExpressions;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfMetadataReader : IPdfMetadataReader
{
    private readonly IProcessRunner _processRunner;
    private readonly string _pdfInfoExecutable;

    public PdfMetadataReader()
        : this(new SystemProcessRunner(), "pdfinfo")
    {
    }

    public PdfMetadataReader(IProcessRunner processRunner, string pdfInfoExecutable = "pdfinfo")
    {
        _processRunner = processRunner;
        _pdfInfoExecutable = pdfInfoExecutable;
    }

    public async Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pdfPath))
            return null;

        var pdfInfoCount = await TryReadWithPdfInfoAsync(pdfPath, cancellationToken);
        if (pdfInfoCount is > 0)
            return pdfInfoCount;

        try
        {
            using var stream = File.OpenRead(pdfPath);
            using var reader = new BinaryReader(stream);

            var buffer = new byte[1024];
            var bytesRead = reader.Read(buffer, 0, buffer.Length);
            var header = System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);

            if (!header.StartsWith("%PDF-", StringComparison.OrdinalIgnoreCase))
                return null;

            stream.Position = 0;
            var fullContent = Encoding.Latin1.GetString(reader.ReadBytes((int)Math.Min(stream.Length, int.MaxValue)));
            var pageObjectCount = Regex.Matches(fullContent, @"/Type\s*/Page(?!s)\b").Count;

            if (pageObjectCount > 0)
                return pageObjectCount;

            var countMatches = Regex.Matches(fullContent, @"/Count\s+(\d+)");
            return countMatches
                .Select(m => int.TryParse(m.Groups[1].Value, out var count) ? count : 0)
                .Where(count => count > 0)
                .DefaultIfEmpty()
                .Max() is var maxCount and > 0 ? maxCount : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int?> TryReadWithPdfInfoAsync(string pdfPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessRunRequest(_pdfInfoExecutable, [pdfPath], Timeout: TimeSpan.FromSeconds(15)),
                cancellationToken);

            if (result.ExitCode != 0 || result.TimedOut)
                return null;

            foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line, @"^\s*Pages:\s*(\d+)\s*$", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var pages) && pages > 0)
                    return pages;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
