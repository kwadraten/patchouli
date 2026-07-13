using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrCandidateAdoption(
    OcrCandidateAdoptionId AdoptionId,
    OcrRunId OcrRunId,
    DocumentInstanceId DocumentInstanceId,
    IReadOnlyList<DocumentTreeRevisionId> AdoptedTreeRevisionIds,
    string AdoptedPagesJson,
    DateTimeOffset CreatedAt);
