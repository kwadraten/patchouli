using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrCandidateAdoption(
    OcrCandidateAdoptionId AdoptionId,
    OcrRunId OcrRunId,
    DocumentInstanceId DocumentInstanceId,
    LayoutRevisionId AdoptedRevisionId,
    string AdoptedPagesJson,
    DateTimeOffset CreatedAt);
