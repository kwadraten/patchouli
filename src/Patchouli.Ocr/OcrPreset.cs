using Patchouli.Core.Ids;

namespace Patchouli.Ocr;

public sealed record OcrPreset(
    OcrPresetId PresetId,
    LibraryId LibraryId,
    string Name,
    string? Description,
    OcrPresetVersionId? CurrentVersionId,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
