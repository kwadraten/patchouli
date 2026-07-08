using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

public sealed class UnavailableOcrEngine : IOcrEngine
{
    public string EngineId => "unavailable";

    public Task<OcrEnginePageResult> RunPageAsync(
        Page page,
        OcrPresetVersion presetVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OcrEnginePageResult(
            page.PageId,
            false,
            null,
            null,
            "ocr_engine_unavailable",
            "Page-level mock OCR is disabled in product mode."));
    }
}
