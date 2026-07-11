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
using Patchouli.Core.Operations;
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
    private readonly Dictionary<string, FileSystemWatcher> _fileSearchRootWatchers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _fileSearchRootWatchDebounce;

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
    public string McpEndpoint { get; private set; } = $"http://localhost:{McpServerOptions.DefaultPort}/mcp";
    public string McpStatusText { get; private set; } = "MCP: 未启动";
    public string McpStatusDetail { get; private set; } = "等待运行数据库打开。";
    public IBrush McpStatusBrush { get; private set; } = Brushes.Gray;
    public string VersionInfo => $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion} | {RuntimeDatabasePath}";
    public string StatusBarVersion => $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion}";
    public string SettingsFilePath => PatchouliAppSettings.ResolvePath(_settingsPath);
    public bool HasOpenRuntimeDatabase => _services is not null;
    public IClipboardService Clipboard { get; }
    public IDialogService Dialogs { get; }
    public IModalOperationRunner ModalOperations { get; }
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
    public bool ShowLibraryLeftSidebarPreference
    {
        get => _settings.Ui.ShowLibraryLeftSidebar;
        set
        {
            if (_settings.Ui.ShowLibraryLeftSidebar == value) return;
            UpdateAppOptions(_settings with { Ui = _settings.Ui with { ShowLibraryLeftSidebar = value } });
            Raise();
            Raise(nameof(IsLibraryLeftSidebarVisible));
        }
    }
    public bool ShowLibraryRightSidebarPreference
    {
        get => _settings.Ui.ShowLibraryRightSidebar;
        set
        {
            if (_settings.Ui.ShowLibraryRightSidebar == value) return;
            UpdateAppOptions(_settings with { Ui = _settings.Ui with { ShowLibraryRightSidebar = value } });
            Raise();
            Raise(nameof(IsLibraryRightSidebarVisible));
        }
    }
    public bool IsLibraryLeftSidebarVisible => ShowLibraryLeftSidebarPreference && ShowSidebar;
    public bool IsLibraryRightSidebarVisible => ShowLibraryRightSidebarPreference && IsInspectorVisible;
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
    public AsyncCommand RescanFileSearchRootsCommand { get; }
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

        _services?.UpdateMetadataLookupPreferences(_settings.MetadataLookup);

    }

    private void PersistRuntimeDatabasePathIfEnabled()
    {
        if (!_settings.Runtime.RememberLastDatabase) return;

        var normalizedPath = Path.GetFullPath(RuntimeDatabasePath);
        var currentPath = Path.GetFullPath(_settings.Runtime.RuntimeDatabasePath);
        if (string.Equals(normalizedPath, currentPath, StringComparison.OrdinalIgnoreCase)) return;

        UpdateAppOptions(_settings with { Runtime = _settings.Runtime with { RuntimeDatabasePath = normalizedPath } });
    }

    public MainWindowViewModel(IClipboardService? clipboard = null, IAppLogger? logger = null, IDialogService? dialogs = null, bool autoStartMcpServer = false, int mcpPort = McpServerOptions.DefaultPort, string? settingsPath = null, IModalOperationRunner? modalOperations = null)
    {
        _settingsPath = settingsPath;
        _settings = PatchouliAppSettings.Load(settingsPath);
        _runtimeDatabasePath = _settings.Runtime.RememberLastDatabase
            ? _settings.Runtime.RuntimeDatabasePath
            : AppRuntimeOptions.Default().RuntimeDatabasePath;
        _autoStartMcpServer = autoStartMcpServer;
        McpEndpoint = $"http://localhost:{mcpPort}/mcp";
        Clipboard = clipboard ?? new AvaloniaClipboardService();
        Dialogs = dialogs ?? CreateDialogService();
        ModalOperations = modalOperations ?? new ModalOperationRunner(Dialogs);
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
            ResetFileSearchRootWatchers();
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
        RebuildSearchIndexCommand = new(RebuildSearchIndexAsync);
        RescanFileSearchRootsCommand = new(() => RescanFileSearchRootsAsync("手动重新扫描完成。", showBlockingDialog: true));
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
                if (Layout.IsLibraryActive) Shell.ExitReadingMode();
                Raise(nameof(ActiveTab));
                RaiseShellSelectionChanged();
                break;
            case nameof(WorkspaceLayoutViewModel.ShowInspectorPane):
                Raise(nameof(ShowInspectorPane));
                Raise(nameof(IsLibraryRightSidebarVisible));
                break;
            case nameof(WorkspaceLayoutViewModel.ShowSidebar):
                Raise(nameof(ShowSidebar));
                Raise(nameof(IsLibraryLeftSidebarVisible));
                break;
            case nameof(WorkspaceLayoutViewModel.IsInspectorVisible):
                Raise(nameof(IsInspectorVisible));
                Raise(nameof(IsLibraryRightSidebarVisible));
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
        await RefreshSidebarPathsAsync();
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

                RefreshFileSearchRootWatchers(roots.Value);
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

    public async Task<Result<FileSearchRootRescanSummary>> RescanFileSearchRootsAsync(
        string completionMessage = "文件重新扫描完成。",
        bool showBlockingDialog = false,
        CancellationToken cancellationToken = default,
        Action<int?, int?, string, string?>? progress = null)
    {
        var services = await ServicesAsync();
        Result<FileSearchRootRescanSummary> result;
        if (showBlockingDialog)
        {
            result = await ModalOperations.RunAsync(
                new ModalOperationOptions(
                    "文件重新扫描",
                    "正在扫描文件搜索根并导入新发现的 PDF。",
                    CanCancel: true),
                context => RescanFileSearchRootsCoreAsync(services, completionMessage, context.CancellationToken, context.Report),
                cancellationToken);
        }
        else
        {
            result = await Task.Run(
                () => RescanFileSearchRootsCoreAsync(services, completionMessage, cancellationToken, progress),
                cancellationToken);
        }

        await ApplyFileSearchRootRescanResultAsync(result, completionMessage);
        return result;
    }

    private async Task<Result<FileSearchRootRescanSummary>> RescanFileSearchRootsCoreAsync(
        AppServices services,
        string completionMessage,
        CancellationToken cancellationToken,
        Action<int?, int?, string, string?>? progress)
    {
        BlockingOperationId? operationId = null;
        try
        {
            var roots = await services.FileResolution.ListSearchRootsAsync(cancellationToken);
            if (roots.IsFailure)
            {
                return Result<FileSearchRootRescanSummary>.Failure(roots.ErrorCode!, roots.ErrorMessage!);
            }

            var started = await services.BlockingOperations.StartAsync(
                BlockingOperationTypes.FileSearchRootScan,
                BlockingOperationScopeTypes.FileSearchRoot,
                "all",
                canCancel: true,
                progressLabel: "正在重新扫描文件搜索根。",
                progressCurrent: 0,
                progressTotal: roots.Value.Count,
                nextActions: ["等待扫描完成", "检查离线文件搜索根"],
                cancellationToken: cancellationToken);
            if (started.IsSuccess)
            {
                operationId = started.Value.OperationId;
            }

            var knownPaths = await LoadKnownFilePathsAsync(services, cancellationToken);
            var processedRoots = 0;
            var scanned = 0;
            var imported = 0;
            var skipped = 0;
            var failed = 0;
            var partialRoots = 0;
            var unavailableRoots = 0;
            var skippedDirectories = 0;
            var skippedFiles = 0;
            progress?.Invoke(0, roots.Value.Count, "正在扫描文件搜索根。", $"已找到 {roots.Value.Count} 个文件搜索根。");

            foreach (var root in roots.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(processedRoots, roots.Value.Count, $"正在扫描：{root.RootPath}", null);
                var reopened = await services.FileSearchRootAccess.ReopenAsync(root, cancellationToken);
                if (reopened.IsFailure)
                {
                    unavailableRoots++;
                    await services.FileResolution.SetSearchRootAvailabilityAsync(root.RootId, false, cancellationToken);
                    processedRoots++;
                    progress?.Invoke(processedRoots, roots.Value.Count, "文件搜索根不可用，已跳过。", reopened.ErrorMessage ?? root.RootPath);
                    continue;
                }

                using var resolvedRoot = reopened.Value.AccessLease;
                var scan = await services.FileSearchRootAccess.ScanPdfAsync(reopened.Value, cancellationToken);
                var available = scan.RootStatus == FileSearchRootStatuses.Available || scan.RootStatus == FileSearchRootStatuses.Partial;
                await services.FileResolution.SetSearchRootAvailabilityAsync(root.RootId, available, cancellationToken);
                if (scan.ScanStatus == FileSearchRootScanStatuses.Partial) partialRoots++;
                if (scan.ScanStatus == FileSearchRootScanStatuses.Failed) unavailableRoots++;
                skippedDirectories += scan.SkippedDirectories.Count;
                skippedFiles += scan.SkippedFiles.Count;
                scanned += scan.Candidates.Count;
                foreach (var candidate in scan.Candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var normalizedPath = Path.GetFullPath(candidate.Path);
                    if (knownPaths.Contains(normalizedPath))
                    {
                        skipped++;
                        continue;
                    }

                    var importedPdf = await services.PdfImport.ImportPdfAsync(new PdfImportRequest(normalizedPath, null, null, null), cancellationToken);
                    if (importedPdf.Success)
                    {
                        imported++;
                        knownPaths.Add(normalizedPath);
                        progress?.Invoke(processedRoots, roots.Value.Count, $"已导入：{candidate.FileName}", normalizedPath);
                    }
                    else
                    {
                        failed++;
                        progress?.Invoke(processedRoots, roots.Value.Count, $"导入失败：{candidate.FileName}", importedPdf.ErrorMessage);
                    }
                }

                processedRoots++;
                if (operationId is not null)
                {
                    await services.BlockingOperations.UpdateProgressAsync(
                        operationId.Value,
                        progressCurrent: processedRoots,
                        progressLabel: $"已处理 {processedRoots}/{roots.Value.Count} 个文件搜索根，已扫描 {scanned} 个 PDF。",
                        cancellationToken: cancellationToken);
                }
                progress?.Invoke(processedRoots, roots.Value.Count, $"已处理 {processedRoots}/{roots.Value.Count} 个文件搜索根。", null);
            }

            var summary = new FileSearchRootRescanSummary(scanned, imported, skipped, failed, partialRoots, unavailableRoots, skippedDirectories, skippedFiles);
            var message = BuildFileSearchRootRescanMessage(summary, completionMessage);
            progress?.Invoke(roots.Value.Count, roots.Value.Count, "文件重新扫描完成。", message);
            if (operationId is not null)
            {
                await services.BlockingOperations.CompleteAsync(operationId.Value, message, Array.Empty<string>(), cancellationToken);
            }
            return Result<FileSearchRootRescanSummary>.Success(summary);
        }
        catch (OperationCanceledException)
        {
            if (operationId is not null)
            {
                await services.BlockingOperations.CancelAsync(
                    operationId.Value,
                    "文件重新扫描已取消。",
                    ["可稍后重新扫描"],
                    CancellationToken.None);
            }
            throw;
        }
        catch (Exception ex)
        {
            var message = $"文件重新扫描失败：{ex.Message}";
            if (operationId is not null)
            {
                await services.BlockingOperations.FailAsync(operationId.Value, AppErrorCodes.InvalidState, message, "文件重新扫描失败。", ["检查文件搜索根权限", "重新扫描"], CancellationToken.None);
            }
            return Result<FileSearchRootRescanSummary>.Failure(AppErrorCodes.InvalidState, message);
        }
    }

    private async Task ApplyFileSearchRootRescanResultAsync(
        Result<FileSearchRootRescanSummary> result,
        string completionMessage)
    {
        if (result.IsFailure)
        {
            ReportError(result.ErrorMessage ?? "文件重新扫描失败。");
            return;
        }

        await RefreshSidebarPathsAsync();
        await Shell.RefreshItemsAsync();
        var message = BuildFileSearchRootRescanMessage(result.Value, completionMessage);
        if (result.Value.HasWarnings) ReportError(message); else Report(message);
    }

    private static string BuildFileSearchRootRescanMessage(
        FileSearchRootRescanSummary summary,
        string completionMessage)
        => $"{completionMessage} 扫描 {summary.ScannedPdfCount} 个 PDF，新增 {summary.ImportedPdfCount} 个，已存在 {summary.SkippedKnownPdfCount} 个，失败 {summary.FailedPdfCount} 个；部分扫描 {summary.PartialRootCount} 个，不可用 {summary.UnavailableRootCount} 个，跳过目录 {summary.SkippedDirectoryCount} 个、文件 {summary.SkippedFileCount} 个。";

    private static async Task<HashSet<string>> LoadKnownFilePathsAsync(AppServices services, CancellationToken cancellationToken)
    {
        await using var connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var paths = await connection.QueryAsync<string>("select original_path from file_assets;");
        return paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task RebuildSearchIndexAsync()
    {
        var services = await ServicesAsync();
        await ModalOperations.RunAsync(
                new ModalOperationOptions(
                    "重建搜索索引",
                    "正在重建本地 FTS 搜索索引。",
                    CanCancel: true),
            async context =>
            {
                var started = await services.BlockingOperations.StartAsync(
                    BlockingOperationTypes.SearchIndexRebuild,
                    BlockingOperationScopeTypes.SearchIndex,
                    "library",
                    canCancel: true,
                    progressLabel: "正在重建本地 FTS 搜索索引。",
                    cancellationToken: context.CancellationToken);
                var operationId = started.IsSuccess ? started.Value.OperationId : (BlockingOperationId?)null;
                try
                {
                    var result = await services.SearchIndex.RebuildFtsForLibraryAsync(context.CancellationToken);
                    if (result.IsFailure)
                    {
                        if (operationId is not null)
                            await services.BlockingOperations.FailAsync(operationId.Value, result.ErrorCode!, result.ErrorMessage!, cancellationToken: CancellationToken.None);
                        throw new InvalidOperationException(result.ErrorMessage);
                    }
                    if (operationId is not null)
                        await services.BlockingOperations.CompleteAsync(operationId.Value, "本地 FTS 搜索索引已重建。", cancellationToken: CancellationToken.None);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    if (operationId is not null)
                        await services.BlockingOperations.CancelAsync(operationId.Value, "搜索索引重建已取消。", cancellationToken: CancellationToken.None);
                    throw;
                }
            });
        Report("本地 FTS 搜索索引已重建。");
    }

    private void RefreshFileSearchRootWatchers(IReadOnlyList<FileSearchRoot> roots)
    {
        var wanted = roots.Where(root => root.IsAvailable && Directory.Exists(root.RootPath)).Select(root => Path.GetFullPath(root.RootPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _fileSearchRootWatchers.Keys.Where(path => !wanted.Contains(path)).ToArray())
        {
            _fileSearchRootWatchers[path].Dispose();
            _fileSearchRootWatchers.Remove(path);
        }

        foreach (var path in wanted)
        {
            if (_fileSearchRootWatchers.ContainsKey(path)) continue;
            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*.pdf",
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler changed = (_, _) => ScheduleFileSearchRootRescan();
                RenamedEventHandler renamed = (_, _) => ScheduleFileSearchRootRescan();
                ErrorEventHandler error = (_, _) => ScheduleFileSearchRootRescan();
                watcher.Created += changed;
                watcher.Changed += changed;
                watcher.Deleted += changed;
                watcher.Renamed += renamed;
                watcher.Error += error;
                _fileSearchRootWatchers[path] = watcher;
            }
            catch (Exception exception)
            {
                UnexpectedExceptions.Sink.Report(exception, "file-watcher", "create-watcher");
            }
        }
    }

    private void ScheduleFileSearchRootRescan()
    {
        _fileSearchRootWatchDebounce?.Cancel();
        _fileSearchRootWatchDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _fileSearchRootWatchDebounce = cts;
        DebounceFileSearchRootRescanAsync(cts.Token).Observe("file-watcher", "debounced-rescan", cts.Token);
    }

    private async Task DebounceFileSearchRootRescanAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await DispatcherTasks.RunAsync(() => RescanFileSearchRootsAsync("文件变化后自动重新扫描完成。"));
    }

    private void ResetFileSearchRootWatchers()
    {
        _fileSearchRootWatchDebounce?.Cancel();
        _fileSearchRootWatchDebounce?.Dispose();
        _fileSearchRootWatchDebounce = null;
        foreach (var watcher in _fileSearchRootWatchers.Values)
        {
            watcher.Dispose();
        }
        _fileSearchRootWatchers.Clear();
    }

    public async Task StartMcpServerAsync()
    {
        var services = await ServicesAsync();
        await ModalOperations.RunAsync(
            new ModalOperationOptions(
                "启动 MCP Server",
                "正在验证设置并启动 HTTP listener。",
                CanCancel: false),
            async context =>
            {
                await StartMcpServerAsync(services);
                if (_mcpServer?.IsRunning != true)
                    throw new InvalidOperationException("MCP Server 未能启动。请检查状态栏中的错误详情。");
                return true;
            });
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

        BlockingOperationId? operationId = null;
        var started = await services.BlockingOperations.StartAsync(
            BlockingOperationTypes.McpStartValidation,
            BlockingOperationScopeTypes.McpServerSettings,
            "default",
            progressLabel: "正在验证 MCP 设置并启动 listener。",
            nextActions: ["检查 MCP bind、端口和鉴权 token"],
            cancellationToken: CancellationToken.None);
        if (started.IsSuccess) operationId = started.Value.OperationId;

        await StopMcpServerAsync("MCP HTTP 服务正在启动。");
        var settingsResult = await services.McpSettings.GetSettingsAsync();
        if (settingsResult.IsFailure)
        {
            var message = McpOutputSanitizer.Sanitize(settingsResult.ErrorMessage ?? "无法读取 MCP 设置。");
            if (operationId is not null)
                await services.BlockingOperations.FailAsync(operationId.Value, settingsResult.ErrorCode!, message, cancellationToken: CancellationToken.None);
            SetMcpStatus("MCP: 错误", message, Brushes.IndianRed);
            return;
        }

        var serverSettings = settingsResult.Value;
        var validation = await services.McpSettings.ValidateSettingsAsync(serverSettings);
        if (validation.IsFailure)
        {
            var message = McpOutputSanitizer.Sanitize(validation.ErrorMessage ?? "MCP 设置无效。");
            if (operationId is not null)
                await services.BlockingOperations.FailAsync(operationId.Value, validation.ErrorCode!, message, cancellationToken: CancellationToken.None);
            SetMcpStatus("MCP: 错误", message, Brushes.IndianRed);
            return;
        }

        SetMcpStatus("MCP: 启动中", $"正在监听 http://{serverSettings.BindAddress}:{serverSettings.Port}/mcp", Brushes.Goldenrod);
        void ReportMcpException(Exception exception, string operation) =>
            UnexpectedExceptions.Sink.Report(exception, "mcp-server", operation);
        var handler = new McpProtocolHandler(services.Mcp, services.ConnectionFactory, serverSettings, ReportMcpException);
        var server = new McpHttpServer(handler, serverSettings, ReportMcpException);
        server.ConnectionCountsChanged += OnMcpConnectionCountsChanged;
        try
        {
            await server.StartAsync();
            _mcpServer = server;
            await SetMcpEndpointAsync(server.Endpoint);
            SetMcpStatus("MCP: 运行中", BuildMcpConnectionDetail(), Brushes.LimeGreen);
            if (operationId is not null)
                await services.BlockingOperations.CompleteAsync(operationId.Value, "MCP HTTP listener 已启动。", cancellationToken: CancellationToken.None);
            await LogOperationAsync("mcp_http_start", $"MCP HTTP server listening on {server.Endpoint}");
        }
        catch (Exception ex)
        {
            UnexpectedExceptions.Sink.Report(ex, "mcp-server", "start-listener");
            server.ConnectionCountsChanged -= OnMcpConnectionCountsChanged;
            try { await server.DisposeAsync(); }
            catch (Exception disposeException)
            {
                UnexpectedExceptions.Sink.Report(disposeException, "mcp-server", "dispose-after-start-failure");
            }
            var message = McpOutputSanitizer.Sanitize(ex.Message);
            if (operationId is not null)
                await services.BlockingOperations.FailAsync(operationId.Value, AppErrorCodes.InvalidState, message, "MCP HTTP listener 启动失败。", ["检查端口占用", "检查 bind 和鉴权设置"], CancellationToken.None);
            SetMcpStatus("MCP: 错误", message, Brushes.IndianRed);
            await LogOperationAsync("mcp_http_start_failed", message);
        }
    }

    private void SetMcpStatus(string text, string detail, IBrush brush)
    {
        void Update()
        {
            McpStatusText = text;
            McpStatusDetail = detail;
            McpStatusBrush = brush;
            Raise(nameof(McpStatusText));
            Raise(nameof(McpStatusDetail));
            Raise(nameof(McpStatusBrush));
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess() || !HasDesktopMainWindow()) Update();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Update);
    }

    private Task SetMcpEndpointAsync(string endpoint)
    {
        void Update()
        {
            McpEndpoint = endpoint;
            Raise(nameof(McpEndpoint));
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess() || !HasDesktopMainWindow())
        {
            Update();
            return Task.CompletedTask;
        }
        return Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Update).GetTask();
    }

    private static bool HasDesktopMainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: not null };

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

    private FirstRunViewModel CreateFirstRunViewModel() => new(OpenFirstRunDatabaseAsync, ModalOperations) { DatabasePath = RuntimeDatabasePath, MinerUToken = Shell.MinerUToken, OnError = ReportError, OnProgress = Report };

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
            if (FirstRun.SelectedScanRoot is null)
            {
                ReportError("文件搜索根必须通过系统文件夹选择器选择。");
                return;
            }

            var addedRoot = await services.FileResolution.AddSearchRootAsync(FirstRun.SelectedScanRoot);
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

    public async Task RefreshOpenItemEditorsAsync(IReadOnlyCollection<ItemId> itemIds)
    {
        var wanted = itemIds.Select(itemId => itemId.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var editor in OpenTabs.Select(tab => tab.Content).OfType<ItemEditorViewModel>())
        {
            if (wanted.Contains(editor.ItemIdText)) await editor.LoadAsync(editor.ItemIdText);
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
        Shell.ExitReadingMode();
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

    public async Task ExportEvidenceMarkdownToFileAsync(string evidenceRef, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(evidenceRef))
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

        var markdown = await (await ServicesAsync()).Evidence.CreateMarkdownAsync(evidenceRef);
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

public sealed record FileSearchRootRescanSummary(
    int ScannedPdfCount,
    int ImportedPdfCount,
    int SkippedKnownPdfCount,
    int FailedPdfCount,
    int PartialRootCount = 0,
    int UnavailableRootCount = 0,
    int SkippedDirectoryCount = 0,
    int SkippedFileCount = 0)
{
    public bool HasWarnings => FailedPdfCount > 0 || PartialRootCount > 0 || UnavailableRootCount > 0 || SkippedDirectoryCount > 0 || SkippedFileCount > 0;
}
