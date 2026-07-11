using MuPDF.NET;
using Patchouli.Core.Layout;
using Page = MuPDF.NET.Page;

namespace Patchouli.Ocr;

public sealed class PdfRendererUnavailableException : Exception
{
    public PdfRendererUnavailableException(string message, Exception? innerException = null) : base(message,
        innerException)
    {
    }
}

public sealed class PdfRendererTimeoutException : Exception
{
    public PdfRendererTimeoutException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

public sealed class MuPdfNetPdfPageRenderer : IPdfPageRenderer, IPdfPageMemoryRenderer, IPdfPageRendererAvailability,
    IPdfPageRendererIdentity
{
    public string GetRendererBasisVersion(int dpi)
    {
        return $"mupdf-net-dpi{dpi}";
    }

    public Task<PdfRendererAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PdfRendererAvailability("MuPDF.NET", true, "MuPDF.NET renderer is available."));
    }

    public async Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath, int pageIndex, string outputPath,
        int dpi, CancellationToken cancellationToken = default)
    {
        PdfPageRasterOutput raster = await RenderPageToPngBytesAsync(pdfPath, pageIndex, dpi, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, raster.PngBytes, cancellationToken);
        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("MuPDF.NET did not produce the expected PNG output.");
        }

        return new PdfPageRenderOutput(raster.WidthPixels, raster.HeightPixels, raster.Rotation, raster.CoordinateBasis,
            raster.BasisWidth, raster.BasisHeight, raster.RendererBasisVersion);
    }

    public async Task<PdfPageRasterOutput> RenderPageToPngBytesAsync(string pdfPath, int pageIndex, int dpi,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0)
        {
            throw new InvalidOperationException("Page index must be non-negative.");
        }

        if (dpi is < 50 or > 600)
        {
            throw new InvalidOperationException("Render DPI must be between 50 and 600.");
        }

        if (!File.Exists(pdfPath))
        {
            throw new InvalidOperationException("PDF source file was not found.");
        }

        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                float scale = dpi / 72f;
                using Document document = new(pdfPath);
                if (pageIndex >= document.PageCount)
                {
                    throw new InvalidOperationException("Page index is outside the PDF page range.");
                }

                Page page = document.LoadPage(pageIndex);
                Pixmap pixmap = page.GetPixmap(new Matrix(scale, scale), new Colorspace(ColorspaceType.Rgb));
                pixmap.SetDpi(dpi, dpi);
                byte[] bytes = pixmap.ToPng();
                return new PdfPageRasterOutput(bytes, pixmap.Width, pixmap.Height, 0, CoordinateBasis.NormalizedPage,
                    pixmap.Width, pixmap.Height, GetRendererBasisVersion(dpi));
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            throw new PdfRendererUnavailableException("MuPDF.NET native runtime could not be loaded.", ex);
        }
        catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException)
        {
            throw new PdfRendererUnavailableException("MuPDF.NET native runtime could not be loaded.", ex);
        }
    }
}
