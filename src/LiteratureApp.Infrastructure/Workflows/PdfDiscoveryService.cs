using LiteratureApp.Core.Import;

namespace LiteratureApp.Infrastructure.Workflows;

public sealed class PdfDiscoveryService
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".svn", ".hg", ".vs"
    };

    public Task<PdfScanResult> ScanDirectoryAsync(string scanRoot, CancellationToken cancellationToken = default)
    {
        var candidates = new List<PdfCandidate>();

        try
        {
            if (!Directory.Exists(scanRoot))
                return Task.FromResult(new PdfScanResult([], 0, scanRoot));

            var pdfFiles = Directory.EnumerateFiles(scanRoot, "*.pdf", SearchOption.AllDirectories);

            foreach (var filePath in pdfFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var dir = Path.GetDirectoryName(filePath);
                if (dir is not null && ShouldExclude(dir, scanRoot))
                    continue;

                try
                {
                    var info = new FileInfo(filePath);
                    candidates.Add(new PdfCandidate(
                        filePath,
                        info.Name,
                        info.Length,
                        info.LastWriteTimeUtc,
                        null,
                        "discovered"));
                }
                catch
                {
                    // Skip files that can't be accessed
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(new PdfScanResult([], 0, scanRoot));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new PdfScanResult([], 0, scanRoot));
        }

        return Task.FromResult(new PdfScanResult(
            candidates.OrderBy(c => c.FileName).ToArray(),
            candidates.Count,
            scanRoot));
    }

    private static bool ShouldExclude(string directoryPath, string scanRoot)
    {
        var relative = Path.GetRelativePath(scanRoot, directoryPath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => ExcludedDirs.Contains(p));
    }
}
