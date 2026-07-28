namespace Patchouli.Core.Import;

public sealed record PdfCandidate(
    string Path,
    string FileName,
    long SizeBytes,
    DateTimeOffset? ModifiedAt,
    int? PageCount,
    string Status,
    string Readiness = Files.FileLocalityReadiness.LocalReady,
    bool IsCloudPath = false);

public sealed record PdfScanResult(
    IReadOnlyList<PdfCandidate> Candidates,
    int TotalCount,
    string ScanRoot,
    IReadOnlyList<Files.FileSearchRootIssue>? SkippedDirectories = null,
    IReadOnlyList<Files.FileSearchRootIssue>? SkippedFiles = null,
    IReadOnlyList<Files.FileSearchRootExcludedEntry>? ExcludedEntries = null,
    string RootStatus = Files.FileSearchRootStatuses.Available,
    string ScanStatus = Files.FileSearchRootScanStatuses.Complete);

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
