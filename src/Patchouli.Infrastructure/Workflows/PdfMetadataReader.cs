using Patchouli.Core.Import;
using MuPDF.NET;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfMetadataReader : IPdfMetadataReader
{
    public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(pdfPath))
            return Task.FromResult<int?>(null);

        try
        {
            using var document = new Document(pdfPath);
            return Task.FromResult<int?>(document.PageCount > 0 ? document.PageCount : null);
        }
        catch
        {
            return Task.FromResult<int?>(null);
        }
    }
}
