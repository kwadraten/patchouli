namespace Patchouli.Ocr;

public sealed record OcrTableCell(
    int RowIndex,
    int ColIndex,
    int RowSpan = 1,
    int ColSpan = 1,
    bool IsHeader = false);
