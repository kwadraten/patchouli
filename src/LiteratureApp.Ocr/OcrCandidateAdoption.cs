using LiteratureApp.Core.Ids;

namespace LiteratureApp.Ocr;

public sealed record OcrCandidateAdoption(
    OcrCandidateAdoptionId AdoptionId,
    OcrRunId OcrRunId,
    DocumentInstanceId DocumentInstanceId,
    LayoutRevisionId AdoptedRevisionId,
    string AdoptedPagesJson,
    DateTimeOffset CreatedAt);
