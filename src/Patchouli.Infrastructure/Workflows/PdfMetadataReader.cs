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
/// reported as <c>null</c> (there is nothing to read) — including a file that disappears
/// between the existence check and the native open, which the native layer surfaces as
/// <see cref="FileNotFoundException"/>; that race is translated to the same null rather
/// than escaping as an untyped failure the workflow cannot classify.
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

        int pageCount;
        try
        {
            pageCount = await _engine.GetPageCountAsync(pdfPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Deleted between the existence check and the native open: same as missing.
            return null;
        }

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

        try
        {
            return await _engine.GetPageInfosAsync(pdfPath, pageCount.Value, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Deleted between the page-count read and the page-info read: same as missing.
            return null;
        }
    }
}
