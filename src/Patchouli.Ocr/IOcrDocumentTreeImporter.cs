using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record OcrDocumentTreeImportRequest(
    DocumentInstanceId DocumentInstanceId,
    OcrDocumentTreeCandidate Candidate,
    string RevisionSource = "import");

public sealed record OcrDocumentTreeImportResult(
    IReadOnlyList<DocumentTreeRevisionId> WorkingRevisionIds,
    int BoxesCreated,
    IReadOnlyList<OcrDiagnostic> Diagnostics);

public interface IOcrDocumentTreeImporter
{
    Task<Result<OcrDocumentTreeImportResult>> BeginWorkingAsync(
        OcrDocumentTreeImportRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DocumentTreeRevisionId>>> CommitAsync(
        IReadOnlyList<DocumentTreeRevisionId> workingRevisionIds,
        DocumentCommitId? commitId = null,
        CancellationToken cancellationToken = default);
}
