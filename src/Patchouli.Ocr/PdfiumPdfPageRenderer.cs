using System.Runtime.InteropServices;
using Patchouli.Core.Layout;
using SkiaSharp;

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

public sealed class PdfiumPdfPageRenderer : IPdfPageRenderer, IPdfPageMemoryRenderer, IPdfPageRendererAvailability,
    IPdfPageRendererIdentity, IPdfPagePixelBufferRenderer, IPdfPageSessionRenderer
{
    private readonly PdfiumDocumentEngine _engine;

    public PdfiumPdfPageRenderer(PdfiumDocumentEngine? engine = null)
    {
        _engine = engine ?? new PdfiumDocumentEngine();
    }

    public string GetRendererBasisVersion(int dpi)
    {
        return $"pdfium-{PdfiumDocumentEngine.Version}-dpi{dpi}";
    }

    public async Task<IPdfPageSession> OpenSessionAsync(string pdfPath,
        CancellationToken cancellationToken = default)
    {
        PdfiumDocumentSession session = await _engine.OpenSessionAsync(pdfPath, cancellationToken);
        return new PdfiumPageSession(session);
    }

    public async Task<PdfRendererAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _engine.CheckAvailabilityAsync(cancellationToken);
            return new PdfRendererAvailability("PDFium", true,
                $"PDFium {PdfiumDocumentEngine.Version} renderer is available.");
        }
        catch (PdfRendererUnavailableException exception)
        {
            return new PdfRendererAvailability("PDFium", false, exception.Message);
        }
    }

    public async Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath, int pageIndex, string outputPath,
        int dpi, CancellationToken cancellationToken = default)
    {
        PdfPageRasterOutput raster = await RenderPageToPngBytesAsync(pdfPath, pageIndex, dpi, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllBytesAsync(outputPath, raster.PngBytes, cancellationToken);
        return new PdfPageRenderOutput(raster.WidthPixels, raster.HeightPixels, raster.Rotation, raster.CoordinateBasis,
            raster.BasisWidth, raster.BasisHeight, raster.RendererBasisVersion);
    }

    public async Task<PdfPageRasterOutput> RenderPageToPngBytesAsync(string pdfPath, int pageIndex, int dpi,
        CancellationToken cancellationToken = default)
    {
        PdfPagePixelBufferOutput raster =
            await RenderPageToBgraBytesAsync(pdfPath, pageIndex, dpi, cancellationToken);
        using SKBitmap bitmap = new(new SKImageInfo(raster.WidthPixels, raster.HeightPixels, SKColorType.Bgra8888,
            SKAlphaType.Premul));
        IntPtr destination = bitmap.GetPixels();
        int destinationStride = bitmap.RowBytes;
        for (int row = 0; row < raster.HeightPixels; row++)
        {
            Marshal.Copy(raster.BgraBytes, row * raster.Stride, IntPtr.Add(destination, row * destinationStride),
                raster.WidthPixels * 4);
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = png.ToArray();
        return new PdfPageRasterOutput(bytes, raster.WidthPixels, raster.HeightPixels, raster.Rotation,
            raster.CoordinateBasis, raster.BasisWidth, raster.BasisHeight, raster.RendererBasisVersion);
    }

    public async Task<PdfPagePixelBufferOutput> RenderPageToBgraBytesAsync(string pdfPath, int pageIndex, int dpi,
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

        PdfiumPageBitmap raster = await _engine.RenderPageAsync(pdfPath, pageIndex, dpi, cancellationToken);
        return ToBufferOutput(raster, dpi);
    }

    private static PdfPagePixelBufferOutput ToBufferOutput(PdfiumPageBitmap raster, int dpi)
    {
        return new PdfPagePixelBufferOutput(raster.BgraBytes, raster.Width, raster.Height, raster.Stride, 0,
            CoordinateBasis.NormalizedPage, raster.Width, raster.Height,
            $"pdfium-{PdfiumDocumentEngine.Version}-dpi{dpi}");
    }

    private sealed class PdfiumPageSession : IPdfPageSession
    {
        private readonly PdfiumDocumentSession _session;

        public PdfiumPageSession(PdfiumDocumentSession session)
        {
            _session = session;
        }

        public string Path => _session.Path;

        public int PageCount => _session.PageCount;

        public async Task<PdfPagePixelBufferOutput> RenderPageAsync(int pageIndex, int dpi,
            CancellationToken cancellationToken = default)
        {
            PdfiumPageBitmap raster = await _session.RenderPageAsync(pageIndex, dpi, cancellationToken);
            return ToBufferOutput(raster, dpi);
        }

        public ValueTask DisposeAsync()
        {
            return _session.DisposeAsync();
        }
    }
}
