using LiteratureApp.Core.Ids;

namespace LiteratureApp.Ocr;

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
