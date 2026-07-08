using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

public sealed record OcrLayoutBlock(
    string NodeType,
    string TextPolicy,
    int ReadingOrder,
    string? Text = null,
    string? LaTex = null,
    NormalizedBBox? BBox = null,
    double? Confidence = null,
    OcrTableCell? TableCell = null,
    IReadOnlyList<OcrLayoutBlock>? Children = null)
{
    public string? EffectiveText => !string.IsNullOrWhiteSpace(Text) ? Text : LaTex;
}
