using System.Collections.ObjectModel;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels;

public enum SyncCenterSection
{
    Overview,
    Publish,
    Receive
}

/// <summary>
/// The user-facing Sync Center state. The coordinator owns all internal paths, staging, device identity, and lineage;
/// this model only selects an intentional user operation and presents its durable outcome.
/// </summary>
public sealed class SnapshotViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private SnapshotContentResolutionPlan? _contentPlan;
    private SnapshotSyncStatus? _status;
    private SnapshotIncomingPlan? _incoming;
    private string _exportDestinationDirectory = "";
    private string _packageManifestPath = "";
    private string _incomingCopyDestinationPath = "";
    private bool _confirmApply;
    private string _operationMessage = "同步中心尚未检查同步目录。";
    private NavCategoryViewModel _activeNavSection = null!;

    public SnapshotViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        PublishCommand = new AsyncCommand(PublishAsync);
        ExportCommand = new AsyncCommand(ExportAsync);
        CheckCurrentCommand = new AsyncCommand(CheckCurrentAsync);
        OpenPackageCommand = new AsyncCommand(OpenPackageAsync);
        ResolveConflictsCommand = new AsyncCommand(ResolveConflictsAsync);
        ApplyCommand = new AsyncCommand(ApplyAsync);
        DiscardIncomingCommand = new AsyncCommand(DiscardIncomingAsync);
        KeepIncomingCopyCommand = new AsyncCommand(KeepIncomingCopyAsync);
        _activeNavSection = NavSections[0];
    }

    /// <summary>Left-navigation sections. The sync center has no save concept: every button is an
    /// immediate operation and the path inputs stay session-level.</summary>
    public ObservableCollection<NavCategoryViewModel> NavSections { get; } =
    [
        new("状态概览", "Info", SyncCenterSection.Overview),
        new("发布与导出", "Cloud", SyncCenterSection.Publish),
        new("接收与检查", "Search", SyncCenterSection.Receive)
    ];

    public NavCategoryViewModel ActiveNavSection
    {
        get => _activeNavSection;
        set
        {
            if (ReferenceEquals(_activeNavSection, value))
            {
                return;
            }

            _activeNavSection = value;
            Raise();
            Raise(nameof(IsOverviewSectionActive));
            Raise(nameof(IsPublishSectionActive));
            Raise(nameof(IsReceiveSectionActive));
        }
    }

    public bool IsOverviewSectionActive => Equals(_activeNavSection.Content, SyncCenterSection.Overview);
    public bool IsPublishSectionActive => Equals(_activeNavSection.Content, SyncCenterSection.Publish);
    public bool IsReceiveSectionActive => Equals(_activeNavSection.Content, SyncCenterSection.Receive);

    public string ExportDestinationDirectory
    {
        get => _exportDestinationDirectory;
        set
        {
            if (_exportDestinationDirectory == value)
            {
                return;
            }

            _exportDestinationDirectory = value;
            Raise();
        }
    }

    /// <summary>Path selected from a portable package; it is never a runtime/staging path.</summary>
    public string PackageManifestPath
    {
        get => _packageManifestPath;
        set
        {
            if (_packageManifestPath == value)
            {
                return;
            }

            _packageManifestPath = value;
            Raise();
        }
    }

    public bool ConfirmApply
    {
        get => _confirmApply;
        set
        {
            if (_confirmApply == value)
            {
                return;
            }

            _confirmApply = value;
            Raise();
            Raise(nameof(CanApply));
        }
    }

    /// <summary>Destination selected for preserving a reviewed incoming branch as a standalone library copy.</summary>
    public string IncomingCopyDestinationPath
    {
        get => _incomingCopyDestinationPath;
        set
        {
            if (_incomingCopyDestinationPath == value)
            {
                return;
            }

            _incomingCopyDestinationPath = value;
            Raise();
        }
    }

    public SnapshotSyncOperationState OperationState =>
        _status?.State ?? SnapshotSyncOperationState.NotConfigured;

    public string OperationStateText => OperationState switch
    {
        SnapshotSyncOperationState.NotConfigured => "尚未配置",
        SnapshotSyncOperationState.Ready => "已就绪",
        SnapshotSyncOperationState.Validating => "正在验证",
        SnapshotSyncOperationState.Publishing => "正在发布",
        SnapshotSyncOperationState.Published => "已发布",
        SnapshotSyncOperationState.Exporting => "正在导出",
        SnapshotSyncOperationState.CheckingIncoming => "正在检查传入快照",
        SnapshotSyncOperationState.InspectingBranch => "正在检查快照分支",
        SnapshotSyncOperationState.AwaitingContentConflicts => "等待内容冲突处理",
        SnapshotSyncOperationState.Applying => "正在应用",
        SnapshotSyncOperationState.Applied => "已应用",
        SnapshotSyncOperationState.Cancelled => "已取消",
        SnapshotSyncOperationState.Failed => "操作失败",
        _ => OperationState.ToString()
    };

    public string SyncRootSummary => _status is { IsSyncRootAvailable: true, SyncRootId: not null }
        ? $"已就绪（绑定 {_status.SyncRootId}）"
        : "不可用或尚未配置";

    public string LibrarySummary => _status?.LibraryId is { Length: > 0 } libraryId
        ? libraryId
        : "身份未知";

    public string DeviceSummary
    {
        get
        {
            SyncAppSettings sync = _main.AppOptions.Sync;
            return $"{sync.DeviceName}（{sync.DeviceId}）";
        }
    }

    public string BranchDetailSummary
    {
        get
        {
            if (_status is null)
            {
                return "尚未读取同步状态。";
            }

            SnapshotSyncLocalState local = _status.LocalState;
            return
                $"最近发布：{ShortId(local.LastPublishedSnapshotId)}；最近应用：{ShortId(local.LastAppliedSnapshotId)}；最近远端：{ShortId(local.LastSeenRemoteSnapshotId)}";
        }
    }

    public string LastErrorText => _status?.LocalState.LastError is { Length: > 0 } error
        ? $"最近错误：{error}"
        : "";

    public bool HasLastError => LastErrorText.Length > 0;

    public string LocalSnapshotSummary => _status?.LocalState.LineageSnapshotId is { Length: > 0 } snapshotId
        ? $"本机分支：{ShortId(snapshotId)}"
        : "本机尚无已发布或已应用的快照";

    public string RemoteSnapshotSummary => _status?.RemoteCurrent?.SnapshotId is { Length: > 0 } snapshotId
        ? $"同步目录当前快照：{ShortId(snapshotId)}"
        : "同步目录中尚无可用快照";

    public string OperationMessage
    {
        get => _operationMessage;
        private set
        {
            if (_operationMessage == value)
            {
                return;
            }

            _operationMessage = value;
            Raise();
        }
    }

    public int IncomingItemCount => _incoming?.Items.Count ?? 0;
    public int IncomingDocumentCount => _incoming?.Documents.Count ?? 0;

    public int BlockingConflictCount => _incoming?.Conflicts.Count(conflict =>
        conflict.Severity == ConflictSeverity.Blocking &&
        conflict.ResolutionStatus == ConflictResolutionStatus.Unresolved) ?? 0;

    public int WarningCount => _incoming?.Warnings.Count ?? 0;
    public bool HasIncomingPlan => _contentPlan is not null;

    public bool CanResolveContentConflicts => _contentPlan?.BranchImportPlan.Conflicts.Any(
        IsExecutableContentConflict) == true;

    public bool CanApply => _contentPlan is not null && ConfirmApply && BlockingConflictCount == 0;

    public string IncomingSummary => _incoming is null
        ? "尚未打开传入快照。"
        : $"传入快照包含 {IncomingItemCount} 条题录、{IncomingDocumentCount} 个文档实例、{BlockingConflictCount} 个阻塞冲突。";

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand CheckCurrentCommand { get; }
    public AsyncCommand OpenPackageCommand { get; }
    public AsyncCommand ResolveConflictsCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand DiscardIncomingCommand { get; }
    public AsyncCommand KeepIncomingCopyCommand { get; }

    public async Task RefreshAsync()
    {
        Result<SnapshotSyncStatus> result = await (await _main.ServicesAsync()).SnapshotSync.GetStatusAsync();
        if (result.IsSuccess)
        {
            _status = result.Value;
            OperationMessage = result.Value.Warnings.Count == 0
                ? "同步状态已更新。"
                : string.Join(" ", result.Value.Warnings);
        }
        else
        {
            OperationMessage = DescribeFailure(result);
        }

        RaiseStatus();
    }

    private async Task PublishAsync()
    {
        try
        {
            Result<SnapshotPublishResult> result = await _main.ModalOperations.RunAsync(
                new ModalOperationOptions("发布到同步目录", "正在创建并验证快照。", true),
                async context =>
                    await (await _main.ServicesAsync()).SnapshotSync.PublishAsync(context.CancellationToken));
            OperationMessage = result.IsSuccess
                ? "快照已发布到同步目录。"
                : DescribeFailure(result);
            await RefreshAfterOperationAsync("publish_snapshot", result.IsSuccess);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            OperationMessage = "操作已取消。";
            await RefreshAfterCancellationAsync("publish_snapshot");
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            Result<SnapshotExportResult> result = await _main.ModalOperations.RunAsync(
                new ModalOperationOptions("导出快照包", "正在创建可移动的目录快照包。", true),
                async context => await (await _main.ServicesAsync()).SnapshotSync.ExportAsync(
                    new SnapshotExportRequest(ExportDestinationDirectory),
                    context.CancellationToken));
            OperationMessage = result.IsSuccess
                ? "快照目录包已导出，同步目录内容未受影响。"
                : DescribeFailure(result);
            await RefreshAfterOperationAsync("export_snapshot_package", result.IsSuccess);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            OperationMessage = "操作已取消。";
            await RefreshAfterCancellationAsync("export_snapshot_package");
        }
    }

    private async Task CheckCurrentAsync()
    {
        await InspectAsync(SnapshotIncomingRequest.CurrentSyncRoot, "检查同步目录的更新", "inspect_sync_current");
    }

    private async Task OpenPackageAsync()
    {
        await InspectAsync(
            new SnapshotIncomingRequest(SnapshotIncomingSource.ExportPackage, PackageManifestPath),
            "打开快照目录包",
            "inspect_snapshot_package");
    }

    private async Task InspectAsync(SnapshotIncomingRequest request, string title, string operation)
    {
        try
        {
            Result<SnapshotIncomingPlan> result = await _main.ModalOperations.RunAsync(
                new ModalOperationOptions(title, "正在验证并检查传入内容。", true),
                async context => await (await _main.ServicesAsync()).SnapshotSync.InspectIncomingAsync(request,
                    context.CancellationToken));
            if (result.IsSuccess)
            {
                _incoming = result.Value;
                _contentPlan = result.Value.ContentPlan;
                ConfirmApply = false;
                OperationMessage = BlockingConflictCount > 0
                    ? "存在阻塞冲突，请先解决冲突再应用。"
                    : "传入快照已检查。请确认后再应用。";
            }
            else
            {
                _incoming = null;
                _contentPlan = null;
                OperationMessage = DescribeFailure(result);
            }

            await RefreshAfterOperationAsync(operation, result.IsSuccess);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            OperationMessage = "操作已取消。";
            await RefreshAfterCancellationAsync(operation);
        }
    }

    private async Task ApplyAsync()
    {
        if (_contentPlan is null)
        {
            OperationMessage = "请先检查一个传入快照。";
            RaiseIncoming();
            return;
        }

        try
        {
            Result<SnapshotApplyResult> result = await _main.ModalOperations.RunAsync(
                new ModalOperationOptions("应用快照内容", "正在验证并应用传入内容。", true),
                async context => await (await _main.ServicesAsync()).SnapshotSync.ApplyAsync(
                    _contentPlan with { IsExplicitlyConfirmed = ConfirmApply },
                    context.CancellationToken));
            if (result.IsSuccess)
            {
                ClearIncomingPlan();
                await _main.RefreshSyncedMetadataLookupAsync();
                OperationMessage = "快照内容已应用，搜索索引将在后台自动更新。";
            }
            else
            {
                OperationMessage = DescribeFailure(result);
            }

            await RefreshAfterOperationAsync("apply_snapshot_plan", result.IsSuccess);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            OperationMessage = "操作已取消。";
            await RefreshAfterCancellationAsync("apply_snapshot_plan");
        }
    }

    private async Task DiscardIncomingAsync()
    {
        if (_contentPlan is null)
        {
            OperationMessage = "请先检查一个传入快照。";
            RaiseIncoming();
            return;
        }

        try
        {
            Result result = await _main.ModalOperations.RunAsync(
                new ModalOperationOptions("丢弃传入快照", "正在清理已检查的传入内容。", true),
                async context => await (await _main.ServicesAsync()).SnapshotSync.DiscardIncomingAsync(
                    _contentPlan,
                    context.CancellationToken));
            if (result.IsSuccess)
            {
                ClearIncomingPlan();
                OperationMessage = "已丢弃传入内容，当前资料库未被修改。";
            }
            else
            {
                OperationMessage = DescribeFailure(result);
            }

            await RefreshAfterOperationAsync("discard_snapshot_branch", result.IsSuccess);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            OperationMessage = "操作已取消。";
            await RefreshAfterCancellationAsync("discard_snapshot_branch");
        }
    }

    private async Task KeepIncomingCopyAsync()
    {
        if (_contentPlan is null)
        {
            OperationMessage = "请先检查一个传入快照。";
            RaiseIncoming();
            return;
        }

        try
        {
            Result<string> result = await _main.ModalOperations.RunAsync(
                new ModalOperationOptions("保留传入副本", "正在创建独立资料库副本并清理传入内容。", true),
                async context =>
                    await (await _main.ServicesAsync()).SnapshotSync.KeepIncomingAsSeparateLibraryCopyAsync(
                        _contentPlan,
                        IncomingCopyDestinationPath,
                        context.CancellationToken));
            if (result.IsSuccess)
            {
                ClearIncomingPlan();
                OperationMessage = $"已保留独立资料库副本：{result.Value}";
            }
            else
            {
                OperationMessage = DescribeFailure(result);
            }

            await RefreshAfterOperationAsync("keep_snapshot_branch_copy", result.IsSuccess);
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            OperationMessage = "操作已取消。";
            await RefreshAfterCancellationAsync("keep_snapshot_branch_copy");
        }
    }

    private async Task ResolveConflictsAsync()
    {
        if (_contentPlan is null)
        {
            OperationMessage = "请先检查一个传入快照。";
            RaiseIncoming();
            return;
        }

        while (FindNextExecutableContentConflict() is { } conflict)
        {
            AppServices services = await _main.ServicesAsync();
            SnapshotContentConflictActionExecutor executor = new(services.SnapshotSync, _contentPlan,
                conflict.ConflictCode);
            Result<ConflictResolutionResult> resolution = await _main.ResolveConflictAsync(conflict, executor);
            if (resolution.IsFailure)
            {
                OperationMessage = DescribeFailure(resolution);
                await RefreshAfterOperationAsync("resolve_snapshot_content_conflict", false);
                return;
            }

            if (!resolution.Value.WasExecuted)
            {
                OperationMessage = "内容冲突仍未解决；可再次打开冲突处理。";
                RaiseIncoming();
                return;
            }

            ReplaceContentPlan(executor.Plan);
        }

        OperationMessage = BlockingConflictCount == 0
            ? "内容冲突已处理。请重新确认后再应用传入内容。"
            : "仍有未支持的阻塞冲突，暂不能应用传入内容。";
        await RefreshAfterOperationAsync("resolve_snapshot_content_conflict", BlockingConflictCount == 0);
    }

    private async Task RefreshAfterOperationAsync(string operation, bool succeeded)
    {
        await _main.LogOperationAsync(operation,
            succeeded ? "Snapshot sync operation completed." : "Snapshot sync operation failed.");
        await RefreshStatusAsync();
    }

    private async Task RefreshAfterCancellationAsync(string operation)
    {
        await _main.LogOperationAsync(operation, "Snapshot sync operation cancelled.");
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        Result<SnapshotSyncStatus> status = await (await _main.ServicesAsync()).SnapshotSync.GetStatusAsync();
        if (status.IsSuccess)
        {
            _status = status.Value;
        }

        RaiseStatus();
        RaiseIncoming();
    }

    private void RaiseStatus()
    {
        Raise(nameof(OperationState));
        Raise(nameof(OperationStateText));
        Raise(nameof(SyncRootSummary));
        Raise(nameof(LocalSnapshotSummary));
        Raise(nameof(RemoteSnapshotSummary));
        Raise(nameof(LibrarySummary));
        Raise(nameof(DeviceSummary));
        Raise(nameof(BranchDetailSummary));
        Raise(nameof(LastErrorText));
        Raise(nameof(HasLastError));
    }

    private void RaiseIncoming()
    {
        Raise(nameof(IncomingItemCount));
        Raise(nameof(IncomingDocumentCount));
        Raise(nameof(BlockingConflictCount));
        Raise(nameof(WarningCount));
        Raise(nameof(HasIncomingPlan));
        Raise(nameof(CanResolveContentConflicts));
        Raise(nameof(CanApply));
        Raise(nameof(IncomingSummary));
    }

    private ConflictDescriptor? FindNextExecutableContentConflict()
    {
        return _contentPlan?.BranchImportPlan.Conflicts.FirstOrDefault(IsExecutableContentConflict);
    }

    private static bool IsExecutableContentConflict(ConflictDescriptor conflict)
    {
        return conflict.ResolutionStatus == ConflictResolutionStatus.Unresolved &&
               conflict.Severity == ConflictSeverity.Blocking &&
               conflict.ConflictCode is ConflictCode.SameIdDifferentContent or ConflictCode.PrimaryDocumentConflict;
    }

    private void ReplaceContentPlan(SnapshotContentResolutionPlan plan)
    {
        _contentPlan = plan;
        if (_incoming is not null)
        {
            _incoming = _incoming with
            {
                ContentPlan = plan,
                Conflicts = plan.BranchImportPlan.Conflicts
            };
        }

        ConfirmApply = false;
        RaiseIncoming();
    }

    private void ClearIncomingPlan()
    {
        _contentPlan = null;
        _incoming = null;
        ConfirmApply = false;
    }

    private static string ShortId(string? snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            return "无";
        }

        return snapshotId.Length <= 8 ? snapshotId : snapshotId[..8];
    }

    private static string DescribeFailure(IOperationOutcome result)
    {
        return $"操作失败：{result.ErrorMessage ?? "未知错误"}";
    }
}
