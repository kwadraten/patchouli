using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Ocr;

public interface IOcrPresetService
{
    Task<Result<OcrPreset>> CreatePresetAsync(string name, string? description, string engineId, string modelId, string? modelPath, string parametersJson, bool applyOnSuccess, CancellationToken cancellationToken = default);
    Task<Result<OcrPreset>> GetPresetAsync(OcrPresetId presetId, CancellationToken cancellationToken = default);
    Task<Result<OcrPresetVersion>> CreatePresetVersionAsync(OcrPresetId presetId, string engineId, string modelId, string? modelPath, string parametersJson, bool applyOnSuccess, CancellationToken cancellationToken = default);
    Task<Result<OcrPresetVersion>> GetCurrentVersionAsync(OcrPresetId presetId, CancellationToken cancellationToken = default);
    Task<Result<OcrPresetVersion>> RebindModelPathAsync(OcrPresetId presetId, string newModelPath, CancellationToken cancellationToken = default);
    Task<Result> ArchivePresetAsync(OcrPresetId presetId, CancellationToken cancellationToken = default);
}
