using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.UI.Services;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class SyncSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly SettingsDraftStore _draftStore;
    [ExcludeFromDerivedGeneration] private string DraftKey => "sync:" + Path.GetFullPath(_main.RuntimeDatabasePath);
    private SyncAppSettings _persisted;
    private SyncAppSettings _draft;
    private LibraryId? _libraryId;
    private int _libraryGeneration = -1;
    private long _editGeneration;
    private long _loadGeneration;
    private bool _isDirty;
    private bool _isConstructing;
    private bool _isSyncing;
    private readonly ObservableCollection<SyncSettingScopeRowViewModel> _settingScopeRows = new();

    public SyncSettingsViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
        _main = main;
        _draftStore = new SettingsDraftStore(main.SettingsFilePath);
        _persisted = main.AppOptions.Sync;
        _draft = _persisted;
        OpenSyncCenterCommand = new AsyncCommand(main.OpenSyncCenterAsync);
        PublishSnapshotCommand = new AsyncCommand(PublishSnapshotAsync);
        CheckIncomingSnapshotCommand = new AsyncCommand(CheckIncomingSnapshotAsync);
        ExportSnapshotPackageCommand = new AsyncCommand(ExportSnapshotPackageAsync);
        Status = "已保存";
        SyncFromDraft(_draft);
        _isConstructing = false;
    }

    [ExcludeFromDerivedGeneration] public string DeviceId => _draft.DeviceId;

    [ObservableProperty] public partial string DeviceName { get; set; } = "";

    partial void OnDeviceNameChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _draft = _draft with { DeviceName = value };
        MarkDirty();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string SyncRoot { get; set; } = "";

    partial void OnSyncRootChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _draft = _draft with { SyncRoot = value };
        MarkDirty();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetadataLookupScopeText))]
    [NotifyPropertyChangedFor(nameof(MetadataLookupEffectiveSourceText))]
    public partial bool SyncMetadataLookup { get; set; }

    partial void OnSyncMetadataLookupChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _draft = _draft.WithSettingEnabled(LibrarySettingKeys.MetadataLookup, value);
        MarkDirty();
        RefreshScopeRows();
    }

    [ExcludeFromDerivedGeneration]
    public string MetadataLookupScopeText =>
        SyncMetadataLookup ? "随当前资料库的内容快照同步" : "仅此设备";

    [ExcludeFromDerivedGeneration]
    public string MetadataLookupEffectiveSourceText =>
        SyncMetadataLookup ? "资料库 setting record" : "本机 JSON 设置";

    [ExcludeFromDerivedGeneration]
    public string MetadataLookupSchemaText
    {
        get
        {
            SettingCatalogEntry entry = LibrarySettingCatalog.GetRequired(LibrarySettingKeys.MetadataLookup);
            return $"{entry.SettingKey} · schema {entry.SchemaVersion} · {entry.MergePolicy}";
        }
    }

    [ObservableProperty] public partial bool SyncAgentSessions { get; set; }

    partial void OnSyncAgentSessionsChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _draft = _draft with { SyncAgentSessions = value };
        MarkDirty();
    }

    [ObservableProperty] public partial bool SyncWorkflowDefinitions { get; set; }

    partial void OnSyncWorkflowDefinitionsChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _draft = _draft with { SyncWorkflowDefinitions = value };
        MarkDirty();
    }

    [ExcludeFromDerivedGeneration]
    public string AgentSessionsScopeText =>
        SyncAgentSessions ? "会话目录随内容快照同步" : "会话历史仅保留在本机";

    [ExcludeFromDerivedGeneration]
    public string WorkflowDefinitionsScopeText =>
        SyncWorkflowDefinitions ? "工作流定义随内容快照同步" : "工作流定义仅保留在本机";

    public AsyncCommand OpenSyncCenterCommand { get; }
    public AsyncCommand PublishSnapshotCommand { get; }
    public AsyncCommand CheckIncomingSnapshotCommand { get; }
    public AsyncCommand ExportSnapshotPackageCommand { get; }

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<SyncSettingScopeRowViewModel> SettingScopeRows => _settingScopeRows;

    [ExcludeFromDerivedGeneration] public string SnapshotOperationStateText => _main.GetSnapshotOperationStateText();

    [ExcludeFromDerivedGeneration] public string SnapshotOperationMessage => _main.GetSnapshotOperationMessage();

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty;

    public override async Task SaveAsync()
    {
        long editRevision = _editGeneration;
        SyncAppSettings draft = _draft;
        string draftKey = DraftKey;
        int generation = _main.LibraryGeneration;
        string? incompleteReason = string.IsNullOrWhiteSpace(draft.SyncRoot) ? "请补全本机同步目录。" : null;
        if (incompleteReason is null)
        {
            try
            {
                _ = Path.GetFullPath(draft.SyncRoot);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                  or PathTooLongException)
            {
                incompleteReason = $"请修正本机同步目录：{exception.Message}";
            }
        }

        if (incompleteReason is not null)
        {
            await SaveIncompleteDraftAsync(draft, draftKey, editRevision, incompleteReason);
            return;
        }

        Result<LibraryId> library = await EnsureLibraryIdAsync();
        if (library.IsFailure)
        {
            if (library.ErrorCode == AppErrorCodes.NotFound && generation == _main.LibraryGeneration)
            {
                await SaveIncompleteDraftAsync(draft, draftKey, editRevision,
                    "请打开或创建书库后完成同步配置。");
                return;
            }

            LastError = library.ErrorMessage;
            SaveState = SettingsSaveState.Failed;
            Status = $"保存失败：{library.ErrorMessage}";
            RaiseState();
            return;
        }

        if (generation != _main.LibraryGeneration)
        {
            LastError = "资料库已切换，请重新加载同步设置后再保存。";
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
                               Path.GetFullPath(draft.SyncRoot),
                               StringComparison.OrdinalIgnoreCase);
        string logicalRootId = rootChanged || string.IsNullOrWhiteSpace(_persisted.SyncRootId)
            ? await ResolveSyncRootIdAsync(draft.SyncRoot)
            : _persisted.SyncRootId;
        IReadOnlyList<string> enabledKeys = draft.EnabledSettingKeys;
        SnapshotSyncLocalState snapshotState = rootChanged
            ? SnapshotSyncLocalState.NotConfigured
            : _persisted.SnapshotState ?? SnapshotSyncLocalState.NotConfigured;
        DeviceRootBindingAppSettings binding = new(
            library.Value.ToString(),
            LogicalRootKinds.SyncRoot,
            logicalRootId,
            draft.DeviceId,
            Path.GetFullPath(draft.SyncRoot),
            "settings_ui",
            Directory.Exists(draft.SyncRoot),
            FileSearchRootAuthorizationKinds.None,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            snapshotState,
            enabledKeys);
        SyncAppSettings savedDraft = draft.WithDeviceBinding(binding) with
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
            ? await _main.UpdateAppOptionsAsync(_main.AppOptions with { Sync = savedDraft }, "Sync")
            : await _main.SetMetadataLookupSyncEnabledAsync(syncMetadataLookup, savedDraft);

        if (result.IsSuccess)
        {
            await _draftStore.WriteAsync<SyncAppSettings>(draftKey, null);
            _persisted = ToLibraryDraft(savedDraft, binding);
            if (editRevision == _editGeneration && generation == _main.LibraryGeneration)
            {
                SyncFromDraft(_persisted);
                _isDirty = false;
            }

            LastError = null;
            ValidationState = SettingsValidationState.Valid;
            SaveState = IsDirty ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
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

    private async Task SaveIncompleteDraftAsync(SyncAppSettings draft, string key, long revision, string reason)
    {
        await _draftStore.WriteAsync(key, draft);
        _persisted = draft;
        _isDirty = revision != _editGeneration;
        LastError = reason;
        ValidationState = SettingsValidationState.Invalid;
        SaveState = IsDirty ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
        Status = $"已保存草稿，{reason}";
        RaiseState();
    }

    public override Task DiscardAsync()
    {
        _editGeneration++;
        _loadGeneration++;
        SyncFromDraft(_persisted);
        _isDirty = false;
        SaveState = SettingsSaveState.Clean;
        Status = "已放弃更改";
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

        long loadGeneration = ++_loadGeneration;
        long editGeneration = _editGeneration;
        int generation = _main.LibraryGeneration;
        SyncAppSettings? savedForm = await _draftStore.ReadAsync<SyncAppSettings>(DraftKey, cancellationToken);
        if (savedForm is not null && loadGeneration == _loadGeneration && editGeneration == _editGeneration &&
            generation == _main.LibraryGeneration && !IsDirty)
        {
            _persisted = savedForm;
            SyncFromDraft(savedForm);
            ValidationState = SettingsValidationState.Invalid;
            LastError = string.IsNullOrWhiteSpace(savedForm.SyncRoot)
                ? "请补全本机同步目录。"
                : "请打开或创建书库后确认同步目录并完成配置。";
            SaveState = SettingsSaveState.Saved;
            Status = $"已恢复草稿，{LastError}";
            RaiseState();
            return;
        }

        Result<LibraryId> library = await EnsureLibraryIdAsync(cancellationToken);
        if (loadGeneration != _loadGeneration ||
            editGeneration != _editGeneration ||
            IsDirty ||
            generation != _main.LibraryGeneration)
        {
            return;
        }

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
        SyncFromDraft(_persisted);
        _isDirty = false;
        LastError = null;
        SaveState = SettingsSaveState.Clean;
        Status = "已加载同步设置";
        RaiseState();
    }

    private void MarkDirty()
    {
        _editGeneration++;
        _loadGeneration++;
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

    public void NotifyLibraryContextChanged()
    {
        _loadGeneration++;
        _libraryId = null;
        _libraryGeneration = -1;
        if (IsDirty)
        {
            return;
        }

        _persisted = _main.AppOptions.Sync;
        SyncFromDraft(_persisted);
        SaveState = SettingsSaveState.Clean;
        Status = "同步设置将在新资料库加载后刷新";
        RaiseState();
    }

    private async Task<Result<LibraryId>> EnsureLibraryIdAsync(CancellationToken cancellationToken = default)
    {
        int generation = _main.LibraryGeneration;
        if (_libraryId is not null && _libraryGeneration == generation)
        {
            return Result<LibraryId>.Success(_libraryId.Value);
        }

        HostServices services = await _main.ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync(cancellationToken);
        if (library.IsFailure)
        {
            return Result<LibraryId>.Failure(library.ErrorCode!, library.ErrorMessage!);
        }

        if (generation != _main.LibraryGeneration)
        {
            return Result<LibraryId>.Failure(AppErrorCodes.InvalidState,
                "The active library changed while loading settings.");
        }

        _libraryId = library.Value.LibraryId;
        _libraryGeneration = generation;
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

    private void SyncFromDraft(SyncAppSettings draft)
    {
        _isSyncing = true;
        _draft = draft;
        DeviceName = draft.DeviceName;
        SyncRoot = draft.SyncRoot;
        SyncMetadataLookup = draft.IsSettingEnabled(LibrarySettingKeys.MetadataLookup);
        SyncAgentSessions = draft.SyncAgentSessions;
        SyncWorkflowDefinitions = draft.SyncWorkflowDefinitions;
        _isSyncing = false;
        Raise(nameof(DeviceId));
        RefreshScopeRows();
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
