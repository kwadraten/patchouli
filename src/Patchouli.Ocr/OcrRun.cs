using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrRun(
    OcrRunId OcrRunId,
    DocumentInstanceId DocumentInstanceId,
    OcrPresetId PresetId,
    OcrPresetVersionId PresetVersionId,
    string EngineId,
    string ModelId,
    string ParametersSnapshotJson,
    LayoutRevisionId? SourceRevisionId,
    LayoutRevisionId? OutputRevisionId,
    OcrRunId? RetryOfRunId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
