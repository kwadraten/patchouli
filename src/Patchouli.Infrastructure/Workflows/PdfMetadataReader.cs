using PDFiumCore;
using Patchouli.Core.Import;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfMetadataReader : IPdfMetadataReader, IPdfPageInfoReader
{
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
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

    public async Task<IReadOnlyList<PdfPageInfoResult>?> GetPageInfosAsync(
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pdfPath))
        {
            return null;
        }

        try
        {
            int? pageCount = await GetPageCountAsync(pdfPath, cancellationToken);
            if (pageCount is null or <= 0)
            {
                return null;
            }

            return await ReadPageInfosAsync(pdfPath, pageCount.Value, cancellationToken);
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

    private static async Task<IReadOnlyList<PdfPageInfoResult>?> ReadPageInfosAsync(
        string pdfPath,
        int pageCount,
        CancellationToken cancellationToken)
    {
        List<PdfPageInfoResult> results = new(pageCount);
        await NativeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FpdfDocumentT document = fpdfview.FPDF_LoadDocument(pdfPath, null!);
            if (document.__Instance == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(ReadPageInfo(document, pageIndex));
                }
            }
            finally
            {
                fpdfview.FPDF_CloseDocument(document);
            }

            return results;
        }
        finally
        {
            NativeGate.Release();
        }
    }

    private static PdfPageInfoResult ReadPageInfo(FpdfDocumentT document, int pageIndex)
    {
        FpdfPageT page = fpdfview.FPDF_LoadPage(document, pageIndex);
        if (page.__Instance == IntPtr.Zero)
        {
            return new PdfPageInfoResult(false, null, $"PDFium could not load page {pageIndex}.");
        }

        try
        {
            double width = fpdfview.FPDF_GetPageWidthF(page);
            double height = fpdfview.FPDF_GetPageHeightF(page);
            int rotation = fpdf_edit.FPDFPageGetRotation(page) switch
            {
                1 => 90,
                2 => 180,
                3 => 270,
                _ => 0
            };
            return new PdfPageInfoResult(true, new PdfPageInfo(width, height, rotation), null);
        }
        catch (Exception exception) // Classified into the page failure result; the page records it (ADR 0035).
        {
            return new PdfPageInfoResult(false, null, exception.Message);
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }
}
