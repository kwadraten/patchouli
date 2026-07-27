using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

public sealed record OcrPageRange(int StartPageIndex, int PageCount);

public sealed record OcrImageContext(
    double PageWidthPoints,
    double PageHeightPoints,
    int RenderDpi,
    NormalizedBBox? RegionBBox);

public abstract record OcrUploadSource
{
    public sealed record WholeDocument(string PdfPath) : OcrUploadSource;

    public sealed record PageRanges(string PdfPath, IReadOnlyList<OcrPageRange> Ranges) : OcrUploadSource;

    public sealed record PageImage(string ImagePath, OcrImageContext Context) : OcrUploadSource;

    public sealed record RegionImage(string ImagePath, OcrImageContext Context) : OcrUploadSource;
}

public enum OcrParseShape
{
    StructuredTree,
    PlainText
}
