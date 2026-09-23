using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.Themes;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「外观与显示」section: selects the UI color palette and the reading-mode font family
/// and size. Once the settings page is open, edits persist automatically through the debounced
/// auto-save pipeline, like the other editable sections.</summary>
public sealed partial class AppearanceSettingsViewModel : SettingsSectionViewModelBase
{
    private const string SystemDefaultFontLabel = "系统默认";
    private const double MinReadingFontSize = 10;
    private const double MaxReadingFontSize = 28;

    private readonly MainWindowViewModel _main;
    private string _persistedPaletteId;
    private string _persistedFontFamily;
    private double _persistedFontSize;
    private IReadOnlyList<string>? _readingFontFamilies;
    private bool _isDirty;
    private bool _isConstructing;
    private bool _isSyncing;

    public AppearanceSettingsViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
        _main = main;
        Palettes = Array.AsReadOnly(UiColorPalettes.All.Select(static palette => new PaletteOption(palette))
            .ToArray());
        _persistedPaletteId = UiColorPalettes.ResolveId(main.AppOptions.Ui.PaletteId);
        SelectedPalette = Palettes.First(option => option.PaletteId == _persistedPaletteId);
        _persistedFontFamily = NormalizeFontFamily(main.AppOptions.Ui.ReadingFontFamily);
        SelectedReadingFontFamily = _persistedFontFamily;
        _persistedFontSize = ClampReadingFontSize(main.AppOptions.Ui.ReadingFontSize);
        ReadingFontSize = _persistedFontSize;
        _isConstructing = false;
    }

    public ReadOnlyCollection<PaletteOption> Palettes { get; }

    [ObservableProperty] public partial PaletteOption SelectedPalette { get; set; } = null!;

    partial void OnSelectedPaletteChanged(PaletteOption value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        UpdateDirtyState();
        MarkDirty("有未保存的更改");
    }

    /// <summary>Selectable display labels for the reading font family. The first entry is the
    /// localized 「系统默认」 placeholder whose persisted value is the empty string; the rest are
    /// the host's system font family names, de-duplicated and sorted. The list is built lazily on
    /// first access so that constructing the section never touches the Avalonia font manager.</summary>
    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<string> ReadingFontFamilies
    {
        get
        {
            if (_readingFontFamilies is null)
            {
                _readingFontFamilies = BuildReadingFontFamilies();
            }

            return _readingFontFamilies;
        }
    }

    /// <summary>The persisted font family name for reading mode. An empty string means the system
    /// default font; any other value is a concrete family name.</summary>
    [ObservableProperty]
    public partial string SelectedReadingFontFamily { get; set; } = "";

    partial void OnSelectedReadingFontFamilyChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string normalized = NormalizeFontFamily(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            SelectedReadingFontFamily = normalized;
            return;
        }

        UpdateDirtyState();
        MarkDirty("有未保存的更改");
    }

    /// <summary>The reading-mode font size, clamped to the supported [10, 28] range.</summary>
    [ObservableProperty]
    public partial double ReadingFontSize { get; set; }

    partial void OnReadingFontSizeChanged(double value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        double clamped = ClampReadingFontSize(value);
        if (value != clamped)
        {
            ReadingFontSize = clamped;
            return;
        }

        UpdateDirtyState();
        MarkDirty("有未保存的更改");
    }

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty && !IsSaving;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Unsaved drafts stay in memory when switching sections; only clean sections re-sync.
        if (!IsDirty)
        {
            SyncFromPersisted();
        }

        return Task.CompletedTask;
    }

    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";

        bool paletteChanged = SelectedPalette.PaletteId != _persistedPaletteId;
        if (paletteChanged)
        {
            bool paletteSaved = await _main.SaveAppearancePaletteAsync(SelectedPalette.PaletteId);
            if (!paletteSaved)
            {
                LastError = "无法保存外观设置。";
                SaveState = SettingsSaveState.Failed;
                Status = "保存失败";
                Raise(nameof(IsDirty));
                Raise(nameof(CanSave));
                return;
            }

            _persistedPaletteId = SelectedPalette.PaletteId;
        }

        bool fontChanged = !string.Equals(SelectedReadingFontFamily, _persistedFontFamily, StringComparison.Ordinal)
                           || ReadingFontSize != _persistedFontSize;
        if (fontChanged)
        {
            bool fontSaved = await _main.SaveReadingFontImmediatelyAsync(SelectedReadingFontFamily, ReadingFontSize);
            if (!fontSaved)
            {
                LastError = "无法保存外观设置。";
                SaveState = SettingsSaveState.Failed;
                Status = "保存失败";
                Raise(nameof(IsDirty));
                Raise(nameof(CanSave));
                return;
            }

            _persistedFontFamily = SelectedReadingFontFamily;
            _persistedFontSize = ReadingFontSize;
        }

        _isDirty = false;
        LastError = null;
        SaveState = SettingsSaveState.Saved;
        ValidationState = SettingsValidationState.Valid;
        Status = "已保存";
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
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
        _isSyncing = true;
        try
        {
            _persistedPaletteId = UiColorPalettes.ResolveId(_main.AppOptions.Ui.PaletteId);
            SelectedPalette = Palettes.First(option => option.PaletteId == _persistedPaletteId);
            _persistedFontFamily = NormalizeFontFamily(_main.AppOptions.Ui.ReadingFontFamily);
            SelectedReadingFontFamily = _persistedFontFamily;
            _persistedFontSize = ClampReadingFontSize(_main.AppOptions.Ui.ReadingFontSize);
            ReadingFontSize = _persistedFontSize;
            _isDirty = false;
            Raise(nameof(SelectedPalette));
            Raise(nameof(SelectedReadingFontFamily));
            Raise(nameof(ReadingFontSize));
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void UpdateDirtyState()
    {
        bool paletteDirty = SelectedPalette.PaletteId != _persistedPaletteId;
        bool fontDirty =
            !string.Equals(SelectedReadingFontFamily, _persistedFontFamily, StringComparison.Ordinal);
        bool sizeDirty = ReadingFontSize != _persistedFontSize;
        _isDirty = paletteDirty || fontDirty || sizeDirty;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void MarkDirty(string message)
    {
        SaveState = SettingsSaveState.Dirty;
        Status = message;
    }

    private static IReadOnlyList<string> BuildReadingFontFamilies()
    {
        List<string> names = [SystemDefaultFontLabel];
        try
        {
            List<string> systemFonts = FontManager.Current.SystemFonts
                .Select(static font => font.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToList();
            names.AddRange(systemFonts);
        }
        catch (InvalidOperationException)
        {
            // Headless or uninitialized platforms expose no font manager; the system-default
            // entry stays as the only choice until the UI host initializes Avalonia.
        }

        return names.AsReadOnly();
    }

    private static double ClampReadingFontSize(double value)
    {
        return Math.Clamp(value, MinReadingFontSize, MaxReadingFontSize);
    }

    private static string NormalizeFontFamily(string? value)
    {
        return (value ?? string.Empty).Trim();
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
