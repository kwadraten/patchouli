using Patchouli.Core.Files;

namespace Patchouli.Core.Import;

public sealed record PdfScanResult(
    IReadOnlyList<PdfCandidate> Candidates,
    int TotalCount,
    string ScanRoot,
    IReadOnlyList<FileSearchRootIssue>? SkippedDirectories = null,
    IReadOnlyList<FileSearchRootIssue>? SkippedFiles = null,
    IReadOnlyList<FileSearchRootExcludedEntry>? ExcludedEntries = null,
    string RootStatus = FileSearchRootStatuses.Available,
    string ScanStatus = FileSearchRootScanStatuses.Complete);

public sealed record PdfImportRequest(
    string PdfPath,
    string? Title,
    string? Authors,
    int? PageCount);

public sealed record PdfImportPageFailure(int PageIndex, string ErrorMessage);

public sealed record PdfImportResult(
    bool Success,
    string? ErrorMessage,
    string? Status,
    string? CreatedItemId,
    string? CreatedFileAssetId,
    string? CreatedDocumentInstanceId,
    int PageCount = 0,
    int FailedPageCount = 0,
    IReadOnlyList<PdfImportPageFailure>? PageFailures = null)
{
    public bool IsDuplicate => Status is "already_imported" or "already_imported_deleted";
}
