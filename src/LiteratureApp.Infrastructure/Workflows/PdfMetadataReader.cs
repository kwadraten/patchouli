using LiteratureApp.Core.Import;

namespace LiteratureApp.Infrastructure.Workflows;

public sealed class PdfMetadataReader : IPdfMetadataReader
{
    public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pdfPath))
            return Task.FromResult<int?>(null);

        try
        {
            using var stream = File.OpenRead(pdfPath);
            using var reader = new BinaryReader(stream);

            var buffer = new byte[1024];
            var bytesRead = reader.Read(buffer, 0, buffer.Length);
            var header = System.Text.Encoding.ASCII.GetString(buffer, 0, bytesRead);

            if (!header.StartsWith("%PDF-", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<int?>(null);

            // Search for /Type /Page (not /Pages) to count page objects
            stream.Position = 0;
            var fullContent = System.Text.Encoding.ASCII.GetString(reader.ReadBytes((int)Math.Min(stream.Length, 1024 * 1024)));

            var count = CountOccurrences(fullContent, "/Type /Page\n")
                      + CountOccurrences(fullContent, "/Type /Page\r")
                      + CountOccurrences(fullContent, "/Type/Page\n")
                      + CountOccurrences(fullContent, "/Type/Page\r");

            return Task.FromResult<int?>(count > 0 ? count : null);
        }
        catch
        {
            return Task.FromResult<int?>(null);
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
