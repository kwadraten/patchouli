using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrPageResult(
    OcrPageResultId ResultId,
    OcrRunId OcrRunId,
    PageId PageId,
    string State,
    LayoutRevisionId? StagingLayoutRevisionId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
