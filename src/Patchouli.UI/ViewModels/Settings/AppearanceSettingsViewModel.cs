using System.Collections.ObjectModel;
using Avalonia.Media;
using Patchouli.UI.Themes;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「外观与显示」section: selects the UI color palette. The choice only takes effect
/// (and persists) through the header 保存设置 action, like the other editable sections.</summary>
public sealed class AppearanceSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private PaletteOption _selectedPalette;
    private string _persistedPaletteId;
    private bool _isDirty;

    public AppearanceSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        Palettes = Array.AsReadOnly(UiColorPalettes.All.Select(static palette => new PaletteOption(palette))
            .ToArray());
        _persistedPaletteId = UiColorPalettes.ResolveId(main.AppOptions.Ui.PaletteId);
        _selectedPalette = Palettes.First(option => option.PaletteId == _persistedPaletteId);
    }

    public ReadOnlyCollection<PaletteOption> Palettes { get; }

    public PaletteOption SelectedPalette
    {
        get => _selectedPalette;
        set
        {
            if (_selectedPalette.PaletteId == value.PaletteId)
            {
                return;
            }

            _selectedPalette = value;
            UpdateDirtyState();
            Raise();
            MarkDirty("有未保存的更改");
        }
    }

    public override bool SupportsEditing => true;
    public override bool IsDirty => _isDirty;
    public override bool CanSave => _isDirty && !IsSaving;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Unsaved drafts stay in memory when switching sections; only clean sections re-sync.
        if (!IsDirty)
        {
            SyncFromPersisted();
        }

        return Task.CompletedTask;
    }

    public override Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        bool saved = _main.SaveAppearancePalette(_selectedPalette.PaletteId);
        if (!saved)
        {
            LastError = "无法保存外观配色设置。";
            SaveState = SettingsSaveState.Failed;
            Status = "保存失败";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            return Task.CompletedTask;
        }

        _persistedPaletteId = _selectedPalette.PaletteId;
        _isDirty = false;
        LastError = null;
        SaveState = SettingsSaveState.Saved;
        ValidationState = SettingsValidationState.Valid;
        Status = "已保存";
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        SyncFromPersisted();
        SaveState = SettingsSaveState.Clean;
        Status = "";
        return Task.CompletedTask;
    }

    private void SyncFromPersisted()
    {
        _persistedPaletteId = UiColorPalettes.ResolveId(_main.AppOptions.Ui.PaletteId);
        _selectedPalette = Palettes.First(option => option.PaletteId == _persistedPaletteId);
        _isDirty = false;
        Raise(nameof(SelectedPalette));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void UpdateDirtyState()
    {
        _isDirty = _selectedPalette.PaletteId != _persistedPaletteId;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void MarkDirty(string message)
    {
        SaveState = SettingsSaveState.Dirty;
        Status = message;
    }
}

/// <summary>A selectable palette with swatch brushes for the picker and its preview.</summary>
public sealed class PaletteOption
{
    public PaletteOption(UiColorPalette palette)
    {
        PaletteId = palette.Id;
        DisplayName = palette.DisplayName;
        AccentBrush = BrushOf(palette, "PrimaryContainerColor");
        SurfaceBrush = BrushOf(palette, "SurfaceColor");
        TextBrush = BrushOf(palette, "OnSurfaceColor");
        TertiaryBrush = BrushOf(palette, "TertiaryColor");
    }

    public string PaletteId { get; }
    public string DisplayName { get; }
    public IBrush AccentBrush { get; }
    public IBrush SurfaceBrush { get; }
    public IBrush TextBrush { get; }
    public IBrush TertiaryBrush { get; }

    private static IBrush BrushOf(UiColorPalette palette, string colorKey)
    {
        return new SolidColorBrush(Color.Parse(palette.Colors[colorKey]));
    }
}
