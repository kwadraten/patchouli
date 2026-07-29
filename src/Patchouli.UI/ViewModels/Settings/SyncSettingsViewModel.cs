using System.Text.Json;
using System.Collections.ObjectModel;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Core.Settings;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SyncSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private SyncAppSettings _persisted;
    private SyncAppSettings _draft;
    private LibraryId? _libraryId;
    private readonly ObservableCollection<SyncSettingScopeRowViewModel> _settingScopeRows = new();

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
        RefreshScopeRows();
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
                RefreshScopeRows();
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
    public ObservableCollection<SyncSettingScopeRowViewModel> SettingScopeRows => _settingScopeRows;
    public string SnapshotOperationStateText => _main.Snapshot.OperationStateText;
    public string SnapshotOperationMessage => _main.Snapshot.OperationMessage;
    public override bool SupportsEditing => true;

    private bool _isDirty;

    public override bool IsDirty => _isDirty;

    public override bool CanSave => _isDirty && !string.IsNullOrWhiteSpace(_draft.SyncRoot);

    public override async Task SaveAsync()
    {
        Result<LibraryId> library = await EnsureLibraryIdAsync();
        if (library.IsFailure)
        {
            LastError = library.ErrorMessage;
            SaveState = SettingsSaveState.Failed;
            Status = $"保存失败：{library.ErrorMessage}";
            RaiseState();
            return;
        }

        if (string.IsNullOrWhiteSpace(_draft.SyncRoot))
        {
            LastError = "请先选择本机同步目录。";
            SaveState = SettingsSaveState.Failed;
            Status = $"保存失败：{LastError}";
            RaiseState();
            return;
        }

        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        bool rootChanged = string.IsNullOrWhiteSpace(_persisted.SyncRoot) ||
                           !string.Equals(
                               Path.GetFullPath(_persisted.SyncRoot),
                               Path.GetFullPath(_draft.SyncRoot),
                               StringComparison.OrdinalIgnoreCase);
        string logicalRootId = rootChanged || string.IsNullOrWhiteSpace(_persisted.SyncRootId)
            ? await ResolveSyncRootIdAsync(_draft.SyncRoot)
            : _persisted.SyncRootId;
        IReadOnlyList<string> enabledKeys = _draft.EnabledSettingKeys;
        SnapshotSyncLocalState snapshotState = rootChanged
            ? SnapshotSyncLocalState.NotConfigured
            : _persisted.SnapshotState ?? SnapshotSyncLocalState.NotConfigured;
        DeviceRootBindingAppSettings binding = new(
            library.Value.ToString(),
            LogicalRootKinds.SyncRoot,
            logicalRootId,
            _draft.DeviceId,
            Path.GetFullPath(_draft.SyncRoot),
            "settings_ui",
            Directory.Exists(_draft.SyncRoot),
            FileSearchRootAuthorizationKinds.None,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            snapshotState,
            enabledKeys);
        SyncAppSettings savedDraft = _draft.WithDeviceBinding(binding) with
        {
            SyncRoot = binding.LocalPath,
            SyncRootId = binding.LogicalRootId,
            SnapshotState = binding.SnapshotState,
            SyncedSettingKeys = enabledKeys,
            SyncMetadataLookup = enabledKeys.Contains(LibrarySettingKeys.MetadataLookup, StringComparer.Ordinal)
        };
        bool syncMetadataLookup = savedDraft.IsSettingEnabled(LibrarySettingKeys.MetadataLookup);
        bool persistedSyncMetadataLookup = _persisted.IsSettingEnabled(LibrarySettingKeys.MetadataLookup);
        SettingsSaveResult result = syncMetadataLookup == persistedSyncMetadataLookup
            ? _main.UpdateAppOptions(_main.AppOptions with { Sync = savedDraft })
            : await _main.SetMetadataLookupSyncEnabledAsync(syncMetadataLookup, savedDraft);

        if (result.IsSuccess)
        {
            _persisted = ToLibraryDraft(savedDraft, binding);
            _draft = _persisted;
            _isDirty = false;
            LastError = null;
            SaveState = SettingsSaveState.Saved;
            Status = "已保存";
            RefreshScopeRows();
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
        RefreshScopeRows();
        RaiseState();
        return Task.CompletedTask;
    }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsDirty)
        {
            Status = "同步设置有未保存的更改，已保留当前草稿。";
            return;
        }

        Result<LibraryId> library = await EnsureLibraryIdAsync(cancellationToken);
        if (library.IsFailure)
        {
            LastError = library.ErrorMessage;
            SaveState = SettingsSaveState.Failed;
            Status = $"加载失败：{library.ErrorMessage}";
            RaiseState();
            return;
        }

        SyncAppSettings sync = _main.AppOptions.Sync;
        DeviceRootBindingAppSettings? binding = sync.CurrentSyncRootBinding(library.Value);
        _persisted = ToLibraryDraft(sync, binding);
        _draft = _persisted;
        _isDirty = false;
        LastError = null;
        SaveState = SettingsSaveState.Clean;
        Status = "已加载同步设置";
        Raise(nameof(DeviceName));
        Raise(nameof(SyncRoot));
        Raise(nameof(SyncMetadataLookup));
        Raise(nameof(MetadataLookupScopeText));
        Raise(nameof(MetadataLookupEffectiveSourceText));
        Raise(nameof(MetadataLookupSchemaText));
        RefreshScopeRows();
        RaiseState();
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

    private async Task<Result<LibraryId>> EnsureLibraryIdAsync(CancellationToken cancellationToken = default)
    {
        if (_libraryId is not null)
        {
            return Result<LibraryId>.Success(_libraryId.Value);
        }

        AppServices services = await _main.ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<LibraryId>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        _libraryId = library.Value.LibraryId;
        return Result<LibraryId>.Success(_libraryId.Value);
    }

    private static SyncAppSettings ToLibraryDraft(
        SyncAppSettings sync,
        DeviceRootBindingAppSettings? binding)
    {
        if (binding is null)
        {
            return sync with
            {
                SyncRoot = "",
                SyncRootId = "",
                SnapshotState = SnapshotSyncLocalState.NotConfigured,
                SyncedSettingKeys = [],
                SyncMetadataLookup = false
            };
        }

        IReadOnlyList<string> enabledKeys = LibrarySettingCatalog.NormalizeSnapshotKeys(binding.SyncedSettingKeys);
        return sync with
        {
            SyncRoot = binding.LocalPath,
            SyncRootId = binding.LogicalRootId,
            SnapshotState = binding.SnapshotState ?? SnapshotSyncLocalState.NotConfigured,
            SyncedSettingKeys = enabledKeys,
            SyncMetadataLookup = enabledKeys.Contains(LibrarySettingKeys.MetadataLookup, StringComparer.Ordinal)
        };
    }

    private void RefreshScopeRows()
    {
        _settingScopeRows.Clear();
        foreach (SettingCatalogEntry entry in LibrarySettingCatalog.All)
        {
            bool enabled = entry.IsSnapshotEligible &&
                           _draft.EnabledSettingKeys.Contains(entry.SettingKey, StringComparer.Ordinal);
            _settingScopeRows.Add(new SyncSettingScopeRowViewModel(entry, enabled));
        }

        Raise(nameof(SettingScopeRows));
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
            if (current is not null && !string.IsNullOrWhiteSpace(current.SyncRootId))
            {
                return current.SyncRootId.Trim();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _ = exception;
        }

        return Guid.NewGuid().ToString("D");
    }
}

