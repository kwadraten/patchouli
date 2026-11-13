using Patchouli.Ocr;

namespace Patchouli.UI;

public sealed record OcrEnginesAppSettings(
    string DocumentOcrEngine,
    string PageOcrEngine,
    string RegionOcrEngine)
{
    public static OcrEnginesAppSettings Default()
    {
        // First-run setup requires a MinerU token, so the implicit engine is usable from the
        // first session without an additional local-model download.
        return new OcrEnginesAppSettings(OcrEngineIds.MinerU, OcrEngineIds.MinerU, OcrEngineIds.MinerU);
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
