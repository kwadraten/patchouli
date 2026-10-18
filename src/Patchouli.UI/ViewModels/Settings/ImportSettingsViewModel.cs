using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Settings;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>Issue #11 file handling behavior option for the settings UI.</summary>
public sealed class FileHandlingBehaviorOption
{
    public FileHandlingBehaviorOption(FileHandlingBehavior value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public FileHandlingBehavior Value { get; }

    public string DisplayName { get; }

    public static IReadOnlyList<FileHandlingBehaviorOption> All { get; } =
    [
        new(FileHandlingBehavior.KeepInPlace, "保留在原位置（默认）"),
        new(FileHandlingBehavior.MoveToFirstSearchRoot, "移动到第一个文件搜索根"),
    ];

    public static FileHandlingBehaviorOption FromValue(FileHandlingBehavior value)
    {
        return All.First(option => option.Value == value);
    }
}

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

        LoadBehaviorSettingsAsync().Observe("settings", "load-file-handling-behavior");
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

    // --- Issue #11 file handling behavior settings ("trigger action → behavior") ---

    /// <summary>The available behavior options shown in the UI.</summary>
    public IReadOnlyList<FileHandlingBehaviorOption> BehaviorOptions => FileHandlingBehaviorOption.All;

    [ObservableProperty]
    public partial FileHandlingBehaviorOption? McpAddBehaviorOption { get; set; }

    [ObservableProperty]
    public partial FileHandlingBehaviorOption? PdfDragDropBehaviorOption { get; set; }

    private bool _isLoadingBehavior;

    partial void OnMcpAddBehaviorOptionChanged(FileHandlingBehaviorOption? value)
    {
        OnBehaviorOptionChanged();
    }

    partial void OnPdfDragDropBehaviorOptionChanged(FileHandlingBehaviorOption? value)
    {
        OnBehaviorOptionChanged();
    }

    private void OnBehaviorOptionChanged()
    {
        if (_isConstructing || _isSyncing || _isLoadingBehavior)
        {
            return;
        }

        // Behavior settings save immediately (device-local system settings).
        SaveBehaviorSettingsAsync().Observe("settings", "save-file-handling-behavior");
    }

    private async Task LoadBehaviorSettingsAsync()
    {
        _isLoadingBehavior = true;
        try
        {
            FileHandlingBehaviorSettings settings = await _main.GetFileHandlingBehaviorAsync();
            McpAddBehaviorOption = FileHandlingBehaviorOption.FromValue(settings.McpAddPdfBehavior);
            PdfDragDropBehaviorOption = FileHandlingBehaviorOption.FromValue(settings.PdfDragDropBehavior);
        }
        finally
        {
            _isLoadingBehavior = false;
        }
    }

    private async Task SaveBehaviorSettingsAsync()
    {
        FileHandlingBehaviorSettings settings = new(
            McpAddBehaviorOption?.Value ?? FileHandlingBehavior.KeepInPlace,
            PdfDragDropBehaviorOption?.Value ?? FileHandlingBehavior.KeepInPlace);
        await _main.SaveFileHandlingBehaviorAsync(settings);
        // Re-sync the UI from what was actually persisted.
        await LoadBehaviorSettingsAsync();
    }
}
