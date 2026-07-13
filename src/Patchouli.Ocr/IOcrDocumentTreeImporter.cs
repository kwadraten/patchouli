using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr;

public sealed record OcrDocumentTreeImportRequest(
    DocumentInstanceId DocumentInstanceId,
    OcrDocumentTreeCandidate Candidate,
    string RevisionSource = "import");

public sealed record OcrDocumentTreeImportResult(
    IReadOnlyList<DocumentTreeRevisionId> StagingRevisionIds,
    int BoxesCreated,
    IReadOnlyList<OcrDiagnostic> Diagnostics);

public interface IOcrDocumentTreeImporter
{
    Task<Result<OcrDocumentTreeImportResult>> StageAsync(
        OcrDocumentTreeImportRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DocumentTreeRevisionId>>> AdoptAsync(
        IReadOnlyList<DocumentTreeRevisionId> stagingRevisionIds,
        CancellationToken cancellationToken = default);
}
