using System.Runtime.InteropServices;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public static class PageRenderPurpose
{
    public const string Ocr = "ocr";
    public const string Preview = "preview";
    public const string Thumbnail = "thumbnail";
}

public static class PageRenderStatus
{
    public const string Rendered = "rendered";
    public const string FromCache = "from_cache";
    public const string SourceMissing = "source_missing";
    public const string SourceChanged = "source_changed";
    public const string Conflict = "conflict";
    public const string UnsupportedFile = "unsupported_file";
    public const string RenderFailed = "render_failed";
    public const string RendererTimeout = "renderer_timeout";
    public const string RendererUnavailable = "renderer_unavailable";
}

public sealed record PageRenderRequest(
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    FileAssetId? FileAssetId = null,
    int Dpi = 200,
    string OutputFormat = "png",
    string Purpose = PageRenderPurpose.Ocr,
    bool ForceRerender = false);

public sealed record PageRenderCacheKey(
    LibraryId LibraryId,
    DocumentInstanceId DocumentInstanceId,
    PageId PageId,
    FileAssetId FileAssetId,
    int PageIndex,
    int Dpi,
    string RendererBasisVersion,
    string SourceFileHash);

public sealed record PageRenderResult(
    string Status,
    PageId PageId,
    DocumentInstanceId DocumentInstanceId,
    string? CacheImagePath,
    int WidthPixels,
    int HeightPixels,
    int Dpi,
    int Rotation,
    string CoordinateBasis,
    string RendererBasisVersion,
    string SourceFileStatus,
    string? SourceFileHash,
    string? Warning,
    bool IsFromCache);

public sealed record PdfPageRenderOutput(
    int WidthPixels,
    int HeightPixels,
    int Rotation,
    string CoordinateBasis,
    double BasisWidth,
    double BasisHeight,
    string RendererBasisVersion);

public sealed record PdfPageRasterOutput(
    byte[] PngBytes,
    int WidthPixels,
    int HeightPixels,
    int Rotation,
    string CoordinateBasis,
    double BasisWidth,
    double BasisHeight,
    string RendererBasisVersion);

public sealed class PdfPagePixelBufferLease : IDisposable
{
    private byte[]? _bgraBytes;
    private GCHandle _handle;

    public PdfPagePixelBufferLease(byte[] bgraBytes, int widthPixels, int heightPixels, int stride, int rotation,
        string coordinateBasis, double basisWidth, double basisHeight, string rendererBasisVersion)
    {
        _bgraBytes = bgraBytes ?? throw new ArgumentNullException(nameof(bgraBytes));
        _handle = GCHandle.Alloc(bgraBytes, GCHandleType.Pinned);
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        Stride = stride;
        Rotation = rotation;
        CoordinateBasis = coordinateBasis;
        BasisWidth = basisWidth;
        BasisHeight = basisHeight;
        RendererBasisVersion = rendererBasisVersion;
    }

    public ReadOnlyMemory<byte> BgraBytes =>
        _bgraBytes ?? throw new ObjectDisposedException(nameof(PdfPagePixelBufferLease));

    public IntPtr PixelAddress => _bgraBytes is null
        ? throw new ObjectDisposedException(nameof(PdfPagePixelBufferLease))
        : _handle.AddrOfPinnedObject();

    public int WidthPixels { get; }
    public int HeightPixels { get; }
    public int Stride { get; }
    public int Rotation { get; }
    public string CoordinateBasis { get; }
    public double BasisWidth { get; }
    public double BasisHeight { get; }
    public string RendererBasisVersion { get; }

    public void Dispose()
    {
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }

        _bgraBytes = null;
    }
}

public sealed record PdfPagePixelBufferOutput(
    byte[] BgraBytes,
    int WidthPixels,
    int HeightPixels,
    int Stride,
    int Rotation,
    string CoordinateBasis,
    double BasisWidth,
    double BasisHeight,
    string RendererBasisVersion);

public interface IPdfPageRenderer
{
    Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath, int pageIndex, string outputPath, int dpi,
        CancellationToken cancellationToken = default);
}

public interface IPdfPageMemoryRenderer
{
    Task<PdfPageRasterOutput> RenderPageToPngBytesAsync(string pdfPath, int pageIndex, int dpi,
        CancellationToken cancellationToken = default);
}

public interface IPdfPagePixelBufferRenderer
{
    Task<PdfPagePixelBufferOutput> RenderPageToBgraBytesAsync(string pdfPath, int pageIndex, int dpi,
        CancellationToken cancellationToken = default);
}

public interface IPdfPageRendererIdentity
{
    string GetRendererBasisVersion(int dpi);
}

public sealed record PdfRendererAvailability(string RendererName, bool IsAvailable, string Message);

public interface IPdfPageRendererAvailability
{
    Task<PdfRendererAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);
}

public interface IPageRenderService
{
    Task<Result<PageRenderResult>> RenderPageAsync(PageRenderRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<string?>> GetCachedRenderPathAsync(PageRenderRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<PdfPagePixelBufferLease>> RenderPreviewAsync(PageRenderRequest request,
        CancellationToken cancellationToken = default);

    Task<Result> ClearRenderCacheForDocumentAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<OcrInputDescriptor>> BuildOcrInputFromRenderedPageAsync(DocumentInstanceId documentInstanceId,
        PageId pageId, int dpi = 200, CancellationToken cancellationToken = default);

    Task<PdfRendererAvailability> GetRendererAvailabilityAsync(CancellationToken cancellationToken = default);
}
