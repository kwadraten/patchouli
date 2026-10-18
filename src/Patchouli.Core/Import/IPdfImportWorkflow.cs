namespace Patchouli.Core.Import;

/// <summary>
/// Seam over the atomic PDF import workflow so the shared file import service stays
/// testable without a database. The implementation keeps per-page failure tolerance
/// and threshold rules; callers never bypass them.
/// </summary>
public interface IPdfImportWorkflow
{
    Task<PdfImportResult> ImportPdfAsync(
        PdfImportRequest request,
        CancellationToken cancellationToken = default);
}
