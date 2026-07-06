using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using Dapper;
using Patchouli.Core.Credentials;
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

namespace Patchouli.UI;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class AsyncCommand : System.Windows.Input.ICommand
{
    private readonly Func<Task> _run; public AsyncCommand(Func<Task> run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } } public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await _run();
    public Task ExecuteAsync() => _run();
}

public enum WorkspaceTabKind
{
    Library,
    PdfWorkspace,
    Settings,
    SearchResults,
    OcrQueue,
    ItemEditor
}

public sealed class MainWindowViewModel : ViewModelBase
{
    private AppServices? _services;
    private McpHttpServer? _mcpServer;
    private readonly bool _autoStartMcpServer;
    private readonly int _mcpPort;
    private PatchouliAppSettings _settings;
    private readonly string? _settingsPath;
    private string _runtimeDatabasePath;
    private bool _showInspectorPane = true;
    private WorkspaceTabKind _activeTab = WorkspaceTabKind.Library;
    private bool _isPdfWorkspaceTabOpen;
    private bool _isSettingsTabOpen;
    private bool _isSearchTabOpen;
    private bool _isOcrQueueTabOpen;
    private bool _isItemEditorTabOpen;

    public string RuntimeDatabasePath { get => _runtimeDatabasePath; set { _runtimeDatabasePath = value; Raise(); Raise(nameof(VersionInfo)); } }
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
    public IAppLogger Logger { get; }
    public LibraryShellViewModel Shell { get; }
    public SettingsViewModel Settings { get; }
    public FirstRunViewModel FirstRun { get; private set; }
    public bool IsFirstRunVisible { get; set; }
    public bool IsLibraryVisible => !IsFirstRunVisible;
    public bool IsSearchEnabled => !IsFirstRunVisible;
    public bool ShowInspectorPane { get => _showInspectorPane; set { if (_showInspectorPane == value) return; _showInspectorPane = value; Raise(); Raise(nameof(IsInspectorVisible)); } }
    public WorkspaceTabKind ActiveTab => _activeTab;
    public bool IsSettingsVisible => IsLibraryVisible && _activeTab == WorkspaceTabKind.Settings;
    public bool IsSearchVisible => IsLibraryVisible && _activeTab == WorkspaceTabKind.SearchResults;
    public bool IsOcrQueueVisible => IsLibraryVisible && _activeTab == WorkspaceTabKind.OcrQueue;
    public bool IsItemEditorVisible => IsLibraryVisible && _activeTab == WorkspaceTabKind.ItemEditor;
    public bool ShowWorkspaceShell => ShowLibraryPage || ShowPdfWorkspacePage;
    public bool ShowLibraryPage => IsLibraryVisible && _activeTab == WorkspaceTabKind.Library && Shell.ShowLibraryList;
    public bool ShowPdfWorkspacePage => IsLibraryVisible && _activeTab == WorkspaceTabKind.PdfWorkspace && Shell.ShowPdfWorkspace;
    public bool ShowSettingsWorkspace => IsLibraryVisible && IsSettingsVisible;
    public bool ShowSearchWorkspace => IsLibraryVisible && IsSearchVisible;
    public bool ShowOcrQueueWorkspace => IsLibraryVisible && IsOcrQueueVisible;
    public bool ShowItemEditorWorkspace => IsLibraryVisible && IsItemEditorVisible;
    public bool ShowSidebar => ShowLibraryPage;
    public bool IsInspectorVisible => ShowLibraryPage && ShowInspectorPane;
    public bool ShowSelectedDocumentTab => IsLibraryVisible && _isPdfWorkspaceTabOpen;
    public bool ShowSettingsTab => IsLibraryVisible && _isSettingsTabOpen;
    public bool ShowSearchTab => IsLibraryVisible && _isSearchTabOpen;
    public bool ShowOcrQueueTab => IsLibraryVisible && _isOcrQueueTabOpen;
    public bool ShowItemEditorTab => IsLibraryVisible && _isItemEditorTabOpen;
    public bool IsLibraryTabActive => IsLibraryVisible && _activeTab == WorkspaceTabKind.Library;
    public bool IsReaderTabActive => IsLibraryVisible && _activeTab == WorkspaceTabKind.PdfWorkspace;
    public bool IsOcrQueueTabActive => ShowOcrQueueWorkspace;
    public bool IsItemEditorTabActive => ShowItemEditorWorkspace;
    public string LibraryTabTitle => string.IsNullOrWhiteSpace(Shell.LibraryName) ? "我的书库" : Shell.LibraryName;
    public string PdfTabTitle => string.IsNullOrWhiteSpace(Shell.SelectedItem?.FileName)
        ? Shell.SelectedItem?.Title ?? "PDF 阅读"
        : Shell.SelectedItem.FileName;
    public LibraryViewModel Library { get; }
    public BibliographyViewModel Bibliography { get; }
    public FileDocumentViewModel FileDocument { get; }
    public PageLayoutViewModel PageLayout { get; }
    public MockOcrViewModel MockOcr { get; }
    public OcrQueueViewModel OcrQueue { get; }
    public PdfRenderViewModel PdfRender { get; }
    public PdfWorkspaceViewModel PdfWorkspace { get; }
    public SearchEvidenceViewModel SearchEvidence { get; }
    public SearchProfileViewModel SearchProfiles { get; }
    public ItemEditorViewModel ItemEditor { get; }
    public McpPreviewViewModel McpPreview { get; }
    public SnapshotViewModel Snapshot { get; }
    public SnapshotBranchViewModel SnapshotBranch { get; }
    public AsyncCommand OpenDatabaseCommand { get; }
    public AsyncCommand CompleteFirstRunCommand { get; }
    public AsyncCommand ShowLibraryCommand { get; }
    public AsyncCommand ShowReadingCommand { get; }
    public AsyncCommand RunToolbarSearchCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }
    public AsyncCommand OpenOcrQueueCommand { get; }
    public AsyncCommand ActivateSettingsTabCommand { get; }
    public AsyncCommand ActivateSearchTabCommand { get; }
    public AsyncCommand ActivateOcrQueueTabCommand { get; }
    public AsyncCommand CreateItemMenuCommand { get; }
    public AsyncCommand OpenItemEditorCommand { get; }
    public AsyncCommand EditSelectedItemCommand { get; }
    public AsyncCommand RunSelectedItemOcrCommand { get; }
    public AsyncCommand ClosePdfWorkspaceTabCommand { get; }
    public AsyncCommand CloseSettingsTabCommand { get; }
    public AsyncCommand CloseSearchTabCommand { get; }
    public AsyncCommand CloseOcrQueueTabCommand { get; }
    public AsyncCommand CloseItemEditorTabCommand { get; }
    public AsyncCommand RebuildSearchIndexCommand { get; }
    public AsyncCommand ExportEvidenceMarkdownCommand { get; }
    public AsyncCommand ToggleInspectorPaneCommand { get; }
    public AsyncCommand ShowAboutCommand { get; }
    public AsyncCommand ShowLicenseCommand { get; }

    public MainWindowViewModel(IClipboardService? clipboard = null, IAppLogger? logger = null, bool autoStartMcpServer = false, int mcpPort = McpServerOptions.DefaultPort, string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        _settings = PatchouliAppSettings.Load(settingsPath);
        _runtimeDatabasePath = _settings.Runtime.RuntimeDatabasePath;
        _autoStartMcpServer = autoStartMcpServer;
        _mcpPort = mcpPort;
        McpEndpoint = $"http://localhost:{mcpPort}";
        Clipboard = clipboard ?? new AvaloniaClipboardService();
        Logger = logger ?? new SimpleFileLogger(_settings.Runtime.LogDirectory);
        PdfWorkspace = new(this);
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
        ItemEditor = new(this);
        McpPreview = new(this);
        Snapshot = new(this);
        SnapshotBranch = new(this);
        Shell.MinerUToken = _settings.MinerU.Token;
        Settings.SyncFromCurrentSettings(_settings.MinerU.Token);
        OpenDatabaseCommand = new(async () =>
        {
            await StopMcpServerAsync("正在切换运行数据库。");
            _services = await AppServices.CreateAsync(RuntimeDatabasePath, _settings);
            await LoadPersistedMinerUTokenAsync();
            await RefreshSidebarPathsAsync();
            Status = $"数据库已就绪：{RuntimeDatabasePath}";
            Raise(nameof(Status));
            Raise(nameof(VersionInfo));
            Raise(nameof(StatusBarVersion));
            if (_autoStartMcpServer) await StartMcpServerAsync(_services);
        });
        FirstRun = CreateFirstRunViewModel();
        CompleteFirstRunCommand = new(CompleteFirstRunAsync);
        ShowLibraryCommand = new(ShowLibraryAsync);
        ShowReadingCommand = new(ShowReadingAsync);
        RunToolbarSearchCommand = new(RunToolbarSearchAsync);
        OpenSettingsCommand = new(() => OpenSettingsAsync("mineru"));
        OpenOcrQueueCommand = new(OpenOcrQueueAsync);
        ActivateSettingsTabCommand = new(() => ActivateExistingTabAsync(WorkspaceTabKind.Settings));
        ActivateSearchTabCommand = new(() => ActivateExistingTabAsync(WorkspaceTabKind.SearchResults));
        ActivateOcrQueueTabCommand = new(() => ActivateExistingTabAsync(WorkspaceTabKind.OcrQueue));
        CreateItemMenuCommand = new(OpenNewItemEditorAsync);
        OpenItemEditorCommand = new(OpenItemEditorTabAsync);
        EditSelectedItemCommand = new(EditSelectedItemAsync);
        RunSelectedItemOcrCommand = new(RunSelectedItemOcrAsync);
        ClosePdfWorkspaceTabCommand = new(ClosePdfWorkspaceTabAsync);
        CloseSettingsTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.Settings));
        CloseSearchTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.SearchResults));
        CloseOcrQueueTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.OcrQueue));
        CloseItemEditorTabCommand = new(() => CloseTabAsync(WorkspaceTabKind.ItemEditor));
        RebuildSearchIndexCommand = new(() => ShowPlaceholderAsync("重建 FTS 索引入口将在后续任务中接入。"));
        ExportEvidenceMarkdownCommand = new(() => ExportEvidenceMarkdownToFileAsync(null));
        ToggleInspectorPaneCommand = new(() => { ShowInspectorPane = !ShowInspectorPane; return Task.CompletedTask; });
        ShowAboutCommand = new(() => ShowPlaceholderAsync(StatusBarVersion));
        ShowLicenseCommand = new(() => ShowPlaceholderAsync("许可证页面将在后续任务中接入。"));
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
            await _mcpServer.DisposeAsync();
            _mcpServer = null;
        }

        SetMcpStatus("MCP: 未启动", detail, Brushes.Gray);
    }

    private async Task StartMcpServerAsync(AppServices services)
    {
        if (_mcpServer?.IsRunning == true)
        {
            SetMcpStatus("MCP: 运行中", $"服务地址：{McpEndpoint}", Brushes.LimeGreen);
            return;
        }

        await StopMcpServerAsync("MCP HTTP 服务正在启动。");
        SetMcpStatus("MCP: 启动中", $"正在监听 {McpEndpoint}", Brushes.Goldenrod);
        var server = new McpHttpServer(new McpProtocolHandler(services.Mcp, services.ConnectionFactory), _mcpPort);
        try
        {
            await server.StartAsync();
            _mcpServer = server;
            McpEndpoint = server.Endpoint;
            Raise(nameof(McpEndpoint));
            SetMcpStatus("MCP: 运行中", $"服务地址：{server.Endpoint}", Brushes.LimeGreen);
            await LogOperationAsync("mcp_http_start", $"MCP HTTP server listening on {server.Endpoint}");
        }
        catch (Exception ex)
        {
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
        Raise(nameof(ShowWorkspaceShell));
        Raise(nameof(ShowLibraryPage));
        Raise(nameof(ShowPdfWorkspacePage));
        Raise(nameof(ShowSettingsWorkspace));
        Raise(nameof(ShowSearchWorkspace));
        Raise(nameof(ShowOcrQueueWorkspace));
        Raise(nameof(ShowItemEditorWorkspace));
        Raise(nameof(ShowSidebar));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(ShowSelectedDocumentTab));
        Raise(nameof(ShowSettingsTab));
        Raise(nameof(ShowSearchTab));
        Raise(nameof(ShowOcrQueueTab));
        Raise(nameof(ShowItemEditorTab));
        Raise(nameof(LibraryTabTitle));
        return Task.CompletedTask;
    }

    public async Task HideInlineFirstRunAsync()
    {
        IsFirstRunVisible = false;
        Raise(nameof(IsFirstRunVisible));
        Raise(nameof(IsLibraryVisible));
        Raise(nameof(IsSearchEnabled));
        Raise(nameof(ShowWorkspaceShell));
        Raise(nameof(ShowLibraryPage));
        Raise(nameof(ShowPdfWorkspacePage));
        Raise(nameof(ShowSettingsWorkspace));
        Raise(nameof(ShowSearchWorkspace));
        Raise(nameof(ShowOcrQueueWorkspace));
        Raise(nameof(ShowItemEditorWorkspace));
        Raise(nameof(ShowSidebar));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(ShowSelectedDocumentTab));
        Raise(nameof(ShowSettingsTab));
        Raise(nameof(ShowSearchTab));
        Raise(nameof(ShowOcrQueueTab));
        Raise(nameof(ShowItemEditorTab));
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
        Settings.SyncFromCurrentSettings(trimmed);
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
        Settings.SyncFromCurrentSettings(token);
        Shell.NotifyMinerUTokenChanged();
    }

    public void RaiseShellSelectionChanged()
    {
        Raise(nameof(ShowSelectedDocumentTab));
        Raise(nameof(PdfTabTitle));
        Raise(nameof(ShowLibraryPage));
        Raise(nameof(ShowPdfWorkspacePage));
        Raise(nameof(ShowSidebar));
        Raise(nameof(IsInspectorVisible));
        Raise(nameof(IsLibraryTabActive));
        Raise(nameof(IsReaderTabActive));
        Raise(nameof(IsItemEditorTabActive));
    }

    public void RaiseLibraryTitleChanged()
    {
        Raise(nameof(LibraryTabTitle));
    }

    public async Task LogOperationAsync(string operation, string message)
    {
        try { await Logger.LogAsync(operation, message); }
        catch { }
    }

    public async Task OpenSettingsAsync(string section, string? statusMessage = null)
    {
        _isSettingsTabOpen = true;
        await ActivateTabAsync(WorkspaceTabKind.Settings);
        Settings.FocusMinerU(statusMessage);
        if (HasOpenRuntimeDatabase)
        {
            await Settings.RefreshAsync();
        }
        else
        {
            Settings.ClearServiceBackedState();
        }
        RaiseShellSelectionChanged();
    }

    private async Task OpenOcrQueueAsync()
    {
        _isOcrQueueTabOpen = true;
        await ActivateTabAsync(WorkspaceTabKind.OcrQueue);
        await OcrQueue.RefreshAsync();
    }

    private Task ShowPlaceholderAsync(string message)
    {
        Report(message);
        return Task.CompletedTask;
    }

    private async Task ShowLibraryAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.Library);
    }

    private async Task ShowReadingAsync()
    {
        if (Shell.SelectedItem is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        _isPdfWorkspaceTabOpen = true;
        await ActivateTabAsync(WorkspaceTabKind.PdfWorkspace);
    }

    private async Task RunToolbarSearchAsync()
    {
        _isSearchTabOpen = true;
        await ActivateTabAsync(WorkspaceTabKind.SearchResults);
        await SearchEvidence.SearchCommand.ExecuteAsync();
    }

    private async Task OpenNewItemEditorAsync()
    {
        _isItemEditorTabOpen = true;
        await ActivateTabAsync(WorkspaceTabKind.ItemEditor);
        await ItemEditor.NewAsync();
    }

    private async Task EditSelectedItemAsync()
    {
        if (Shell.SelectedItem is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        _isItemEditorTabOpen = true;
        await ActivateTabAsync(WorkspaceTabKind.ItemEditor);
        await ItemEditor.LoadAsync(Shell.SelectedItem.ItemId);
    }

    private Task OpenItemEditorTabAsync()
    {
        if (_isItemEditorTabOpen)
        {
            return ActivateTabAsync(WorkspaceTabKind.ItemEditor);
        }

        return EditSelectedItemAsync();
    }

    private async Task RunSelectedItemOcrAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.Library);
        if (Shell.SelectedItem is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        await Shell.SelectedItem.RunOcrCommand.ExecuteAsync();
    }

    private Task ActivateExistingTabAsync(WorkspaceTabKind tab)
    {
        var isOpen = tab switch
        {
            WorkspaceTabKind.Settings => _isSettingsTabOpen,
            WorkspaceTabKind.SearchResults => _isSearchTabOpen,
            WorkspaceTabKind.OcrQueue => _isOcrQueueTabOpen,
            WorkspaceTabKind.ItemEditor => _isItemEditorTabOpen,
            WorkspaceTabKind.PdfWorkspace => _isPdfWorkspaceTabOpen,
            WorkspaceTabKind.Library => true,
            _ => false
        };

        return isOpen ? ActivateTabAsync(tab) : Task.CompletedTask;
    }

    private async Task ActivateTabAsync(WorkspaceTabKind tab)
    {
        _activeTab = tab;
        if (tab == WorkspaceTabKind.PdfWorkspace)
        {
            await Shell.SwitchToReadingModeAsync();
        }
        else
        {
            await Shell.SwitchToLibraryListAsync();
        }

        RaiseWorkspaceStateChanged();
    }

    private async Task CloseTabAsync(WorkspaceTabKind tab)
    {
        switch (tab)
        {
            case WorkspaceTabKind.Settings:
                _isSettingsTabOpen = false;
                break;
            case WorkspaceTabKind.SearchResults:
                _isSearchTabOpen = false;
                break;
            case WorkspaceTabKind.OcrQueue:
                _isOcrQueueTabOpen = false;
                break;
            case WorkspaceTabKind.ItemEditor:
                _isItemEditorTabOpen = false;
                break;
        }

        if (_activeTab == tab)
            await ActivateTabAsync(WorkspaceTabKind.Library);
        else
            RaiseWorkspaceStateChanged();
    }

    private async Task ClosePdfWorkspaceTabAsync()
    {
        _isPdfWorkspaceTabOpen = false;
        Shell.IsReadingMode = false;
        PdfWorkspace.Clear();
        if (_activeTab == WorkspaceTabKind.PdfWorkspace)
            await ActivateTabAsync(WorkspaceTabKind.Library);
        else
            RaiseWorkspaceStateChanged();
    }

    private void RaiseWorkspaceStateChanged()
    {
        foreach (var property in new[]
        {
            nameof(ActiveTab), nameof(IsSettingsVisible), nameof(IsSearchVisible), nameof(IsOcrQueueVisible),
            nameof(IsItemEditorVisible), nameof(ShowWorkspaceShell), nameof(ShowLibraryPage),
            nameof(ShowPdfWorkspacePage), nameof(ShowSettingsWorkspace), nameof(ShowSearchWorkspace),
            nameof(ShowOcrQueueWorkspace), nameof(ShowItemEditorWorkspace), nameof(ShowSidebar),
            nameof(IsInspectorVisible), nameof(ShowSelectedDocumentTab), nameof(ShowSettingsTab),
            nameof(ShowSearchTab), nameof(ShowOcrQueueTab), nameof(ShowItemEditorTab),
            nameof(IsLibraryTabActive), nameof(IsReaderTabActive), nameof(IsOcrQueueTabActive),
            nameof(IsItemEditorTabActive), nameof(PdfTabTitle)
        })
        {
            Raise(property);
        }
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
public sealed record SidebarFileSearchRootViewModel(
    string RootPath,
    bool IsAvailable,
    DateTimeOffset UpdatedAt,
    int FileCount)
{
    public string AvailabilityText => IsAvailable ? "可用" : "离线";
    public string UpdatedAtText => UpdatedAt.ToLocalTime().ToString("g");
    public string FileCountText => $"{FileCount} 个文件";
}
public sealed class LibraryViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string DisplayName {get;set;}="My Library"; public string RenameTo {get;set;}=""; public string Details {get;set;}="";
    public AsyncCommand CreateCommand {get;} public AsyncCommand RefreshCommand{get;} public AsyncCommand RenameCommand{get;}
    public LibraryViewModel(MainWindowViewModel main){_main=main;CreateCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Library.CreateLibraryAsync(DisplayName);Details=r.IsSuccess?$"{r.Value.DisplayName}\n{r.Value.LibraryId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Details));_main.Report(Details);await _main.LogOperationAsync("create_library", Details);});RefreshCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Library.GetCurrentLibraryAsync();Details=r.IsSuccess?$"{r.Value.DisplayName}\n{r.Value.LibraryId}\nSchema {r.Value.SchemaVersion}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Details));});RenameCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Library.RenameLibraryAsync(RenameTo);Details=r.IsSuccess?$"Renamed: {r.Value.DisplayName}\n{r.Value.LibraryId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Details));await _main.LogOperationAsync("rename_library", Details);});}
}
public sealed class BibliographyViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string ItemType{get;set;}="book";public string Title{get;set;}="";public string Subtitle{get;set;}="";public string ItemId{get;set;}="";public string Scheme{get;set;}="DOI";public string IdentifierValue{get;set;}="";public string Output{get;set;}="";public ObservableCollection<string> RecentItems{get;}=new();public AsyncCommand CreateItemCommand{get;}public AsyncCommand AddIdentifierCommand{get;}
    public BibliographyViewModel(MainWindowViewModel main){_main=main;CreateItemCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Items.CreateItemAsync(ItemType,Title,Subtitle);if(r.IsSuccess){ItemId=r.Value.ItemId.ToString();RecentItems.Add($"{r.Value.ItemId} | {r.Value.Title}");Raise(nameof(ItemId));}Output=r.IsSuccess?$"Item: {r.Value.ItemId}\n{r.Value.Title}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_item", Output);});AddIdentifierCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Items.AddIdentifierAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),Scheme,IdentifierValue,null);Output=r.IsSuccess?$"Identifier: {r.Value.Scheme} {r.Value.Value}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
