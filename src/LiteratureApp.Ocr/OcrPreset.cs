using LiteratureApp.Core.Ids;

namespace LiteratureApp.Ocr;

public sealed record OcrPreset(
    OcrPresetId PresetId,
    LibraryId LibraryId,
    string Name,
    string? Description,
    OcrPresetVersionId? CurrentVersionId,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
