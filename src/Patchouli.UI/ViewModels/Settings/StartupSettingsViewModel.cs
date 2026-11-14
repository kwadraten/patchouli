using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>「启动」settings tab: the startup behavior that used to live inside the Library
/// device card. It owns only the <c>Runtime</c> settings category so saving it never rewrites
/// scanning rules or unrelated sections.</summary>
public sealed partial class StartupSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isConstructing;
    private bool _isSyncing;
    private bool _isDirty;
    private long _editRevision;

    public StartupSettingsViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
        _main = main;
        RememberLastDatabase = main.AppOptions.Runtime.RememberLastDatabase;
        _isConstructing = false;
    }

    [ObservableProperty] public partial bool RememberLastDatabase { get; set; }

    partial void OnRememberLastDatabaseChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        MarkDirty();
    }

    [ExcludeFromDerivedGeneration] public string RuntimeDatabasePath => _main.RuntimeDatabasePath;

    [ExcludeFromDerivedGeneration] public string DefaultSyncRootPath => _main.DefaultSyncRootPath;

    public void NotifyRuntimeDatabasePathChanged()
    {
        Raise(nameof(RuntimeDatabasePath));
    }

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!_isDirty)
        {
            _isSyncing = true;
            try
            {
                RememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
            }
            finally
            {
                _isSyncing = false;
            }
        }

        Raise(nameof(RuntimeDatabasePath));
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        _isSyncing = true;
        try
        {
            RememberLastDatabase = _main.AppOptions.Runtime.RememberLastDatabase;
        }
        finally
        {
            _isSyncing = false;
        }

        _isDirty = false;
        SaveState = SettingsSaveState.Clean;
        LastError = null;
        SetStatus("已放弃更改");
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        return Task.CompletedTask;
    }

    public override async Task SaveAsync()
    {
        long revision = _editRevision;
        AppRuntimeOptions runtime = _main.AppOptions.Runtime with
        {
            RememberLastDatabase = RememberLastDatabase
        };
        if (RememberLastDatabase && !string.IsNullOrWhiteSpace(_main.RuntimeDatabasePath))
        {
            runtime = runtime with { RuntimeDatabasePath = Path.GetFullPath(_main.RuntimeDatabasePath) };
        }

        SaveState = SettingsSaveState.Saving;
        SetStatus("正在保存...");
        SettingsSaveResult saved = await _main.UpdateAppOptionsAsync(
            _main.AppOptions with { Runtime = runtime }, "Runtime");
        if (saved.IsSuccess)
        {
            _isDirty = revision != _editRevision;
            SaveState = IsDirty ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
            ValidationState = SettingsValidationState.Valid;
            LastError = null;
            SetStatus(IsDirty ? "有未保存的更改" : "已保存");
        }
        else
        {
            SaveState = SettingsSaveState.Failed;
            LastError = saved.ErrorMessage;
            ValidationState = SettingsValidationState.Valid;
            SetStatus($"保存失败：{saved.ErrorMessage}");
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void MarkDirty()
    {
        _editRevision++;
        _isDirty = true;
        SaveState = SettingsSaveState.Dirty;
        LastError = null;
        SetStatus("有未保存的更改");
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void SetStatus(string text)
    {
        Status = text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _main.Report(text);
        }
    }
}
