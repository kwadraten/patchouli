namespace Patchouli.Core.Layout;

public static class LayoutRevisionSource
{
    public const string Manual = "manual";
    public const string Import = "import";
    public const string Mock = "mock";
    public const string OcrStaging = "ocr_staging";
    public const string OcrAdopted = "ocr_adopted";

    public static bool IsKnown(string source) => source is Manual or Import or Mock or OcrStaging or OcrAdopted;
}
