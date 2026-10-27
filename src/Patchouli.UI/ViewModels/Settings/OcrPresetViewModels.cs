using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
///     A selectable OCR preset for a scope picker. <see cref="OcrPresetOption.Automatic" /> means
///     "resolve the legacy engine default at run time" so existing behavior stays backward
///     compatible when the user never picks a preset.
/// </summary>
public sealed record OcrPresetOption(
    string PresetId,
    string DisplayName,
    string EngineId,
    string? VersionSummary = null,
    bool IsUnavailable = false)
{
    public static readonly OcrPresetOption Automatic = new(string.Empty, "自动（跟随引擎默认）", "");

    /// <summary>A stored choice that no longer resolves — displayed, never silently reassigned.</summary>
    public static OcrPresetOption Unavailable(string presetId)
    {
        string shortId = presetId.Length > 8 ? presetId[..8] : presetId;
        return new OcrPresetOption(presetId, $"不可用（{shortId}…）", "", IsUnavailable: true);
    }
}

/// <summary>A text-layer provider choice on a preset row (options come from registry metadata).</summary>
public sealed record OcrTextLayerProviderOption(
    string ProviderId,
    string DisplayName,
    bool IsUnavailable = false)
{
    /// <summary>Host-only "no text-layer" marker — never requires an extension package.</summary>
    public static readonly OcrTextLayerProviderOption Disabled = new("disabled", "不使用文本层");

    /// <summary>A stored provider that the registry no longer offers — preserved, never hidden.</summary>
    public static OcrTextLayerProviderOption Unavailable(string providerId)
    {
        return new OcrTextLayerProviderOption(providerId, $"不可用（{providerId}）",
            true);
    }
}

/// <summary>
///     One image-OCR engine choice on a preset row; options come from
///     <c>IOcrEngineCatalog</c> registry metadata, never <c>OcrAdapters</c>.
/// </summary>
public sealed class OcrEngineOption
{
    public OcrEngineOption(string engineId, string displayName, bool isUnavailable = false)
    {
        EngineId = engineId;
        DisplayName = displayName;
        IsUnavailable = isUnavailable;
    }

    public string EngineId { get; }
    public string DisplayName { get; }
    public bool IsUnavailable { get; }

    /// <summary>A stored engine that the registry no longer offers — preserved, never hidden.</summary>
    public static OcrEngineOption Unavailable(string engineId)
    {
        return new OcrEngineOption(engineId, $"不可用（{engineId}）", true);
    }
}

/// <summary>
///     Per-scope preset picker used by settings and enqueue entrypoints. Stores the chosen preset
///     UUID (or empty for automatic) per library/scope; persistence is owned by
///     <c>OcrPresetSelectionService</c> callers, not this view-model.
/// </summary>
public sealed partial class OcrPresetScopePickerViewModel : ViewModelBase
{
    public OcrPresetScopePickerViewModel(OcrScope scope, string scopeLabel)
    {
        Scope = scope;
        ScopeLabel = scopeLabel;
        Options = new ObservableCollection<OcrPresetOption> { OcrPresetOption.Automatic };
    }

    public OcrScope Scope { get; }
    public string ScopeLabel { get; }
    public ObservableCollection<OcrPresetOption> Options { get; }
    [ObservableProperty] public partial OcrPresetOption SelectedOption { get; set; } = OcrPresetOption.Automatic;

    /// <summary>Reloads option rows; keeps the previously stored preset id selected when present.</summary>
    public void Apply(IEnumerable<OcrPresetOption> presets, string? storedPresetId)
    {
        Options.Clear();
        Options.Add(OcrPresetOption.Automatic);
        foreach (OcrPresetOption option in presets)
        {
            Options.Add(option);
        }

        OcrPresetOption? match = Options.FirstOrDefault(o =>
            string.Equals(o.PresetId, storedPresetId ?? string.Empty, StringComparison.Ordinal));
        // A stored id that no longer resolves stays selected as an explicit "unavailable" row —
        // saving keeps the stored value untouched for that scope instead of reassigning it to
        // Automatic or the latest same-engine preset behind the user's back.
        if (match is null && !string.IsNullOrWhiteSpace(storedPresetId))
        {
            match = OcrPresetOption.Unavailable(storedPresetId);
            Options.Add(match);
        }

        SelectedOption = match ?? OcrPresetOption.Automatic;
    }
}
