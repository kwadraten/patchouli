using System.Collections.Generic;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「OCR 组件」settings tab: the three existing catalog owners rendered as three cards in
/// one tab. Each catalog keeps its own load/save lifecycle and catalog view.</summary>
public sealed class OcrComponentsSettingsViewModel : ViewModelBase
{
    public OcrComponentsSettingsViewModel(
        OcrComponentCatalogSettingsViewModel layoutModels,
        OcrComponentCatalogSettingsViewModel ocrEngines,
        OcrComponentCatalogSettingsViewModel textLayers)
    {
        LayoutModels = layoutModels;
        OcrEngines = ocrEngines;
        TextLayers = textLayers;
        Catalogs = [layoutModels, ocrEngines, textLayers];
    }

    public OcrComponentCatalogSettingsViewModel LayoutModels { get; }
    public OcrComponentCatalogSettingsViewModel OcrEngines { get; }
    public OcrComponentCatalogSettingsViewModel TextLayers { get; }
    public IReadOnlyList<OcrComponentCatalogSettingsViewModel> Catalogs { get; }
}
