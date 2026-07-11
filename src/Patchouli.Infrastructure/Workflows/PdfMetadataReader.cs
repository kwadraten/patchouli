using Patchouli.Core.Import;
using MuPDF.NET;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfMetadataReader : IPdfMetadataReader
{
    public Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => GetPageCount(pdfPath, cancellationToken), cancellationToken);
    }

    private static int? GetPageCount(string pdfPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(pdfPath))
        {
            return null;
        }

        try
        {
            using Document document = new(pdfPath);
            cancellationToken.ThrowIfCancellationRequested();
            return document.PageCount > 0 ? document.PageCount : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
