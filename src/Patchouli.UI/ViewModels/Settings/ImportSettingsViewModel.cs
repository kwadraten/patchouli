using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「导入」section: configures the failed-page ratio threshold for whole-book imports.
/// The UI presents the ratio as a percent in [0, 90]; persistence stores the 0–0.9 double.
/// Once the settings page is open, edits persist automatically through the debounced
/// auto-save pipeline, like the other editable sections.</summary>
public sealed partial class ImportSettingsViewModel : SettingsSectionViewModelBase
{
    private const double MinRatioPercent = 0;
    private const double MaxRatioPercent = 90;

    private readonly MainWindowViewModel _main;
    private double _persistedPercent;
    private bool _isDirty;
    private bool _isConstructing;
    private bool _isSyncing;

    public ImportSettingsViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
        _main = main;
        _persistedPercent = main.AppOptions.Import.MaxFailedPageRatio * 100;
        MaxFailedPageRatioPercent = _persistedPercent;
        _isConstructing = false;
    }

    /// <summary>The failed-page ratio threshold in percent, clamped to [0, 90]. When the share of
    /// failed pages strictly exceeds the threshold the whole book import fails and rolls back; at
    /// or below it failed pages are imported as placeholder pages.</summary>
    [ObservableProperty]
    public partial double MaxFailedPageRatioPercent { get; set; }

    partial void OnMaxFailedPageRatioPercentChanged(double value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        double clamped = Math.Clamp(value, MinRatioPercent, MaxRatioPercent);
        if (value != clamped)
        {
            MaxFailedPageRatioPercent = clamped;
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

        double percent = MaxFailedPageRatioPercent;
        bool saved = await _main.SaveImportSettingsAsync(percent / 100.0);
        if (!saved)
        {
            LastError = "无法保存导入设置。";
            SaveState = SettingsSaveState.Failed;
            Status = "保存失败";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            return;
        }

        _persistedPercent = percent;
        UpdateDirtyState();
        LastError = null;
        SaveState = IsDirty ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
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
            _persistedPercent = _main.AppOptions.Import.MaxFailedPageRatio * 100;
            MaxFailedPageRatioPercent = _persistedPercent;
            _isDirty = false;
            Raise(nameof(MaxFailedPageRatioPercent));
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
        _isDirty = MaxFailedPageRatioPercent != _persistedPercent;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void MarkDirty(string message)
    {
        SaveState = SettingsSaveState.Dirty;
        Status = message;
    }
}
