using Patchouli.Core.Import;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfMetadataReader : IPdfMetadataReader
{
    private readonly PdfiumDocumentEngine _engine;

    public PdfMetadataReader(PdfiumDocumentEngine? engine = null)
    {
        _engine = engine ?? new PdfiumDocumentEngine();
    }

    public async Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pdfPath))
        {
            return null;
        }

        try
        {
            int pageCount = await _engine.GetPageCountAsync(pdfPath, cancellationToken);
            return pageCount > 0 ? pageCount : null;
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
