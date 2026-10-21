using Patchouli.Core.Import;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Workflows;

/// <summary>
/// Reads PDF metadata through the shared <see cref="PdfiumDocumentEngine"/>, which routes
/// every native call through the single process-wide PDFium scheduler (issue #31). This
/// reader owns no native gate and no native calls of its own.
/// Page-count failures are not swallowed: a PDFium failure surfaces as a typed
/// <see cref="PdfiumDocumentException"/> carrying the native error code and path, and
/// cancellation propagates as <see cref="OperationCanceledException"/>. A missing file is
/// the only case reported as <c>null</c> (there is nothing to read).
/// </summary>
public sealed class PdfMetadataReader : IPdfMetadataReader, IPdfPageInfoReader
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

        int pageCount = await _engine.GetPageCountAsync(pdfPath, cancellationToken);
        if (pageCount > 0)
        {
            return pageCount;
        }

        throw new PdfiumDocumentException(
            $"PDFium returned a non-positive page count ({pageCount}) for this PDF.", null, pdfPath);
    }

    public async Task<IReadOnlyList<PdfPageInfoResult>?> GetPageInfosAsync(
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pdfPath))
        {
            return null;
        }

        int? pageCount = await GetPageCountAsync(pdfPath, cancellationToken);
        if (pageCount is null or <= 0)
        {
            return null;
        }

        return await _engine.GetPageInfosAsync(pdfPath, pageCount.Value, cancellationToken);
    }
}
