using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Credentials;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Conflicts;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Infrastructure.Workflows;
using Patchouli.Mcp;
using Patchouli.McpServer;
using Patchouli.Ocr;
using Patchouli.Core.Search;
using Patchouli.UI.Themes;
using Patchouli.Host.Composition;
using Patchouli.Host.Settings;
using Patchouli.Host.Caching;
using Patchouli.Host.Import;
using Patchouli.Host.Lifecycle;
using Patchouli.Host.Mcp;
using Patchouli.Host.Watching;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Patchouli.UI;

namespace Patchouli.UI.ViewModels;

using Settings;
using Editor;
using Csl;
using Core;
using Dialogs;
using Views;
using Services;

public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly TimeSpan RevisionBufferWindow = TimeSpan.FromMilliseconds(20);
    private readonly IScheduler _timingScheduler;
    private readonly IScheduler _uiScheduler;
    private readonly SerialDisposable _revisionSubscription = new();
    private readonly SerialDisposable _mcpSubscriptions = new();
    private readonly SerialDisposable _watcherSubscriptions = new();
    private readonly Subject<bool> _queryRewriteRequests = new();
    private bool _suppressQueryRewritePersist;

    private static readonly Action<Exception, string, string?> ReportUnexpectedException =
        static (exception, boundary, operation) =>
            UnexpectedExceptions.Sink.Report(exception, boundary, operation);

    private HostServices? _services;
    private ILibraryRevisionService? _observedLibraryRevisions;
    private McpServerHost? _mcpHost;
    private FileSearchRootWatcherService? _fileSearchRootWatcher;
    private LibraryImportOrchestrator? _importOrchestrator;
    private LibraryRevisionMonitor? _libraryRevisionMonitor;
    private RuntimeHostLease? _runtimeHostLease;
    private RescanCompletionCapture? _activeRescanCapture;
    private readonly bool _autoStartMcpServer;
    private readonly bool _enforceRuntimeHostOwnership;
    private PatchouliAppSettings _settings;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IHostActivityTracker _activityTracker;
    private readonly bool _ownsActivityTracker;
    private IDisposable? _activitySubscription;
    private readonly string? _settingsPath;
    private int _libraryGeneration;
    private bool _queryRewriteEnabledPersisted = true;
    private bool _synchronizingToolbarSearch;
    private SettingsViewModel? _settingsViewModel;
    private LibraryViewModel? _libraryViewModel;
    private BibliographyViewModel? _bibliographyViewModel;
    private FileDocumentViewModel? _fileDocumentViewModel;
    private OcrQueueViewModel? _ocrQueueViewModel;
    private SearchEvidenceViewModel? _searchEvidenceViewModel;

    public WorkspaceLayoutViewModel Layout { get; }
    public WorkspaceManager Workspace { get; }
    [ExcludeFromDerivedGeneration] public ObservableCollection<WorkspaceTabViewModel> OpenTabs => Layout.Tabs;

    public WorkspaceTabViewModel? ActiveTab
    {
        get => Layout.ActiveTab;
        set => Layout.ActiveTab = value;
    }

    [ObservableProperty] public partial string RuntimeDatabasePath { get; set; }

    partial void OnRuntimeDatabasePathChanged(string value)
    {
        _settingsViewModel?.NotifyRuntimeDatabasePathChanged();
        OpenDatabaseCommand?.NotifyCanExecuteChanged();
    }

    [ExcludeFromDerivedGeneration] public string DefaultSyncRootPath => _settings.Runtime.DefaultSyncRoot;

    public ObservableCollection<SidebarFileSearchRootViewModel> FileSearchRoots { get; } = new();

    [ObservableProperty] public partial bool HasFileSearchRoots { get; private set; }

    [ObservableProperty] public partial bool NoFileSearchRoots { get; private set; } = true;

    [ObservableProperty] public partial string Status { get; set; } = "请选择运行数据库路径，然后创建或打开资料库。";

    [ObservableProperty] public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial string McpEndpoint { get; private set; } = $"http://localhost:{McpServerOptions.DefaultPort}/mcp";

    [ObservableProperty] public partial string McpStatusText { get; private set; } = "MCP: 未启动";

    [ExcludeFromDerivedGeneration] public bool McpServerRunning => _mcpHost?.IsRunning == true;

    [ExcludeFromDerivedGeneration] public bool CanStopMcpServer => !_enforceRuntimeHostOwnership;

    [ExcludeFromDerivedGeneration] public long? McpRunningSettingsRevision => _mcpHost?.RunningSettingsRevision;

    [ObservableProperty] public partial string McpStatusDetail { get; private set; } = "等待运行数据库打开。";

    [ObservableProperty] public partial IBrush McpStatusBrush { get; private set; } = Brushes.Gray;

    public string VersionInfo =>
        $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion} | {RuntimeDatabasePath}";

    [ExcludeFromDerivedGeneration]
    public string StatusBarVersion =>
        $"{Patchouli.Core.BuildInfo.AppName} {Patchouli.Core.BuildInfo.Version} | Schema {Patchouli.Core.BuildInfo.SchemaVersion}";

    [ExcludeFromDerivedGeneration] public string SettingsFilePath => PatchouliAppSettings.ResolvePath(_settingsPath);

    [ExcludeFromDerivedGeneration] public bool HasOpenRuntimeDatabase => _services is not null;

    [ExcludeFromDerivedGeneration] public int LibraryGeneration => Volatile.Read(ref _libraryGeneration);

    internal bool IsCurrentLibraryContext(HostServices services, int generation)
    {
        return generation == LibraryGeneration && ReferenceEquals(services, _services);
    }

    public IClipboardService Clipboard { get; }
    public IFilePickerService FilePicker { get; }
    public IDialogService Dialogs { get; }
    public IModalOperationRunner ModalOperations { get; }
    public IAppLogger Logger { get; }
    public LibraryShellViewModel Shell { get; }

    [ExcludeFromDerivedGeneration]
    public SettingsViewModel Settings =>
        _settingsViewModel ??= CreateSettingsViewModel();

    [ExcludeFromDerivedGeneration] public bool HasDirtySettings => _settingsViewModel?.HasDirtySections == true;

    [ObservableProperty] public partial FirstRunViewModel FirstRun { get; private set; }

    [ObservableProperty] public partial bool IsFirstRunVisible { get; set; }

    public bool IsLibraryVisible => !IsFirstRunVisible;
    public bool IsSearchEnabled => !IsFirstRunVisible;

    [ObservableProperty] public partial bool IsStartupLoadingVisible { get; set; } = true;

    [ObservableProperty] public partial string StartupLoadingStatus { get; set; } = "正在启动…";

    public bool ShowInspectorPane
    {
        get => Layout.ShowInspectorPane;
        set => Layout.ShowInspectorPane = value;
    }

    [ObservableProperty] public partial bool ShowSidebar { get; private set; }

    [ObservableProperty] public partial bool IsInspectorVisible { get; private set; }

    partial void OnShowSidebarChanged(bool value)
    {
        Shell?.RaisePageStateChanged();
    }

    partial void OnIsInspectorVisibleChanged(bool value)
    {
        Shell?.RaisePageStateChanged();
    }

    public bool ShowLibraryLeftSidebarPreference
    {
        get => _settings.Ui.ShowLibraryLeftSidebar;
        set
        {
            if (_settings.Ui.ShowLibraryLeftSidebar == value)
            {
                return;
            }

            UpdateAppOptionsDeferred(
                _settings with { Ui = _settings.Ui with { ShowLibraryLeftSidebar = value } }, "Ui");

            // Manual Raise notifies ShowLibraryLeftSidebarPreference property change to trigger UI update and downstream generator derivation for IsLibraryLeftSidebarVisible.
            Raise();
            Shell.RaisePageStateChanged();
        }
    }

    public bool ShowLibraryRightSidebarPreference
    {
        get => _settings.Ui.ShowLibraryRightSidebar;
        set
        {
            if (_settings.Ui.ShowLibraryRightSidebar == value)
            {
                return;
            }

            UpdateAppOptionsDeferred(
                _settings with { Ui = _settings.Ui with { ShowLibraryRightSidebar = value } }, "Ui");

            // Manual Raise notifies ShowLibraryRightSidebarPreference property change to trigger UI update and downstream generator derivation for IsLibraryRightSidebarVisible.
            Raise();
            Shell.RaisePageStateChanged();
        }
    }

    public bool IsLibraryLeftSidebarVisible => ShowLibraryLeftSidebarPreference && ShowSidebar;
    public bool IsLibraryRightSidebarVisible => ShowLibraryRightSidebarPreference && IsInspectorVisible;

    /// <summary>
    /// Per-library query-rewrite switch bound to the 搜索 menu checkbox. The setter fires a persist
    /// request; <see cref="ApplyQueryRewriteEnabled"/> is the non-persisting path used while loading
    /// per-library state and when reverting a failed persist.
    /// </summary>
    [ObservableProperty]
    public partial bool QueryRewriteEnabled { get; set; }

    partial void OnQueryRewriteEnabledChanged(bool value)
    {
        if (_suppressQueryRewritePersist)
        {
            return;
        }

        _queryRewriteRequests.OnNext(value);
    }

    [ObservableProperty] public partial bool ShowSelectedDocumentTab { get; private set; }

    [ObservableProperty] public partial bool ShowSettingsTab { get; private set; }

    [ObservableProperty] public partial bool ShowItemEditorTab { get; private set; }

    [ObservableProperty] public partial bool IsLibraryTabActive { get; private set; }

    [ObservableProperty] public partial bool IsReaderTabActive { get; private set; }

    [ObservableProperty] public partial bool IsSettingsVisible { get; private set; }

    [ObservableProperty] public partial bool IsItemEditorVisible { get; private set; }

    [ObservableProperty] public partial string LibraryTabTitle { get; private set; } = "我的书库";

    partial void OnLibraryTabTitleChanged(string value)
    {
        WorkspaceTabViewModel? libTab = OpenTabs.FirstOrDefault(t => t.Kind == WorkspaceTabKind.Library);
        if (libTab != null)
        {
            libTab.Title = value;
        }
    }

    [ObservableProperty] public partial string PdfTabTitle { get; private set; } = "";

    [ExcludeFromDerivedGeneration]
    public LibraryViewModel Library =>
        _libraryViewModel ??= CreateWithActivityTracker(() => new LibraryViewModel(this));

    [ExcludeFromDerivedGeneration]
    public PdfWorkspaceViewModel PdfWorkspace =>
        GetWorkspaceContent<PdfWorkspaceViewModel>(WorkspaceTabKind.PdfWorkspace) ??
        throw new InvalidOperationException("PDF workspace tab is not open.");

    [ExcludeFromDerivedGeneration]
    public ItemEditorViewModel ItemEditor => GetWorkspaceContent<ItemEditorViewModel>(WorkspaceTabKind.ItemEditor) ??
                                             throw new InvalidOperationException("Item editor tab is not open.");

    [ExcludeFromDerivedGeneration]
    public BibliographyViewModel Bibliography =>
        _bibliographyViewModel ??= CreateWithActivityTracker(() => new BibliographyViewModel(this));

    [ExcludeFromDerivedGeneration]
    public FileDocumentViewModel FileDocument =>
        _fileDocumentViewModel ??= CreateWithActivityTracker(() => new FileDocumentViewModel(this));

    [ExcludeFromDerivedGeneration]
    public OcrQueueViewModel OcrQueue =>
        _ocrQueueViewModel ??= CreateWithActivityTracker(() => new OcrQueueViewModel(this));

    [ExcludeFromDerivedGeneration]
    public SearchEvidenceViewModel SearchEvidence =>
        _searchEvidenceViewModel ??= CreateSearchEvidenceViewModel();

    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<SearchModeOption> ToolbarSearchModeOptions => SearchEvidenceViewModel.AvailableModeOptions;

    [ObservableProperty] public partial string ToolbarSearchQuery { get; set; } = "";

    [ObservableProperty]
    public partial SearchModeOption ToolbarSearchMode { get; set; } = SearchEvidenceViewModel.AvailableModeOptions[1];

    partial void OnToolbarSearchQueryChanged(string value)
    {
        if (!_synchronizingToolbarSearch && _searchEvidenceViewModel is { } search)
        {
            search.Query = value;
        }
    }

    partial void OnToolbarSearchModeChanged(SearchModeOption value)
    {
        if (!_synchronizingToolbarSearch && _searchEvidenceViewModel is { } search)
        {
            search.SelectedMode = value;
        }
    }

    private McpPreviewViewModel? _mcpPreview;

    [ExcludeFromDerivedGeneration]
    public McpPreviewViewModel McpPreview =>
        _mcpPreview ??= CreateWithActivityTracker(() => new McpPreviewViewModel(this));

    private SnapshotViewModel? _snapshot;

    [ExcludeFromDerivedGeneration] public SnapshotViewModel Snapshot => _snapshot ??= CreateSnapshotViewModel();

    private AboutViewModel? _about;

    [ExcludeFromDerivedGeneration]
    public AboutViewModel About => _about ??= CreateWithActivityTracker(() => new AboutViewModel(this));

    public AsyncCommand OpenDatabaseCommand { get; }
    public AsyncCommand CompleteFirstRunCommand { get; }
    public AsyncCommand ShowLibraryCommand { get; }
    public AsyncCommand ShowReadingCommand { get; }
    public AsyncCommand RunToolbarSearchCommand { get; }
    public AsyncCommand OpenAdvancedSearchCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }
    public AsyncCommand OpenSearchRewriteSettingsCommand { get; }
    public AsyncCommand OpenMcpSettingsCommand { get; }
    public AsyncCommand OpenOcrQueueCommand { get; }
    public AsyncCommand ActivateSettingsTabCommand { get; }
    public AsyncCommand ActivateSearchTabCommand { get; }
    public AsyncCommand ActivateOcrQueueTabCommand { get; }
    public AsyncCommand ActivateAboutTabCommand { get; }
    public AsyncCommand CheckSyncStateCommand { get; }
    public AsyncCommand CopyCslBibliographyCommand { get; }
    public AsyncCommand ExportItemCommand { get; }
    public AsyncCommand ImportBiblatexBatchCommand { get; }
    public AsyncCommand ExportBiblatexCommand { get; }
    public AsyncCommand CopyBiblatexCommand { get; }
    public AsyncCommand CreateItemMenuCommand { get; }
    public AsyncCommand OpenItemEditorCommand { get; }
    public AsyncCommand EditSelectedItemCommand { get; }
    public AsyncCommand DetectDuplicateItemsCommand { get; }
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
    public UiCommandDescriptor PublishSnapshotDescriptor { get; }
    public UiCommandDescriptor ExportSnapshotPackageDescriptor { get; }
    public UiCommandDescriptor ReceiveSnapshotDescriptor { get; }
    public UiCommandDescriptor OpenSnapshotPackageDescriptor { get; }
    public UiCommandDescriptor CopyCslBibliographyDescriptor { get; }
    public UiCommandDescriptor ExportItemDescriptor { get; }
    public UiCommandDescriptor ImportBiblatexBatchDescriptor { get; }
    public UiCommandDescriptor ExportBiblatexDescriptor { get; }
    public UiCommandDescriptor CopyBiblatexDescriptor { get; }

    [ExcludeFromDerivedGeneration] public PatchouliAppSettings AppOptions => _settings;
    [ExcludeFromDerivedGeneration] public IAppSettingsStore SettingsStore => _settingsStore;
    [ExcludeFromDerivedGeneration] public IHostActivityTracker ActivityTracker => _activityTracker;

    [ObservableProperty] public partial bool IsActivityBusy { get; private set; }
    [ObservableProperty] public partial string? ActivitySummary { get; private set; }
    [ObservableProperty] public partial string? ActivityReason { get; private set; }
    [ObservableProperty] public partial string ActivityDescription { get; private set; } = "空闲";

    private void UpdateActivitySnapshot(HostActivitySnapshot snapshot)
    {
        IsActivityBusy = snapshot.IsBusy;
        ActivitySummary = snapshot.ActiveSummary;
        ActivityReason = snapshot.IsBusy ? null : snapshot.SleepReason;
        ActivityDescription = FormatActivityDescription(snapshot);
    }

    public static string FormatActivityDescription(HostActivitySnapshot snapshot)
    {
        if (snapshot.IsBusy)
        {
            return string.IsNullOrWhiteSpace(snapshot.ActiveSummary)
                ? "运行中"
                : $"运行中: {snapshot.ActiveSummary}";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.SleepReason))
        {
            return $"等待中: {snapshot.SleepReason}";
        }

        return "空闲";
    }

    public void UpdateAppOptionsDeferred(Func<PatchouliAppSettings, PatchouliAppSettings> updater,
        string? fieldCategory = "Ui")
    {
        ArgumentNullException.ThrowIfNull(updater);
        _settings = updater(_settings);
        _settingsStore.Update(_ => _settings, fieldCategory);
    }

    public void UpdateAppOptionsDeferred(PatchouliAppSettings settings, string? fieldCategory = "Ui")
    {
        UpdateAppOptionsDeferred(_ => settings, fieldCategory);
    }

    public SettingsSaveResult UpdateAppOptions(PatchouliAppSettings settings)
    {
        ApplyAppOptions(settings);
        _settingsStore.Update(_ => settings);
        return SettingsSaveResult.Success;
    }

    public async Task<SettingsSaveResult> UpdateAppOptionsAsync(
        PatchouliAppSettings settings,
        string? fieldCategory = null,
        CancellationToken cancellationToken = default)
    {
        ApplyAppOptions(settings);
        _settingsStore.Update(_ => settings, fieldCategory);
        return await _settingsStore.SaveImmediatelyAsync(cancellationToken);
    }

    private void ApplyAppOptions(PatchouliAppSettings settings)
    {
        _settings = settings;
        _services?.UpdateMetadataLookupPreferences(settings.MetadataLookup);
        _services?.UpdateFileScanExclusions(settings.FileScanning);
    }

    public async Task<SettingsSaveResult> SaveMetadataLookupSettingsAsync(MetadataLookupAppSettings metadataLookup)
    {
        if (!HasAnyMetadataLookupSyncEnabled())
        {
            return await UpdateAppOptionsAsync(_settings with { MetadataLookup = metadataLookup }, "MetadataLookup");
        }

        HostServices services = await ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsFailure)
        {
            return new SettingsSaveResult(false, library.ErrorCode, library.ErrorMessage, "library_identity", true);
        }

        if (!_settings.Sync.IsSettingEnabled(LibrarySettingKeys.MetadataLookup, library.Value.LibraryId))
        {
            return await UpdateAppOptionsAsync(_settings with { MetadataLookup = metadataLookup }, "MetadataLookup");
        }

        MetadataLookupAppSettings normalized = MetadataLookupAppSettings.MergeWithDefaults(metadataLookup.Sources);
        return await services.LibrarySettingCoordinator.SaveEnabledAsync(
            LibrarySettingKeys.MetadataLookup,
            normalized,
            _settings.Sync.DeviceId,
            _ => UpdateAppOptionsAsync(_settings with { MetadataLookup = normalized }, "MetadataLookup"),
            services.UpdateMetadataLookupPreferences);
    }

    private bool HasAnyMetadataLookupSyncEnabled()
    {
        return _settings.Sync.SyncMetadataLookup ||
               _settings.Sync.Bindings.Any(binding =>
                   string.Equals(binding.RootKind, LogicalRootKinds.SyncRoot, StringComparison.Ordinal) &&
                   LibrarySettingCatalog.NormalizeSnapshotKeys(binding.SyncedSettingKeys)
                       .Contains(LibrarySettingKeys.MetadataLookup, StringComparer.Ordinal));
    }

    public async Task<SettingsSaveResult> SetMetadataLookupSyncEnabledAsync(
        bool enabled,
        SyncAppSettings? syncOverride = null)
    {
        HostServices services = await ServicesAsync();
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsFailure)
        {
            return new SettingsSaveResult(false, library.ErrorCode, library.ErrorMessage, "library_identity", true);
        }

        bool currentlyEnabled =
            _settings.Sync.IsSettingEnabled(LibrarySettingKeys.MetadataLookup, library.Value.LibraryId);
        if (enabled == currentlyEnabled && syncOverride is null)
        {
            return SettingsSaveResult.Success;
        }

        SyncAppSettings nextSync = syncOverride ?? _settings.Sync.WithSettingEnabled(
            library.Value.LibraryId,
            LibrarySettingKeys.MetadataLookup,
            enabled);
        if (enabled)
        {
            MetadataLookupAppSettings normalized =
                MetadataLookupAppSettings.MergeWithDefaults(_settings.MetadataLookup.Sources);
            return await services.LibrarySettingCoordinator.SaveEnabledAsync(
                LibrarySettingKeys.MetadataLookup,
                normalized,
                _settings.Sync.DeviceId,
                _ => UpdateAppOptionsAsync(_settings with { Sync = nextSync }, "Sync"),
                services.UpdateMetadataLookupPreferences);
        }

        return await services.LibrarySettingCoordinator.DisableAndMaterializeAsync<MetadataLookupAppSettings>(
            LibrarySettingKeys.MetadataLookup,
            currentlyEnabled,
            _settings.MetadataLookup,
            (materialized, _) => UpdateAppOptionsAsync(_settings with
            {
                MetadataLookup = MetadataLookupAppSettings.MergeWithDefaults(materialized.Sources),
                Sync = nextSync
            }, null),
            materialized => services.UpdateMetadataLookupPreferences(
                MetadataLookupAppSettings.MergeWithDefaults(materialized.Sources)));
    }

    private async Task PersistRuntimeDatabasePathIfEnabledAsync()
    {
        if (!_settings.Runtime.RememberLastDatabase)
        {
            return;
        }

        string normalizedPath = Path.GetFullPath(RuntimeDatabasePath);
        string currentPath = Path.GetFullPath(_settings.Runtime.RuntimeDatabasePath);
        if (string.Equals(normalizedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SettingsSaveResult saved = await UpdateAppOptionsAsync(
            _settings with { Runtime = _settings.Runtime with { RuntimeDatabasePath = normalizedPath } }, "Runtime");
        if (!saved.IsSuccess)
        {
            ReportError(saved.ErrorMessage ?? "无法保存运行数据库路径。");
        }
    }

    public MainWindowViewModel(IClipboardService? clipboard = null, IAppLogger? logger = null,
        IDialogService? dialogs = null, bool autoStartMcpServer = false, int mcpPort = McpServerOptions.DefaultPort,
        string? settingsPath = null, IModalOperationRunner? modalOperations = null,
        IFilePickerService? filePicker = null, bool enforceRuntimeHostOwnership = false,
        IScheduler? timingScheduler = null, IScheduler? uiScheduler = null,
        IAppSettingsStore? settingsStore = null,
        IHostActivityTracker? activityTracker = null)
    {
        _timingScheduler = timingScheduler ?? TaskPoolScheduler.Default;
        _uiScheduler = uiScheduler ?? (SynchronizationContext.Current is { } synchronizationContext
            ? new SynchronizationContextScheduler(synchronizationContext)
            : ImmediateScheduler.Instance);
        Register(_revisionSubscription);
        Register(_mcpSubscriptions);
        Register(_watcherSubscriptions);
        _settingsPath = settingsPath;
        SettingsLoadFailure? settingsLoadFailure = null;
        _settings = PatchouliAppSettings.Load(settingsPath, failure => settingsLoadFailure ??= failure);
        _settingsStore = settingsStore ?? new AppSettingsStore(settingsPath, _settings);
        _activityTracker = activityTracker ?? new HostActivityTracker();
        _ownsActivityTracker = activityTracker is null;
        using IDisposable commandActivityTracker = AsyncCommand.UseActivityTracker(_activityTracker);
        UpdateActivitySnapshot(_activityTracker.Current);
        _activitySubscription = _activityTracker.SnapshotStream
            .ObserveOn(_uiScheduler)
            .Subscribe(UpdateActivitySnapshot);
        Register(_activitySubscription);
        if (settingsLoadFailure is not null)
        {
            Status = settingsLoadFailure.ErrorMessage;
            StatusIsError = true;
        }

        if (settingsLoadFailure is null &&
            (string.IsNullOrWhiteSpace(_settings.Sync.DeviceId) ||
             string.IsNullOrWhiteSpace(_settings.Sync.SyncRootId)))
        {
            PatchouliAppSettings initializedSettings = _settings with
            {
                Sync = _settings.Sync with
                {
                    DeviceId = string.IsNullOrWhiteSpace(_settings.Sync.DeviceId)
                        ? Guid.NewGuid().ToString("D")
                        : _settings.Sync.DeviceId,
                    SyncRootId = string.IsNullOrWhiteSpace(_settings.Sync.SyncRootId)
                        ? Guid.NewGuid().ToString("D")
                        : _settings.Sync.SyncRootId
                }
            };
            _settings = initializedSettings;
            _settingsStore.Update(_ => initializedSettings, "Sync");
        }

        ThemePaletteApplier.Apply(_settings.Ui.PaletteId);

        RuntimeDatabasePath = _settings.Runtime.RememberLastDatabase
            ? _settings.Runtime.RuntimeDatabasePath
            : AppRuntimeOptions.Default().RuntimeDatabasePath;
        _autoStartMcpServer = autoStartMcpServer;
        _enforceRuntimeHostOwnership = enforceRuntimeHostOwnership;
        McpEndpoint = $"http://localhost:{mcpPort}/mcp";
        Clipboard = clipboard ?? new AvaloniaClipboardService();
        FilePicker = filePicker ?? new AvaloniaFilePickerService();
        Dialogs = dialogs ?? CreateDialogService();
        ModalOperations = modalOperations ?? new ModalOperationRunner(Dialogs);
        Logger = logger ?? new SimpleFileLogger(_settings.Runtime.LogDirectory);
        Layout = new WorkspaceLayoutViewModel();
        Workspace = new WorkspaceManager(Layout);
        Shell = new LibraryShellViewModel(this);

        Shell.MinerUToken = "";

        IDisposable queryRewriteSub = _queryRewriteRequests
            .ObserveOn(_uiScheduler)
            .Select(enabled => Observable.FromAsync(async ct =>
            {
                try
                {
                    await PersistQueryRewriteEnabledAsync(enabled, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    UnexpectedExceptions.Sink.Report(ex, "query-rewrite", "persist");
                }
            }))
            .Switch()
            .Subscribe();
        Register(queryRewriteSub);
        Register(_queryRewriteRequests);

        IObservable<EventPattern<PropertyChangedEventArgs>> layoutChanges =
            Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => Layout.PropertyChanged += h,
                h => Layout.PropertyChanged -= h);

        layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.HasPdfWorkspaceTab))
            .Select(_ => Layout.HasPdfWorkspaceTab)
            .BindOutput(this, v => ShowSelectedDocumentTab = v, _uiScheduler, null, true, Layout.HasPdfWorkspaceTab);

        layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.HasSettingsTab))
            .Select(_ => Layout.HasSettingsTab)
            .BindOutput(this, v => ShowSettingsTab = v, _uiScheduler, null, true, Layout.HasSettingsTab);

        layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.HasItemEditorTab))
            .Select(_ => Layout.HasItemEditorTab)
            .BindOutput(this, v => ShowItemEditorTab = v, _uiScheduler, null, true, Layout.HasItemEditorTab);

        layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.IsLibraryActive))
            .Select(_ => Layout.IsLibraryActive)
            .BindOutput(this, v => IsLibraryTabActive = v, _uiScheduler, null, true, Layout.IsLibraryActive);

        layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.IsReaderActive))
            .Select(_ => Layout.IsReaderActive)
            .BindOutput(this, v => IsReaderTabActive = v, _uiScheduler, null, true, Layout.IsReaderActive);

        layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.IsSettingsActive))
            .Select(_ => Layout.IsSettingsActive)
            .BindOutput(this, v => IsSettingsVisible = v, _uiScheduler, null, true, Layout.IsSettingsActive);

        layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.IsItemEditorActive))
            .Select(_ => Layout.IsItemEditorActive)
            .BindOutput(this, v => IsItemEditorVisible = v, _uiScheduler, null, true, Layout.IsItemEditorActive);

        IObservable<EventPattern<PropertyChangedEventArgs>> shellChanges =
            Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => Shell.PropertyChanged += h,
                h => Shell.PropertyChanged -= h);

        IObservable<bool> layoutShowSidebar = layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.ShowSidebar))
            .Select(_ => Layout.ShowSidebar)
            .StartWith(Layout.ShowSidebar);

        IObservable<bool> shellReadingMode = shellChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(LibraryShellViewModel.IsReadingMode))
            .Select(_ => Shell.IsReadingMode)
            .StartWith(Shell.IsReadingMode);

        layoutShowSidebar.CombineLatest(shellReadingMode, (show, reading) => show && !reading)
            .BindOutput(this, v => ShowSidebar = v, _uiScheduler, null, true,
                Layout.ShowSidebar && !Shell.IsReadingMode);

        IObservable<bool> layoutInspector = layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.IsInspectorVisible))
            .Select(_ => Layout.IsInspectorVisible)
            .StartWith(Layout.IsInspectorVisible);

        layoutInspector.CombineLatest(shellReadingMode, (inspector, reading) => inspector && !reading)
            .BindOutput(this, v => IsInspectorVisible = v, _uiScheduler, null, true,
                Layout.IsInspectorVisible && !Shell.IsReadingMode);

        shellChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(LibraryShellViewModel.LibraryName))
            .Select(_ => string.IsNullOrWhiteSpace(Shell.LibraryName) ? "我的书库" : Shell.LibraryName)
            .BindOutput(this, v => LibraryTabTitle = v, _uiScheduler, null, true,
                string.IsNullOrWhiteSpace(Shell.LibraryName) ? "我的书库" : Shell.LibraryName);

        shellChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(LibraryShellViewModel.SelectedItem))
            .Select(_ =>
                BuildItemWorkspaceTabTitle("PDF 工作台",
                    Shell.SelectedItem?.Title ?? Shell.SelectedItem?.FileName ?? "PDF 阅读"))
            .BindOutput(this, v => PdfTabTitle = v, _uiScheduler, null, true,
                BuildItemWorkspaceTabTitle("PDF 工作台",
                    Shell.SelectedItem?.Title ?? Shell.SelectedItem?.FileName ?? "PDF 阅读"));

        IObservable<EventPattern<NotifyCollectionChangedEventArgs>> rootsChanged =
            Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => FileSearchRoots.CollectionChanged += h,
                h => FileSearchRoots.CollectionChanged -= h);

        rootsChanged.Select(_ => FileSearchRoots.Count > 0)
            .BindOutput(this, v => HasFileSearchRoots = v, _uiScheduler, null, true, FileSearchRoots.Count > 0);

        rootsChanged.Select(_ => FileSearchRoots.Count == 0)
            .BindOutput(this, v => NoFileSearchRoots = v, _uiScheduler, null, true, FileSearchRoots.Count == 0);

        IDisposable activeTabSub = layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.ActiveTab))
            .ObserveOn(_uiScheduler)
            .Subscribe(_ =>
            {
                if (Layout.IsLibraryActive)
                {
                    Shell.ExitReadingMode();
                }

                OnPropertyChanged(nameof(ActiveTab));
            }, exception => UnexpectedExceptions.Sink.Report(exception, "layout-active-tab"));
        Register(activeTabSub);

        IDisposable inspectorPaneSub = layoutChanges
            .Where(e => e.EventArgs.PropertyName is null or nameof(WorkspaceLayoutViewModel.ShowInspectorPane))
            .ObserveOn(_uiScheduler)
            .Subscribe(_ =>
            {
                OnPropertyChanged(nameof(ShowInspectorPane));
                Shell.RaisePageStateChanged();
            }, exception => UnexpectedExceptions.Sink.Report(exception, "layout-inspector-pane"));
        Register(inspectorPaneSub);

        OpenDatabaseCommand = new AsyncCommand(async () =>
        {
            if (HasDirtySettings)
            {
                ReportError("设置有未保存的更改，请先保存或放弃后再切换数据库。");
                return;
            }

            await BeginLibrarySwitchAsync("正在切换运行数据库。");
            await RunWithStartupLoadingAsync(async () =>
            {
                HostServices services;
                try
                {
                    services = await CreateOwnedHostServicesAsync();
                    SetServices(services);
                }
                catch (UnsupportedLibrarySchemaException exception)
                {
                    string epochs = exception.SchemaVersions.Count == 0
                        ? "未知"
                        : string.Join("、", exception.SchemaVersions.Order());
                    ReportError($"无法打开资料库：检测到不受 Patchouli 0.3.5 支持的数据库 schema epoch（{epochs}）。" +
                                "0.3.5 不会自动迁移旧资料库；请新建资料库并重新导入源文档。");
                    return;
                }

                StartupLoadingStatus = "正在同步元数据查找设置…";
                await RefreshSyncedMetadataLookupAsync(services);
                if (_settingsViewModel is { } settings)
                {
                    await settings.ReloadCleanSectionsAsync();
                }

                await RefreshQueryRewriteEnabledAsync(services);
                await PersistRuntimeDatabasePathIfEnabledAsync();
                StartupLoadingStatus = "正在恢复 MinerU 凭据…";
                await LoadPersistedMinerUTokenAsync();
                StartupLoadingStatus = "正在刷新文件搜索路径…";
                await RefreshSidebarPathsAsync();
                Status = $"数据库已就绪：{RuntimeDatabasePath}";
                if (_autoStartMcpServer)
                {
                    StartupLoadingStatus = "正在启动 MCP 服务器…";
                    await StartMcpServerAsync(services);
                }
            });
        }, () => !string.IsNullOrWhiteSpace(RuntimeDatabasePath));
        FirstRun = CreateFirstRunViewModel();

        Workspace.OpenOrActivate(WorkspaceTabKind.Library, "Library", "我的书库", "Database", false, () => Shell);

        CompleteFirstRunCommand = new AsyncCommand(CompleteFirstRunAsync);
        ShowLibraryCommand = new AsyncCommand(ShowLibraryAsync);
        ShowReadingCommand = new AsyncCommand(ShowReadingAsync);
        RunToolbarSearchCommand = new AsyncCommand(RunToolbarSearchAsync);
        OpenAdvancedSearchCommand = new AsyncCommand(OpenAdvancedSearchAsync);
        OpenSettingsCommand = new AsyncCommand(() => OpenSettingsAsync("mineru"));
        OpenSearchRewriteSettingsCommand = new AsyncCommand(OpenSearchRewriteSettingsAsync);
        OpenMcpSettingsCommand = new AsyncCommand(() => OpenSettingsAsync("mcp"));
        OpenOcrQueueCommand = new AsyncCommand(OpenOcrQueueAsync);
        ActivateSettingsTabCommand = new AsyncCommand(() => ActivateExistingTabAsync(WorkspaceTabKind.Settings));
        ActivateSearchTabCommand = new AsyncCommand(() => ActivateExistingTabAsync(WorkspaceTabKind.SearchResults));
        ActivateOcrQueueTabCommand = new AsyncCommand(() => ActivateExistingTabAsync(WorkspaceTabKind.OcrQueue));
        ActivateAboutTabCommand = new AsyncCommand(() => ActivateExistingTabAsync(WorkspaceTabKind.About));
        CheckSyncStateCommand = new AsyncCommand(OpenSyncCenterAsync);
        CopyCslBibliographyCommand = new AsyncCommand(CopyCslBibliographyAsync, () => Shell.SelectedItem is not null);
        ExportItemCommand = new AsyncCommand(ExportSelectedItemBibliographyAsync, () => Shell.SelectedItem is not null);
        ImportBiblatexBatchCommand = new AsyncCommand(ImportBiblatexBatchAsync);
        ExportBiblatexCommand = new AsyncCommand(ExportBiblatexAsync);
        CopyBiblatexCommand = new AsyncCommand(CopyBiblatexAsync);
        CreateItemMenuCommand = new AsyncCommand(OpenNewItemEditorAsync);
        OpenItemEditorCommand = new AsyncCommand(OpenItemEditorTabAsync);
        EditSelectedItemCommand = new AsyncCommand(EditSelectedItemAsync, () => Shell.SelectedItem is not null);
        DetectDuplicateItemsCommand =
            new AsyncCommand(() => Shell.DetectDuplicatesAsync(), () => HasOpenRuntimeDatabase);
        RunSelectedItemOcrCommand = new AsyncCommand(RunSelectedItemOcrAsync, () => Shell.SelectedItem is not null);
        ClosePdfWorkspaceTabCommand = new AsyncCommand(() => CloseTabAsync(WorkspaceTabKind.PdfWorkspace),
            () => Layout.HasPdfWorkspaceTab);
        CloseSettingsTabCommand = new AsyncCommand(CloseSettingsTabAsync, () => Layout.HasSettingsTab);
        CloseSearchTabCommand = new AsyncCommand(() => CloseTabAsync(WorkspaceTabKind.SearchResults));
        CloseOcrQueueTabCommand = new AsyncCommand(() => CloseTabAsync(WorkspaceTabKind.OcrQueue));
        CloseItemEditorTabCommand = new AsyncCommand(() => CloseTabAsync(WorkspaceTabKind.ItemEditor),
            () => Layout.HasItemEditorTab);
        CloseAboutTabCommand = new AsyncCommand(() => CloseTabAsync(WorkspaceTabKind.About));
        RebuildSearchIndexCommand = new AsyncCommand(RebuildSearchIndexAsync, () => HasOpenRuntimeDatabase);
        RescanFileSearchRootsCommand = new AsyncCommand(() => RescanFileSearchRootsAsync("手动重新扫描完成。", true),
            () => HasOpenRuntimeDatabase);
        ToggleInspectorPaneCommand = new AsyncCommand(() =>
        {
            ShowInspectorPane = !ShowInspectorPane;
            return Task.CompletedTask;
        });
        ShowAboutCommand = new AsyncCommand(OpenAboutAsync);
        OpenCslStyleManagerCommand = new AsyncCommand(OpenCslStyleManagerAsync);

        IDisposable commandInvalidationSub = layoutChanges.ObserveOn(_uiScheduler).Subscribe(_ =>
        {
            ClosePdfWorkspaceTabCommand?.NotifyCanExecuteChanged();
            CloseSettingsTabCommand?.NotifyCanExecuteChanged();
            CloseItemEditorTabCommand?.NotifyCanExecuteChanged();
        }, exception => UnexpectedExceptions.Sink.Report(exception, "layout-command-invalidation"));
        Register(commandInvalidationSub);

        IDisposable shellCommandSub = shellChanges.ObserveOn(_uiScheduler).Subscribe(_ =>
        {
            EditSelectedItemCommand?.NotifyCanExecuteChanged();
            ExportItemCommand?.NotifyCanExecuteChanged();
            RunSelectedItemOcrCommand?.NotifyCanExecuteChanged();
            CopyCslBibliographyCommand?.NotifyCanExecuteChanged();
        }, exception => UnexpectedExceptions.Sink.Report(exception, "shell-command-invalidation"));
        Register(shellCommandSub);

        CheckSyncStateDescriptor = new UiCommandDescriptor("sync.open_center", "打开同步中心", CheckSyncStateCommand);
        PublishSnapshotDescriptor = new UiCommandDescriptor(
            "sync.publish",
            "发布到同步目录",
            new AsyncCommand(async () =>
            {
                await OpenSyncCenterAsync();
                await Snapshot.PublishCommand.ExecuteAsync();
            }));
        ExportSnapshotPackageDescriptor = new UiCommandDescriptor(
            "sync.export_package",
            "导出快照包…",
            new AsyncCommand(OpenSyncCenterAsync));
        ReceiveSnapshotDescriptor = new UiCommandDescriptor(
            "sync.receive_current",
            "检查/接收 current 快照",
            new AsyncCommand(async () =>
            {
                await OpenSyncCenterAsync();
                await Snapshot.CheckCurrentCommand.ExecuteAsync();
            }));
        OpenSnapshotPackageDescriptor = new UiCommandDescriptor(
            "sync.open_package",
            "从快照包打开…",
            new AsyncCommand(OpenSyncCenterAsync));
        RefreshSyncDescriptors();
        CopyCslBibliographyDescriptor =
            new UiCommandDescriptor("csl.copy_bibliography", "复制 CSL 题录", CopyCslBibliographyCommand);
        ExportItemDescriptor = new UiCommandDescriptor("csl.export_item", "导出 CSL 题录", ExportItemCommand);
        ImportBiblatexBatchDescriptor =
            new UiCommandDescriptor("biblatex.import_batch", "从 BibLaTeX 批量导入…", ImportBiblatexBatchCommand);
        ExportBiblatexDescriptor =
            new UiCommandDescriptor("biblatex.export", "导出 BibLaTeX…", ExportBiblatexCommand);
        CopyBiblatexDescriptor =
            new UiCommandDescriptor("biblatex.copy", "复制 BibLaTeX", CopyBiblatexCommand);
    }

    public Task<bool> SaveDirtySettingsAsync()
    {
        return _settingsViewModel?.SaveAllDirtySectionsAsync() ?? Task.FromResult(true);
    }

    private T CreateWithActivityTracker<T>(Func<T> factory)
    {
        using IDisposable activityTracker = AsyncCommand.UseActivityTracker(_activityTracker);
        return factory();
    }

    private SettingsViewModel CreateSettingsViewModel()
    {
        SettingsViewModel settings = CreateWithActivityTracker(() => new SettingsViewModel(this));
        settings.OcrProviderSettings.LoadPersistedToken(Shell.MinerUToken);
        return settings;
    }

    private SearchEvidenceViewModel CreateSearchEvidenceViewModel()
    {
        SearchEvidenceViewModel search =
            CreateWithActivityTracker(() => new SearchEvidenceViewModel(this));
        _synchronizingToolbarSearch = true;
        try
        {
            search.Query = ToolbarSearchQuery;
            search.SelectedMode = ToolbarSearchMode;
        }
        finally
        {
            _synchronizingToolbarSearch = false;
        }

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is not (nameof(SearchEvidenceViewModel.Query) or
                nameof(SearchEvidenceViewModel.SelectedMode)))
            {
                return;
            }

            _synchronizingToolbarSearch = true;
            try
            {
                ToolbarSearchQuery = search.Query;
                ToolbarSearchMode = search.SelectedMode;
            }
            finally
            {
                _synchronizingToolbarSearch = false;
            }
        };
        search.PropertyChanged += handler;
        Register(Disposable.Create(() => search.PropertyChanged -= handler));
        Register(search);
        return search;
    }

    private SnapshotViewModel CreateSnapshotViewModel()
    {
        SnapshotViewModel snapshot = CreateWithActivityTracker(() => new SnapshotViewModel(this));
        IObservable<EventPattern<PropertyChangedEventArgs>> snapshotChanges =
            Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => snapshot.PropertyChanged += h,
                h => snapshot.PropertyChanged -= h);
        IDisposable snapshotSub = snapshotChanges
            .ObserveOn(_uiScheduler)
            .Subscribe(e =>
            {
                if (e.EventArgs.PropertyName is null or
                    nameof(SnapshotViewModel.OperationStateText) or
                    nameof(SnapshotViewModel.OperationMessage))
                {
                    _settingsViewModel?.SyncSettings.NotifySnapshotStateChanged();
                }

                if (e.EventArgs.PropertyName is null or
                    nameof(SnapshotViewModel.OperationStateText) or
                    nameof(SnapshotViewModel.OperationState))
                {
                    RefreshSyncDescriptors();
                }
            }, exception => UnexpectedExceptions.Sink.Report(exception, "snapshot-property-changed"));
        Register(snapshotSub);
        return snapshot;
    }

    internal string GetSnapshotOperationStateText()
    {
        return _snapshot?.OperationStateText ?? "尚未打开同步中心";
    }

    internal string GetSnapshotOperationMessage()
    {
        return _snapshot?.OperationMessage ?? "同步中心尚未检查同步目录。";
    }

    public async Task<HostServices> ServicesAsync(bool startMcpServer = true)
    {
        if (_services is not null)
        {
            EnsureLibraryChangeNotifications(_services);
            return _services;
        }

        await RunStartupAsync(startMcpServer);
        return _services!;
    }

    public async Task RunStartupAsync(bool startMcpServer)
    {
        using IActivityScope startupActivity =
            _activityTracker.BeginScope("启动", HostActivityKind.Startup, "初始化运行环境");
        IsStartupLoadingVisible = true;
        StartupLoadingStatus = "正在启动…";
        try
        {
            if (_settingsStore.IsDirty)
            {
                SettingsSaveResult pendingSettings = await _settingsStore.SaveImmediatelyAsync();
                if (!pendingSettings.IsSuccess)
                {
                    ReportError(pendingSettings.ErrorMessage ?? "无法保存启动设置。");
                }
            }

            HostServices services = await CreateOwnedHostServicesAsync();
            SetServices(services);
            StartupLoadingStatus = "正在同步元数据查找设置…";
            startupActivity.UpdateDetail(StartupLoadingStatus);
            await RefreshSyncedMetadataLookupAsync(services);
            StartupLoadingStatus = "正在恢复 MinerU 凭据…";
            startupActivity.UpdateDetail(StartupLoadingStatus);
            await LoadPersistedMinerUTokenAsync();
            StartupLoadingStatus = "正在加载查询重写设置…";
            startupActivity.UpdateDetail(StartupLoadingStatus);
            await RefreshQueryRewriteEnabledAsync(services);
            StartupLoadingStatus = "正在刷新文件搜索路径…";
            startupActivity.UpdateDetail(StartupLoadingStatus);
            await RefreshSidebarPathsAsync();
            if (startMcpServer && _autoStartMcpServer)
            {
                StartupLoadingStatus = "正在启动 MCP 服务器…";
                startupActivity.UpdateDetail(StartupLoadingStatus);
                await StartMcpServerAsync(services);
            }

            StartupLoadingStatus = "正在加载资料库内容…";
            startupActivity.UpdateDetail(StartupLoadingStatus);
            Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
            if (library.IsFailure)
            {
                await ShowInlineFirstRunAsync();
            }
            else
            {
                await Shell.RefreshAsync();
            }
        }
        finally
        {
            IsStartupLoadingVisible = false;
        }
    }

    private Progress<MigrationProgress> CreateMigrationProgress()
    {
        return new Progress<MigrationProgress>(p =>
            StartupLoadingStatus = $"正在应用数据库迁移 ({p.Ordinal}/{p.Total})：{p.Name}");
    }

    private Progress<StartupStage> CreateStartupStageProgress()
    {
        return new Progress<StartupStage>(stage => StartupLoadingStatus = DescribeStartupStage(stage));
    }

    private static string DescribeStartupStage(StartupStage stage)
    {
        return stage switch
        {
            StartupStage.ValidatingPaths => "正在校验并准备存储路径…",
            StartupStage.ComposingServices => "正在初始化服务组件…",
            StartupStage.ApplyingMigrations => "正在检查数据库迁移…",
            StartupStage.AdoptingRootBindings => "正在对账文件搜索根目录绑定…",
            StartupStage.ReconcilingOcrRuns => "正在对账中断的 OCR 任务…",
            StartupStage.StartingOcrQueue => "正在启动 OCR 处理队列…",
            StartupStage.ApplyingSyncedSettings => "正在应用同步的元数据设置…",
            _ => "正在启动…"
        };
    }

    private async Task RunWithStartupLoadingAsync(Func<Task> action)
    {
        IsStartupLoadingVisible = true;
        StartupLoadingStatus = "正在切换数据库…";
        try
        {
            await action();
        }
        finally
        {
            IsStartupLoadingVisible = false;
        }
    }

    private void SetServices(HostServices services)
    {
        if (ReferenceEquals(_services, services))
        {
            EnsureLibraryChangeNotifications(services);
            return;
        }

        DetachLibraryChangeNotifications();
        _services = services;
        EnsureLibraryChangeNotifications(services);
        AttachHostServices(services);
        // Manual Raise: HasOpenRuntimeDatabase derives from private HostServices field _services (not an ObservableObject).
        Raise(nameof(HasOpenRuntimeDatabase));
        DetectDuplicateItemsCommand?.NotifyCanExecuteChanged();
        RebuildSearchIndexCommand?.NotifyCanExecuteChanged();
        RescanFileSearchRootsCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Creates the per-services host wrappers (MCP server host, file-search-root watcher fleet,
    /// import orchestrator) and attaches in-process cache invalidation. Called every
    /// time a new <see cref="HostServices"/> instance is installed.
    /// </summary>
    private void AttachHostServices(HostServices services)
    {
        _mcpHost = new McpServerHost(services, ReportUnexpectedException);
        CompositeDisposable mcpSubs = new();
        mcpSubs.Add(Observable.FromEventPattern<McpServerHostStatusChangedEventArgs>(
                h => _mcpHost.StatusChanged += h,
                h => _mcpHost.StatusChanged -= h)
            .ObserveOn(_uiScheduler)
            .Subscribe(e => OnMcpHostStatusChanged(e.Sender, e.EventArgs)));
        mcpSubs.Add(Observable.FromEventPattern<EventHandler, EventArgs>(
                h => _mcpHost.ConnectionCountsChanged += h,
                h => _mcpHost.ConnectionCountsChanged -= h)
            .ObserveOn(_uiScheduler)
            .Subscribe(e => OnMcpHostConnectionCountsChanged(e.Sender, e.EventArgs)));
        _mcpSubscriptions.Disposable = mcpSubs;

        _fileSearchRootWatcher = new FileSearchRootWatcherService(services, Logger);
        CompositeDisposable watcherSubs = new();
        watcherSubs.Add(Observable.FromEventPattern<FileSearchRootRescanCompleted>(
                h => _fileSearchRootWatcher.RescanCompleted += h,
                h => _fileSearchRootWatcher.RescanCompleted -= h)
            .ObserveOn(_uiScheduler)
            .Subscribe(e => OnFileSearchRootRescanCompleted(e.Sender, e.EventArgs)));
        watcherSubs.Add(Observable.FromEventPattern<FileSearchRootRescanFailed>(
                h => _fileSearchRootWatcher.RescanFailed += h,
                h => _fileSearchRootWatcher.RescanFailed -= h)
            .ObserveOn(_uiScheduler)
            .Subscribe(e => OnFileSearchRootRescanFailed(e.Sender, e.EventArgs)));
        watcherSubs.Add(Observable.FromEventPattern<SearchRootAvailabilityChanged>(
                h => _fileSearchRootWatcher.SearchRootAvailabilityChanged += h,
                h => _fileSearchRootWatcher.SearchRootAvailabilityChanged -= h)
            .ObserveOn(_uiScheduler)
            .Subscribe(e => OnSearchRootAvailabilityChanged(e.Sender, e.EventArgs)));
        _watcherSubscriptions.Disposable = watcherSubs;

        _importOrchestrator = new LibraryImportOrchestrator(
            services,
            new DialogImportConflictPrompt(this),
            new DialogBiblatexImportPrompt(this));

        _libraryRevisionMonitor = services.LibraryRevisionMonitor;
    }

    /// <summary>Tears down the host wrappers created by <see cref="AttachHostServices"/>.</summary>
    private void DetachHostServices()
    {
        _mcpSubscriptions.Disposable = null;
        if (_mcpHost is not null)
        {
            _mcpHost.DisposeAsync().AsTask().Observe("host-services", "dispose-mcp-host");
            _mcpHost = null;
        }

        _watcherSubscriptions.Disposable = null;
        if (_fileSearchRootWatcher is not null)
        {
            _fileSearchRootWatcher.DisposeAsync().AsTask().Observe("host-services", "dispose-root-watcher");
            _fileSearchRootWatcher = null;
        }

        if (_libraryRevisionMonitor is not null)
        {
            _libraryRevisionMonitor.Dispose();
            _libraryRevisionMonitor = null;
        }

        _importOrchestrator = null;
    }

    private void StopLibraryRevisionMonitor()
    {
        _libraryRevisionMonitor?.Stop();
        _libraryRevisionMonitor?.Dispose();
        _libraryRevisionMonitor = null;
    }

    internal async Task<LibraryImportOrchestrator> ImportOrchestratorAsync()
    {
        await ServicesAsync();
        return _importOrchestrator ??
               throw new InvalidOperationException("The import orchestrator is not available.");
    }

    private void EnsureLibraryChangeNotifications(HostServices services)
    {
        if (_observedLibraryRevisions is not null &&
            !ReferenceEquals(_observedLibraryRevisions, services.LibraryRevisions))
        {
            _revisionSubscription.Disposable = null;
        }

        _observedLibraryRevisions = services.LibraryRevisions;
        ILibraryRevisionService observedRevisions = _observedLibraryRevisions;
        IObservable<LibraryChangeSet> changes = Observable
            .FromEventPattern<LibraryRevisionCommittedEventArgs>(
                handler => observedRevisions.ChangeCommitted += handler,
                handler => observedRevisions.ChangeCommitted -= handler)
            .Select(eventPattern => eventPattern.EventArgs.ChangeSet);

        _revisionSubscription.Disposable = ReactiveUiFlow.SubscribeBufferedSequential(
            changes,
            RevisionBufferWindow,
            _timingScheduler,
            _uiScheduler,
            (batch, cancellationToken) =>
                ApplyBufferedChangeSetsAsync(observedRevisions, batch, cancellationToken),
            exception => UnexpectedExceptions.Sink.Report(exception, "main-window-revision"));

        Shell.ObserveLibraryRevisions(_observedLibraryRevisions);
    }

    private void DetachLibraryChangeNotifications()
    {
        Shell.ObserveLibraryRevisions(null);
        _revisionSubscription.Disposable = null;
        _observedLibraryRevisions = null;
    }

    private async Task ApplyBufferedChangeSetsAsync(
        ILibraryRevisionService revisions,
        IReadOnlyList<LibraryChangeSet> batch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(revisions, _observedLibraryRevisions))
        {
            return;
        }

        await RefreshAfterLibraryChangeAsync(revisions, LibraryShellViewModel.MergeChangeSets(batch),
            cancellationToken);
    }

    private async Task RefreshAfterLibraryChangeAsync(
        ILibraryRevisionService revisions,
        LibraryChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        int gen = LibraryGeneration;
        cancellationToken.ThrowIfCancellationRequested();
        if (_services is null || !ReferenceEquals(revisions, _observedLibraryRevisions) || gen != LibraryGeneration)
        {
            return;
        }

        if (changeSet.ItemIds.Count > 0)
        {
            await RefreshOpenItemEditorsAsync(changeSet.ItemIds, changeSet.NewRevision);
        }

        if (gen != LibraryGeneration)
        {
            return;
        }

        if (changeSet.StyleIds.Count > 0)
        {
            foreach (CslStyleManagerViewModel manager in OpenTabs.Select(tab => tab.Content)
                         .OfType<CslStyleManagerViewModel>())
            {
                await manager.RefreshInstalledStylesAsync();
            }

            foreach (ItemEditorViewModel editor in OpenTabs.Select(tab => tab.Content)
                         .OfType<ItemEditorViewModel>())
            {
                await editor.RefreshStylePreviewAsync();
            }
        }

        if (gen != LibraryGeneration)
        {
            return;
        }

        if (changeSet.CollectionIds.Count > 0)
        {
            // Collection catalog changes reach already-open advanced searches and editors through
            // the same committed change-set flow: options/catalogs reload in place while staged
            // editor selections are preserved.
            if (_searchEvidenceViewModel is { } search)
            {
                await search.ReloadFilterOptionsAsync();
            }

            foreach (ItemEditorViewModel editor in OpenTabs.Select(tab => tab.Content)
                         .OfType<ItemEditorViewModel>())
            {
                await editor.RefreshCollectionCatalogAsync();
            }
        }
    }

    public void StartMcpServerInBackground()
    {
        if (!_autoStartMcpServer)
        {
            return;
        }

        StartMcpServerInBackgroundAsync().Observe("application-initialization", "start-mcp-server");
    }

    private async Task StartMcpServerInBackgroundAsync()
    {
        HostServices services = await ServicesAsync(false);
        await StartMcpServerAsync(services);
    }

    public async Task RefreshSyncedMetadataLookupAsync()
    {
        await RefreshSyncedMetadataLookupAsync(await ServicesAsync());
    }

    private async Task RefreshSyncedMetadataLookupAsync(HostServices services)
    {
        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsFailure ||
            !_settings.Sync.IsSettingEnabled(LibrarySettingKeys.MetadataLookup, library.Value.LibraryId))
        {
            return;
        }

        Result<MetadataLookupAppSettings?> synced = await services.GetSyncedMetadataLookupAsync();
        if (synced.IsFailure || synced.Value is null)
        {
            return;
        }

        _settings = _settings with { MetadataLookup = synced.Value };
        services.UpdateMetadataLookupPreferences(synced.Value);
        _settingsViewModel?.MetadataLookupSettings.ReloadFromEffectiveSettingsIfClean(synced.Value);
    }

    /// <summary>Loads the per-library 搜索重写 switch without persisting it back to the service.</summary>
    public async Task RefreshQueryRewriteEnabledAsync()
    {
        if (_services is null)
        {
            return;
        }

        await RefreshQueryRewriteEnabledAsync(_services);
    }

    private async Task RefreshQueryRewriteEnabledAsync(HostServices services)
    {
        Result<SearchProfileSettings> settings = await services.SearchProfiles.GetSearchSettingsAsync();
        if (settings.IsFailure)
        {
            ApplyQueryRewriteEnabled(false);
            return;
        }

        _queryRewriteEnabledPersisted = settings.Value.RewriteEnabled;
        ApplyQueryRewriteEnabled(settings.Value.RewriteEnabled);
    }

    internal async Task SetQueryRewriteEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await PersistQueryRewriteEnabledAsync(enabled, cancellationToken);
    }

    private async Task PersistQueryRewriteEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        int gen = LibraryGeneration;
        cancellationToken.ThrowIfCancellationRequested();
        HostServices? services = _services;
        if (services is null)
        {
            ApplyQueryRewriteEnabled(enabled);
            return;
        }

        ApplyQueryRewriteEnabled(enabled);
        Result saved = await services.SearchProfiles.SetRewriteEnabledAsync(enabled);
        cancellationToken.ThrowIfCancellationRequested();
        if (gen != LibraryGeneration)
        {
            return;
        }

        if (saved.IsFailure)
        {
            ApplyQueryRewriteEnabled(_queryRewriteEnabledPersisted);
            ReportError($"查询重写设置保存失败：{saved.ErrorCode} {saved.ErrorMessage}");
            return;
        }

        _queryRewriteEnabledPersisted = enabled;
        Report(enabled ? "已启用查询重写。" : "已停用查询重写。");
    }

    private void ApplyQueryRewriteEnabled(bool enabled)
    {
        if (QueryRewriteEnabled == enabled)
        {
            return;
        }

        _suppressQueryRewritePersist = true;
        try
        {
            QueryRewriteEnabled = enabled;
        }
        finally
        {
            _suppressQueryRewritePersist = false;
        }
    }

    public async Task<Result<ConflictResolutionResult>> ResolveConflictAsync(
        ConflictDescriptor conflict,
        IConflictActionExecutor? executor = null,
        CancellationToken cancellationToken = default)
    {
        HostServices services = await ServicesAsync();
        ConflictResolutionCoordinator coordinator = new(Dialogs, services.ConflictActions);
        return await coordinator.ResolveAsync(conflict, executor, cancellationToken);
    }

    public async Task RefreshSidebarPathsAsync()
    {
        FileSearchRoots.Clear();

        try
        {
            HostServices services = await ServicesAsync();
            Result<IReadOnlyList<FileSearchRoot>> roots = await services.FileResolution.ListSearchRootsAsync();
            if (roots.IsSuccess)
            {
                Result<IReadOnlyList<string>> paths = await services.Files.ListOriginalPathsAsync();
                if (paths.IsFailure)
                {
                    throw new InvalidOperationException(paths.ErrorMessage);
                }

                string[] filePaths = paths.Value.ToArray();

                foreach (FileSearchRoot root in roots.Value)
                {
                    int fileCount = filePaths.Count(path => IsPathUnderRoot(path, root.RootPath));

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

        // FileSearchRoots mutations are notified via ObservableCollection.CollectionChanged (wired to HasFileSearchRoots/NoFileSearchRoots via Rx).
        // DefaultSyncRootPath is not changed here; Shell page state is refreshed below.
        Shell.RaisePageStateChanged();
    }

    public async Task<Result<FileSearchRootRescanSummary>> RescanFileSearchRootsAsync(
        string completionMessage = "文件重新扫描完成。",
        bool showBlockingDialog = false,
        CancellationToken cancellationToken = default,
        Action<int?, int?, string, string?>? progress = null,
        string trigger = FileSearchRootWatcherService.ManualTrigger)
    {
        HostServices services = await ServicesAsync();
        int libraryGeneration = LibraryGeneration;
        FileSearchRootWatcherService watcher = _fileSearchRootWatcher ??
                                               throw new InvalidOperationException("文件搜索根监视服务不可用。");
        // The service raises its completion events synchronously on the rescan thread before
        // returning. Capture them for this call so the UI follow-up is awaited by the caller
        // (manual rescans); watcher-triggered rescans with no direct caller are marshaled to the
        // UI thread by the event handlers instead.
        RescanCompletionCapture capture = new();
        _activeRescanCapture = capture;
        try
        {
            Result<FileSearchRootRescanSummary> result;
            if (showBlockingDialog)
            {
                result = await ModalOperations.RunAsync(
                    new ModalOperationOptions(
                        "文件重新扫描",
                        "正在扫描文件搜索根并导入新发现的 PDF。",
                        true),
                    context => watcher.RescanFileSearchRootsAsync(
                        completionMessage,
                        context.CancellationToken,
                        context.Report,
                        trigger),
                    cancellationToken);
            }
            else
            {
                result = await watcher.RescanFileSearchRootsAsync(completionMessage, cancellationToken, progress,
                    trigger);
            }

            if (!IsCurrentLibraryContext(services, libraryGeneration) ||
                !ReferenceEquals(watcher, _fileSearchRootWatcher))
            {
                return result;
            }

            if (capture.Failed is { } failed)
            {
                ReportError(failed.Message);
            }
            else if (capture.Completed is { } completed)
            {
                await ApplyFileSearchRootRescanResultAsync(completed);
            }

            return result;
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested || exception.CancellationToken.IsCancellationRequested)
        {
            Report("文件重新扫描已取消。");
            throw;
        }
        finally
        {
            if (ReferenceEquals(_activeRescanCapture, capture))
            {
                _activeRescanCapture = null;
            }
        }
    }

    private sealed class RescanCompletionCapture
    {
        public FileSearchRootRescanCompleted? Completed { get; set; }
        public FileSearchRootRescanFailed? Failed { get; set; }
    }

    private void OnFileSearchRootRescanCompleted(object? sender, FileSearchRootRescanCompleted completed)
    {
        if (!ReferenceEquals(sender, _fileSearchRootWatcher))
        {
            return;
        }

        if (_activeRescanCapture is { } capture)
        {
            capture.Completed = completed;
            return;
        }

        ApplyFileSearchRootRescanResultAsync(completed)
            .Observe("file-search-root-rescan", "completed");
    }

    private async Task ApplyFileSearchRootRescanResultAsync(FileSearchRootRescanCompleted completed)
    {
        int gen = LibraryGeneration;
        await RefreshSidebarPathsAsync();
        if (LibraryGeneration != gen)
        {
            return;
        }

        await Shell.RefreshItemsAsync();
        if (LibraryGeneration != gen)
        {
            return;
        }

        if (completed.Summary.HasWarnings)
        {
            ReportError(completed.Message);
        }
        else
        {
            Report(completed.Message);
        }
    }

    private void OnFileSearchRootRescanFailed(object? sender, FileSearchRootRescanFailed failed)
    {
        if (!ReferenceEquals(sender, _fileSearchRootWatcher))
        {
            return;
        }

        if (_activeRescanCapture is { } capture)
        {
            capture.Failed = failed;
            return;
        }

        ReportError(failed.Message);
    }

    private void OnSearchRootAvailabilityChanged(object? sender, SearchRootAvailabilityChanged change)
    {
        if (!ReferenceEquals(sender, _fileSearchRootWatcher))
        {
            return;
        }

        for (int index = 0; index < FileSearchRoots.Count; index++)
        {
            if (!string.Equals(FileSearchRoots[index].RootPath, change.RootPath,
                    StringComparison.OrdinalIgnoreCase) ||
                FileSearchRoots[index].IsAvailable == change.IsAvailable)
            {
                continue;
            }

            FileSearchRoots[index] = FileSearchRoots[index] with { IsAvailable = change.IsAvailable };
        }
    }

    private async Task RebuildSearchIndexAsync()
    {
        HostServices services = await ServicesAsync();
        int libraryGeneration = LibraryGeneration;
        try
        {
            await ModalOperations.RunAsync(
                new ModalOperationOptions(
                    "重建搜索索引",
                    "正在重建本地 FTS 搜索索引。",
                    true),
                async context =>
                {
                    Result<BlockingOperation> started = await services.BlockingOperations.StartAsync(
                        BlockingOperationTypes.SearchIndexRebuild,
                        BlockingOperationScopeTypes.SearchIndex,
                        "library",
                        true,
                        "正在重建本地 FTS 搜索索引。",
                        cancellationToken: context.CancellationToken);
                    BlockingOperationId? operationId =
                        started.IsSuccess ? started.Value.OperationId : (BlockingOperationId?)null;
                    try
                    {
                        Result result =
                            await services.SearchIndex.RebuildFtsForLibraryAsync(context.CancellationToken);
                        if (result.IsFailure)
                        {
                            if (operationId is not null)
                            {
                                await services.BlockingOperations.FailAsync(operationId.Value, result.ErrorCode!,
                                    result.ErrorMessage!, cancellationToken: CancellationToken.None);
                            }

                            throw new InvalidOperationException(result.ErrorMessage);
                        }

                        if (operationId is not null)
                        {
                            await services.BlockingOperations.CompleteAsync(operationId.Value, "本地 FTS 搜索索引已重建。",
                                cancellationToken: CancellationToken.None);
                        }

                        return true;
                    }
                    catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                    {
                        if (operationId is not null)
                        {
                            await services.BlockingOperations.CancelAsync(operationId.Value, "搜索索引重建已取消。",
                                cancellationToken: CancellationToken.None);
                        }

                        throw;
                    }
                });
            if (IsCurrentLibraryContext(services, libraryGeneration))
            {
                Report("本地 FTS 搜索索引已重建。");
            }
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken.IsCancellationRequested)
        {
            Report("搜索索引重建已取消。");
        }
    }

    private void RefreshFileSearchRootWatchers(IReadOnlyList<FileSearchRoot> roots)
    {
        _fileSearchRootWatcher?.RefreshWatchers(roots);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
    }

    public async Task ShutdownAsync()
    {
        await StopFileSearchRootWatchersAsync();
        DetachLibraryChangeNotifications();
        Shell.Dispose();
        await StopMcpServerAsync(allowRuntimeHostShutdown: true);
        StopLibraryRevisionMonitor();
        await StopHostServicesAsync();
        await ReleaseRuntimeHostAsync();
        await DisposeSettingsStoreAsync();
        DisposeActivityTracker();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeActivityTracker();
        }

        base.Dispose(disposing);
    }

    private async Task DisposeSettingsStoreAsync()
    {
        if (_settingsStore is not null)
        {
            try
            {
                await _settingsStore.DisposeAsync();
            }
            catch (Exception ex)
            {
                UnexpectedExceptions.Sink.Report(ex, "app-settings-store", "dispose");
            }
        }
    }

    private void DisposeActivityTracker()
    {
        _activitySubscription?.Dispose();
        _activitySubscription = null;
        if (_ownsActivityTracker && _activityTracker is IDisposable trackerDisposable)
        {
            try
            {
                trackerDisposable.Dispose();
            }
            catch (Exception ex)
            {
                UnexpectedExceptions.Sink.Report(ex, "activity-tracker", "dispose");
            }
        }
    }

    private async Task StopFileSearchRootWatchersAsync()
    {
        if (_fileSearchRootWatcher is not null)
        {
            await _fileSearchRootWatcher.StopAsync();
        }
    }

    public async Task StartMcpServerAsync()
    {
        await ServicesAsync();
        await ModalOperations.RunAsync(
            new ModalOperationOptions(
                "启动 MCP Server",
                "正在验证设置并启动 HTTP listener。",
                false),
            async context =>
            {
                await StartMcpServerAsync(_services!);
                if (_mcpHost?.IsRunning != true)
                {
                    throw new InvalidOperationException("MCP Server 未能启动。请检查状态栏中的错误详情。");
                }

                return true;
            });
    }

    public async Task StopMcpServerAsync(
        string detail = "MCP HTTP 服务已停止。",
        bool allowRuntimeHostShutdown = false)
    {
        if (!allowRuntimeHostShutdown && _enforceRuntimeHostOwnership && _runtimeHostLease is not null)
        {
            SetMcpStatus(
                McpServerRunning ? "MCP: 运行中" : "MCP: 错误",
                "桌面宿主持有资料库期间必须保持 MCP endpoint 可用，以便 patchouli-cli 连接。",
                McpServerRunning ? Brushes.LimeGreen : Brushes.OrangeRed);
            return;
        }

        if (_mcpHost is not null)
        {
            await _mcpHost.StopAsync(detail);
            return;
        }

        SetMcpStatus("MCP: 未启动", detail, Brushes.Gray);
    }

    /// <summary>
    /// Restarts the MCP server from the currently persisted settings (settings-apply flow).
    /// </summary>
    public async Task RestartMcpServerAsync(string detail = "应用新设置")
    {
        await ServicesAsync();
        if (_mcpHost is null)
        {
            return;
        }

        await _mcpHost.StopAsync(detail);
        await StartMcpServerAsync(_services!);
        if (!_mcpHost.IsRunning)
        {
            throw new InvalidOperationException("MCP Server 未能重启。请检查状态栏中的错误详情。");
        }
    }

    /// <summary>
    /// Stops MCP before a library/DB switch.
    /// Caller starts MCP again after the new library is ready when appropriate.
    /// </summary>
    public async Task BeginLibrarySwitchAsync(string detail = "正在切换资料库。")
    {
        Interlocked.Increment(ref _libraryGeneration);
        Raise(nameof(LibraryGeneration));
        DetachLibraryChangeNotifications();
        _searchEvidenceViewModel?.DetachLibraryContext();
        _ocrQueueViewModel?.DetachLibraryContext();
        _snapshot?.DetachLibraryContext();
        await StopMcpServerAsync(detail, true);
        await StopFileSearchRootWatchersAsync();
        StopLibraryRevisionMonitor();
        await StopHostServicesAsync();
        DetachHostServices();
        _services = null;
        await ReleaseRuntimeHostAsync();
        _queryRewriteEnabledPersisted = false;
        ApplyQueryRewriteEnabled(false);
        _settingsViewModel?.NotifyLibraryContextChanged();
        // Manual Raise: HasOpenRuntimeDatabase derives from lifecycle field _services being cleared.
        Raise(nameof(HasOpenRuntimeDatabase));
        DetectDuplicateItemsCommand?.NotifyCanExecuteChanged();
        RebuildSearchIndexCommand?.NotifyCanExecuteChanged();
        RescanFileSearchRootsCommand?.NotifyCanExecuteChanged();
    }

    private async Task StartMcpServerAsync(HostServices services)
    {
        if (_mcpHost is null)
        {
            throw new InvalidOperationException("MCP Server 主机不可用。");
        }

        await _mcpHost.StartAsync();
        if (!_mcpHost.IsRunning)
        {
            Result<McpServerSettings> configured = await services.McpSettings.GetSettingsAsync();
            if (configured.IsSuccess)
            {
                await _mcpHost.StartAsync(configured.Value with
                {
                    Port = RuntimeHostCoordinator.ReserveEphemeralLoopbackPort()
                });
            }
        }

        if (_mcpHost.IsRunning)
        {
            await PublishRuntimeHostAsync(services);
        }
    }

    private async Task PublishRuntimeHostAsync(HostServices services)
    {
        if (_runtimeHostLease is null || _mcpHost?.IsRunning != true)
        {
            return;
        }

        Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
        if (library.IsSuccess)
        {
            await _runtimeHostLease.PublishAsync(library.Value.LibraryId.ToString(), _mcpHost.Endpoint);
        }
    }

    private async Task<HostServices> CreateOwnedHostServicesAsync()
    {
        if (!_enforceRuntimeHostOwnership)
        {
            return await CreateHostServicesAsync(RuntimeDatabasePath);
        }

        if (_runtimeHostLease is not null)
        {
            throw new InvalidOperationException("A runtime-host lease is already active.");
        }

        _runtimeHostLease = await RuntimeHostCoordinator.AcquireDesktopAsync(
            RuntimeDatabasePath, TimeSpan.FromSeconds(10));
        try
        {
            return await CreateHostServicesAsync(_runtimeHostLease.DatabasePath);
        }
        catch
        {
            await ReleaseRuntimeHostAsync();
            throw;
        }
    }

    private Task<HostServices> CreateHostServicesAsync(string databasePath)
    {
        return HostServices.CreateAsync(
            databasePath,
            _settings,
            SettingsFilePath,
            CreateMigrationProgress(),
            reportUnexpectedException: ReportUnexpectedException,
            startupProgress: CreateStartupStageProgress(),
            activityTracker: _activityTracker);
    }

    private async Task StopHostServicesAsync()
    {
        if (_services is not null)
        {
            await _services.ShutdownAsync();
        }
    }

    private async Task ReleaseRuntimeHostAsync()
    {
        RuntimeHostLease? lease = _runtimeHostLease;
        _runtimeHostLease = null;
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }

    private void OnMcpHostStatusChanged(object? sender, McpServerHostStatusChangedEventArgs change)
    {
        if (!ReferenceEquals(sender, _mcpHost))
        {
            return;
        }

        (string text, IBrush brush) = change.Status switch
        {
            McpServerHostStatus.Running => ("MCP: 运行中", Brushes.LimeGreen),
            McpServerHostStatus.Starting => ("MCP: 启动中", Brushes.Goldenrod),
            McpServerHostStatus.Error => ("MCP: 错误", Brushes.IndianRed),
            _ => ("MCP: 未启动", Brushes.Gray)
        };

        SetMcpStatus(text, change.Detail, brush);

        if (change.Status == McpServerHostStatus.Running && _mcpHost is { } host)
        {
            SetMcpEndpoint(host.Endpoint);
            // Manual Raise: McpRunningSettingsRevision bridges _mcpHost external status change into ViewModel PropertyChanged.
            Raise(nameof(McpRunningSettingsRevision));
            LogOperationAsync("mcp_http_start", $"MCP HTTP server listening on {host.Endpoint}")
                .Observe("mcp-server", "log-start");
        }
        else if (change.Status == McpServerHostStatus.Error)
        {
            LogOperationAsync("mcp_http_start_failed", change.Detail)
                .Observe("mcp-server", "log-start-failed");
        }
        else if (change.Status == McpServerHostStatus.Stopped)
        {
            // Manual Raise: McpRunningSettingsRevision bridges _mcpHost external status change into ViewModel PropertyChanged.
            Raise(nameof(McpRunningSettingsRevision));
        }
    }

    private void SetMcpStatus(string text, string detail, IBrush brush)
    {
        McpStatusText = text;
        McpStatusDetail = detail;
        McpStatusBrush = brush;
        // Manual Raise: McpServerRunning bridges _mcpHost external status change into ViewModel PropertyChanged.
        Raise(nameof(McpServerRunning));
    }

    private void SetMcpEndpoint(string endpoint)
    {
        McpEndpoint = endpoint;
    }

    private string BuildMcpConnectionDetail()
    {
        long active = _mcpHost?.Server?.ActiveConnectionCount ?? 0;
        long total = _mcpHost?.Server?.TotalConnectionCount ?? 0;
        return $"连接数: {active} / {total}";
    }

    private void OnMcpHostConnectionCountsChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _mcpHost) || _mcpHost?.IsRunning != true)
        {
            return;
        }

        McpStatusDetail = BuildMcpConnectionDetail();
    }

    public void Report(string message)
    {
        Status = message;
        StatusIsError = false;
    }

    public void ReportError(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    public Task ShowInlineFirstRunAsync()
    {
        FirstRun = CreateFirstRunViewModel();
        FirstRun.DatabasePath = RuntimeDatabasePath;
        IsFirstRunVisible = true;
        return Task.CompletedTask;
    }

    public async Task HideInlineFirstRunAsync()
    {
        IsFirstRunVisible = false;
        await Shell.RefreshItemsAsync();
        await RefreshQueryRewriteEnabledAsync();
    }

    private FirstRunViewModel CreateFirstRunViewModel()
    {
        return new FirstRunViewModel(OpenFirstRunDatabaseAsync, ModalOperations, CompleteFirstRunAsync)
        {
            DatabasePath = RuntimeDatabasePath, MinerUToken = Shell.MinerUToken, OnError = ReportError,
            OnProgress = Report
        };
    }

    private async Task<LibraryImportOrchestrator?> OpenFirstRunDatabaseAsync(string path)
    {
        RuntimeDatabasePath = path;
        await OpenDatabaseCommand.ExecuteAsync();
        return _importOrchestrator;
    }

    private async Task CompleteFirstRunAsync()
    {
        await FirstRun.FinishSetupCommand.ExecuteAsync();
        if (!FirstRun.IsComplete)
        {
            return;
        }

        if (_services is not null)
        {
            await PublishRuntimeHostAsync(_services);
        }

        bool persisted = await SaveMinerUTokenSettingsAsync(FirstRun.MinerUToken);
        if (!persisted)
        {
            return;
        }

        Report("初始化完成。请选择题录，并通过右键菜单运行 MinerU OCR。");
        if (!string.IsNullOrWhiteSpace(FirstRun.ScanRoot))
        {
            HostServices services = await ServicesAsync();
            if (FirstRun.SelectedScanRoot is null)
            {
                ReportError("文件搜索根必须通过系统文件夹选择器选择。");
                return;
            }

            Result<FileSearchRoot> addedRoot =
                await services.FileResolution.AddSearchRootAsync(FirstRun.SelectedScanRoot);
            if (addedRoot.IsFailure && addedRoot.ErrorCode != AppErrorCodes.InvalidState)
            {
                Report(addedRoot.ErrorMessage ?? "无法登记 FileSearchRoot。");
            }
        }

        await RefreshSidebarPathsAsync();
        await HideInlineFirstRunAsync();
    }

    public MinerUConfiguration CreateMinerUConfiguration(string token)
    {
        return _settings.MinerU.ToConfiguration(token);
    }

    public async Task<string> GetPersistedMinerUTokenAsync()
    {
        HostServices services = await ServicesAsync();
        Result<string> secret = await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU);
        return secret.IsSuccess ? secret.Value : "";
    }

    public async Task<bool> SaveMinerUTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        HostServices services = await ServicesAsync();
        Result<ProviderCredentialMetadata> saved =
            await services.Credentials.SaveAsync(ProviderIds.MinerU, "MinerU API token",
                token.Trim());
        if (saved.IsFailure)
        {
            Report(saved.ErrorMessage ?? "无法保存 MinerU API token。");
            return false;
        }

        _settings = PatchouliAppSettings.Load(SettingsFilePath);
        return true;
    }

    public async Task<bool> SaveMinerUTokenSettingsAsync(string token)
    {
        return await SaveMinerUSettingsAsync(token, _settings.MinerU.ModelVersion,
            _settings.MinerU.PollingTimeoutSeconds);
    }

    public async Task<bool> SaveMinerUSettingsAsync(string token, string modelVersion, int pollingTimeoutSeconds)
    {
        if (modelVersion is not ("vlm" or "pipeline"))
        {
            ReportError("MinerU 模型必须选择 vlm 或 pipeline。");
            return false;
        }

        string trimmed = token.Trim();
        bool persisted = await SaveMinerUTokenAsync(trimmed);
        if (!persisted)
        {
            return false;
        }

        SettingsSaveResult settingsSaved = await UpdateAppOptionsAsync(_settings with
        {
            MinerU = _settings.MinerU with
            {
                ModelVersion = modelVersion,
                PollingTimeoutSeconds = pollingTimeoutSeconds
            }
        }, "MinerU");
        if (!settingsSaved.IsSuccess)
        {
            ReportError(settingsSaved.ErrorMessage ?? "无法保存 MinerU 模型设置。");
            return false;
        }

        Shell.MinerUToken = trimmed;
        FirstRun.MinerUToken = trimmed;
        _settingsViewModel?.OcrProviderSettings.LoadPersistedCredential(trimmed);
        Report("MinerU 凭据已保存。");
        return true;
    }

    public async Task<bool> SaveMinerUModelSettingsAsync(string modelVersion, int pollingTimeoutSeconds)
    {
        if (modelVersion is not ("vlm" or "pipeline"))
        {
            ReportError("MinerU 模型必须选择 vlm 或 pipeline。");
            return false;
        }

        SettingsSaveResult settingsSaved = await UpdateAppOptionsAsync(_settings with
        {
            MinerU = _settings.MinerU with
            {
                ModelVersion = modelVersion,
                PollingTimeoutSeconds = pollingTimeoutSeconds
            }
        }, "MinerU");
        if (!settingsSaved.IsSuccess)
        {
            ReportError(settingsSaved.ErrorMessage ?? "无法保存 MinerU 模型设置。");
            return false;
        }

        Report("MinerU 模型设置已保存。");
        return true;
    }

    public async Task<bool> SaveOcrEngineSettingsAsync(OcrEnginesAppSettings engines)
    {
        HostServices services = await ServicesAsync();
        IReadOnlyList<string> availableEngineIds = services.OcrAdapters.ListCapabilities()
            .Select(capability => capability.EngineId)
            .ToList();
        string[] requested = [engines.DocumentOcrEngine, engines.PageOcrEngine, engines.RegionOcrEngine];
        foreach (string engineId in requested)
        {
            if (string.IsNullOrWhiteSpace(engineId))
            {
                ReportError("OCR 引擎选择不能为空。");
                return false;
            }

            if (!availableEngineIds.Contains(engineId, StringComparer.Ordinal))
            {
                ReportError($"OCR 引擎 '{engineId}' 当前不可用。");
                return false;
            }
        }

        SettingsSaveResult saved = await UpdateAppOptionsAsync(
            _settings with { OcrEngines = engines }, "OcrEngines");
        if (!saved.IsSuccess)
        {
            ReportError(saved.ErrorMessage ?? "无法保存 OCR 引擎设置。");
            return false;
        }

        Report("OCR 引擎选择已保存。");
        return true;
    }

    /// <summary>Persists the selected UI palette and applies it to the live theme brushes.</summary>
    public async Task<bool> SaveAppearancePaletteAsync(string paletteId)
    {
        string resolved = UiColorPalettes.ResolveId(paletteId);
        SettingsSaveResult saved = await UpdateAppOptionsAsync(
            _settings with { Ui = _settings.Ui with { PaletteId = resolved } }, "Ui");
        if (!saved.IsSuccess)
        {
            ReportError(saved.ErrorMessage ?? "无法保存外观配色设置。");
            return false;
        }

        ThemePaletteApplier.Apply(resolved);
        Report("外观配色已保存。");
        return true;
    }

    /// <summary>Persists the reading-mode font family and size. An empty family selects the
    /// system default font; the size is clamped to the supported [10, 28] range. Like the
    /// palette, the choice only takes effect through the header 保存设置 action.</summary>
    public bool SaveReadingFont(string fontFamily, double fontSize)
    {
        string family = (fontFamily ?? string.Empty).Trim();
        double size = Math.Clamp(fontSize, 10, 28);
        UpdateAppOptionsDeferred(
            _settings with { Ui = _settings.Ui with { ReadingFontFamily = family, ReadingFontSize = size } }, "Ui");
        return true;
    }

    public async Task<bool> SaveReadingFontImmediatelyAsync(string fontFamily, double fontSize)
    {
        string family = (fontFamily ?? string.Empty).Trim();
        double size = Math.Clamp(fontSize, 10, 28);
        SettingsSaveResult saved = await UpdateAppOptionsAsync(
            _settings with { Ui = _settings.Ui with { ReadingFontFamily = family, ReadingFontSize = size } }, "Ui");
        if (!saved.IsSuccess)
        {
            ReportError(saved.ErrorMessage ?? "无法保存阅读字体设置。");
            return false;
        }

        Report("阅读字体设置已保存。");
        return true;
    }

    /// <summary>Persists the whole-book import failure threshold. The failed-page ratio is clamped
    /// to [0, 0.9]; strictly above the threshold a book import fails and rolls back.</summary>
    public async Task<bool> SaveImportSettingsAsync(double maxFailedPageRatio)
    {
        double ratio = ImportAppSettings.ClampRatio(maxFailedPageRatio);
        SettingsSaveResult saved = await UpdateAppOptionsAsync(
            _settings with { Import = _settings.Import with { MaxFailedPageRatio = ratio } }, "Import");
        if (!saved.IsSuccess)
        {
            ReportError(saved.ErrorMessage ?? "无法保存导入设置。");
            return false;
        }

        Report("导入设置已保存。");
        return true;
    }

    public async Task<bool> RemoveMinerUCredentialAsync()
    {
        ConfirmDialogResult? choice = await Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "移除 MinerU 凭据",
                "将从本机凭据存储中删除 MinerU API token。移除后本机无法执行 OCR，直到重新配置。",
                "移除",
                confirmDanger: true));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return true;
        }

        HostServices services = await ServicesAsync();
        Result removed = await services.Credentials.RemoveAsync(ProviderIds.MinerU);
        if (removed.IsFailure)
        {
            ReportError(removed.ErrorMessage ?? "无法移除 MinerU API token。");
            return false;
        }

        _settings = PatchouliAppSettings.Load(SettingsFilePath);
        Shell.MinerUToken = "";
        FirstRun.MinerUToken = "";
        _settingsViewModel?.OcrProviderSettings.LoadPersistedCredential("");
        Report("MinerU 凭据已移除。");
        return true;
    }

    private async Task LoadPersistedMinerUTokenAsync()
    {
        string token = await GetPersistedMinerUTokenAsync();
        Shell.MinerUToken = token;
        FirstRun.MinerUToken = token;
        _settingsViewModel?.OcrProviderSettings.LoadPersistedToken(token);
    }

    private static IDialogService CreateDialogService()
    {
        DialogService service = new();
        service.Register<BlockingOperationDialogViewModel, BlockingOperationDialog>();
        service.Register<ConflictResolutionDialogViewModel, ConflictResolutionDialog>();
        service.Register<BiblatexImportPreviewDialogViewModel, BiblatexImportPreviewDialog>();
        service.Register<ConfirmDialogViewModel, ConfirmDialog>();
        service.Register<PurgeConfirmDialogViewModel, PurgeConfirmDialog>();
        service.Register<TagNamePromptDialogViewModel, TagNamePromptDialog>();
        service.Register<PdfBBoxViewModel, BoxEditorDialog>();
        service.Register<PdfWorkspaceViewModel, NewBoxDialog>();
        service.Register<ItemMergePreviewDialogViewModel, ItemMergePreviewDialog>();
        service.Register<DuplicateItemsDialogViewModel, DuplicateItemsDialog>();
        return service;
    }

    public void RaiseLibraryTitleChanged()
    {
        WorkspaceTabViewModel? libTab = OpenTabs.FirstOrDefault(t => t.Kind == WorkspaceTabKind.Library);
        if (libTab != null)
        {
            libTab.Title = LibraryTabTitle;
        }

        // Manual Raise retained for explicit cross-component synchronization contract.
        Raise(nameof(LibraryTabTitle));
    }

    public async Task LogOperationAsync(string operation, string message)
    {
        try
        {
            await Logger.LogAsync(operation, message);
        }
        catch (Exception exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "operation-log", operation);
        }
    }

    private T? GetWorkspaceContent<T>(WorkspaceTabKind kind) where T : ViewModelBase
    {
        return Workspace.FindKind(kind)?.Content as T;
    }

    public void RefreshItemWorkspaceTabTitles(string itemId, string itemTitle, ViewModelBase? editorContent = null)
    {
        WorkspaceTabViewModel? pdfTab = Workspace.Find($"PdfWorkspace_{itemId}");
        if (pdfTab is not null)
        {
            pdfTab.Title = BuildItemWorkspaceTabTitle("PDF 工作台", itemTitle);
        }

        WorkspaceTabViewModel? editorTab = Workspace.Find($"ItemEditor_{itemId}") ??
                                           OpenTabs.FirstOrDefault(tab =>
                                               editorContent is not null &&
                                               ReferenceEquals(tab.Content, editorContent));
        if (editorTab is not null)
        {
            editorTab.Title = BuildItemWorkspaceTabTitle("编辑题录", itemTitle);
        }
    }

    public async Task RefreshOpenItemEditorsAsync(IReadOnlyCollection<ItemId> itemIds)
    {
        HashSet<string> wanted = itemIds.Select(itemId => itemId.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (ItemEditorViewModel editor in OpenTabs.Select(tab => tab.Content).OfType<ItemEditorViewModel>())
        {
            if (wanted.Contains(editor.ItemIdText) && !editor.IsSaving)
            {
                await editor.LoadAsync(editor.ItemIdText);
            }
        }
    }

    internal async Task RefreshOpenItemEditorsAsync(IReadOnlyCollection<ItemId> itemIds, long newRevision)
    {
        HashSet<string> wanted = itemIds.Select(itemId => itemId.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (ItemEditorViewModel editor in OpenTabs.Select(tab => tab.Content).OfType<ItemEditorViewModel>())
        {
            if (wanted.Contains(editor.ItemIdText)
                && !editor.IsSaving
                && !editor.HasUnsavedChanges
                && editor.LoadedRevision < newRevision)
            {
                await editor.LoadAsync(editor.ItemIdText);
            }
        }
    }

    /// <summary>
    /// Returns whether the given item is open in an editor with unsaved changes. Used by the merge
    /// service to refuse merging items that are currently being edited.
    /// </summary>
    public bool ItemHasUnsavedEdits(ItemId itemId)
    {
        string id = itemId.ToString();
        return OpenTabs.Select(tab => tab.Content).OfType<ItemEditorViewModel>()
            .Any(editor => string.Equals(editor.ItemIdText, id, StringComparison.Ordinal) && editor.HasUnsavedChanges);
    }

    private static string BuildItemWorkspaceTabTitle(string pageName, string itemTitle)
    {
        string title = string.IsNullOrWhiteSpace(itemTitle) ? "未命名题录" : itemTitle.Trim();
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
        await ActivateTabAsync(WorkspaceTabKind.CslStyleManager, "CslStyleManager", "CSL 样式", "Quote", true,
            () => new CslStyleManagerViewModel(this));
        if (ActiveTab?.Content is CslStyleManagerViewModel csl)
        {
            await csl.InitializeAsync();
        }
    }

    public async Task OpenSettingsAsync(string section, string? statusMessage = null)
    {
        await ActivateTabAsync(WorkspaceTabKind.Settings, "Settings", "设置", "Menu", true, () => Settings);
        string icon = section.ToLowerInvariant() switch
        {
            "mcp" => "Server",
            "csl" => "Quote",
            "library" => "Database",
            "search_rewrite" => "Filter",
            _ => "ScanText"
        };
        Settings.ActiveCategory = Settings.Categories.Single(c => c.IconName == icon);
        await Settings.WaitForActiveSectionLoadAsync();
    }

    private async Task OpenSearchRewriteSettingsAsync()
    {
        await OpenSettingsAsync("search_rewrite");
        await Settings.SearchRewriteSettings.LoadAsync();
    }

    public async Task OpenAboutAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.About, "About", "关于", "Info", true, () => About);
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
        LibraryItemViewModel? item = Shell.SelectedItem;
        if (item is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        await ShowReadingAsync(item);
    }

    public async Task ShowReadingAsync(LibraryItemViewModel item)
    {
        string tabId = $"PdfWorkspace_{item.ItemId}";
        string title = BuildItemWorkspaceTabTitle("PDF 工作台", item.Title ?? item.FileName);
        await ActivateTabAsync(WorkspaceTabKind.PdfWorkspace, tabId, title, "FolderOpen", true,
            () => new PdfWorkspaceViewModel(this, item));

        if (ActiveTab?.Content is PdfWorkspaceViewModel pdf && !pdf.HasImage)
        {
            await pdf.LoadAsync();
        }
    }

    private async Task RunToolbarSearchAsync()
    {
        PatchouliNavigationParseResult navigation = PatchouliUriNavigationParser.ParseInput(ToolbarSearchQuery);
        if (navigation.HasProtocolPrefix)
        {
            if (navigation.Target is null)
            {
                ReportError(navigation.ErrorMessage ?? "无法解析 Patchouli URI。");
                return;
            }

            await NavigateToPatchouliUriAsync(navigation.Target);
            return;
        }

        SearchEvidenceViewModel search = SearchEvidence;
        await ActivateTabAsync(WorkspaceTabKind.SearchResults, "SearchResults", "搜索结果", "Search", true,
            () => search);
        await search.SearchCommand.ExecuteAsync();
    }

    private async Task OpenAdvancedSearchAsync()
    {
        SearchEvidenceViewModel search = SearchEvidence;
        await ActivateTabAsync(WorkspaceTabKind.SearchResults, "SearchResults", "搜索结果", "Search", true,
            () => search);
        await search.OpenAdvancedSearchCommand.ExecuteAsync();
    }

    public async Task NavigateToSearchHitAsync(string versionedUri)
    {
        PatchouliNavigationParseResult parsed = PatchouliUriNavigationParser.ParseInput(versionedUri);
        if (!parsed.IsSuccess || parsed.Target is null)
        {
            ReportError(parsed.ErrorMessage ?? "无法解析证据 URI。");
            return;
        }

        await NavigateToPatchouliUriAsync(parsed.Target);
    }

    private async Task NavigateToPatchouliUriAsync(PatchouliNavigationTarget target)
    {
        switch (target.Kind)
        {
            case PatchouliNavigationKind.Item:
                await NavigateToItemUriAsync(target);
                return;
            case PatchouliNavigationKind.TextDocument:
            case PatchouliNavigationKind.TextPage:
                await NavigateToTextUriAsync(target);
                return;
            case PatchouliNavigationKind.CslStyle:
                await OpenCslStyleManagerAsync();
                Report($"已打开 CSL 样式管理：{target.ResourceId}");
                return;
            default:
                ReportError("不支持的 Patchouli URI。");
                return;
        }
    }

    private async Task NavigateToItemUriAsync(PatchouliNavigationTarget target)
    {
        await Shell.RefreshItemsAsync();
        LibraryItemViewModel? item = Shell.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.ItemId, target.ResourceId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            ReportError($"题录不存在：{target.CanonicalUri}");
            return;
        }

        Shell.SelectedItem = item;
        await EditItemByIdAsync(item.ItemId);
        Report($"已打开题录：{item.Title}");
    }

    private async Task NavigateToTextUriAsync(PatchouliNavigationTarget target)
    {
        HostServices services = await ServicesAsync();
        await Shell.RefreshItemsAsync();
        LibraryItemViewModel? item = await Shell.ResolveDocumentItemAsync(target.ResourceId);
        if (item is null)
        {
            ReportError($"文档或其关联题录不可用：{target.CanonicalUri}");
            return;
        }

        Shell.SelectedItem = item;
        await ShowReadingAsync(item);
        if (ActiveTab?.Content is not PdfWorkspaceViewModel pdf)
        {
            return;
        }

        int pageIndex = target.PageIndex ?? 0;
        await pdf.GoToPageAsync(pageIndex + 1);
        if (target.RevisionId is null && target.BoxId is null)
        {
            Report($"已打开文档第 {pageIndex + 1} 页。");
            return;
        }

        Result<EvidencePageText> resolved = await services.VersionedEvidenceReader.GetBoxTextAsync(
            DocumentInstanceId.Parse(target.ResourceId),
            pageIndex + 1,
            target.RevisionId,
            target.BoxId);
        if (resolved.IsFailure)
        {
            ReportError($"证据解析失败：{resolved.ErrorMessage}");
            return;
        }

        bool highlighted = target.BoxId is not null && pdf.TryHighlightBox(target.BoxId.Value);
        if (!highlighted)
        {
            Report($"已打开证据页面；当前布局中没有可高亮的原 Box（{resolved.Value.TreeRevisionId}）。");
        }
        else
        {
            Report($"已打开并高亮证据（版本 {resolved.Value.TreeRevisionId}）。");
        }
    }

    private async Task OpenNewItemEditorAsync()
    {
        string tabId = $"ItemEditor_New_{Guid.NewGuid()}";
        await ActivateTabAsync(WorkspaceTabKind.ItemEditor, tabId, "新建题录", "Pencil", true,
            () => new ItemEditorViewModel(this));

        if (ActiveTab?.Content is ItemEditorViewModel editor)
        {
            await editor.NewAsync();
        }
    }

    public async Task EditItemByIdAsync(string itemId)
    {
        string tabId = $"ItemEditor_{itemId}";
        await ActivateTabAsync(WorkspaceTabKind.ItemEditor, tabId, "编辑题录", "Pencil", true,
            () => new ItemEditorViewModel(this));
        if (ActiveTab?.Content is ItemEditorViewModel editor)
        {
            await editor.LoadAsync(itemId);
        }
    }

    private async Task EditSelectedItemAsync()
    {
        if (!Shell.CanModifyLibraryItems)
        {
            Report("回收站中的题录不可编辑。");
            return;
        }

        if (Shell.SelectedItems.Count > 1)
        {
            Report("编辑元数据仅支持单选，请只选中一个题录。");
            return;
        }

        LibraryItemViewModel? item = Shell.SelectedItem;
        if (item is null)
        {
            Report("请先选择一个题录。");
            return;
        }

        string tabId = $"ItemEditor_{item.ItemId}";
        await ActivateTabAsync(WorkspaceTabKind.ItemEditor, tabId, BuildItemWorkspaceTabTitle("编辑题录", item.Title),
            "Pencil", true, () => new ItemEditorViewModel(this));
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
        if (!Shell.CanModifyLibraryItems)
        {
            Report("回收站中的题录不可运行 OCR。");
            return;
        }

        IReadOnlyList<LibraryItemViewModel> items = Shell.SelectedItems.Count > 0
            ? Shell.SelectedItems.ToArray()
            : Shell.SelectedItem is not null
                ? [Shell.SelectedItem]
                : Array.Empty<LibraryItemViewModel>();
        if (items.Count == 0)
        {
            Report("请先选择题录。");
            return;
        }

        await ActivateTabAsync(WorkspaceTabKind.Library, "Library", LibraryTabTitle, "Database", false, () => Shell);
        await Shell.RunOcrBatchAsync(items);
    }

    private Task ActivateExistingTabAsync(WorkspaceTabKind kind)
    {
        Workspace.ActivateKind(kind);
        return Task.CompletedTask;
    }

    public async Task OpenSyncCenterAsync()
    {
        await ActivateTabAsync(WorkspaceTabKind.SyncCenter, "SyncCenter", "同步中心", "RefreshCw", true, () => Snapshot);
        await Snapshot.RefreshAsync();
    }

    private void RefreshSyncDescriptors()
    {
        bool busy = _snapshot?.OperationState is SnapshotSyncOperationState.Validating
            or SnapshotSyncOperationState.Publishing
            or SnapshotSyncOperationState.Exporting
            or SnapshotSyncOperationState.CheckingIncoming
            or SnapshotSyncOperationState.InspectingBranch
            or SnapshotSyncOperationState.Applying;
        foreach (UiCommandDescriptor descriptor in new[]
                 {
                     PublishSnapshotDescriptor, ExportSnapshotPackageDescriptor, ReceiveSnapshotDescriptor,
                     OpenSnapshotPackageDescriptor
                 })
        {
            descriptor.Enabled = !busy;
            descriptor.DisabledReason = busy ? "同步操作进行中，请等待完成。" : string.Empty;
        }
    }


    /// <summary>
    /// Runs the single-entry BibLaTeX import into the item editor. Returns true only
    /// when an entry was mapped and applied; failures and cancellations return false
    /// so the caller can keep the current editor state and its status message.
    /// </summary>
    public async Task<bool> ImportBiblatexTextIntoEditorAsync(string text, string? bibFileDirectory,
        ItemId? targetItemId)
    {
        LibraryImportOrchestrator orchestrator = await ImportOrchestratorAsync();
        Result<BiblatexImportApplyResult?> result =
            await orchestrator.ImportBiblatexTextAsync(text, bibFileDirectory, targetItemId, CancellationToken.None);
        if (result.IsFailure)
        {
            ReportError($"BibLaTeX 导入失败：{result.ErrorCode} {result.ErrorMessage}");
            return false;
        }

        if (result.Value is null)
        {
            Report("已取消 BibLaTeX 导入。");
            return false;
        }

        BiblatexImportApplyResult applied = result.Value;
        Report(applied.StatusMessage);
        await Shell.ApplyChangeSetAsync(applied.CreatedItemIds.Concat(applied.UpdatedItemIds)
            .Select(ItemId.Parse).ToArray());
        if (targetItemId is { } existingItemId)
        {
            await EditItemByIdAsync(existingItemId.ToString());
        }
        else if (applied.CreatedItemIds is [string createdId, ..])
        {
            await EditItemByIdAsync(createdId);
        }

        return true;
    }

    private async Task ImportBiblatexBatchAsync()
    {
        string? path = await FilePicker.OpenFileAsync("选择 BibLaTeX 文件", "BibLaTeX", ["*.bib"]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        LibraryImportOrchestrator orchestrator = await ImportOrchestratorAsync();
        Result<BiblatexImportApplyResult?> result =
            await orchestrator.ImportBiblatexFileAsync(path, CancellationToken.None);
        if (result.IsFailure)
        {
            Report($"BibLaTeX 批量导入失败：{result.ErrorCode} {result.ErrorMessage}");
            return;
        }

        if (result.Value is null)
        {
            Report("已取消 BibLaTeX 批量导入。");
            return;
        }

        Report(result.Value.StatusMessage);
        await Shell.ApplyChangeSetAsync(result.Value.CreatedItemIds.Concat(result.Value.UpdatedItemIds)
            .Select(ItemId.Parse).ToArray());
    }

    private async Task ExportBiblatexAsync()
    {
        IReadOnlyList<ItemId> ids = GetSelectedItemIds();
        if (ids.Count == 0)
        {
            Report("请先选择一个或多个题录。");
            return;
        }

        HostServices services = await ServicesAsync();
        Result<string> text = await services.BiblatexImport.ExportItemsAsync(ids);
        if (text.IsFailure)
        {
            Report($"BibLaTeX 导出失败：{text.ErrorCode} {text.ErrorMessage}");
            return;
        }

        string? path = await FilePicker.SaveFileAsync(
            "导出 BibLaTeX",
            ids.Count == 1 ? "export.bib" : "export-batch.bib",
            "BibLaTeX",
            ["*.bib"]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await File.WriteAllTextAsync(path, text.Value, new System.Text.UTF8Encoding(false));
        Report($"已导出 {ids.Count} 条 BibLaTeX 到 {path}");
    }

    private async Task CopyBiblatexAsync()
    {
        IReadOnlyList<ItemId> ids = GetSelectedItemIds();
        if (ids.Count == 0)
        {
            Report("请先选择一个或多个题录。");
            return;
        }

        HostServices services = await ServicesAsync();
        Result<string> text = await services.BiblatexImport.ExportItemsAsync(ids);
        if (text.IsFailure)
        {
            Report($"BibLaTeX 复制失败：{text.ErrorCode} {text.ErrorMessage}");
            return;
        }

        await Clipboard.SetTextAsync(text.Value);
        Report($"已复制 {ids.Count} 条 BibLaTeX 到剪贴板。");
    }

    private IReadOnlyList<ItemId> GetSelectedItemIds()
    {
        if (Shell.HasBatchSelection)
        {
            return Shell.SelectedItems.Select(static item => ItemId.Parse(item.ItemId)).ToArray();
        }

        return Shell.SelectedItem is null
            ? Array.Empty<ItemId>()
            : new[] { ItemId.Parse(Shell.SelectedItem.ItemId) };
    }


    private async Task CopyCslBibliographyAsync()
    {
        IReadOnlyList<ItemId> itemIds = GetSelectedItemIds();
        if (itemIds.Count == 0)
        {
            Report("请先选择题录。");
            return;
        }

        Result<CslRenderResult> rendered =
            await (await ServicesAsync()).CslRenderer.RenderAsync(new CslRenderRequest(itemIds));
        if (rendered.IsFailure)
        {
            Report($"CSL 题录生成失败：{rendered.ErrorCode} {rendered.ErrorMessage}");
            return;
        }

        await Clipboard.SetTextAsync(rendered.Value.RenderedText);
        string warning = rendered.Value.Warnings.Count > 0
            ? $"，warnings: {string.Join("; ", rendered.Value.Warnings)}"
            : "";
        string itemDesc = itemIds.Count > 1 ? $"{itemIds.Count} 条题录" : "题录";
        Report($"已复制 {itemDesc} CSL：{rendered.Value.StyleDisplayName}{warning}");
    }

    private async Task ExportSelectedItemBibliographyAsync()
    {
        IReadOnlyList<ItemId> itemIds = GetSelectedItemIds();
        if (itemIds.Count == 0)
        {
            Report("请先选择题录。");
            return;
        }

        Result<CslRenderResult> rendered =
            await (await ServicesAsync()).CslRenderer.RenderAsync(new CslRenderRequest(itemIds));
        if (rendered.IsFailure)
        {
            Report($"题录导出失败：{rendered.ErrorCode} {rendered.ErrorMessage}");
            return;
        }

        await Clipboard.SetTextAsync(rendered.Value.RenderedHtml);
        string itemDesc = itemIds.Count > 1 ? $"{itemIds.Count} 条题录" : "题录";
        Report($"已导出 {itemDesc} HTML 到剪贴板：{rendered.Value.StyleDisplayName}");
    }

    private Task ActivateTabAsync(WorkspaceTabKind kind, string tabId, string title, string iconName, bool isClosable,
        Func<ViewModelBase> contentFactory)
    {
        Workspace.OpenOrActivate(kind, tabId, title, iconName, isClosable, contentFactory);
        return Task.CompletedTask;
    }

    private Task CloseTabAsync(WorkspaceTabKind kind)
    {
        Workspace.CloseKind(kind);
        return Task.CompletedTask;
    }

    private Task CloseSettingsTabAsync()
    {
        if (_settingsViewModel?.HasDirtySections == true)
        {
            _settingsViewModel.GlobalStatus = "设置有未保存的更改；请先保存或放弃后再关闭设置。";
            return Task.CompletedTask;
        }

        Workspace.CloseKind(WorkspaceTabKind.Settings);
        return Task.CompletedTask;
    }

    private Task CloseTabAsync(string tabId)
    {
        Workspace.Close(tabId);
        return Task.CompletedTask;
    }

    public async Task ExportEvidenceMarkdownToFileAsync(string versionedUri, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(versionedUri))
        {
            Report("请先选择一个可导出的版本化证据 URI。");
            SearchEvidence.Output = "ERROR validation_failed: Versioned evidence URI is required.";
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

        PatchouliNavigationParseResult parsed = PatchouliUriNavigationParser.ParseInput(versionedUri);
        if (!parsed.IsSuccess || parsed.Target is not { Kind: PatchouliNavigationKind.TextPage } target)
        {
            Report("无法解析版本化证据 URI。");
            SearchEvidence.Output = "ERROR validation_failed: Invalid versioned evidence URI.";
            SearchEvidence.RaiseOutput();
            return;
        }

        HostServices services = await ServicesAsync();
        Result<EvidencePageText> markdown = await services.VersionedEvidenceReader.GetBoxTextAsync(
            DocumentInstanceId.Parse(target.ResourceId),
            (target.PageIndex ?? 0) + 1,
            target.RevisionId,
            target.BoxId);
        if (markdown.IsFailure)
        {
            string message = $"ERROR {markdown.ErrorCode}: {markdown.ErrorMessage}";
            SearchEvidence.Output = message;
            SearchEvidence.RaiseOutput();
            Report(message);
            return;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
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
        string fullPath = Path.GetFullPath(path);
        string fullRoot = EnsureTrailingDirectorySeparator(rootPath);
        return string.Equals(fullPath, Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }
}
