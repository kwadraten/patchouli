using LiteratureApp.Core.Ids;

namespace LiteratureApp.Ocr;

public sealed record OcrPresetVersion(
    OcrPresetVersionId PresetVersionId,
    OcrPresetId PresetId,
    string EngineId,
    string ModelId,
    string? ModelPath,
    string ParametersJson,
    bool ApplyOnSuccess,
    DateTimeOffset CreatedAt);
