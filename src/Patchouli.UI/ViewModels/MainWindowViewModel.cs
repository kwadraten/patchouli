using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Dapper;
using Patchouli.Core.Credentials;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Search;
namespace Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.ViewModels.Editor;
using Patchouli.UI.ViewModels.Csl;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.Views;
using Patchouli.UI.Services;

public sealed class MainWindowViewModel : ViewModelBase
{
    private AppServices? _services;
    private McpHttpServer? _mcpServer;
    private readonly bool _autoStartMcpServer;
    private PatchouliAppSettings _settings;
    private readonly string? _settingsPath;
    private string _runtimeDatabasePath;

    public WorkspaceLayoutViewModel Layout { get; }
    public WorkspaceManager Workspace { get; }
    public ObservableCollection<WorkspaceTabViewModel> OpenTabs => Layout.Tabs;
    
    public WorkspaceTabViewModel? ActiveTab
    {
        get => Layout.ActiveTab;
        set
        {
            Layout.ActiveTab = value;
        }
    }

    public string RuntimeDatabasePath
    {
        get => _runtimeDatabasePath;
        set
        {
            if (_runtimeDatabasePath == value) return;
            _runtimeDatabasePath = value;
            Raise();
            Raise(nameof(VersionInfo));
            Settings?.LibrarySettings.NotifyRuntimeDatabasePathChanged();
        }
    }
    public string DefaultSyncRootPath => _settings.Runtime.DefaultSyncRoot;
    public ObservableCollection<SidebarFileSearchRootViewModel> FileSearchRoots { get; } = new();
    public bool HasFileSearchRoots => FileSearchRoots.Count > 0;
    public bool NoFileSearchRoots => !HasFileSearchRoots;
    public string Status { get; set; } = "请选择运行数据库路径，然后创建或打开资料库。";
    public bool StatusIsError { get; set; }
    public string McpEndpoint { get; private set; } = $"http://localhost:{McpServerOptions.DefaultPort}";
    public string McpStatusText { get; private set; } = "MCP: 未启动";
    public string McpStatusDetail { get; private set; } = "等待运行数据库打开。";
    public IBrush McpStatusBrush { get; private set; } = Brushes.Gray;
    public string VersionInfo => $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion} | {RuntimeDatabasePath}";
    public string StatusBarVersion => $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion}";
    public string SettingsFilePath => PatchouliAppSettings.ResolvePath(_settingsPath);
    public bool HasOpenRuntimeDatabase => _services is not null;
    public IClipboardService Clipboard { get; }
    public IDialogService Dialogs { get; }
    public IAppLogger Logger { get; }
    public LibraryShellViewModel Shell { get; }
    public SettingsViewModel Settings { get; }
    public FirstRunViewModel FirstRun { get; private set; }
    public bool IsFirstRunVisible { get; set; }
    public bool IsLibraryVisible => !IsFirstRunVisible;
    public bool IsSearchEnabled => !IsFirstRunVisible;
    public bool ShowInspectorPane { get => Layout.ShowInspectorPane; set => Layout.ShowInspectorPane = value; }
    public bool ShowSidebar => Layout.ShowSidebar && !Shell.IsReadingMode;
    public bool IsInspectorVisible => Layout.IsInspectorVisible && !Shell.IsReadingMode;
    public bool ShowSelectedDocumentTab => Layout.HasPdfWorkspaceTab;
    public bool ShowSettingsTab => Layout.HasSettingsTab;
    public bool ShowItemEditorTab => Layout.HasItemEditorTab;
    public bool IsLibraryTabActive => Layout.IsLibraryActive;
    public bool IsReaderTabActive => Layout.IsReaderActive;
    public bool IsSettingsVisible => Layout.IsSettingsActive;
    public bool IsItemEditorVisible => Layout.IsItemEditorActive;
    public string LibraryTabTitle => string.IsNullOrWhiteSpace(Shell.LibraryName) ? "我的书库" : Shell.LibraryName;
    public string PdfTabTitle => BuildItemWorkspaceTabTitle("PDF 工作台", Shell.SelectedItem?.Title ?? Shell.SelectedItem?.FileName ?? "PDF 阅读");
    public LibraryViewModel Library { get; }
    public PdfWorkspaceViewModel PdfWorkspace => GetWorkspaceContent<PdfWorkspaceViewModel>(WorkspaceTabKind.PdfWorkspace) ?? throw new InvalidOperationException("PDF workspace tab is not open.");
    public ItemEditorViewModel ItemEditor => GetWorkspaceContent<ItemEditorViewModel>(WorkspaceTabKind.ItemEditor) ?? throw new InvalidOperationException("Item editor tab is not open.");
    public BibliographyViewModel Bibliography { get; }
    public FileDocumentViewModel FileDocument { get; }
    public PageLayoutViewModel PageLayout { get; }
    public MockOcrViewModel MockOcr { get; }
    public OcrQueueViewModel OcrQueue { get; }
    public PdfRenderViewModel PdfRender { get; }
    public SearchEvidenceViewModel SearchEvidence { get; }
    public SearchProfileViewModel SearchProfiles { get; }
    public McpPreviewViewModel McpPreview { get; }
    public SnapshotViewModel Snapshot { get; }
    public SnapshotBranchViewModel SnapshotBranch { get; }
    public AboutViewModel About { get; }
    public AsyncCommand OpenDatabaseCommand { get; }
    public AsyncCommand CompleteFirstRunCommand { get; }
    public AsyncCommand ShowLibraryCommand { get; }
    public AsyncCommand ShowReadingCommand { get; }
    public AsyncCommand RunToolbarSearchCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }
    public AsyncCommand OpenMcpSettingsCommand { get; }
    public AsyncCommand OpenOcrQueueCommand { get; }
    public AsyncCommand ActivateSettingsTabCommand { get; }
    public AsyncCommand ActivateSearchTabCommand { get; }
    public AsyncCommand ActivateOcrQueueTabCommand { get; }
    public AsyncCommand ActivateAboutTabCommand { get; }
    public AsyncCommand CheckSyncStateCommand { get; }
    public AsyncCommand CopyCslBibliographyCommand { get; }
    public AsyncCommand ExportItemCommand { get; }
    public AsyncCommand CreateItemMenuCommand { get; }
    public AsyncCommand OpenItemEditorCommand { get; }
    public AsyncCommand EditSelectedItemCommand { get; }
    public AsyncCommand RunSelectedItemOcrCommand { get; }
    public AsyncCommand ClosePdfWorkspaceTabCommand { get; }
    public AsyncCommand CloseSettingsTabCommand { get; }
    public AsyncCommand CloseSearchTabCommand { get; }
    public AsyncCommand CloseOcrQueueTabCommand { get; }
    public AsyncCommand CloseItemEditorTabCommand { get; }
    public AsyncCommand CloseAboutTabCommand { get; }
    public AsyncCommand RebuildSearchIndexCommand { get; }
    public AsyncCommand ExportEvidenceMarkdownCommand { get; }
    public AsyncCommand ToggleInspectorPaneCommand { get; }
    public AsyncCommand ShowAboutCommand { get; }
    public AsyncCommand OpenCslStyleManagerCommand { get; }
    public UiCommandDescriptor CheckSyncStateDescriptor { get; }
    public UiCommandDescriptor CopyCslBibliographyDescriptor { get; }
    public UiCommandDescriptor ExportItemDescriptor { get; }

    public PatchouliAppSettings AppOptions => _settings;

    public void UpdateAppOptions(PatchouliAppSettings settings)

    {

        _settings = settings;

        _settings.Save(_settingsPath);

    }

    private void PersistRuntimeDatabasePathIfEnabled()
    {
        if (!_settings.Runtime.RememberLastDatabase) return;

        var normalizedPath = Path.GetFullPath(RuntimeDatabasePath);
        var currentPath = Path.GetFullPath(_settings.Runtime.RuntimeDatabasePath);
        if (string.Equals(normalizedPath, currentPath, StringComparison.OrdinalIgnoreCase)) return;

        UpdateAppOptions(_settings with { Runtime = _settings.Runtime with { RuntimeDatabasePath = normalizedPath } });
    }

    public MainWindowViewModel(IClipboardService? clipboard = null, IAppLogger? logger = null, IDialogService? dialogs = null, bool autoStartMcpServer = false, int mcpPort = McpServerOptions.DefaultPort, string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        _settings = PatchouliAppSettings.Load(settingsPath);
        _runtimeDatabasePath = _settings.Runtime.RememberLastDatabase
            ? _settings.Runtime.RuntimeDatabasePath
            : AppRuntimeOptions.Default().RuntimeDatabasePath;
        _autoStartMcpServer = autoStartMcpServer;
        McpEndpoint = $"http://localhost:{mcpPort}";
        Clipboard = clipboard ?? new AvaloniaClipboardService();
        Dialogs = dialogs ?? CreateDialogService();
        Logger = logger ?? new SimpleFileLogger(_settings.Runtime.LogDirectory);
        Layout = new WorkspaceLayoutViewModel();
        Layout.PropertyChanged += OnLayoutPropertyChanged;
        Workspace = new WorkspaceManager(Layout);
        Shell = new(this);
        Settings = new(this);
        Library = new(this);
        Bibliography = new(this);
        FileDocument = new(this);
        PageLayout = new(this);
        MockOcr = new(this);
        OcrQueue = new(this);
        PdfRender = new(this);
        SearchEvidence = new(this);
        SearchProfiles = new(this);
        McpPreview = new(this);
        Snapshot = new(this);
        SnapshotBranch = new(this);
        About = new(this);
        Shell.MinerUToken = _settings.MinerU.Token;
        Settings.MinerUTokenInput = _settings.MinerU.Token;
        OpenDatabaseCommand = new(async () =>
        {
            await StopMcpServerAsync("正在切换运行数据库。");
            _services = await AppServices.CreateAsync(RuntimeDatabasePath, _settings);
            PersistRuntimeDatabasePathIfEnabled();
            await LoadPersistedMinerUTokenAsync();
            await RefreshSidebarPathsAsync();
            Status = $"数据库已就绪：{RuntimeDatabasePath}";
            Raise(nameof(Status));
            Raise(nameof(VersionInfo));
            Raise(nameof(StatusBarVersion));
            if (_autoStartMcpServer) await StartMcpServerAsync(_services);
        });
        FirstRun = CreateFirstRunViewModel();
        
        Workspace.OpenOrActivate(WorkspaceTabKind.Library, "Library", "我的书库", "Database", false, () => Shell);
        
        CompleteFirstRunCommand = new(CompleteFirstRunAsync);
        ShowLibraryCommand = new(ShowLibraryAsync);
        ShowReadingCommand = new(ShowReadingAsync);
        RunToolbarSearchCommand = new(RunToolbarSearchAsync);
        OpenSettingsCommand = new(() => OpenSettingsAsync("mineru"));
        OpenMcpSettingsCommand = new(() => OpenSettingsAsync("mcp"));
        OpenOcrQueueCommand = new(OpenOcrQueueAsync);
        ActivateSettingsTabCommand = new(() => ActivateExistingTabAsync(WorkspaceTabKind.Settings));
        ActivateSearchTabCommand = new(() => ActivateExistingTabAsync(WorkspaceTabKind.SearchResults));
        ActivateOcrQueueTabCommand = new(() => ActivateExistingTabAsync(WorkspaceTabKind.OcrQueue));
        ActivateAboutTabCommand = new(() => ActivateExistingTabAsync(WorkspaceTabKind.About));
        CheckSyncStateCommand = new(CheckSyncStateAsync);
        CopyCslBibliographyCommand = new(CopyCslBibliographyAsync);
        ExportItemCommand = new(ExportSelectedItemBibliographyAsync);
        CreateItemMenuCommand = new(OpenNewItemEditorAsync);
        OpenItemEditorCommand = new(OpenItemEditorTabAsync);
        EditSelectedItemCommand = new(EditSelectedItemAsync);
        RunSelectedItemOcrCommand = new(RunSelectedItemOcrAsync);
        ClosePdfWorkspaceTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.PdfWorkspace));
        CloseSettingsTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.Settings));
        CloseSearchTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.SearchResults));
        CloseOcrQueueTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.OcrQueue));
        CloseItemEditorTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.ItemEditor));
        CloseAboutTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.About));
        RebuildSearchIndexCommand = new(() => ShowPlaceholderAsync("重建 FTS 索引入口将在后续任务中接入。"));
        ExportEvidenceMarkdownCommand = new(() => ExportEvidenceMarkdownToFileAsync(null));
        ToggleInspectorPaneCommand = new(() => { ShowInspectorPane = !ShowInspectorPane; return Task.CompletedTask; });
        ShowAboutCommand = new AsyncCommand(OpenAboutAsync);
        OpenCslStyleManagerCommand = new AsyncCommand(OpenCslStyleManagerAsync);
        CheckSyncStateDescriptor = new UiCommandDescriptor("sync.check_state", "查看同步/冲突状态", CheckSyncStateCommand);
        CopyCslBibliographyDescriptor = new UiCommandDescriptor("csl.copy_bibliography", "复制 CSL 题录", CopyCslBibliographyCommand);
        ExportItemDescriptor = new UiCommandDescriptor("csl.export_item", "导出题录", ExportItemCommand);
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceLayoutViewModel.ActiveTab):
                Raise(nameof(ActiveTab));
                RaiseShellSelectionChanged();
                break;
            case nameof(WorkspaceLayoutViewModel.ShowInspectorPane):
                Raise(nameof(ShowInspectorPane));
                break;
            case nameof(WorkspaceLayoutViewModel.ShowSidebar):
                Raise(nameof(ShowSidebar));
                break;
            case nameof(WorkspaceLayoutViewModel.IsInspectorVisible):
                Raise(nameof(IsInspectorVisible));
                break;
            case nameof(WorkspaceLayoutViewModel.HasPdfWorkspaceTab):
                Raise(nameof(ShowSelectedDocumentTab));
                break;
            case nameof(WorkspaceLayoutViewModel.HasSettingsTab):
                Raise(nameof(ShowSettingsTab));
                break;
            case nameof(WorkspaceLayoutViewModel.HasItemEditorTab):
                Raise(nameof(ShowItemEditorTab));
                break;
            case nameof(WorkspaceLayoutViewModel.IsLibraryActive):
                Raise(nameof(IsLibraryTabActive));
                break;
            case nameof(WorkspaceLayoutViewModel.IsReaderActive):
                Raise(nameof(IsReaderTabActive));
                break;
            case nameof(WorkspaceLayoutViewModel.IsSettingsActive):
                Raise(nameof(IsSettingsVisible));
                break;
            case nameof(WorkspaceLayoutViewModel.IsItemEditorActive):
                Raise(nameof(IsItemEditorVisible));
                break;
        }
    }

    public async Task<AppServices> ServicesAsync()
    {
        if (_services is not null) return _services;

        _services = await AppServices.CreateAsync(RuntimeDatabasePath, _settings);
        await LoadPersistedMinerUTokenAsync();
        if (_autoStartMcpServer)
        {
            await StartMcpServerAsync(_services);
        }

        return _services;
    }

    public async Task RefreshSidebarPathsAsync()
    {
        FileSearchRoots.Clear();

        try
        {
            var services = await ServicesAsync();
            var roots = await services.FileResolution.ListSearchRootsAsync();
            if (roots.IsSuccess)
            {
                await using var connection = services.ConnectionFactory.CreateConnection();
                await connection.OpenAsync();

                var filePaths = (await connection.QueryAsync<string>(
                    "select original_path from file_assets;")).ToArray();

                foreach (var root in roots.Value)
                {
                    var fileCount = filePaths.Count(path => IsPathUnderRoot(path, root.RootPath));

                    FileSearchRoots.Add(new SidebarFileSearchRootViewModel(
                        root.RootPath,
                        root.IsAvailable,
                        root.UpdatedAt,
                        fileCount));
                }
            }
        }
        catch
        {
            FileSearchRoots.Clear();
        }

        Raise(nameof(FileSearchRoots));
        Raise(nameof(HasFileSearchRoots));
        Raise(nameof(NoFileSearchRoots));
        Raise(nameof(DefaultSyncRootPath));
    }

    public async Task StartMcpServerAsync()
    {
        var services = await ServicesAsync();
        await StartMcpServerAsync(services);
    }

    public async Task StopMcpServerAsync(string detail = "MCP HTTP 服务已停止。")
    {
        if (_mcpServer is not null)
        {
            _mcpServer.ConnectionCountsChanged -= OnMcpConnectionCountsChanged;
            await _mcpServer.DisposeAsync();
            _mcpServer = null;
        }

        SetMcpStatus("MCP: 未启动", detail, Brushes.Gray);
    }

    private async Task StartMcpServerAsync(AppServices services)
    {
        if (_mcpServer?.IsRunning == true)
        {
            SetMcpStatus("MCP: 运行中", BuildMcpConnectionDetail(), Brushes.LimeGreen);
            return;
        }

        await StopMcpServerAsync("MCP HTTP 服务正在启动。");
        var settingsResult = await services.McpSettings.GetSettingsAsync();
        if (settingsResult.IsFailure)
        {
            var message = McpOutputSanitizer.Sanitize(settingsResult.ErrorMessage ?? "无法读取 MCP 设置。");
            SetMcpStatus("MCP: 错误", message, Brushes.IndianRed);
            return;
        }

        var serverSettings = settingsResult.Value;
        var validation = await services.McpSettings.ValidateSettingsAsync(serverSettings);
        if (validation.IsFailure)
        {
            var message = McpOutputSanitizer.Sanitize(validation.ErrorMessage ?? "MCP 设置无效。");
            SetMcpStatus("MCP: 错误", message, Brushes.IndianRed);
            await ShowBlockingOperationAsync("MCP 启动被阻止", message, "请在 MCP 设置中改回 127.0.0.1，或配置 token 后再允许局域网访问。", ["Validation failed before HTTP listener started.", message]);
            return;
        }

        SetMcpStatus("MCP: 启动中", $"正在监听 http://{serverSettings.BindAddress}:{serverSettings.Port}", Brushes.Goldenrod);
        var server = new McpHttpServer(new McpProtocolHandler(services.Mcp, services.ConnectionFactory, serverSettings), serverSettings);
        server.ConnectionCountsChanged += OnMcpConnectionCountsChanged;
        try
        {
            await server.StartAsync();
            _mcpServer = server;
            McpEndpoint = server.Endpoint;
            Raise(nameof(McpEndpoint));
            SetMcpStatus("MCP: 运行中", BuildMcpConnectionDetail(), Brushes.LimeGreen);
            await LogOperationAsync("mcp_http_start", $"MCP HTTP server listening on {server.Endpoint}");
        }
        catch (Exception ex)
        {
            server.ConnectionCountsChanged -= OnMcpConnectionCountsChanged;
            await server.DisposeAsync();
            var message = McpOutputSanitizer.Sanitize(ex.Message);
            SetMcpStatus("MCP: 错误", message, Brushes.IndianRed);
            await LogOperationAsync("mcp_http_start_failed", message);
        }
    }

    private void SetMcpStatus(string text, string detail, IBrush brush)
    {
        McpStatusText = text;
        McpStatusDetail = detail;
        McpStatusBrush = brush;
        Raise(nameof(McpStatusText));
        Raise(nameof(McpStatusDetail));
        Raise(nameof(McpStatusBrush));
    }

    private string BuildMcpConnectionDetail()
    {
        var active = _mcpServer?.ActiveConnectionCount ?? 0;
        var total = _mcpServer?.TotalConnectionCount ?? 0;
        return $"连接数: {active} / {total}";
    }

    private void OnMcpConnectionCountsChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _mcpServer)) return;

        void Update()
        {
            if (!ReferenceEquals(sender, _mcpServer) || _mcpServer?.IsRunning != true) return;
            McpStatusDetail = BuildMcpConnectionDetail();
            Raise(nameof(McpStatusDetail));
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Update();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Update);
        }
    }

    public void Report(string message) { Status = message; StatusIsError = false; Raise(nameof(Status)); Raise(nameof(StatusIsError)); }
    public void ReportError(string message) { Status = message; StatusIsError = true; Raise(nameof(Status)); Raise(nameof(StatusIsError)); }

    public Task ShowInlineFirstRunAsync()
    {
        FirstRun = CreateFirstRunViewModel();
        FirstRun.DatabasePath = RuntimeDatabasePath;
        IsFirstRunVisible = true;
        Raise(nameof(FirstRun));
        Raise(nameof(IsFirstRunVisible));
        Raise(nameof(IsLibraryVisible));
        Raise(nameof(IsSearchEnabled));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(LibraryTabTitle));
        return Task.CompletedTask;
    }

    public async Task HideInlineFirstRunAsync()
    {
        IsFirstRunVisible = false;
        Raise(nameof(IsFirstRunVisible));
        Raise(nameof(IsLibraryVisible));
        Raise(nameof(IsSearchEnabled));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(LibraryTabTitle));
        await Shell.RefreshItemsAsync();
    }

    private FirstRunViewModel CreateFirstRunViewModel() => new(OpenFirstRunDatabaseAsync) { DatabasePath = RuntimeDatabasePath, MinerUToken = Shell.MinerUToken, OnError = ReportError };

    private async Task<(FirstRunWorkflow Workflow, PdfDiscoveryService Discovery)> OpenFirstRunDatabaseAsync(string path)
    {
        RuntimeDatabasePath = path;
        await OpenDatabaseCommand.ExecuteAsync();
        var services = await ServicesAsync();
        return (services.FirstRunWorkflow, services.PdfDiscovery);
    }

    private async Task CompleteFirstRunAsync()
    {
        await FirstRun.FinishSetupCommand.ExecuteAsync();
        if (!FirstRun.IsComplete) return;
        var persisted = await SaveMinerUTokenSettingsAsync(FirstRun.MinerUToken);
        if (!persisted) return;
        Report("初始化完成。请选择题录，并通过右键菜单运行 MinerU OCR。");
        if (!string.IsNullOrWhiteSpace(FirstRun.ScanRoot))
        {
            var services = await ServicesAsync();
            var addedRoot = await services.FileResolution.AddSearchRootAsync(FirstRun.ScanRoot);
            if (addedRoot.IsFailure && addedRoot.ErrorCode != AppErrorCodes.InvalidState)
            {
                Report(addedRoot.ErrorMessage ?? "无法登记 FileSearchRoot。");
            }
        }
        await RefreshSidebarPathsAsync();
        await HideInlineFirstRunAsync();
    }

    public MinerUConfiguration CreateMinerUConfiguration(string token) => _settings.MinerU.ToConfiguration(token);

    public async Task<string> GetPersistedMinerUTokenAsync()
    {
        var services = await ServicesAsync();
        var secret = await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU);
        return secret.IsSuccess ? secret.Value : "";
    }

    public async Task<bool> SaveMinerUTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var services = await ServicesAsync();
        var saved = await services.Credentials.SaveOrUpdateProviderCredentialAsync(ProviderIds.MinerU, "MinerU API token", token.Trim());
        if (saved.IsFailure) { Report(saved.ErrorMessage ?? "无法保存 MinerU API token。"); return false; }
        return true;
    }

    public async Task<bool> SaveMinerUTokenSettingsAsync(string token)
    {
        var trimmed = token.Trim();
        var persisted = await SaveMinerUTokenAsync(trimmed);
        if (!persisted) return false;

        _settings = _settings with { MinerU = _settings.MinerU with { Token = trimmed } };
        _settings.Save(_settingsPath);
        Shell.MinerUToken = trimmed;
        FirstRun.MinerUToken = trimmed;
        Settings.MinerUTokenInput = trimmed;
        Shell.NotifyMinerUTokenChanged();
        Report("MinerU 凭据已保存。");
        return true;
    }

    private async Task LoadPersistedMinerUTokenAsync()
    {
        var token = await GetPersistedMinerUTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) token = _settings.MinerU.Token;
        Shell.MinerUToken = token;
        FirstRun.MinerUToken = token;
        Settings.MinerUTokenInput = token;
        Shell.NotifyMinerUTokenChanged();
    }

    public void RaiseShellSelectionChanged()
    {
        RaiseWorkspaceStateChanged();
    }

    private static IDialogService CreateDialogService()
    {
        var service = new DialogService();
        service.Register<BlockingOperationDialogViewModel, BlockingOperationDialog>();
        service.Register<ConflictResolutionDialogViewModel, ConflictResolutionDialog>();
        return service;
    }

    private async Task ShowBlockingOperationAsync(string title, string reason, string guidance, IReadOnlyList<string> logs)
    {
        var vm = new BlockingOperationDialogViewModel
        {
            Title = title,
            Reason = reason,
            Impact = "该操作已被阻止，未改变运行中的 MCP 服务。",
            IsIndeterminate = false,
            ProgressValue = 100,
            RecoveryGuidance = guidance
        };
        foreach (var log in logs)
        {
            vm.AddLog(log);
        }

        await Dialogs.ShowDialogAsync(vm);
    }

    public void RaiseLibraryTitleChanged()
    {
        var libTab = OpenTabs.FirstOrDefault(t => t.Kind == WorkspaceTabKind.Library);
        if (libTab != null) libTab.Title = LibraryTabTitle;
        Raise(nameof(LibraryTabTitle));
    }

    public async Task LogOperationAsync(string operation, string message)
    {
        try { await Logger.LogAsync(operation, message); }
        catch { }
    }

    private T? GetWorkspaceContent<T>(WorkspaceTabKind kind) where T : ViewModelBase =>
        Workspace.FindKind(kind)?.Content as T;

    public void RefreshItemWorkspaceTabTitles(string itemId, string itemTitle, ViewModelBase? editorContent = null)
    {
        var pdfTab = Workspace.Find($"PdfWorkspace_{itemId}");
        if (pdfTab is not null)
        {
            pdfTab.Title = BuildItemWorkspaceTabTitle("PDF 工作台", itemTitle);
        }

        var editorTab = Workspace.Find($"ItemEditor_{itemId}") ?? OpenTabs.FirstOrDefault(tab => editorContent is not null && ReferenceEquals(tab.Content, editorContent));
        if (editorTab is not null)
        {
            editorTab.Title = BuildItemWorkspaceTabTitle("编辑题录", itemTitle);
        }
    }

    private static string BuildItemWorkspaceTabTitle(string pageName, string itemTitle)
    {
        var title = string.IsNullOrWhiteSpace(itemTitle) ? "未命名题录" : itemTitle.Trim();
        return TruncateWorkspaceTabTitle($"{pageName}：{title}");
    }

    private static string TruncateWorkspaceTabTitle(string title)
    {
        const int maxLength = 32;
        const string suffix = "...";
        return title.Length <= maxLength
            ? title
            : title[..(maxLength - suffix.Length)] + suffix;
    }

    private async Task OpenCslStyleManagerAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.CslStyleManager, "CslStyleManager", "CSL 样式", "Quote", true, () => new CslStyleManagerViewModel(this));
        if (ActiveTab?.Content is CslStyleManagerViewModel csl)
        {
            await csl.InitializeAsync();
        }
    }

    public async Task OpenSettingsAsync(string section, string? statusMessage = null)
    {
        await ActivateTabAsync(WorkspaceTabKind.Settings, "Settings", "设置", "Menu", true, () => Settings);
        var icon = section.Equals("mcp", StringComparison.OrdinalIgnoreCase) ? "Server" : section.Equals("csl", StringComparison.OrdinalIgnoreCase) ? "Quote" : section.Equals("library", StringComparison.OrdinalIgnoreCase) ? "Database" : "ScanText";
        Settings.ActiveCategory = Settings.Categories.FirstOrDefault(c => c.Icon == icon);
        if (string.Equals(section, "mcp", StringComparison.OrdinalIgnoreCase))
        {
            await Settings.McpSettings.LoadAsync();
        }
        
        RaiseShellSelectionChanged();
    }

    public async Task OpenAboutAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.About, "About", "关于", "Info", true, () => About);
        RaiseShellSelectionChanged();
    }

    private async Task OpenOcrQueueAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.OcrQueue, "OcrQueue", "OCR 队列", "List", true, () => OcrQueue);
        await OcrQueue.RefreshAsync();
    }

    private Task ShowPlaceholderAsync(string message)
    {
        Report(message);
        return Task.CompletedTask;
    }

    private async Task ShowLibraryAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.Library, "Library", LibraryTabTitle, "Database", false, () => Shell);
    }

    public async Task ShowReadingAsync()
    {
        var item = Shell.SelectedItem;
        if (item is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        var tabId = $"PdfWorkspace_{item.ItemId}";
        var title = BuildItemWorkspaceTabTitle("PDF 工作台", item.Title ?? item.FileName);
        await ActivateTabAsync(WorkspaceTabKind.PdfWorkspace, tabId, title, "FolderOpen", true, () => new PdfWorkspaceViewModel(this));
        
        if (ActiveTab?.Content is PdfWorkspaceViewModel pdf && !pdf.HasImage)
        {
            await pdf.LoadSelectedItemAsync(item);
        }
    }

    private async Task RunToolbarSearchAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.SearchResults, "SearchResults", "搜索结果", "Search", true, () => SearchEvidence);
        await SearchEvidence.SearchCommand.ExecuteAsync();
    }

    private async Task OpenNewItemEditorAsync()
    {
        var tabId = $"ItemEditor_New_{Guid.NewGuid()}";
        await ActivateTabAsync(WorkspaceTabKind.ItemEditor, tabId, "新建题录", "Pencil", true, () => new ItemEditorViewModel(this));
        
        if (ActiveTab?.Content is ItemEditorViewModel editor)
        {
            await editor.NewAsync();
        }
    }

    private async Task EditSelectedItemAsync()
    {
        var item = Shell.SelectedItem;
        if (item is null)
        {
            Report("请先选择一个题录。");
            return;
        }
        var tabId = $"ItemEditor_{item.ItemId}";
        await ActivateTabAsync(WorkspaceTabKind.ItemEditor, tabId, BuildItemWorkspaceTabTitle("编辑题录", item.Title), "Pencil", true, () => new ItemEditorViewModel(this));
        if (ActiveTab?.Content is ItemEditorViewModel editor)
        {
            await editor.LoadAsync(item.ItemId);
        }
}

    private Task OpenItemEditorTabAsync()
    {
        if (Workspace.ActivateKind(WorkspaceTabKind.ItemEditor))
        {
            return Task.CompletedTask;
        }

        return EditSelectedItemAsync();
    }

    private async Task RunSelectedItemOcrAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.Library, "Library", LibraryTabTitle, "Database", false, () => Shell);
        if (Shell.SelectedItem is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        await Shell.SelectedItem.RunOcrCommand.ExecuteAsync();
    }

    private Task ActivateExistingTabAsync(WorkspaceTabKind kind)
    {
        Workspace.ActivateKind(kind);
        return Task.CompletedTask;
    }

    private Task CheckSyncStateAsync()
    {
        Report("同步/冲突状态：当前未检测到需要处理的阻塞项。快照导入冲突将作为独立分支打开以供检查。");
        return Task.CompletedTask;
    }

    private async Task CopyCslBibliographyAsync()
    {
        var item = Shell.SelectedItem;
        if (item is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        var rendered = await (await ServicesAsync()).CslRenderer.RenderAsync(new CslRenderRequest([ItemId.Parse(item.ItemId)]));
        if (rendered.IsFailure)
        {
            Report($"CSL 题录生成失败：{rendered.ErrorCode} {rendered.ErrorMessage}");
            return;
        }

        await Clipboard.SetTextAsync(rendered.Value.RenderedText);
        var warning = rendered.Value.Warnings.Count > 0 ? $"，warnings: {string.Join("; ", rendered.Value.Warnings)}" : "";
        Report($"已复制 CSL 题录：{rendered.Value.StyleDisplayName}{warning}");
    }

    private async Task ExportSelectedItemBibliographyAsync()
    {
        var item = Shell.SelectedItem;
        if (item is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        var rendered = await (await ServicesAsync()).CslRenderer.RenderAsync(new CslRenderRequest([ItemId.Parse(item.ItemId)]));
        if (rendered.IsFailure)
        {
            Report($"题录导出失败：{rendered.ErrorCode} {rendered.ErrorMessage}");
            return;
        }

        await Clipboard.SetTextAsync(rendered.Value.RenderedHtml);
        Report($"已导出题录 HTML 到剪贴板：{rendered.Value.StyleDisplayName}");
    }

    private Task ActivateTabAsync(WorkspaceTabKind kind, string tabId, string title, string iconName, bool isClosable, Func<ViewModelBase> contentFactory)
    {
        Workspace.OpenOrActivate(kind, tabId, title, iconName, isClosable, contentFactory);
        return Task.CompletedTask;
    }

    private Task CloseTabAsync(WorkspaceTabKind kind)
    {
        Workspace.CloseKind(kind);
        return Task.CompletedTask;
    }

    private Task CloseTabAsync(string tabId)
    {
        Workspace.Close(tabId);
        return Task.CompletedTask;
    }

    private void RaiseWorkspaceStateChanged()
    {
        Raise(nameof(ShowSidebar));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(ShowSelectedDocumentTab));
        Raise(nameof(ShowSettingsTab));
        Raise(nameof(ShowItemEditorTab));
        Raise(nameof(IsLibraryTabActive));
        Raise(nameof(IsReaderTabActive));
        Raise(nameof(IsSettingsVisible));
        Raise(nameof(IsItemEditorVisible));
    }

    public async Task ExportEvidenceMarkdownToFileAsync(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(SearchEvidence.EvidenceRef))
        {
            Report("请先选择一个可导出的 EvidenceRef。");
            SearchEvidence.Output = "ERROR validation_failed: EvidenceRef is required.";
            SearchEvidence.RaiseOutput();
            return;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            Report("请选择 Evidence Markdown 导出路径。");
            SearchEvidence.Output = "ERROR validation_failed: Export path is required.";
            SearchEvidence.RaiseOutput();
            return;
        }

        var markdown = await (await ServicesAsync()).Evidence.CreateMarkdownAsync(SearchEvidence.EvidenceRef);
        if (markdown.IsFailure)
        {
            var message = $"ERROR {markdown.ErrorCode}: {markdown.ErrorMessage}";
            SearchEvidence.Output = message;
            SearchEvidence.RaiseOutput();
            Report(message);
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(targetPath, markdown.Value.Markdown);
        SearchEvidence.Markdown = markdown.Value.Markdown;
        SearchEvidence.Output = $"Exported Evidence Markdown: {targetPath}";
        SearchEvidence.RaiseMarkdown();
        SearchEvidence.RaiseOutput();
        Report(SearchEvidence.Output);
        await LogOperationAsync("export_evidence_markdown", SearchEvidence.Output);
    }

    private static bool IsPathUnderRoot(string path, string rootPath)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = EnsureTrailingDirectorySeparator(rootPath);
        return string.Equals(fullPath, Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }
}
