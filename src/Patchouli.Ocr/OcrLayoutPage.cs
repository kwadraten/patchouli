using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrLayoutPage(
    PageId PageId,
    int PageIndex,
    double? Width,
    double? Height,
    IReadOnlyList<OcrLayoutBlock> Blocks);