public sealed class SyncSettingScopeRowViewModel
{
    public SyncSettingScopeRowViewModel(SettingCatalogEntry entry, bool enabled)
    {
        SettingKey = entry.SettingKey;
        DisplayName = DisplayNameFor(entry.SettingKey);
        AllowedSyncText = entry.IsSnapshotEligible ? "允许随库同步" : "不允许同步";
        EnabledText = entry.IsSnapshotEligible ? enabled ? "已启用" : "未启用" : "排除";
        OwnerText = OwnerTextFor(entry);
        SourceText = SourceTextFor(entry, enabled);
        SchemaText = $"schema {entry.SchemaVersion} · {entry.MergePolicy}";
    }

    public string SettingKey { get; }
    public string DisplayName { get; }
    public string AllowedSyncText { get; }
    public string EnabledText { get; }
    public string OwnerText { get; }
    public string SourceText { get; }
    public string SchemaText { get; }

    private static string DisplayNameFor(string settingKey)
    {
        return settingKey switch
        {
            LibrarySettingKeys.MetadataLookup => "元数据来源优先级",
            "runtime" => "运行路径与启动选项",
            "mineru" => "MinerU 提供商配置",
            "ui" => "界面偏好",
            "file_scanning" => "文件扫描排除规则",
            "sync_binding" => "同步根绑定",
            "mcp" => "MCP 服务配置",
            "credentials" => "提供商凭据",
            "device_bindings" => "设备 root binding",
            "snapshot_runtime_state" => "快照运行状态",
            _ => settingKey
        };
    }

    private static string OwnerTextFor(SettingCatalogEntry entry)
    {
        if (entry.Scope == SettingStorageScope.RuntimeOnly)
        {
            return "运行时";
        }

        return entry.IsSnapshotEligible ? "资料库 setting record" : "本机设备设置";
    }

    private static string SourceTextFor(SettingCatalogEntry entry, bool enabled)
    {
        if (entry.IsSecret)
        {
            return "本机加密/脱敏边界";
        }

        if (entry.Scope == SettingStorageScope.RuntimeOnly)
        {
            return "运行状态，不持久进快照";
        }

        return entry.IsSnapshotEligible && enabled ? "随内容快照发布/接收" : "本机 JSON 或设备 binding";
    }
}
