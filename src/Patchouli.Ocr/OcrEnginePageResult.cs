using Patchouli.Core.Ids;
using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

public sealed record OcrEnginePageResult(
    PageId PageId,
    bool Succeeded,
    string? Text,
    NormalizedBBox? BBox,
    string? ErrorCode,
    string? ErrorMessage,
    SourceBBox? SourceBBox = null);
