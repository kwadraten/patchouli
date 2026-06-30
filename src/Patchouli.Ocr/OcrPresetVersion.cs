using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrPresetVersion(
    OcrPresetVersionId PresetVersionId,
    OcrPresetId PresetId,
    string EngineId,
    string ModelId,
    string? ModelPath,
    string ParametersJson,
    bool ApplyOnSuccess,
    DateTimeOffset CreatedAt);
