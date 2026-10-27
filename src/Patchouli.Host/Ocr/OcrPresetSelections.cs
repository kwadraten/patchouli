using Patchouli.UI;

namespace Patchouli.Host;

/// <summary>
///     One library's device-local OCR preset selections, keyed by <see cref="OcrScope" />. Ids are
///     stored as strings; the selection service validates them against the preset table and the
///     active library before any enqueue uses them. Null per scope means "no explicit choice —
///     resolve the implicit default".
/// </summary>
public sealed record OcrPresetSelections(
    string? DocumentPresetId = null,
    string? PagePresetId = null,
    string? RegionPresetId = null)
{
    public static readonly OcrPresetSelections Empty = new();

    public string? PresetIdFor(OcrScope scope)
    {
        return scope switch
        {
            OcrScope.Document => DocumentPresetId,
            OcrScope.Page => PagePresetId,
            OcrScope.Region => RegionPresetId,
            _ => null
        };
    }

    public OcrPresetSelections WithPreset(OcrScope scope, string? presetId)
    {
        string? normalized = string.IsNullOrWhiteSpace(presetId) ? null : presetId.Trim();
        return scope switch
        {
            OcrScope.Document => this with { DocumentPresetId = normalized },
            OcrScope.Page => this with { PagePresetId = normalized },
            OcrScope.Region => this with { RegionPresetId = normalized },
            _ => this
        };
    }
}
