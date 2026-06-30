using Patchouli.Core.Layout;

namespace Patchouli.Ocr;

public interface IOcrEngine
{
    string EngineId { get; }

    Task<OcrEnginePageResult> RunPageAsync(
        Page page,
        OcrPresetVersion presetVersion,
        CancellationToken cancellationToken);
}
