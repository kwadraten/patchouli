using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Cli;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Mcp;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class McpSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly IScheduler _timingScheduler;
    private readonly IScheduler _uiScheduler;
    private readonly Subject<Unit> _previewRequests = new();
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    private McpServerSettings _settings = new(4536, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);

    private McpServerSettings _persistedSettings =
        new(4536, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);

    private bool _isDirty;
    private bool _isConstructing;
    private bool _isSyncing;
    private long _editRevision;

    private long _loadGeneration;

    // Semantic cancellation: persistence file write window requires explicit cancellation token interruption.
    private CancellationTokenSource? _activeSaveCancellation;
    private int _previewGeneration;
    private CliInstallation _cliInstallation = new(null, null, false);
    private McpServerSettings? _discardBaseline;
    private bool _isConfirmingLeave;
    private bool _restartFailed;

    public McpSettingsViewModel(MainWindowViewModel main)
        : this(
            main,
            TaskPoolScheduler.Default,
            SynchronizationContext.Current is { } synchronizationContext
                ? new SynchronizationContextScheduler(synchronizationContext)
                : CurrentThreadScheduler.Instance)
    {
    }

    internal McpSettingsViewModel(
        MainWindowViewModel main,
        IScheduler timingScheduler,
        IScheduler uiScheduler)
    {
        _isConstructing = true;
        _main = main;
        _timingScheduler = timingScheduler;
        _uiScheduler = uiScheduler;

        Register(_previewRequests);

        Register(ReactiveUiFlow.SubscribeLatest(
            _previewRequests,
            TimeSpan.Zero,
            _timingScheduler,
            _uiScheduler,
            RefreshLibraryPreviewInternalAsync,
            ex => UnexpectedExceptions.Sink.Report(ex, nameof(McpSettingsViewModel), "RefreshLibraryPreview")));

        IDisposable mainSubscription = Observable
            .FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => _main.PropertyChanged += h,
                h => _main.PropertyChanged -= h)
            .Where(e => e.EventArgs.PropertyName is nameof(MainWindowViewModel.McpStatusText)
                or nameof(MainWindowViewModel.McpEndpoint)
                or nameof(MainWindowViewModel.McpServerRunning)
                or nameof(MainWindowViewModel.McpRunningSettingsRevision))
            .ObserveOn(_uiScheduler)
            .Subscribe(e =>
                {
                    Raise(e.EventArgs.PropertyName switch
                    {
                        nameof(MainWindowViewModel.McpStatusText) => nameof(McpStatusText),
                        nameof(MainWindowViewModel.McpEndpoint) => nameof(McpEndpoint),
                        nameof(MainWindowViewModel.McpServerRunning) => nameof(McpServerRunning),
                        nameof(MainWindowViewModel.McpRunningSettingsRevision) => nameof(RequiresReload),
                        _ => e.EventArgs.PropertyName ?? string.Empty
                    });
                    RefreshRequiresReload();
                },
                ex => UnexpectedExceptions.Sink.Report(ex, nameof(McpSettingsViewModel), "MainPropertyChanged"));
        Register(mainSubscription);

        GenerateTokenCommand = new AsyncCommand(GenerateTokenAsync);
        StartMcpCommand = new AsyncCommand(StartMcpAsync);
        StopMcpCommand = new AsyncCommand(StopMcpAsync, () => _main.CanStopMcpServer);
        SaveAndRestartCommand = new AsyncCommand(SaveAndRestartAsync);
        AddCliToPathCommand = new AsyncCommand(AddCliToPathAsync);
        RemoveCliFromPathCommand = new AsyncCommand(RemoveCliFromPathAsync);
        RefreshLibraryPreviewCommand = new AsyncCommand(RefreshLibraryPreviewAsync);
        CopyEndpointCommand = new AsyncCommand(CopyEndpointAsync);

        RunSendPermissions = CreateRunSendPermissions();
        SyncFromSettings(_settings);
        _isConstructing = false;
    }

    [ObservableProperty] public partial bool ExposeLibraryTags { get; set; }

    partial void OnExposeLibraryTagsChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _settings = _settings with { ExposeLibraryTags = value };
        MarkDirty();
    }

    [ObservableProperty] public partial bool ExposeLibraryCollections { get; set; }

    partial void OnExposeLibraryCollectionsChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _settings = _settings with { ExposeLibraryCollections = value };
        MarkDirty();
    }

    [ObservableProperty] public partial string LibraryPreviewText { get; private set; } = "";

    public AsyncCommand RefreshLibraryPreviewCommand { get; }

    /// <summary>
    /// Explains the relationship between this preview and the running MCP server. Exposure is
    /// saved-and-restarted policy, so the preview always renders the persisted settings rather
    /// than the unsaved draft and must never imply that an edit is already live.
    /// </summary>
    [ExcludeFromDerivedGeneration]
    public string LibraryPreviewHint
    {
        get
        {
            if (_isDirty)
            {
                return "预览使用已保存的设置；保存并重启 MCP Server 后该更改才会生效。";
            }

            if (RequiresReload)
            {
                return "正在运行的 MCP Server 仍使用旧设置，重启后预览才与运行输出一致。";
            }

            return "预览与正在运行的 MCP Server 暴露策略一致。";
        }
    }

    public Task RefreshLibraryPreviewAsync()
    {
        _previewRequests.OnNext(Unit.Default);
        return Task.CompletedTask;
    }

    private async Task RefreshLibraryPreviewInternalAsync(CancellationToken cancellationToken)
    {
        int generation = Interlocked.Increment(ref _previewGeneration);
        try
        {
            HostServices services = await _main.ServicesAsync();
            bool exposeTags = _persistedSettings.ExposeLibraryTags;
            bool exposeCollections = _persistedSettings.ExposeLibraryCollections;
            Result<McpLibraryProjection> projection = await Task.Run(() => services.Mcp
                .GetLibraryProjectionAsync(exposeTags, exposeCollections), cancellationToken);
            if (cancellationToken.IsCancellationRequested || generation != Volatile.Read(ref _previewGeneration))
            {
                return;
            }

            LibraryPreviewText = projection.IsSuccess
                ? McpYamlCodec.Encode(projection.Value)
                : $"ERROR {projection.ErrorCode}: {projection.ErrorMessage}";
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested || generation != Volatile.Read(ref _previewGeneration))
            {
                return;
            }

            LibraryPreviewText = $"ERROR: {exception.Message}";
        }

        Raise(nameof(LibraryPreviewHint));
    }

    [ObservableProperty] public partial int Port { get; set; }

    partial void OnPortChanged(int value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _settings = _settings with { Port = value };
        MarkDirty();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllowExternalAccess))]
    [NotifyPropertyChangedFor(nameof(IsAllowExternalAccessWarningVisible))]
    public partial string BindAddress { get; set; } = "127.0.0.1";

    partial void OnBindAddressChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string next = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
        if (_settings.BindAddress == next)
        {
            return;
        }

        _settings = _settings with { BindAddress = next };
        MarkDirty();
    }

    [ExcludeFromDerivedGeneration]
    public bool AllowExternalAccess
    {
        get => string.Equals(BindAddress, "0.0.0.0", StringComparison.Ordinal);
        set
        {
            string next = value ? "0.0.0.0" : "127.0.0.1";
            if (BindAddress == next)
            {
                return;
            }

            BindAddress = next;
        }
    }

    [ExcludeFromDerivedGeneration]
    public bool IsAllowExternalAccessWarningVisible => AllowExternalAccess && string.IsNullOrWhiteSpace(ServerToken);

    [ObservableProperty] public partial bool CorsEnabled { get; set; }

    partial void OnCorsEnabledChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _settings = _settings with { CorsEnabled = value };
        MarkDirty();
    }

    public string TransportDescription => "Streamable HTTP：使用同一个 /mcp 地址，POST 发送 JSON-RPC，GET 建立 SSE。";

    [ObservableProperty] public partial string AllowedOriginsText { get; set; } = "";

    partial void OnAllowedOriginsTextChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string[] origins = value.Split(new[] { '\r', '\n', ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_settings.AllowedOrigins.SequenceEqual(origins, StringComparer.Ordinal))
        {
            return;
        }

        _settings = _settings with { AllowedOrigins = origins };
        MarkDirty();
    }

    [ObservableProperty] public partial bool AuthRequired { get; set; }

    partial void OnAuthRequiredChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _settings = _settings with { AuthRequired = value };
        MarkDirty();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllowExternalAccessWarningVisible))]
    public partial string ServerToken { get; set; } = "";

    partial void OnServerTokenChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string? next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (_settings.Token != next)
        {
            _settings = _settings with { Token = next };
            MarkDirty();
        }
    }

    [ExcludeFromDerivedGeneration] public ObservableCollection<McpToolOverrideViewModel> ToolOverrides { get; } = new();

    [ExcludeFromDerivedGeneration] public string McpEndpoint => _main.McpEndpoint;

    [ExcludeFromDerivedGeneration] public string McpStatusText => _main.McpStatusText;

    [ExcludeFromDerivedGeneration] public bool McpServerRunning => _main.McpServerRunning;

    [ExcludeFromDerivedGeneration] public bool CanStopMcpServer => _main.CanStopMcpServer;

    [ObservableProperty] public partial string CliStatusText { get; private set; } = "patchouli-cli 未检测到（未随应用安装）";

    [ObservableProperty] public partial bool IsCliInPath { get; private set; }

    public AsyncCommand GenerateTokenCommand { get; }
    public AsyncCommand StartMcpCommand { get; }
    public AsyncCommand StopMcpCommand { get; }
    public AsyncCommand SaveAndRestartCommand { get; }
    public AsyncCommand AddCliToPathCommand { get; }
    public AsyncCommand RemoveCliFromPathCommand { get; }
    public AsyncCommand CopyEndpointCommand { get; }
    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty;

    [ExcludeFromDerivedGeneration]
    public bool HasPendingLeaveDecision => IsDirty || RequiresReload || _isConfirmingLeave;

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (_isConfirmingLeave)
        {
            return false;
        }

        if (!HasPendingLeaveDecision)
        {
            return true;
        }

        _isConfirmingLeave = true;
        try
        {
            _discardBaseline = _main.McpRunningSettings ?? _discardBaseline ?? _persistedSettings;
            ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
                new ConfirmDialogViewModel("应用 MCP 设置？",
                    "MCP 设置已更改，需要保存并重启服务后才能生效。请选择放弃更改，或保存并重启后离开。",
                    "保存并重启", "放弃更改"));
            if (choice == ConfirmDialogResult.Discard)
            {
                return await DiscardPendingChangesAsync();
            }

            if (choice != ConfirmDialogResult.Confirm)
            {
                return false;
            }

            IMcpServerSettingsService service = (await _main.ServicesAsync()).McpSettings;
            McpServerSettings draft = _settings;
            Result validation = await Task.Run(() => service.ValidateSettingsAsync(draft));
            if (validation.IsFailure)
            {
                LastError = validation.ErrorMessage;
                ValidationState = SettingsValidationState.Invalid;
                SetStatus($"无法保存并重启 MCP：{validation.ErrorMessage}");
                return false;
            }

            try
            {
                await SaveAndRestartAsync();
                return !IsDirty && !RequiresReload && _main.McpServerRunning;
            }
            catch (InvalidOperationException exception)
            {
                LastError = exception.Message;
                SetStatus($"MCP 重启失败：{exception.Message}");
                // The draft was persisted, but it has not been applied to a running listener.
                RequiresReload = true;
                _restartFailed = true;
                return false;
            }
        }
        finally
        {
            _isConfirmingLeave = false;
        }
    }

    private async Task<bool> DiscardPendingChangesAsync()
    {
        McpServerSettings baseline = _discardBaseline ?? _persistedSettings;
        if (!SameConfiguration(baseline, _persistedSettings))
        {
            IMcpServerSettingsService service = (await _main.ServicesAsync()).McpSettings;
            long revision = _persistedSettings.Revision;
            Result<McpServerSettings> restored =
                await Task.Run(() => service.SaveDraftSettingsAsync(baseline, revision));
            if (restored.IsFailure)
            {
                LastError = restored.ErrorMessage;
                SetStatus($"无法放弃 MCP 更改：{restored.ErrorMessage}");
                return false;
            }

            _persistedSettings = restored.Value;
        }

        await DiscardAsync();
        _restartFailed = false;
        RefreshRequiresReload();
        return !IsDirty && !RequiresReload;
    }

    public override async Task DiscardAsync()
    {
        _activeSaveCancellation?.Cancel();
        _editRevision++;
        _loadGeneration++;
        await _commitGate.WaitAsync();
        try
        {
            HostServices services = await _main.ServicesAsync();
            int generation = _main.LibraryGeneration;
            Result<McpServerSettings> persisted = await Task.Run(() => services.McpSettings.GetSettingsAsync());
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (persisted.IsSuccess)
            {
                _persistedSettings = persisted.Value;
            }

            SyncFromSettings(_persistedSettings);
            _isDirty = false;
            SaveState = SettingsSaveState.Clean;
            LastError = null;
            RefreshRequiresReload();
            await RefreshLibraryPreviewInternalAsync(CancellationToken.None);
            SetStatus("已放弃更改");
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private Task GenerateTokenAsync()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "").Replace("/", "")
            .Replace("=", "");
        ServerToken = token;
        return Task.CompletedTask;
    }

    private Task StartMcpAsync()
    {
        return _main.StartMcpServerAsync();
    }

    private Task StopMcpAsync()
    {
        return _main.StopMcpServerAsync("用户手动停止");
    }

    private async Task CopyEndpointAsync()
    {
        try
        {
            await _main.Clipboard.SetTextAsync(McpEndpoint);
            _main.Report("MCP 服务地址已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, nameof(McpSettingsViewModel), "CopyEndpoint");
            _main.ReportError($"复制 MCP 地址失败：{exception.Message}");
        }
    }

    private async Task AddCliToPathAsync()
    {
        HostServices services = await _main.ServicesAsync();
        Result result = services.CliPath.AddToPath();
        RefreshCliStatus(services);
        if (result.IsFailure)
        {
            LastError = result.ErrorMessage;
            SetStatus(result.ErrorMessage ?? "无法加入 PATH。");
            return;
        }

        SetStatus("patchouli-cli 已加入 PATH（可能需要重启终端生效）。");
    }

    private async Task RemoveCliFromPathAsync()
    {
        HostServices services = await _main.ServicesAsync();
        Result result = services.CliPath.RemoveFromPath();
        RefreshCliStatus(services);
        if (result.IsFailure)
        {
            LastError = result.ErrorMessage;
            SetStatus(result.ErrorMessage ?? "无法从 PATH 移除。");
            return;
        }

        SetStatus("patchouli-cli 已从 PATH 移除。");
    }

    private void RefreshCliStatus(HostServices services)
    {
        _cliInstallation = services.CliPath.GetInstallation();
        if (_cliInstallation.Path is null)
        {
            CliStatusText = "patchouli-cli 未检测到（未随应用安装）";
        }
        else
        {
            string version = string.IsNullOrWhiteSpace(_cliInstallation.Version)
                ? string.Empty
                : $" v{_cliInstallation.Version}";
            CliStatusText = _cliInstallation.InPath
                ? $"patchouli-cli{version} 已就绪，并已加入 PATH"
                : $"patchouli-cli{version} 已就绪，尚未加入 PATH";
        }

        IsCliInPath = _cliInstallation.InPath;
    }

    private async Task SaveAndRestartAsync()
    {
        if (IsDirty)
        {
            await SaveAsync();
        }

        if (IsDirty || SaveState == SettingsSaveState.Failed)
        {
            return;
        }

        await _main.RestartMcpServerAsync("应用新设置");
        if (!_main.McpServerRunning)
        {
            throw new InvalidOperationException("MCP Server 未能启动。请检查状态栏中的错误详情。");
        }

        RequiresReload = false;
        _restartFailed = false;
        _discardBaseline = _persistedSettings;
    }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsDirty)
        {
            SetStatus("MCP 设置有未保存的更改，已保留当前草稿。");
            return;
        }

        bool hadPendingApplication = RequiresReload;
        long loadGeneration = ++_loadGeneration;
        long editRevision = _editRevision;
        IMcpServerSettingsService service = (await _main.ServicesAsync()).McpSettings;
        Result<McpServerSettings> result =
            await Task.Run(() => service.GetSettingsAsync(cancellationToken), cancellationToken);
        if (result.IsFailure)
        {
            LastError = result.ErrorMessage;
            SetStatus(result.ErrorMessage ?? "无法读取 MCP 设置。");
            return;
        }

        Result validation = await Task.Run(() => service.ValidateSettingsAsync(result.Value, cancellationToken),
            cancellationToken);
        if (loadGeneration != _loadGeneration || editRevision != _editRevision || IsDirty)
        {
            SetStatus("MCP 设置已在加载期间变更，已保留当前草稿。");
            return;
        }

        _persistedSettings = result.Value;
        if (_discardBaseline is null || !hadPendingApplication)
        {
            _discardBaseline = _main.McpRunningSettings ?? result.Value;
        }

        SyncFromSettings(result.Value);
        _isDirty = false;
        SaveState = SettingsSaveState.Clean;
        LastError = validation.IsFailure ? validation.ErrorMessage : null;
        ValidationState = validation.IsFailure ? SettingsValidationState.Invalid : SettingsValidationState.Valid;
        RefreshRequiresReload();
        RefreshCliStatus(await _main.ServicesAsync());
        await RefreshLibraryPreviewInternalAsync(cancellationToken);
        SetStatus(validation.IsFailure ? $"已保存，启动服务前仍需补全：{validation.ErrorMessage}" : "已加载数据库 MCP 设置。");
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    public override async Task SaveAsync()
    {
        await _commitGate.WaitAsync();
        CancellationTokenSource saveCancellation = new();
        _activeSaveCancellation?.Cancel();
        _activeSaveCancellation?.Dispose();
        _activeSaveCancellation = saveCancellation;
        int generation = _main.LibraryGeneration;
        try
        {
            SaveState = SettingsSaveState.Saving;
            Status = "正在保存...";
            long revision = _editRevision;
            McpServerSettings draft = _settings with
            {
                AllowedOrigins = _settings.AllowedOrigins.ToArray(),
                ToolOverrides = _settings.ToolOverrides.ToArray()
            };
            IMcpServerSettingsService settingsService = (await _main.ServicesAsync()).McpSettings;
            long persistedRevision = _persistedSettings.Revision;
            CancellationToken token = saveCancellation.Token;
            Result<McpServerSettings> result = await Task.Run(() => settingsService.SaveDraftSettingsAsync(
                draft,
                persistedRevision,
                token), token);
            if (generation != _main.LibraryGeneration)
            {
                return;
            }

            if (result.IsFailure)
            {
                SaveState = SettingsSaveState.Failed;
                LastError = result.ErrorMessage;
                SetStatus(result.ErrorMessage ?? "MCP 设置保存失败。");
                return;
            }

            _persistedSettings = result.Value;
            if (revision == _editRevision)
            {
                _settings = result.Value;
                _isDirty = false;
                SaveState = SettingsSaveState.Saved;
                SetStatus("已保存");
            }
            else
            {
                SaveState = SettingsSaveState.Dirty;
                SetStatus("已保存旧版本，仍有新的未保存更改");
            }

            LastError = null;
            Result validation = await Task.Run(() => settingsService.ValidateSettingsAsync(result.Value, token), token);
            if (generation != _main.LibraryGeneration)
            {
                return;
            }

            ValidationState = validation.IsSuccess ? SettingsValidationState.Valid : SettingsValidationState.Invalid;
            if (validation.IsFailure)
            {
                LastError = validation.ErrorMessage;
                SetStatus($"已保存，启动服务前仍需补全：{validation.ErrorMessage}");
            }

            RefreshRequiresReload();
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
        catch (OperationCanceledException) when (saveCancellation.IsCancellationRequested)
        {
            if (generation != _main.LibraryGeneration)
            {
                return;
            }

            SaveState = SettingsSaveState.Dirty;
            SetStatus("保存已取消，当前草稿仍未保存");
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
        finally
        {
            if (ReferenceEquals(_activeSaveCancellation, saveCancellation))
            {
                _activeSaveCancellation = null;
            }

            saveCancellation.Dispose();
            _commitGate.Release();
        }
    }

    internal void UpdateToolOverride(string toolName, bool enabled)
    {
        List<McpToolOverride> overrides = _settings.ToolOverrides.Where(value => value.ToolName != toolName).ToList();
        if (!enabled)
        {
            overrides.Add(new McpToolOverride(toolName, false, "Disabled in Patchouli settings."));
        }
        else if (toolName is "patchouli.add" or "patchouli.view")
        {
            // The settings reader adds a deny for these tools when their override is absent.
            // Keep an explicit allow marker so a user can opt in globally and survive reload.
            overrides.Add(new McpToolOverride(toolName, true, null));
        }

        _settings = _settings with
        {
            ToolOverrides = overrides.OrderBy(value => value.ToolName, StringComparer.Ordinal).ToArray()
        };
        MarkDirty();
    }

    private void SetStatus(string text)
    {
        Status = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Contains("失败", StringComparison.Ordinal) || text.Contains("无法", StringComparison.Ordinal))
        {
            _main.ReportError(text);
        }
        else
        {
            _main.Report(text);
        }
    }

    private void MarkDirty()
    {
        _editRevision++;
        _loadGeneration++;
        _isDirty = true;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Raise(nameof(LibraryPreviewHint));
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
    }

    private void RefreshRequiresReload()
    {
        RequiresReload = _restartFailed || (_main.McpRunningSettings is { } running &&
                                            !SameConfiguration(running, _persistedSettings));
        Raise(nameof(LibraryPreviewHint));
    }

    private static bool SameConfiguration(McpServerSettings first, McpServerSettings second)
    {
        return first.Port == second.Port && first.BindAddress == second.BindAddress &&
               first.CorsEnabled == second.CorsEnabled && first.AuthRequired == second.AuthRequired &&
               first.Token == second.Token && first.ShellCommandTimeoutSeconds == second.ShellCommandTimeoutSeconds &&
               first.ExposeLibraryTags == second.ExposeLibraryTags &&
               first.ExposeLibraryCollections == second.ExposeLibraryCollections &&
               first.AllowedOrigins.SequenceEqual(second.AllowedOrigins) &&
               first.ToolOverrides.SequenceEqual(second.ToolOverrides) &&
               first.DomainPermissions.SequenceEqual(second.DomainPermissions);
    }

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<McpDomainPermissionRowViewModel> PermissionRows { get; } = new();

    internal void UpdateDomainPermission(string domain, string verb, bool enabled)
    {
        if (!McpPermissionPolicy.Supports(domain, verb))
        {
            return;
        }

        List<McpDomainPermission> permissions = _settings.DomainPermissions.ToList();
        string toolName = "patchouli." + verb;
        bool legacyGlobalDeny = verb != "send" &&
                                _settings.ToolOverrides.Any(value => value.ToolName == toolName && !value.Enabled);
        // Materialize the legacy global deny before removing it, so granting one cell cannot
        // accidentally grant every other domain for that verb.
        if (legacyGlobalDeny)
        {
            permissions.RemoveAll(value => value.Verb == verb);
            permissions.AddRange(
                McpPermissionPolicy.Domains.Select(value => new McpDomainPermission(value, verb, false)));
        }

        permissions.RemoveAll(value => value.Domain == domain && value.Verb == verb);
        permissions.Add(new McpDomainPermission(domain, verb, enabled));
        List<McpToolOverride> overrides = _settings.ToolOverrides
            .Where(value => verb == "send" || value.ToolName != toolName).ToList();
        if (verb is "add" or "view")
        {
            // Preserve the user's opt-in across the legacy migration on the next load. The
            // per-domain deny rows above continue to restrict the other domains.
            overrides.Add(new McpToolOverride(toolName, true, null));
        }

        _settings = _settings with
        {
            DomainPermissions = permissions.ToArray(),
            ToolOverrides = overrides.OrderBy(value => value.ToolName, StringComparer.Ordinal).ToArray()
        };
        MarkDirty();
    }

    [ExcludeFromDerivedGeneration] public RunSendPermissionsViewModel RunSendPermissions { get; private set; }

    private RunSendPermissionsViewModel CreateRunSendPermissions()
    {
        return new RunSendPermissionsViewModel(
            McpPermissionPolicy.RunSendDomains, _settings,
            (domain, enabled) => UpdateDomainPermission(domain, "send", enabled));
    }

    private void ReloadToolOverrides()
    {
        HashSet<string> disabled = _settings.ToolOverrides.Where(value => !value.Enabled)
            .Select(value => value.ToolName).ToHashSet(StringComparer.Ordinal);
        RunSendPermissions = CreateRunSendPermissions();
        Raise(nameof(RunSendPermissions));
        PermissionRows.Clear();
        foreach (string domain in McpPermissionPolicy.Domains.Where(domain =>
                     !McpPermissionPolicy.RunSendDomains.Contains(domain)))
        {
            PermissionRows.Add(new McpDomainPermissionRowViewModel(this, domain, _settings));
        }

        ToolOverrides.Clear();
        foreach (string tool in KnownTools)
        {
            bool isEnabled = !disabled.Contains(tool);
            ToolOverrides.Add(new McpToolOverrideViewModel(this, tool, isEnabled));
        }
    }

    private void SyncFromSettings(McpServerSettings settings)
    {
        _isSyncing = true;
        _settings = settings;
        ExposeLibraryTags = settings.ExposeLibraryTags;
        ExposeLibraryCollections = settings.ExposeLibraryCollections;
        Port = settings.Port;
        BindAddress = settings.BindAddress;
        CorsEnabled = settings.CorsEnabled;
        AllowedOriginsText = string.Join("\n", settings.AllowedOrigins);
        AuthRequired = settings.AuthRequired;
        ServerToken = settings.Token ?? "";
        _isSyncing = false;
        ReloadToolOverrides();
    }

    private static readonly string[] KnownTools =
    [
        "patchouli.find",
        "patchouli.fetch",
        "patchouli.put",
        "patchouli.add",
        "patchouli.view",
        "patchouli.cite",
        "patchouli.send"
    ];
}

