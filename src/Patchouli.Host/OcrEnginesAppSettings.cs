using Patchouli.Ocr;

namespace Patchouli.UI;

public sealed record OcrEnginesAppSettings(
    string DocumentOcrEngine,
    string PageOcrEngine,
    string RegionOcrEngine)
{
    public static OcrEnginesAppSettings Default()
    {
        return new OcrEnginesAppSettings(OcrEngineIds.NdlKoten, OcrEngineIds.NdlKoten, OcrEngineIds.NdlKoten);
    }

    /// <summary>
    ///     Engine-per-scope selection retained for migration/default compatibility only; the
    ///     authoritative per-run choice is the stored preset selection.
    /// </summary>
    public string EngineFor(OcrScope scope)
    {
        return scope switch
        {
            OcrScope.Document => DocumentOcrEngine,
            OcrScope.Page => PageOcrEngine,
            OcrScope.Region => RegionOcrEngine,
            _ => DocumentOcrEngine
        };
    }
}

public enum OcrScope
{
    Document,
    Page,
    Region
}
