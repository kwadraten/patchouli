using Patchouli.UI.ViewModels;
using Patchouli.Infrastructure.Snapshots;

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
        OpenSyncCenterCommand = new AsyncCommand(main.OpenSyncCenterAsync);
        PublishSnapshotCommand = new AsyncCommand(PublishSnapshotAsync);
        CheckIncomingSnapshotCommand = new AsyncCommand(CheckIncomingSnapshotAsync);
    }

    public string DeviceId => _draft.DeviceId;

    public string DeviceName
    {
        get => _draft.DeviceName;
        set
        {
            if (_draft.DeviceName != value)
            {
                _draft = _draft with { DeviceName = value };
                MarkDirty();
                Raise();
            }
        }
    }

    public string SyncRoot
    {
        get => _draft.SyncRoot;
        set
        {
            if (_draft.SyncRoot != value)
            {
                _draft = _draft with { SyncRoot = value };
                MarkDirty();
                Raise();
            }
        }
    }

    public bool SyncMcpSettings
    {
        get => _draft.SyncMcpSettings;
        set
        {
            if (_draft.SyncMcpSettings != value)
            {
                _draft = _draft with { SyncMcpSettings = value };
                MarkDirty();
                Raise();
            }
        }
    }

    public bool SyncProviderCredentials
    {
        get => _draft.SyncProviderCredentials;
        set
        {
            if (_draft.SyncProviderCredentials != value)
            {
                _draft = _draft with { SyncProviderCredentials = value };
                MarkDirty();
                Raise();
            }
        }
    }

    public bool SyncMetadataLookup
    {
        get => _draft.SyncMetadataLookup;
        set
        {
            if (_draft.SyncMetadataLookup != value)
            {
                _draft = _draft with { SyncMetadataLookup = value };
                MarkDirty();
                Raise();
            }
        }
    }

    public AsyncCommand OpenSyncCenterCommand { get; }
    public AsyncCommand PublishSnapshotCommand { get; }
    public AsyncCommand CheckIncomingSnapshotCommand { get; }
    public string SnapshotOperationStateText => _main.Snapshot.OperationStateText;
    public string SnapshotOperationMessage => _main.Snapshot.OperationMessage;
    public bool SupportsEditing => true;
    public bool IsDirty { get; private set; }
    public bool CanSave => IsDirty && !string.IsNullOrWhiteSpace(_draft.SyncRoot);
    public string SaveStateText => _status;
    public string? LastError { get; private set; }

    public async Task SaveAsync()
    {
        _status = "正在保存...";
        Raise(nameof(SaveStateText));
        bool rootChanged = !string.Equals(
            Path.GetFullPath(_persisted.SyncRoot),
            Path.GetFullPath(_draft.SyncRoot),
            StringComparison.OrdinalIgnoreCase);
        SyncAppSettings savedDraft = rootChanged
            ? _draft with
            {
                SyncRootId = Guid.NewGuid().ToString("D"),
                SnapshotState = SnapshotSyncLocalState.NotConfigured
            }
            : _draft;
        SettingsSaveResult result = savedDraft.SyncMetadataLookup == _persisted.SyncMetadataLookup
            ? _main.UpdateAppOptions(_main.AppOptions with { Sync = savedDraft })
            : await _main.SetMetadataLookupSyncEnabledAsync(savedDraft.SyncMetadataLookup);
        if (result.IsSuccess && savedDraft.SyncMetadataLookup != _persisted.SyncMetadataLookup)
        {
            result = _main.UpdateAppOptions(_main.AppOptions with { Sync = savedDraft });
        }

        if (result.IsSuccess)
        {
            _persisted = savedDraft;
            _draft = savedDraft;
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
    }

    public Task DiscardAsync()
    {
        _draft = _persisted;
        IsDirty = false;
        _status = "已放弃更改";
        Raise(nameof(DeviceName));
        Raise(nameof(SyncRoot));
        Raise(nameof(SyncMcpSettings));
        Raise(nameof(SyncProviderCredentials));
        Raise(nameof(SyncMetadataLookup));
        RaiseState();
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
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Raise(nameof(SaveStateText));
        Raise(nameof(LastError));
    }

    public void NotifySnapshotStateChanged()
    {
        Raise(nameof(SnapshotOperationStateText));
        Raise(nameof(SnapshotOperationMessage));
    }

    private async Task PublishSnapshotAsync()
    {
        if (!CanRunOperation())
        {
            return;
        }

        await _main.OpenSyncCenterAsync();
        await _main.Snapshot.PublishCommand.ExecuteAsync();
    }

    private async Task CheckIncomingSnapshotAsync()
    {
        if (!CanRunOperation())
        {
            return;
        }

        await _main.OpenSyncCenterAsync();
        await _main.Snapshot.CheckCurrentCommand.ExecuteAsync();
    }

    private bool CanRunOperation()
    {
        if (!IsDirty)
        {
            return true;
        }

        LastError = "请先保存或放弃同步设置，再执行同步操作。";
        _status = LastError;
        RaiseState();
        return false;
    }
}