public sealed partial class McpToolOverrideViewModel : ViewModelBase
{
    private readonly McpSettingsViewModel _parent;
    private readonly bool _isConstructing;

    public McpToolOverrideViewModel(McpSettingsViewModel parent, string toolName, bool enabled)
    {
        _isConstructing = true;
        _parent = parent;
        ToolName = toolName;
        Enabled = enabled;
        _isConstructing = false;
    }

    public string ToolName { get; }

    [ObservableProperty] public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (_isConstructing)
        {
            return;
        }

        _parent.UpdateToolOverride(ToolName, value);
    }
}

public sealed partial class McpDomainPermissionRowViewModel : ViewModelBase
{
    public McpDomainPermissionRowViewModel(McpSettingsViewModel parent, string domain, McpServerSettings settings)
    {
        Domain = domain == "/" ? "根目录 /" : domain;
        HasChildren = domain == "runs";
        Children = HasChildren ? parent.RunSendPermissions.Children : [];
        Cells = McpPermissionPolicy.Verbs.Select(verb =>
            {
                bool enabled = McpPermissionPolicy.Allows(settings, verb,
                    domain == "/" ? "patchouli://" : "patchouli://" + domain + "/");
                return new McpDomainPermissionCellViewModel(parent, domain, verb, enabled);
            })
            .ToArray();
    }

