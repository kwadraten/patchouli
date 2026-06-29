using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Layout;

namespace LiteratureApp.Ocr;

public sealed record OcrEnginePageResult(
    PageId PageId,
    bool Succeeded,
    string? Text,
    NormalizedBBox? BBox,
    string? ErrorCode,
    string? ErrorMessage,
    SourceBBox? SourceBBox = null);
