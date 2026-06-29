using LiteratureApp.Core.Layout;

namespace LiteratureApp.Ocr;

/// <summary>Test-only renderer. It creates a valid tiny PNG while reporting a stable synthetic page basis.</summary>
public sealed class FakePdfPageRenderer : IPdfPageRenderer
{
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL9dwAAAABJRU5ErkJggg==");
    public async Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath, int pageIndex, string outputPath, int dpi, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, TinyPng, cancellationToken);
        return new PdfPageRenderOutput(1200, 1600, 0, CoordinateBasis.NormalizedPage, 1200, 1600, "fake-pdf-renderer-v1");
    }
}
