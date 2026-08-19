using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrCandidateCommit(
    OcrCandidateAdoptionId CommitId,
    OcrRunId OcrRunId,
    DocumentInstanceId DocumentInstanceId,
    IReadOnlyList<DocumentTreeRevisionId> CommittedTreeRevisionIds,
    string CommittedPagesJson,
    DateTimeOffset CreatedAt);
