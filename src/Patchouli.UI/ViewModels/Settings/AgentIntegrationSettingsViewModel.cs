using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Patchouli.Core.Agents;
using Patchouli.Core.Results;
using Patchouli.Host.Agents;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Agents;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// 「Agent 集成」section: enables the external Agent integration, selects the default catalog agent,
/// detects installed executables and manages per-agent executable overrides. OpenCode additionally
/// offers a one-click installation of the official platform archive. All async work (detection and
/// installation) can be cancelled.
/// </summary>
public sealed partial class AgentIntegrationSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly AgentSkillService _skills;
    private readonly HttpClient _httpClient;
    private readonly AgentInstaller _installer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private AgentIntegrationAppSettings _draft;
    private AgentIntegrationRowViewModel? _selectedRow;
    private CancellationTokenSource? _activeOperation;
    private bool _isDirty;
    private bool _isConstructing;
    private bool _isSyncing;
    private bool _isConfirmingInstall;
    private long _editRevision;

    public AgentIntegrationSettingsViewModel(MainWindowViewModel main, AgentSkillService? skills = null)
    {
        _isConstructing = true;
        _main = main;
        _skills = skills ?? main.AgentSkills;
        _draft = main.AppOptions.Agents.Normalize();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        Register(_httpClient);
        _installer = new AgentInstaller(_httpClient);
        foreach (AgentCatalogEntry entry in AgentCatalog.All)
        {
            AgentIntegrationRowViewModel row = new(this, entry);
            if (string.Equals(entry.Id, _draft.SelectedAgentId, StringComparison.Ordinal))
            {
                _selectedRow = row;
            }

            Agents.Add(row);
        }

        Enabled = _draft.Enabled;
        DetectAllCommand = new AsyncCommand(DetectAllFromCommandAsync);
        CancelOperationCommand = new RelayCommand(_ => CancelActiveOperation());
        _isConstructing = false;
    }

    [ExcludeFromDerivedGeneration] public ObservableCollection<AgentIntegrationRowViewModel> Agents { get; } = new();

    [ObservableProperty] public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _draft = _draft with { Enabled = value };
        NotifyRowsOperationStateChanged();
        MarkDirty();
        Status = value ? "Agent 集成已启用，保存后生效。" : "Agent 集成暂不启用，保存后生效。";
    }

    [ObservableProperty] public partial bool IsDetecting { get; private set; }

    [ObservableProperty] public partial bool IsInstalling { get; private set; }

    public AsyncCommand DetectAllCommand { get; }
    public RelayCommand CancelOperationCommand { get; }

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty && !IsSaving;

    [ExcludeFromDerivedGeneration] public bool CanCancelOperation => _activeOperation is not null;

    [ExcludeFromDerivedGeneration] internal bool HasActiveOperation => _activeOperation is not null;

    [ExcludeFromDerivedGeneration] internal AgentIntegrationRowViewModel? SelectedRow => _selectedRow;

    /// <summary>The catalog id of the default Agent in the current draft.</summary>
    [ExcludeFromDerivedGeneration]
    public string SelectedAgentId => _draft.SelectedAgentId;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        RefreshSkills();
        if (_isDirty)
        {
            Status = "Agent 设置有未保存的更改，已保留当前草稿。";
            return Task.CompletedTask;
        }

        SyncFromSettings();
        _ = DetectAllAsync(false, cancellationToken);
        return Task.CompletedTask;
    }

    public override async Task SaveAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            long revision = _editRevision;
            AgentIntegrationAppSettings draft = _draft.Normalize();
            SaveState = SettingsSaveState.Saving;
            Status = "正在保存...";
            SettingsSaveResult saved = await _main.UpdateAppOptionsAsync(_main.AppOptions with { Agents = draft },
                "Agents");
            if (!saved.IsSuccess)
            {
                LastError = saved.ErrorMessage;
                SaveState = SettingsSaveState.Failed;
                Status = $"保存失败：{saved.ErrorMessage}";
                return;
            }

            LastError = null;
            ValidationState = SettingsValidationState.Valid;
            if (revision == _editRevision)
            {
                _draft = draft;
                _isDirty = false;
                SyncFromSettings();
                SaveState = SettingsSaveState.Saved;
                Status = "已保存";
            }
            else
            {
                _isDirty = true;
                SaveState = SettingsSaveState.Dirty;
                Status = "已保存旧版本，仍有新的未保存更改";
            }

            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public override Task DiscardAsync()
    {
        _activeOperation?.Cancel();
        _isDirty = false;
        LastError = null;
        ValidationState = SettingsValidationState.Unknown;
        SaveState = SettingsSaveState.Clean;
        SyncFromSettings();
        Status = "已放弃更改。可点击「重新检测」刷新安装状态。";
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        return Task.CompletedTask;
    }

    internal void SelectAgent(AgentIntegrationRowViewModel row)
    {
        if (_isSyncing || ReferenceEquals(_selectedRow, row))
        {
            return;
        }

        _selectedRow = row;
        _draft = _draft with { SelectedAgentId = row.Entry.Id };
        NotifySelectionChanged();
        Raise(nameof(SelectedAgentId));
        MarkDirty();
        Status = $"已将「{row.DisplayName}」设为默认 Agent，保存后生效。";
    }

    internal void ApplyCustomPath(AgentIntegrationRowViewModel row, string? path)
    {
        if (_isSyncing)
        {
            return;
        }

        string? normalized = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _draft = _draft.WithCustomPath(row.Entry.Id, normalized);
        MarkDirty();
        Status = normalized is null
            ? $"已恢复「{row.DisplayName}」的自动检测路径。"
            : $"已设置「{row.DisplayName}」的自定义路径，正在重新检测...";
        StartRowDetection(row);
    }

    internal async Task RedetectRowAsync(AgentIntegrationRowViewModel row, CancellationToken commandToken)
    {
        if (!TryBeginOperation(commandToken, out CancellationTokenSource operation, out CancellationToken token))
        {
            Status = "已有 Agent 操作正在进行，请等待完成或取消。";
            return;
        }

        row.BeginDetection();
        try
        {
            HostServices services = await _main.ServicesAsync();
            RefreshSkills();
            AgentDetectionResult result = await services.AgentDetection.DetectAsync(row.Entry, _draft, true, token);
            row.ApplyDetection(result);
            Status = $"「{row.DisplayName}」检测完成。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            row.CancelDetection();
            Status = "Agent 检测已取消。";
        }
        catch (Exception exception)
        {
            row.CancelDetection();
            row.ErrorText = $"检测失败：{exception.Message}";
            Status = $"「{row.DisplayName}」检测失败：{exception.Message}";
        }
        finally
        {
            EndOperation(operation);
        }
    }

    internal async Task InstallAsync(AgentIntegrationRowViewModel row, CancellationToken commandToken)
    {
        if (row.Offer is not { HasPlatformBinary: true } offer)
        {
            row.SetInstallError("当前平台没有可用的官方安装包。");
            return;
        }

        if (_isConfirmingInstall)
        {
            return;
        }

        _isConfirmingInstall = true;
        ConfirmDialogResult? choice;
        try
        {
            string targetDirectory = AgentInstaller.GetInstallDirectory(row.Entry.Id);
            choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
                new ConfirmDialogViewModel(
                    "一键安装 OpenCode",
                    $"版本：{FormatVersion(offer.RegistryVersion)}\n" +
                    $"下载地址：{offer.DownloadUrl}\n" +
                    "大小：未知（以服务器返回为准）\n" +
                    $"目标目录：{targetDirectory}\n\n" +
                    "将下载官方安装包到临时目录，解压后原子替换目标目录；取消或失败会清理半成品。",
                    "下载并安装"));
        }
        finally
        {
            _isConfirmingInstall = false;
        }

        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        if (!TryBeginOperation(commandToken, out CancellationTokenSource operation, out CancellationToken token))
        {
            row.SetInstallError("已有 Agent 操作正在进行，请等待完成或取消。");
            return;
        }

        bool installed = false;
        IsInstalling = true;
        row.BeginInstall();
        try
        {
            Progress<AgentInstallProgress> progress = new(row.ApplyInstallProgress);
            Result<AgentInstallResult> result =
                await _installer.InstallAsync(offer, progress, token);
            if (result.IsFailure)
            {
                row.SetInstallFailure(result.ErrorMessage ?? "安装失败。");
                Status = $"安装失败：{result.ErrorMessage}";
                LastError = result.ErrorMessage;
                return;
            }

            AgentInstallResult installation = result.Value;
            installed = true;
            row.ApplyInstalledPath(installation.ExecutablePath);
            _draft = _draft.WithCustomPath(row.Entry.Id, installation.ExecutablePath);
            MarkDirty();
            row.CompleteInstall();
            Status = $"已安装「{row.DisplayName}」{FormatVersion(installation.Version)}到 {installation.InstallDirectory}。";
            LastError = null;

            try
            {
                HostServices services = await _main.ServicesAsync();
                AgentDetectionResult detection =
                    await services.AgentDetection.DetectAsync(row.Entry, _draft, true, token);
                row.ApplyDetection(detection);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                row.ErrorText = "安装已完成，检测已取消。";
            }
            catch (Exception exception)
            {
                row.ErrorText = $"安装已完成，但检测失败：{exception.Message}";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (installed)
            {
                row.ErrorText = "安装已完成，后续步骤已取消。";
                Status = "安装已完成，后续步骤已取消。";
            }
            else
            {
                row.SetInstallCancelled();
                Status = "安装已取消，临时文件已清理。";
            }
        }
        catch (Exception exception)
        {
            row.SetInstallFailure(exception.Message);
            LastError = exception.Message;
            Status = $"安装失败：{exception.Message}";
        }
        finally
        {
            row.EndInstall();
            IsInstalling = false;
            EndOperation(operation);
        }
    }

    internal async Task ToggleSkillAsync(AgentIntegrationRowViewModel row, CancellationToken commandToken)
    {
        if (!TryBeginOperation(commandToken, out CancellationTokenSource operation, out CancellationToken token))
        {
            return;
        }

        try
        {
            Result<AgentSkillStatus> status = _skills.GetStatus(row.Entry.Id);
            row.ApplySkillStatus(status);
            if (status.IsFailure)
            {
                Status = status.ErrorMessage ?? "无法读取 Skill 状态。";
                return;
            }

            bool clear = status.Value.IsInstalled;
            ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
                new ConfirmDialogViewModel(
                    clear ? "清除已有 Patchouli Skill" : "注入 Patchouli Skill",
                    $"外部 Agent 用户级目录（作用于所有项目）：{status.Value.Directory}\n\n{status.Value.DiscoveryNote}\n\n" +
                    (clear
                        ? "将删除此用户级 patchouli 文件夹及其中的自定义修改。清除后再次注入可恢复原版。"
                        : "将写入同名 patchouli 文件夹；若已存在，将替换其中内容且自定义修改会丢失。" +
                          "应用版本升级时会更新这份已登记安装，也可能覆盖自定义修改；清除后不会自动重装到用户级目录。") +
                    "\n\n此操作不改变 MCP 配置、token 或权限。运行中的会话可能需重启后读取变更；不会自动重启。",
                    clear ? "清除已有" : "注入", confirmDanger: clear));
            if (choice != ConfirmDialogResult.Confirm)
            {
                return;
            }

            token.ThrowIfCancellationRequested();
            Result result = clear
                ? await _skills.ClearAsync(row.Entry.Id, token)
                : await _skills.InstallAsync(row.Entry.Id, token);
            RefreshSkills();
            LastError = result.ErrorMessage;
            row.SkillErrorText = result.ErrorMessage;
            Status = result.IsSuccess
                ? $"已{(clear ? "清除" : "注入")}「{row.DisplayName}」用户级 Patchouli Skill；运行中的会话可能需重启，MCP 配置与权限不变。"
                : $"Skill 操作失败：{result.ErrorMessage}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = "Skill 操作已取消。";
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            row.SkillErrorText = exception.Message;
            Status = $"Skill 操作失败：{exception.Message}";
            try
            {
                RefreshSkills();
            }
            catch (Exception refreshException)
            {
                UnexpectedExceptions.Sink.Report(refreshException, nameof(AgentIntegrationSettingsViewModel),
                    nameof(RefreshSkills));
            }
        }
        finally
        {
            EndOperation(operation);
        }
    }

    internal Task RefreshSkillsAsync()
    {
        RefreshSkills();
        return Task.CompletedTask;
    }

    private void RefreshSkills()
    {
        foreach (AgentIntegrationRowViewModel row in Agents)
        {
            row.ApplySkillStatus(_skills.GetStatus(row.Entry.Id));
        }
    }

    private Task DetectAllFromCommandAsync(CancellationToken cancellationToken)
    {
        return DetectAllAsync(true, cancellationToken);
    }

    private async Task DetectAllAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!TryBeginOperation(cancellationToken, out CancellationTokenSource operation, out CancellationToken token))
        {
            Status = "已有 Agent 操作正在进行，请等待完成或取消。";
            return;
        }

        IsDetecting = true;
        foreach (AgentIntegrationRowViewModel row in Agents)
        {
            row.BeginDetection();
        }

        try
        {
            HostServices services = await _main.ServicesAsync();
            RefreshSkills();
            IReadOnlyList<AgentDetectionResult> results =
                await services.AgentDetection.DetectAllAsync(_draft, forceRefresh, token);
            foreach (AgentDetectionResult result in results)
            {
                AgentIntegrationRowViewModel? row =
                    Agents.FirstOrDefault(item =>
                        string.Equals(item.Entry.Id, result.Entry.Id, StringComparison.Ordinal));
                row?.ApplyDetection(result);
            }

            Status = _isDirty ? "Agent 检测完成，仍有未保存的更改。" : "Agent 检测完成。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (AgentIntegrationRowViewModel row in Agents)
            {
                row.CancelDetection();
            }

            Status = "Agent 检测已取消。";
        }
        catch (Exception exception)
        {
            foreach (AgentIntegrationRowViewModel row in Agents)
            {
                row.CancelDetection();
            }

            LastError = exception.Message;
            Status = $"Agent 检测失败：{exception.Message}";
        }
        finally
        {
            IsDetecting = false;
            EndOperation(operation);
        }
    }

    internal void ReportRepositoryLaunchFailure(string displayName, Exception exception)
    {
        LastError = exception.Message;
        Status = $"无法打开「{displayName}」的官方安装说明：{exception.Message}";
    }

    private void StartRowDetection(AgentIntegrationRowViewModel row)
    {
        RedetectRowAsync(row, CancellationToken.None)
            .Observe(nameof(AgentIntegrationSettingsViewModel), nameof(RedetectRowAsync));
    }

    private bool TryBeginOperation(CancellationToken commandToken, out CancellationTokenSource operation,
        out CancellationToken token)
    {
        if (_activeOperation is not null)
        {
            operation = null!;
            token = default;
            return false;
        }

        operation = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
        _activeOperation = operation;
        token = operation.Token;
        Raise(nameof(CanCancelOperation));
        NotifyRowsOperationStateChanged();
        return true;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_activeOperation, operation))
        {
            _activeOperation = null;
        }

        operation.Dispose();
        Raise(nameof(CanCancelOperation));
        NotifyRowsOperationStateChanged();
    }

    internal void CancelActiveOperation()
    {
        _activeOperation?.Cancel();
    }

    private void SyncFromSettings()
    {
        _isSyncing = true;
        try
        {
            _draft = _main.AppOptions.Agents.Normalize();
            Enabled = _draft.Enabled;
            _selectedRow = Agents.FirstOrDefault(row =>
                string.Equals(row.Entry.Id, _draft.SelectedAgentId, StringComparison.Ordinal));
            Raise(nameof(SelectedAgentId));
            foreach (AgentIntegrationRowViewModel row in Agents)
            {
                row.SetCustomPathFromSettings(_draft.FindCustomPath(row.Entry.Id));
                row.RaiseSelectionChanged();
                row.RaiseOperationStateChanged();
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void NotifySelectionChanged()
    {
        foreach (AgentIntegrationRowViewModel row in Agents)
        {
            row.RaiseSelectionChanged();
        }
    }

    private void NotifyRowsOperationStateChanged()
    {
        foreach (AgentIntegrationRowViewModel row in Agents)
        {
            row.RaiseOperationStateChanged();
        }
    }

    private void MarkDirty()
    {
        _editRevision++;
        _isDirty = true;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
    }

    private static string FormatVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? "版本未知" : $"版本 {version.Trim()}";
    }
}

/// <summary>One catalog Agent row in the Agent integration settings.</summary>
public sealed partial class AgentIntegrationRowViewModel : ViewModelBase
{
    private readonly AgentIntegrationSettingsViewModel _parent;
    private bool _isApplyingPath;

    public AgentIntegrationRowViewModel(AgentIntegrationSettingsViewModel parent, AgentCatalogEntry entry)
    {
        _parent = parent;
        Entry = entry;
        RedetectCommand = new AsyncCommand(ct => _parent.RedetectRowAsync(this, ct));
        InstallCommand = new AsyncCommand(ct => _parent.InstallAsync(this, ct));
        ToggleSkillCommand = new AsyncCommand(ct => _parent.ToggleSkillAsync(this, ct));
        TogglePathEditorCommand = new RelayCommand(_ => IsPathEditorOpen = !IsPathEditorOpen);
        ClearCustomPathCommand = new RelayCommand(_ => CustomPath = null);
        OpenRepositoryCommand = new RelayCommand(_ => OpenRepository());
        CancelInstallCommand = new RelayCommand(_ => _parent.CancelActiveOperation());
    }

    public AgentCatalogEntry Entry { get; }

    [ExcludeFromDerivedGeneration] public string DisplayName => Entry.DisplayName;

    [ExcludeFromDerivedGeneration] public string Description => Entry.Description;

    [ExcludeFromDerivedGeneration] public bool IsDefault => Entry.IsDefault;

    [ExcludeFromDerivedGeneration]
    public bool IsOpenCode =>
        string.Equals(Entry.Id, AgentIntegrationAppSettings.DefaultAgentId, StringComparison.Ordinal);

    [ExcludeFromDerivedGeneration]
    public bool IsSelected
    {
        get => ReferenceEquals(_parent.SelectedRow, this);
        set
        {
            if (value)
            {
                _parent.SelectAgent(this);
            }
        }
    }

    [ObservableProperty] public partial bool IsDetecting { get; private set; }

    [ObservableProperty] public partial bool IsInstalling { get; private set; }

    [ObservableProperty] public partial string StatusText { get; private set; } = "尚未检测";

    [ObservableProperty] public partial string VersionText { get; private set; } = "";

    [ObservableProperty] public partial string PathText { get; private set; } = "";

    [ObservableProperty] public partial string? ErrorText { get; set; }

    [ObservableProperty] public partial double InstallProgress { get; private set; }

    [ObservableProperty] public partial string InstallStageText { get; private set; } = "";

    [ObservableProperty] public partial string? InstallErrorText { get; private set; }

    [ObservableProperty] public partial bool CanInstall { get; private set; }

    [ObservableProperty] public partial string? RepositoryUrl { get; private set; }

    [ObservableProperty] public partial string? CustomPath { get; set; }

    [ObservableProperty] public partial bool IsPathEditorOpen { get; set; }

    [ObservableProperty] public partial bool HasSkill { get; private set; }

    [ObservableProperty] public partial string? SkillErrorText { get; internal set; }

    [ObservableProperty] public partial string SkillToolTip { get; private set; } = "外部 Agent 用户级目录；作用于所有项目。";

    [ExcludeFromDerivedGeneration] public bool HasSkillStatus { get; private set; }

    [ExcludeFromDerivedGeneration] public bool CanManageSkill => CanOperate && HasSkillStatus;

    [ExcludeFromDerivedGeneration] public string SkillActionText => HasSkill ? "清除已有" : "注入";

    [ExcludeFromDerivedGeneration] public string SkillActionIcon => HasSkill ? "Trash2" : "Download";

    [ExcludeFromDerivedGeneration] public bool HasCustomPath => !string.IsNullOrWhiteSpace(CustomPath);

    [ExcludeFromDerivedGeneration] public bool HasRepository => !string.IsNullOrWhiteSpace(RepositoryUrl);

    [ExcludeFromDerivedGeneration] public bool ShowInstallHelp => HasDetection && !IsInstalled;

    [ExcludeFromDerivedGeneration] public bool CanOperate => !_parent.HasActiveOperation;

    [ExcludeFromDerivedGeneration] public bool CanManageAgent => CanOperate && _parent.Enabled;

    [ExcludeFromDerivedGeneration]
    public string InstallHelpText => IsOpenCode
        ? Offer is null
            ? "未读取到官方安装信息。可通过「自定义路径」指定已安装的可执行文件，或参考官方安装说明。"
            : CanInstall
                ? "从官方发布地址下载并安装到 Patchouli 管理的目录，安装完成后自动写入自定义路径。"
                : "当前平台没有官方二进制安装包。请按官方说明安装，或通过「自定义路径」指定可执行文件。"
        : "未检测到该 Agent。可通过「自定义路径」指定已安装的可执行文件，或参考官方安装说明。";

    [ExcludeFromDerivedGeneration] public bool HasDetection { get; private set; }

    [ExcludeFromDerivedGeneration] public bool IsInstalled { get; private set; }

    public AgentInstallOffer? Offer { get; internal set; }

    public AsyncCommand RedetectCommand { get; }

    public AsyncCommand InstallCommand { get; }

    public AsyncCommand ToggleSkillCommand { get; }

    public RelayCommand TogglePathEditorCommand { get; }

    public RelayCommand ClearCustomPathCommand { get; }

    public RelayCommand OpenRepositoryCommand { get; }

    public RelayCommand CancelInstallCommand { get; }

    partial void OnCustomPathChanged(string? value)
    {
        Raise(nameof(HasCustomPath));
        if (!_isApplyingPath)
        {
            _parent.ApplyCustomPath(this, value);
        }
    }

    partial void OnRepositoryUrlChanged(string? value)
    {
        Raise(nameof(HasRepository));
    }

    internal void RaiseSelectionChanged()
    {
        Raise(nameof(IsSelected));
    }

    internal void RaiseOperationStateChanged()
    {
        Raise(nameof(CanOperate));
        Raise(nameof(CanManageSkill));
        Raise(nameof(CanManageAgent));
    }

    internal void ApplySkillStatus(Result<AgentSkillStatus> status)
    {
        HasSkillStatus = status.IsSuccess;
        SkillErrorText = status.ErrorMessage;
        if (status.IsSuccess)
        {
            HasSkill = status.Value.IsInstalled;
            SkillToolTip = $"外部 Agent 用户级目录（作用于所有项目）：{status.Value.Directory}\n" +
                           $"{status.Value.DiscoveryNote}\n" +
                           "若同名 patchouli 文件夹存在，将按现状显示清除已有；清除会丢失自定义修改，需清除后再注入重置。" +
                           "MCP 配置、token 与权限不变；运行中的会话可能需重启。";
        }
        else
        {
            SkillToolTip = status.ErrorMessage ?? "无法读取 Skill 状态，请重新检测。";
        }

        Raise(nameof(HasSkillStatus));
        Raise(nameof(CanManageSkill));
        Raise(nameof(SkillActionText));
        Raise(nameof(SkillActionIcon));
    }

    internal void SetCustomPathFromSettings(string? value)
    {
        _isApplyingPath = true;
        try
        {
            CustomPath = value;
        }
        finally
        {
            _isApplyingPath = false;
        }
    }

    internal void BeginDetection()
    {
        IsDetecting = true;
        StatusText = "检测中...";
        VersionText = "";
        PathText = "";
        ErrorText = null;
        Raise(nameof(ShowInstallHelp));
    }

    internal void CancelDetection()
    {
        IsDetecting = false;
        StatusText = HasDetection ? StatusText : "检测已取消";
        Raise(nameof(ShowInstallHelp));
    }

    internal void ApplyDetection(AgentDetectionResult result)
    {
        IsDetecting = false;
        HasDetection = true;
        IsInstalled = result.IsFound;
        Offer = result.InstallOffer;
        StatusText = result.Status switch
        {
            AgentDetectionStatus.Found => "已安装",
            AgentDetectionStatus.NotInstalled => "未安装",
            AgentDetectionStatus.InvalidCustomPath => "未安装（自定义路径无效）",
            AgentDetectionStatus.InvalidExecutable => "已安装但无法启动",
            AgentDetectionStatus.VersionTooOld => "版本过低",
            AgentDetectionStatus.Unsupported => "不支持启动",
            _ => "未安装"
        };
        VersionText = result.IsFound ? FormatVersion(result.Version) : "";
        PathText = result.IsFound ? result.ExecutablePath ?? "" : "";
        ErrorText = result.IsFound ? null : result.ErrorMessage;
        RepositoryUrl = result.InstallOffer?.Repository;
        CanInstall = IsOpenCode && result.InstallOffer?.HasPlatformBinary == true;
        Raise(nameof(HasDetection));
        Raise(nameof(IsInstalled));
        Raise(nameof(ShowInstallHelp));
        Raise(nameof(InstallHelpText));
    }

    internal void BeginInstall()
    {
        IsInstalling = true;
        InstallProgress = 0;
        InstallStageText = "准备下载...";
        InstallErrorText = null;
        CanInstall = false;
    }

    internal void ApplyInstallProgress(AgentInstallProgress progress)
    {
        InstallProgress = Math.Clamp(progress.Fraction, 0, 1);
        InstallStageText = progress.Stage switch
        {
            AgentInstallStage.Downloading => DescribeDownload(progress),
            AgentInstallStage.Extracting => "正在解压安装包...",
            AgentInstallStage.Activating => "正在完成安装...",
            AgentInstallStage.Completed => "安装完成。",
            _ => InstallStageText
        };
    }

    internal void CompleteInstall()
    {
        InstallProgress = 1;
        InstallStageText = "安装完成。";
        InstallErrorText = null;
    }

    internal void EndInstall()
    {
        IsInstalling = false;
    }

    internal void SetInstallFailure(string message)
    {
        InstallErrorText = message;
        InstallStageText = "安装失败。";
        InstallProgress = 0;
        CanInstall = CreateCanInstallState();
    }

    internal void SetInstallError(string message)
    {
        InstallErrorText = message;
    }

    internal void SetInstallCancelled()
    {
        InstallErrorText = "安装已取消，临时文件已清理。";
        InstallStageText = "";
        InstallProgress = 0;
        CanInstall = CreateCanInstallState();
    }

    internal void ApplyInstalledPath(string executablePath)
    {
        SetCustomPathFromSettings(executablePath);
    }

    private bool CreateCanInstallState()
    {
        return IsOpenCode && Offer?.HasPlatformBinary == true;
    }

    private static string DescribeDownload(AgentInstallProgress progress)
    {
        string received = FormatBytes(progress.BytesReceived);
        if (progress.TotalBytes is > 0)
        {
            double ratio = progress.Fraction / 0.8;
            return $"正在下载... {ratio:P0}（{received} / {FormatBytes(progress.TotalBytes.Value)}）";
        }

        return $"正在下载... {received}";
    }

    private static string FormatVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? "版本未知" : $"版本 {version.Trim()}";
    }

    private static string FormatBytes(long bytes)
    {
        const long kilo = 1024;
        const long mega = kilo * 1024;
        const long giga = mega * 1024;
        if (bytes >= giga)
        {
            return $"{bytes / (double)giga:0.00} GB";
        }

        if (bytes >= mega)
        {
            return $"{bytes / (double)mega:0.00} MB";
        }

        return bytes >= kilo ? $"{bytes / (double)kilo:0.00} KB" : $"{bytes} B";
    }

    private void OpenRepository()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            return;
        }

        try
        {
            if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return;
            }

            using Process? browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, nameof(AgentIntegrationRowViewModel), "OpenRepository");
            _parent.ReportRepositoryLaunchFailure(DisplayName, exception);
        }
    }
}
