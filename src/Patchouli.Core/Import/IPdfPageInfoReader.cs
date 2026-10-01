namespace Patchouli.Core.Import;

public sealed record PdfPageInfo(double Width, double Height, int Rotation);

public sealed record PdfPageInfoResult(bool Success, PdfPageInfo? Info, string? ErrorMessage);

public interface IPdfPageInfoReader
{
    Task<IReadOnlyList<PdfPageInfoResult>?> GetPageInfosAsync(
        string pdfPath,
        CancellationToken cancellationToken = default);
}