    public string Domain { get; }
    public bool HasChildren { get; }
    public IReadOnlyList<RunSendPermissionViewModel> Children { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpansionGlyph))]
    public partial bool IsExpanded { get; set; }

    [ExcludeFromDerivedGeneration] public string ExpansionGlyph => IsExpanded ? "▼" : "▶";
    public IReadOnlyList<McpDomainPermissionCellViewModel> Cells { get; }
}

public sealed partial class McpDomainPermissionCellViewModel : ViewModelBase
{
    private readonly McpSettingsViewModel _parent;
    private readonly string _domain;
    private readonly string _verb;
    private bool _initializing = true;

    public McpDomainPermissionCellViewModel(McpSettingsViewModel parent, string domain, string verb, bool enabled)
    {
        _parent = parent;
        _domain = domain;
        _verb = verb;
        IsAggregate = domain == "runs" && verb == "send";
        Aggregate = parent.RunSendPermissions;
        IsSupported = McpPermissionPolicy.Supports(domain, verb);
        Description = IsSupported ? domain + " / " + verb : $"{domain} 不支持 {verb}，无法授权。";
        Enabled = IsSupported && enabled;
        _initializing = false;
    }

    public string Description { get; }
    public bool IsAggregate { get; }
    public RunSendPermissionsViewModel Aggregate { get; }

    public bool IsSupported { get; }

    [ObservableProperty] public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (!IsSupported)
        {
            if (value)
            {
                Enabled = false;
            }

            return;
        }

        if (!_initializing)
        {
            _parent.UpdateDomainPermission(_domain, _verb, value);
        }
    }
}
