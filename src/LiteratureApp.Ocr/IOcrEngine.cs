using LiteratureApp.Core.Layout;

namespace LiteratureApp.Ocr;

public interface IOcrEngine
{
    string EngineId { get; }

    Task<OcrEnginePageResult> RunPageAsync(
        Page page,
        OcrPresetVersion presetVersion,
        CancellationToken cancellationToken);
}
