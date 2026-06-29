using System.Buffers.Binary;

namespace LiteratureApp.Ocr;

public sealed class PdfRendererUnavailableException : Exception
{
    public PdfRendererUnavailableException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class PdfRendererTimeoutException : Exception
{
    public PdfRendererTimeoutException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class ExternalProcessPdfPageRenderer : IPdfPageRenderer, IPdfPageRendererAvailability
{
    private readonly IProcessRunner _processRunner;
    private readonly string _executable;
    public ExternalProcessPdfPageRenderer(IProcessRunner processRunner, string executable = "pdftoppm") { _processRunner = processRunner; _executable = executable; }

    public async Task<PdfRendererAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _processRunner.RunAsync(new ProcessRunRequest(_executable, ["-v"], Timeout: TimeSpan.FromSeconds(10)), cancellationToken);
            return result.ExitCode == 0 && !result.TimedOut
                ? new PdfRendererAvailability("pdftoppm (Poppler)", true, "pdftoppm is available.")
                : new PdfRendererAvailability("pdftoppm (Poppler)", false, "pdftoppm is unavailable or did not start successfully.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return new PdfRendererAvailability("pdftoppm (Poppler)", false, "pdftoppm is not installed or cannot be executed.");
        }
    }

    public async Task<PdfPageRenderOutput> RenderPageToPngAsync(string pdfPath, int pageIndex, string outputPath, int dpi, CancellationToken cancellationToken = default)
    {
        var available = await CheckAvailabilityAsync(cancellationToken);
        if (!available.IsAvailable) throw new PdfRendererUnavailableException(available.Message);
        if (pageIndex < 0) throw new InvalidOperationException("Page index must be non-negative.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var outputBase = Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath));
        try
        {
            var result = await _processRunner.RunAsync(new ProcessRunRequest(_executable, ["-f", (pageIndex + 1).ToString(), "-l", (pageIndex + 1).ToString(), "-r", dpi.ToString(), "-png", "-singlefile", pdfPath, outputBase], Timeout: TimeSpan.FromSeconds(60)), cancellationToken);
            if (result.TimedOut) throw new PdfRendererTimeoutException("PDF renderer timed out.");
            if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? "pdftoppm failed to render the PDF page." : result.StandardError);
            if (!File.Exists(outputPath)) throw new InvalidOperationException("pdftoppm did not produce the expected PNG output.");
            var (width, height) = await ReadPngDimensionsAsync(outputPath, cancellationToken);
            return new PdfPageRenderOutput(width, height, 0, Core.Layout.CoordinateBasis.NormalizedPage, width, height, $"pdftoppm-poppler-dpi{dpi}");
        }
        catch (System.ComponentModel.Win32Exception ex) { throw new PdfRendererUnavailableException("pdftoppm is not installed or cannot be executed.", ex); }
    }

    private static async Task<(int Width, int Height)> ReadPngDimensionsAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var header = new byte[24];
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read < header.Length || header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71) throw new InvalidOperationException("Renderer output is not a valid PNG.");
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }
}
