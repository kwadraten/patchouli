using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SyncSettingsViewModel : ViewModelBase, ISettingsSection
{
    private readonly MainWindowViewModel _main;
    private SyncAppSettings _persisted;
    private SyncAppSettings _draft;
    private string _status = "已保存";

    public SyncSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _persisted = main.AppOptions.Sync;
        _draft = _persisted;
        OpenSyncCenterCommand = main.CheckSyncStateCommand;
    }

    public string DeviceId => _draft.DeviceId;
    public string DeviceName
    {
        get => _draft.DeviceName;
        set { if (_draft.DeviceName != value) { _draft = _draft with { DeviceName = value }; MarkDirty(); Raise(); } }
    }
    public string SyncRoot
    {
        get => _draft.SyncRoot;
        set { if (_draft.SyncRoot != value) { _draft = _draft with { SyncRoot = value }; MarkDirty(); Raise(); } }
    }
    public bool SyncMcpSettings
    {
        get => _draft.SyncMcpSettings;
        set { if (_draft.SyncMcpSettings != value) { _draft = _draft with { SyncMcpSettings = value }; MarkDirty(); Raise(); } }
    }
    public bool SyncProviderCredentials
    {
        get => _draft.SyncProviderCredentials;
        set { if (_draft.SyncProviderCredentials != value) { _draft = _draft with { SyncProviderCredentials = value }; MarkDirty(); Raise(); } }
    }
    public bool SyncMetadataLookup
    {
        get => _draft.SyncMetadataLookup;
        set { if (_draft.SyncMetadataLookup != value) { _draft = _draft with { SyncMetadataLookup = value }; MarkDirty(); Raise(); } }
    }

    public AsyncCommand OpenSyncCenterCommand { get; }
    public bool SupportsEditing => true;
    public bool IsDirty { get; private set; }
    public bool CanSave => IsDirty && !string.IsNullOrWhiteSpace(_draft.SyncRoot);
    public string SaveStateText => _status;
    public string? LastError { get; private set; }

    public Task SaveAsync()
    {
        SettingsSaveResult result = _main.UpdateAppOptions(_main.AppOptions with { Sync = _draft });
        if (result.IsSuccess)
        {
            _persisted = _draft;
            IsDirty = false;
            LastError = null;
            _status = "已保存";
        }
        else
        {
            LastError = result.ErrorMessage;
            _status = $"保存失败：{result.ErrorMessage}";
        }
        RaiseState();
        return Task.CompletedTask;
    }

    public Task DiscardAsync()
    {
        _draft = _persisted;
        IsDirty = false;
        _status = "已放弃更改";
        Raise(nameof(DeviceName)); Raise(nameof(SyncRoot)); Raise(nameof(SyncMcpSettings)); Raise(nameof(SyncProviderCredentials));
        Raise(nameof(SyncMetadataLookup)); RaiseState();
        return Task.CompletedTask;
    }

    private void MarkDirty()
    {
        IsDirty = true;
        _status = "有未保存的更改";
        RaiseState();
    }

    private void RaiseState()
    {
        Raise(nameof(IsDirty)); Raise(nameof(CanSave)); Raise(nameof(SaveStateText)); Raise(nameof(LastError));
    }
}
