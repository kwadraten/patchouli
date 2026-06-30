namespace Patchouli.Core.Import;

public sealed record PdfCandidate(
    string Path,
    string FileName,
    long SizeBytes,
    DateTimeOffset? ModifiedAt,
    int? PageCount,
    string Status);

public sealed record PdfScanResult(
    IReadOnlyList<PdfCandidate> Candidates,
    int TotalCount,
    string ScanRoot);

public sealed record PdfImportRequest(
    string PdfPath,
    string? Title,
    string? Authors,
    int? PageCount);

public sealed record PdfImportResult(
    bool Success,
    string? ErrorMessage,
    string? Status,
    string? CreatedItemId,
    string? CreatedFileAssetId,
    string? CreatedDocumentInstanceId);
