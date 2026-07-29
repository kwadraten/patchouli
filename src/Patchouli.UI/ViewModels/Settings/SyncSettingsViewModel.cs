using System.Text.Json;
using Patchouli.UI.ViewModels;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Core.Settings;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SyncSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private SyncAppSettings _persisted;
    private SyncAppSettings _draft;

    public SyncSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _persisted = main.AppOptions.Sync;
        _draft = _persisted;
        OpenSyncCenterCommand = new AsyncCommand(main.OpenSyncCenterAsync);
        PublishSnapshotCommand = new AsyncCommand(PublishSnapshotAsync);
        CheckIncomingSnapshotCommand = new AsyncCommand(CheckIncomingSnapshotAsync);
        ExportSnapshotPackageCommand = new AsyncCommand(ExportSnapshotPackageAsync);
        Status = "已保存";
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

    public bool SyncMetadataLookup
    {
        get => _draft.IsSettingEnabled(LibrarySettingKeys.MetadataLookup);
        set
        {
            if (_draft.IsSettingEnabled(LibrarySettingKeys.MetadataLookup) != value)
            {
                _draft = _draft.WithSettingEnabled(LibrarySettingKeys.MetadataLookup, value);
                MarkDirty();
                Raise();
                Raise(nameof(MetadataLookupScopeText));
                Raise(nameof(MetadataLookupEffectiveSourceText));
                Raise(nameof(MetadataLookupSchemaText));
            }
        }
    }

    public string MetadataLookupScopeText =>
        SyncMetadataLookup ? "随当前资料库的内容快照同步" : "仅此设备";

    public string MetadataLookupEffectiveSourceText =>
        SyncMetadataLookup ? "资料库 setting record" : "本机 JSON 设置";

    public string MetadataLookupSchemaText
    {
        get
        {
            SettingCatalogEntry entry = LibrarySettingCatalog.GetRequired(LibrarySettingKeys.MetadataLookup);
            return $"{entry.SettingKey} · schema {entry.SchemaVersion} · {entry.MergePolicy}";
        }
    }

    public AsyncCommand OpenSyncCenterCommand { get; }
    public AsyncCommand PublishSnapshotCommand { get; }
    public AsyncCommand CheckIncomingSnapshotCommand { get; }
    public AsyncCommand ExportSnapshotPackageCommand { get; }
    public string SnapshotOperationStateText => _main.Snapshot.OperationStateText;
    public string SnapshotOperationMessage => _main.Snapshot.OperationMessage;
    public override bool SupportsEditing => true;

    private bool _isDirty;

    public override bool IsDirty => _isDirty;

    public override bool CanSave => _isDirty && !string.IsNullOrWhiteSpace(_draft.SyncRoot);

    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        bool rootChanged = !string.Equals(
            Path.GetFullPath(_persisted.SyncRoot),
            Path.GetFullPath(_draft.SyncRoot),
            StringComparison.OrdinalIgnoreCase);
        SyncAppSettings savedDraft = rootChanged
            ? _draft with
            {
                SyncRootId = await ResolveSyncRootIdAsync(_draft.SyncRoot),
                SnapshotState = SnapshotSyncLocalState.NotConfigured
            }
            : _draft;
        bool syncMetadataLookup = savedDraft.IsSettingEnabled(LibrarySettingKeys.MetadataLookup);
        bool persistedSyncMetadataLookup = _persisted.IsSettingEnabled(LibrarySettingKeys.MetadataLookup);
        SettingsSaveResult result = syncMetadataLookup == persistedSyncMetadataLookup
            ? _main.UpdateAppOptions(_main.AppOptions with { Sync = savedDraft })
            : await _main.SetMetadataLookupSyncEnabledAsync(syncMetadataLookup);
        if (result.IsSuccess && syncMetadataLookup != persistedSyncMetadataLookup)
        {
            result = _main.UpdateAppOptions(_main.AppOptions with { Sync = savedDraft });
        }

        if (result.IsSuccess)
        {
            _persisted = savedDraft;
            _draft = savedDraft;
            _isDirty = false;
            LastError = null;
            SaveState = SettingsSaveState.Saved;
            Status = "已保存";
        }
        else
        {
            LastError = result.ErrorMessage;
            SaveState = SettingsSaveState.Failed;
            Status = $"保存失败：{result.ErrorMessage}";
        }

        RaiseState();
    }

    public override Task DiscardAsync()
    {
        _draft = _persisted;
        _isDirty = false;
        SaveState = SettingsSaveState.Clean;
        Status = "已放弃更改";
        Raise(nameof(DeviceName));
        Raise(nameof(SyncRoot));
        Raise(nameof(SyncMetadataLookup));
        Raise(nameof(MetadataLookupScopeText));
        Raise(nameof(MetadataLookupEffectiveSourceText));
        Raise(nameof(MetadataLookupSchemaText));
        RaiseState();
        return Task.CompletedTask;
    }

    private void MarkDirty()
    {
        _isDirty = true;
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
        RaiseState();
    }

    private void RaiseState()
    {
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
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

    private async Task ExportSnapshotPackageAsync()
    {
        if (!CanRunOperation())
        {
            return;
        }

        await _main.OpenSyncCenterAsync();
    }

    private bool CanRunOperation()
    {
        if (!IsDirty)
        {
            return true;
        }

        LastError = "请先保存或放弃同步设置，再执行同步操作。";
        Status = LastError;
        RaiseState();
        return false;
    }

    private static async Task<string> ResolveSyncRootIdAsync(string syncRoot)
    {
        try
        {
            SnapshotCurrentPointer? current = await SnapshotPublisher.ReadJsonAsync<SnapshotCurrentPointer>(
                Path.Combine(Path.GetFullPath(syncRoot), "current.json"),
                CancellationToken.None);
            if (current is not null && Guid.TryParse(current.SyncRootId, out Guid parsed))
            {
                return parsed.ToString("D");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _ = exception;
        }

        return Guid.NewGuid().ToString("D");
    }
}