public sealed class FileDocumentViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string FilePath{get;set;}="";public string ItemId{get;set;}="";public string FileAssetId{get;set;}="";public string InstanceType{get;set;}="primary_scan";public string Output{get;set;}="";public ObservableCollection<string> RecentFileAssets{get;}=new();public ObservableCollection<string> RecentDocumentInstances{get;}=new();public AsyncCommand RegisterCommand{get;}public AsyncCommand AttachCommand{get;}public AsyncCommand ResolveCommand{get;}
    public FileDocumentViewModel(MainWindowViewModel main){_main=main;RegisterCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Files.RegisterFileAsync(FilePath);if(r.IsSuccess){FileAssetId=r.Value.FileAssetId.ToString();RecentFileAssets.Add($"{r.Value.FileAssetId} | {r.Value.FileName} ({r.Value.Status})");Raise(nameof(FileAssetId));}Output=r.IsSuccess?$"File asset: {r.Value.FileAssetId}\n{r.Value.Status}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("register_file", Output);});AttachCommand=new(async()=>{var f=string.IsNullOrWhiteSpace(FileAssetId)?(Patchouli.Core.Ids.FileAssetId?)null:Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId);var r=await (await _main.ServicesAsync()).Documents.AttachDocumentInstanceAsync(Patchouli.Core.Ids.ItemId.Parse(ItemId),f,InstanceType);if(r.IsSuccess)RecentDocumentInstances.Add($"{r.Value.DocumentInstanceId} | {r.Value.InstanceType}");Output=r.IsSuccess?$"Document: {r.Value.DocumentInstanceId}\nPrimary: {r.Value.IsPrimary}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("attach_document_instance", Output);});ResolveCommand=new(async()=>{var r=await (await _main.ServicesAsync()).FileResolution.ResolveFileAsync(Patchouli.Core.Ids.FileAssetId.Parse(FileAssetId),ResolveFilePurpose.MaintenanceScan);Output=r.IsSuccess?$"{r.Value.Status}\n{r.Value.Confidence}\n{r.Value.RequiredAction}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
public sealed class PageLayoutViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string DocumentInstanceId{get;set;}="";public string PageId{get;set;}="";public string RevisionId{get;set;}="";public string PageIndex{get;set;}="0";public string Text{get;set;}="";public string Output{get;set;}="";public ObservableCollection<string> RecentPages{get;}=new();public ObservableCollection<string> RecentLayoutRevisions{get;}=new();public AsyncCommand CreatePageCommand{get;}public AsyncCommand CreateRevisionCommand{get;}public AsyncCommand AddNodeCommand{get;}public AsyncCommand BuildTextCommand{get;}
    public PageLayoutViewModel(MainWindowViewModel m){_main=m;CreatePageCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Pages.CreatePageAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),int.Parse(PageIndex),null,null,null,0,CoordinateBasis.NormalizedPage,null,null,"ui-mvp-1",null);if(r.IsSuccess){PageId=r.Value.PageId.ToString();RecentPages.Add($"{r.Value.PageId} | {r.Value.PageIndex}");Raise(nameof(PageId));}Output=r.IsSuccess?$"Page: {r.Value.PageId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_page", Output);});CreateRevisionCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.CreateLayoutRevisionAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),LayoutRevisionSource.Manual,true);if(r.IsSuccess){RevisionId=r.Value.LayoutRevisionId.ToString();RecentLayoutRevisions.Add($"{r.Value.LayoutRevisionId} | current");Raise(nameof(RevisionId));}Output=r.IsSuccess?$"Revision: {r.Value.LayoutRevisionId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("create_layout_revision", Output);});AddNodeCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.AddNodeAsync(Patchouli.Core.Ids.LayoutRevisionId.Parse(RevisionId),Patchouli.Core.Ids.PageId.Parse(PageId),null,LayoutNodeType.Paragraph,new NormalizedBBox(.1,.1,.8,.2),Text,TextPolicy.Own,1,LayoutNodeSource.Manual);Output=r.IsSuccess?$"Node: {r.Value.NodeId}":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});BuildTextCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Layout.BuildPagePlainTextAsync(Patchouli.Core.Ids.PageId.Parse(PageId),Patchouli.Core.Ids.LayoutRevisionId.Parse(RevisionId));Output=r.IsSuccess?r.Value.Text:$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});}
}
public sealed class MockOcrViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string Name { get; set; } = "Mock preset";
    public string PresetId { get; set; } = "";
    public string DocumentInstanceId { get; set; } = "";
    public string PageIds { get; set; } = "";
    public string ImagePageId { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string RunId { get; set; } = "";
    public string NewModelPath { get; set; } = "";
    public bool ApplyOnSuccess { get; set; } = true;
    public string ParametersJson { get; set; } = "{}";
    public string Output { get; set; } = "";
    public string Capabilities { get; set; } = "";
    public ObservableCollection<string> RecentRuns { get; } = new();
    public AsyncCommand CreatePresetCommand { get; }
    public AsyncCommand RunCommand { get; }
    public AsyncCommand RunImageCommand { get; }
    public AsyncCommand ShowRunCommand { get; }
    public AsyncCommand AdoptCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand UnsetCurrentCommand { get; }
    public AsyncCommand HideRunCommand { get; }
    public AsyncCommand ShowCapabilitiesCommand { get; }
    public AsyncCommand CheckEnvironmentCommand { get; }
    public AsyncCommand RebindModelPathCommand { get; }

    public MockOcrViewModel(MainWindowViewModel m)
    {
        _main = m;
        CreatePresetCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).OcrPresets.CreatePresetAsync(Name, null, OcrEngineIds.Mock, OcrModelIds.MockBasic, null, ParametersJson, ApplyOnSuccess);
            if (r.IsSuccess)
            {
                PresetId = r.Value.PresetId.ToString();
                Raise(nameof(PresetId));
            }
            Output = r.IsSuccess ? $"Preset: {r.Value.PresetId}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
        RunCommand = new(async () =>
        {
            var pages = PageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Patchouli.Core.Ids.PageId.Parse).ToArray();
            var r = await (await _main.ServicesAsync()).Ocr.RunPresetOnPagesAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId), pages);
            if (r.IsSuccess)
            {
                RunId = r.Value.OcrRunId.ToString();
                RecentRuns.Add($"{r.Value.OcrRunId} | {r.Value.State}");
                Raise(nameof(RunId));
            }
            Output = r.IsSuccess ? $"Run: {r.Value.OcrRunId}\n{r.Value.State}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("run_mock_ocr", Output);
        });
        RunImageCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).Ocr.RunPresetOnImagePageAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId), OcrPresetId.Parse(PresetId), Patchouli.Core.Ids.PageId.Parse(ImagePageId), ImagePath);
            if (r.IsSuccess)
            {
                RunId = r.Value.OcrRunId.ToString();
                RecentRuns.Add($"{r.Value.OcrRunId} | {r.Value.State}");
                Raise(nameof(RunId));
            }
            Output = r.IsSuccess ? $"Image OCR run: {r.Value.OcrRunId}\n{r.Value.State}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("run_local_image_ocr", Output);
        });
        ShowRunCommand = new(async () =>
        {
            var s = await _main.ServicesAsync();
            var run = await s.Ocr.GetRunAsync(OcrRunId.Parse(RunId));
            var pages = await s.Ocr.ListPageResultsAsync(OcrRunId.Parse(RunId));
            Output = run.IsSuccess ? $"{run.Value.State}\n" + string.Join("\n", pages.Value.Select(p => $"{p.PageId}: {p.State} {p.ErrorCode} {p.ErrorMessage}")) : $"ERROR {run.ErrorCode}: {run.ErrorMessage}";
            Raise(nameof(Output));
        });
        AdoptCommand = new(async () =>
        {
            var selected = string.IsNullOrWhiteSpace(PageIds) ? null : PageIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Patchouli.Core.Ids.PageId.Parse).ToArray();
            var r = await (await _main.ServicesAsync()).Ocr.AdoptCandidateRunAsync(OcrRunId.Parse(RunId), selected);
            Output = r.IsSuccess ? $"Adopted: {r.Value.AdoptedRevisionId}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
        CancelCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).Ocr.CancelRunAsync(OcrRunId.Parse(RunId));
            Output = r.IsSuccess ? "Run cancelled." : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
        UnsetCurrentCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).Ocr.UnsetCurrentOcrAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Output = r.IsSuccess ? "Current OCR revision unset." : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("unset_current_ocr", Output);
        });
        HideRunCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).Ocr.HideOcrRunAsync(OcrRunId.Parse(RunId));
            Output = r.IsSuccess ? "OCR run hidden." : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("hide_ocr_run", Output);
        });
        ShowCapabilitiesCommand = new(async () =>
        {
            Capabilities = string.Join("\n", (await _main.ServicesAsync()).OcrAdapters.ListCapabilities().Select(c => $"{c.EngineId}: {c.DisplayName}; requires model path={c.RequiresModelPath}; {c.Notes}"));
            Raise(nameof(Capabilities));
        });
        CheckEnvironmentCommand = new(async () =>
        {
            var s = await _main.ServicesAsync();
            var version = await s.OcrPresets.GetCurrentVersionAsync(OcrPresetId.Parse(PresetId));
            if (version.IsFailure)
            {
                Output = $"ERROR {version.ErrorCode}: {version.ErrorMessage}";
            }
            else
            {
                var check = await s.OcrAdapters.CheckEngineAsync(version.Value.EngineId, version.Value);
                Output = check.IsSuccess ? $"{check.Value.Status}\n{check.Value.Message}\nAction: {check.Value.RequiredAction}" : $"ERROR {check.ErrorCode}: {check.ErrorMessage}";
            }
            Raise(nameof(Output));
        });
        RebindModelPathCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).OcrPresets.RebindModelPathAsync(OcrPresetId.Parse(PresetId), NewModelPath);
            Output = r.IsSuccess ? $"Rebound model path. New preset version: {r.Value.PresetVersionId}" : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Output));
        });
    }
}
public sealed class SearchEvidenceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    public string DocumentInstanceId { get; set; } = "";
    public string Query { get; set; } = "";
    public string UnitId { get; set; } = "";
    public string EvidenceRef { get; set; } = "";
    public string Markdown { get; set; } = "";
    public string Output { get; set; } = "";
    public string IndexStatus { get; private set; } = "";
    public string AffectedScopesSummary { get; private set; } = "";
    public string EstimatedTotalText { get; private set; } = "";
    public ObservableCollection<string> SearchUnits { get; } = new();
    public ObservableCollection<SearchPageResultViewModel> Results { get; } = new();
    public bool HasResults => Results.Count > 0;
    public bool HasNoResults => !HasResults && !string.IsNullOrWhiteSpace(Query);
    public AsyncCommand RebuildCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand CreateEvidenceCommand { get; }
    public AsyncCommand MarkdownCommand { get; }
    public AsyncCommand CopyMarkdownCommand { get; }

    public SearchEvidenceViewModel(MainWindowViewModel m)
    {
        _main = m;
        RebuildCommand = new(async () =>
        {
            var s = await _main.ServicesAsync();
            var a = await s.SearchUnits.RebuildForDocumentInstanceAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            var b = await s.SearchIndex.RebuildFtsForDocumentInstanceAsync(Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId));
            Output = a.IsSuccess && b.IsSuccess ? "Search units and FTS rebuilt." : $"ERROR {a.ErrorCode ?? b.ErrorCode}";
            Raise(nameof(Output));
            await _main.LogOperationAsync("rebuild_search_fts", Output);
        });
        SearchCommand = new(SearchAsync);
        CreateEvidenceCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).Evidence.CreateFromSearchUnitAsync(Patchouli.Core.Ids.SearchUnitId.Parse(UnitId));
            Output = r.IsSuccess ? r.Value.EvidenceRefId : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            if (r.IsSuccess)
            {
                EvidenceRef = r.Value.EvidenceRefId;
                var markdown = await (await _main.ServicesAsync()).Evidence.CreateMarkdownAsync(EvidenceRef);
                if (markdown.IsSuccess) Markdown = markdown.Value.Markdown;
            }
            Raise(nameof(Output));
            Raise(nameof(EvidenceRef));
            Raise(nameof(Markdown));
            await _main.LogOperationAsync("create_evidence_ref", Output);
        });
        MarkdownCommand = new(async () =>
        {
            var r = await (await _main.ServicesAsync()).Evidence.CreateMarkdownAsync(EvidenceRef);
            Markdown = r.IsSuccess ? r.Value.Markdown : "";
            Output = r.IsSuccess ? Markdown : $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            Raise(nameof(Markdown));
            Raise(nameof(Output));
        });
        CopyMarkdownCommand = new(async () =>
        {
            if (string.IsNullOrWhiteSpace(Markdown))
            {
                Output = "ERROR validation_failed: Generate Evidence Markdown first.";
            }
            else
            {
                try
                {
                    await _main.Clipboard.SetTextAsync(Markdown);
                    Output = "Copied Evidence Markdown";
                }
                catch (Exception ex)
                {
                    Output = $"ERROR clipboard_unavailable: {ex.Message}";
                }
            }
            Raise(nameof(Output));
            await _main.LogOperationAsync("copy_evidence_markdown", Output);
        });
    }

    public void RaiseMarkdown() => Raise(nameof(Markdown));
    public void RaiseOutput() => Raise(nameof(Output));

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            Results.Clear();
            SearchUnits.Clear();
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = "";
            Output = "";
            Raise(nameof(IndexStatus));
            Raise(nameof(AffectedScopesSummary));
            Raise(nameof(EstimatedTotalText));
            Raise(nameof(Results));
            Raise(nameof(HasResults));
            Raise(nameof(HasNoResults));
            Raise(nameof(Output));
            _main.Report("请输入搜索词。");
            return;
        }

        var services = await _main.ServicesAsync();
        var r = await services.Search.SearchLibraryAsync(new SearchRequest(Query));
        Results.Clear();
        SearchUnits.Clear();

        if (r.IsSuccess)
        {
            var firstMatchedUnit = default(string);
            var firstEvidenceRef = default(string);
            foreach (var page in r.Value.Results)
            {
                var matchedUnits = new List<SearchMatchedUnitViewModel>();
                foreach (var unit in page.MatchedUnits)
                {
                    SearchUnits.Add($"{unit.UnitId} | {unit.Text}");
                    var evidence = await services.Evidence.CreateFromSearchUnitAsync(unit.UnitId);
                    var evidenceRef = evidence.IsSuccess ? evidence.Value.EvidenceRefId : null;
                    matchedUnits.Add(new SearchMatchedUnitViewModel(
                        unit.UnitId.ToString(),
                        unit.Text,
                        unit.NodeType,
                        unit.ReadingOrder,
                        unit.IsMatch,
                        evidenceRef));
                    firstMatchedUnit ??= unit.UnitId.ToString();
                    firstEvidenceRef ??= evidenceRef;
                }

                Results.Add(new SearchPageResultViewModel(
                    page.ItemTitle,
                    page.DocumentInstanceId.ToString(),
                    page.PageId.ToString(),
                    page.PageLabel,
                    page.PageIndex,
                    page.IndexStatus,
                    page.MatchedUnitsHasMore,
                    matchedUnits));
            }

            UnitId = firstMatchedUnit ?? "";
            EvidenceRef = firstEvidenceRef ?? "";
            IndexStatus = r.Value.IndexStatus;
            AffectedScopesSummary = r.Value.AffectedScopesSummary ?? "";
            EstimatedTotalText = r.Value.EstimatedTotal?.ToString() ?? $"{r.Value.Results.Count} 页";
            Output = JsonSerializer.Serialize(r.Value, new JsonSerializerOptions { WriteIndented = true });
            _main.Report(r.Value.Results.Count > 0
                ? $"搜索完成：{r.Value.Results.Count} 页命中，index status={IndexStatus}。"
                : $"搜索完成：没有命中结果，index status={IndexStatus}。");
        }
        else
        {
            UnitId = "";
            EvidenceRef = "";
            IndexStatus = "";
            AffectedScopesSummary = "";
            EstimatedTotalText = "";
            Output = $"ERROR {r.ErrorCode}: {r.ErrorMessage}";
            _main.Report(Output);
        }

        Raise(nameof(UnitId));
        Raise(nameof(EvidenceRef));
        Raise(nameof(IndexStatus));
        Raise(nameof(AffectedScopesSummary));
        Raise(nameof(EstimatedTotalText));
        Raise(nameof(Results));
        Raise(nameof(HasResults));
        Raise(nameof(HasNoResults));
        Raise(nameof(Output));
    }
}
public sealed class McpPreviewViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string Query{get;set;}="";public string Output{get;set;}="";public string Safety{get;set;}="";public string SpecificPath{get;set;}="";public string SpecificSecret{get;set;}="";public AsyncCommand SearchCommand{get;}public AsyncCommand SafetyCommand{get;}
    public McpPreviewViewModel(MainWindowViewModel m){_main=m;SearchCommand=new(async()=>{var r=await (await _main.ServicesAsync()).Mcp.SearchLibraryAsync(new McpSearchLibraryRequest(Query));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});SafetyCommand=new(()=>{var tokens=new[]{"original_path","resolved_path","file://","/Users/","model_path","cache"}.ToList();if(!string.IsNullOrWhiteSpace(SpecificPath))tokens.Add(SpecificPath);if(!string.IsNullOrWhiteSpace(SpecificSecret))tokens.Add(SpecificSecret);var hit=tokens.FirstOrDefault(x=>Output.Contains(x,StringComparison.Ordinal));Safety=hit is null?"No obvious local path or secret exposure detected.":$"Warning: output contains sensitive token: {hit}";Raise(nameof(Safety));return Task.CompletedTask;});}
}
public sealed class SnapshotViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main; public string SyncRoot{get;set;}="";public string DeviceId{get;set;}="device-ui";public string ManifestPath{get;set;}="";public string StagingRoot{get;set;}="";public string LastSnapshotId{get;set;}="";public string LastManifestPath{get;set;}="";public string Output{get;set;}="";public AsyncCommand PublishCommand{get;}public AsyncCommand ValidateCommand{get;}public AsyncCommand ImportCommand{get;}
    public SnapshotViewModel(MainWindowViewModel m){_main=m;PublishCommand=new(async()=>{var s=await _main.ServicesAsync();var r=await s.SnapshotPublisher.PublishSnapshotAsync(new SnapshotPublishRequest(s.RuntimeDatabasePath,SyncRoot,DeviceId));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";if(r.IsSuccess){ManifestPath=r.Value.ManifestPath;LastManifestPath=r.Value.ManifestPath;LastSnapshotId=r.Value.SnapshotId;}Raise(nameof(Output));Raise(nameof(ManifestPath));Raise(nameof(LastManifestPath));Raise(nameof(LastSnapshotId));await _main.LogOperationAsync("publish_snapshot", Output);});ValidateCommand=new(async()=>{var r=await (await _main.ServicesAsync()).SnapshotImporter.ValidateSnapshotAsync(ManifestPath);Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true}):$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));});ImportCommand=new(async()=>{var s=await _main.ServicesAsync();var r=await s.SnapshotImporter.ImportSnapshotToStagingAsync(new SnapshotImportRequest(ManifestPath,StagingRoot));Output=r.IsSuccess?JsonSerializer.Serialize(r.Value,new JsonSerializerOptions{WriteIndented=true})+"\nImport does not replace active runtime DB.":$"ERROR {r.ErrorCode}: {r.ErrorMessage}";Raise(nameof(Output));await _main.LogOperationAsync("import_snapshot_staging", Output);});}
}
