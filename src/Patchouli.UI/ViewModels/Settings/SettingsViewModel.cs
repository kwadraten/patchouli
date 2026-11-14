using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Extensions.Contracts;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Csl;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly TimeSpan AutoSaveThrottle = TimeSpan.FromMilliseconds(800);

    // Defensive bound for the follow-up passes that drain edits made while a save is in flight.
    private const int MaxAutoSavePasses = 4;

    private readonly MainWindowViewModel _main;
    private readonly IScheduler _timingScheduler;
    private readonly IScheduler _uiScheduler;
    private readonly Subject<Unit> _autoSaveTriggers = new();
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private IDisposable? _autoSaveSubscription;
    private bool _isAutoSaving;
    private bool _isRoutingCommand;
    private Task _activeSectionLoad = Task.CompletedTask;
    private Task _navigationTask = Task.CompletedTask;
    private NavCategoryViewModel _activeCategory = null!;
    private string _reportedErrorSummary = "";
    private string? _reportedErrorStatus;
    private bool _sectionTitlesReady;
    private readonly IReadOnlyList<ISettingsSection> _sections;

    private readonly Dictionary<string, NavCategoryViewModel>
        _sectionCategories = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<ISettingsSection, string> _sectionTitles = new();

    public SettingsViewModel(MainWindowViewModel main)
        : this(
            main,
            TaskPoolScheduler.Default,
            SynchronizationContext.Current is { } synchronizationContext
                ? new SynchronizationContextScheduler(synchronizationContext)
                : CurrentThreadScheduler.Instance)
    {
    }

    internal SettingsViewModel(MainWindowViewModel main, IScheduler timingScheduler, IScheduler uiScheduler)
    {
        using IDisposable commandActivityTracker = AsyncCommand.UseActivityTracker(main.ActivityTracker);
        _main = main;
        _timingScheduler = timingScheduler;
        _uiScheduler = uiScheduler;
        Register(_autoSaveTriggers);
        AppearanceSettings = new AppearanceSettingsViewModel(main);
        StartupSettings = new StartupSettingsViewModel(main);
        LibrarySettings = new LibrarySettingsViewModel(main);
        McpSettings = new McpSettingsViewModel(main);
        VirtualLinkSettings = new VirtualLinkSettingsViewModel(main);
        OcrProviderSettings = new OcrProviderSettingsViewModel(main);
        LayoutModelCatalog = new OcrComponentCatalogSettingsViewModel(main, ExtensionKind.LayoutRecognition);
        OcrEngineCatalog = new OcrComponentCatalogSettingsViewModel(main, ExtensionKind.Ocr);
        OcrTextLayerCatalog =
            new OcrComponentCatalogSettingsViewModel(main, ExtensionKind.PdfTextLayer);
        OcrComponents = new OcrComponentsSettingsViewModel(LayoutModelCatalog, OcrEngineCatalog,
            OcrTextLayerCatalog);
        LlmSettings = new LlmSettingsViewModel(main);
        ExtensionSettings = new ExtensionSettingsViewModel(main);
        AgentIntegrationSettings = new AgentIntegrationSettingsViewModel(main);
        MetadataLookupSettings = new MetadataLookupSettingsViewModel(main);
        SearchRewriteSettings = new SearchRewriteSettingsViewModel(main);
        SearchAwarenessSettings = SearchRewriteSettings.Awareness;
        SearchLinkSettings = new SearchLinkSettingsViewModel(VirtualLinkSettings);
        McpPermissionsSettings = new McpPermissionsSettingsViewModel(McpSettings);
        CslLocalizationSettings = new CslLocalizationSettingsViewModel(main);
        CslStyleManager = new CslStyleManagerViewModel(main);
        CslOnlineStyles = new CslOnlineStylesSettingsViewModel(CslStyleManager);
        CaywSettings = new CaywSettingsViewModel(main);
        Register(CslLocalizationSettings);
        Register(CslStyleManager);
        Register(CslOnlineStyles);
        Register(CaywSettings);
        SyncSettings = new SyncSettingsViewModel(main);
        LocalFileManagement = new LocalFileManagementSettingsViewModel(main);
        NetworkProxySettings = new NetworkProxySettingsViewModel(main);
        SystemIntegrationSettings = new SystemIntegrationSettingsViewModel(
            VirtualLinkSettings, NetworkProxySettings, McpSettings);
        ImportSettings = new ImportSettingsViewModel(main);
        UpdateSettings = new UpdateSettingsViewModel(main);

        ISettingsSection[] sections =
        [
            AppearanceSettings, StartupSettings, LibrarySettings, SyncSettings, McpSettings, VirtualLinkSettings,
            NetworkProxySettings, OcrProviderSettings, OcrEngineCatalog, OcrTextLayerCatalog, LayoutModelCatalog,
            LlmSettings, AgentIntegrationSettings, MetadataLookupSettings, SearchRewriteSettings,
            SearchAwarenessSettings,
            LocalFileManagement, ImportSettings, CslLocalizationSettings, CslStyleManager, CslOnlineStyles,
            CaywSettings, UpdateSettings
        ];

        _sections = sections;

        IDisposable sectionSubscription = Observable.Merge(
                sections.Select(section =>
                    Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                        handler => ((INotifyPropertyChanged)section).PropertyChanged += handler,
                        handler => ((INotifyPropertyChanged)section).PropertyChanged -= handler)))
            .Subscribe(
                _ =>
                {
                    RaiseActiveSectionState();
                    RequestAutoSave();
                },
                ex => UnexpectedExceptions.Sink.Report(ex, "settings-section-property-changed",
                    nameof(SettingsViewModel)));

        Register(sectionSubscription);

        SectionEntries =
        [
            new SettingsSectionEntryViewModel("appearance", "主题与阅读", AppearanceSettings),
            new SettingsSectionEntryViewModel("startup", "启动", StartupSettings),
            new SettingsSectionEntryViewModel("system_integration", "链接与系统", SystemIntegrationSettings),
            new SettingsSectionEntryViewModel("update", "更新", UpdateSettings),
            new SettingsSectionEntryViewModel("library", "文件与扫描", LibrarySettings),
            new SettingsSectionEntryViewModel("import", "导入", ImportSettings),
            new SettingsSectionEntryViewModel("local_files", "本机存储", LocalFileManagement),
            new SettingsSectionEntryViewModel("ocr_presets", "OCR 预设", OcrProviderSettings),
            new SettingsSectionEntryViewModel("ocr_components", "OCR 组件", OcrComponents),
            new SettingsSectionEntryViewModel("translation", "翻译",
                new TranslationDefaultsViewModel(LlmSettings)),
            new SettingsSectionEntryViewModel("metadata", "元数据来源", MetadataLookupSettings),
            new SettingsSectionEntryViewModel("llm", "模型连接", LlmSettings),
            new SettingsSectionEntryViewModel("agents", "Agent", AgentIntegrationSettings),
            new SettingsSectionEntryViewModel("mcp", "MCP 服务", McpSettings),
            new SettingsSectionEntryViewModel("permissions", "访问权限", McpPermissionsSettings),
            new SettingsSectionEntryViewModel("search_rewrite", "搜索重写", SearchRewriteSettings),
            new SettingsSectionEntryViewModel("search_awareness", "页面感知", SearchAwarenessSettings),
            new SettingsSectionEntryViewModel("search_links", "链接解析", SearchLinkSettings),
            new SettingsSectionEntryViewModel("csl_localization", "本地化", CslLocalizationSettings),
            new SettingsSectionEntryViewModel("csl_installed", "已安装样式", CslStyleManager),
            new SettingsSectionEntryViewModel("csl_online", "在线样式", CslOnlineStyles),
            new SettingsSectionEntryViewModel("csl_cayw", "Cite As You Write", CaywSettings),
            new SettingsSectionEntryViewModel("sync", "同步", SyncSettings)
        ];
        foreach (SettingsSectionEntryViewModel entry in SectionEntries)
        {
            if (entry.Content is ISettingsSection section && !_sectionTitles.ContainsKey(section))
            {
                _sectionTitles[section] = entry.Title;
            }
        }

        // The catalog tabs live inside the OCR component group (not in SectionEntries as
        // individual entries); give them titles so save/validation summaries never hit a
        // missing-key lookup.
        _sectionTitles[LayoutModelCatalog] = "版面识别模型";
        _sectionTitles[OcrEngineCatalog] = "OCR 引擎";
        _sectionTitles[OcrTextLayerCatalog] = "文本层提取算法";
        _sectionTitles[VirtualLinkSettings] = "链接与系统";
        _sectionTitles[NetworkProxySettings] = "网络代理";
        _sectionTitlesReady = true;

        SettingsSectionEntryViewModel Entry(string id)
        {
            return SectionEntries.Single(entry => entry.Id == id);
        }

        SettingsSectionGroupViewModel behavior = new(Entry("startup"), Entry("system_integration"),
            Entry("update"));
        SettingsSectionGroupViewModel data = new(Entry("import"), Entry("local_files"));
        SettingsSectionGroupViewModel processing = new(Entry("ocr_presets"), Entry("ocr_components"),
            Entry("translation"), Entry("metadata"));
        SettingsSectionGroupViewModel ai = new(Entry("llm"), Entry("agents"), Entry("mcp"),
            Entry("permissions"));
        SettingsSectionGroupViewModel search = new(Entry("search_rewrite"), Entry("search_awareness"),
            Entry("search_links"));
        SettingsSectionGroupViewModel citations = new(Entry("csl_localization"), Entry("csl_installed"),
            Entry("csl_online"), Entry("csl_cayw"));
        Categories =
        [
            new NavCategoryViewModel("显示", "Palette", AppearanceSettings),
            new NavCategoryViewModel("行为", "MousePointer2", behavior),
            new NavCategoryViewModel("书库", "Database", LibrarySettings),
            new NavCategoryViewModel("数据", "FolderOpen", data),
            new NavCategoryViewModel("文献加工", "ScanText", processing),
            new NavCategoryViewModel("AI 集成", "Sparkles", ai),
            new NavCategoryViewModel("搜索", "Search", search),
            new NavCategoryViewModel("引用", "Quote", citations),
            new NavCategoryViewModel("同步", "Cloud", SyncSettings)
        ];
        foreach (NavCategoryViewModel category in Categories)
        {
            if (category.Content is SettingsSectionGroupViewModel group)
            {
                group.SelectionRequested = entry => RequestNavigation(category, entry);
                foreach (SettingsSectionEntryViewModel entry in group.Entries)
                {
                    _sectionCategories[entry.Id] = category;
                }

                Register(Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                        handler => group.PropertyChanged += handler,
                        handler => group.PropertyChanged -= handler)
                    .Where(change => change.EventArgs.PropertyName == nameof(SettingsSectionGroupViewModel.ActiveEntry))
                    .Subscribe(_ =>
                    {
                        if (ReferenceEquals(ActiveCategory, category))
                        {
                            StartActiveSectionLoad();
                        }
                    }, exception => UnexpectedExceptions.Sink.Report(exception, "settings-subpage")));
                Register(group);
            }
            else
            {
                SettingsSectionEntryViewModel entry =
                    SectionEntries.Single(entry => ReferenceEquals(entry.Content, category.Content));
                _sectionCategories[entry.Id] = category;
            }
        }

        ActiveCategory = Categories.First();

        // Kept as the manual unified entry points for tests and non-view hosts; the settings
        // page persists edits through the auto-save pipeline instead of binding these.
        SaveCommand = new AsyncCommand(SaveAllSectionsAsync);

        DiscardCommand = new AsyncCommand(DiscardAllSectionsAsync);
    }

    public AppearanceSettingsViewModel AppearanceSettings { get; }
    public StartupSettingsViewModel StartupSettings { get; }
    public LibrarySettingsViewModel LibrarySettings { get; }
    public McpSettingsViewModel McpSettings { get; }
    public McpPermissionsSettingsViewModel McpPermissionsSettings { get; }
    public VirtualLinkSettingsViewModel VirtualLinkSettings { get; }
    public OcrProviderSettingsViewModel OcrProviderSettings { get; }
    public OcrComponentCatalogSettingsViewModel LayoutModelCatalog { get; }
    public OcrComponentCatalogSettingsViewModel OcrEngineCatalog { get; }
    public OcrComponentCatalogSettingsViewModel OcrTextLayerCatalog { get; }
    public OcrComponentsSettingsViewModel OcrComponents { get; }
    public LlmSettingsViewModel LlmSettings { get; }
    public ExtensionSettingsViewModel ExtensionSettings { get; }

    public ExtensionSettingsViewModel CreateExtensionEditor()
    {
        return new ExtensionSettingsViewModel(_main);
    }

    public ExtensionCreationViewModel CreateExtensionDraft(ExtensionKind kind)
    {
        return new ExtensionCreationViewModel(_main, kind);
    }

    public AgentIntegrationSettingsViewModel AgentIntegrationSettings { get; }
    public MetadataLookupSettingsViewModel MetadataLookupSettings { get; }
    public SearchRewriteSettingsViewModel SearchRewriteSettings { get; }
    public SearchPageAwarenessSettingsViewModel SearchAwarenessSettings { get; }
    public SearchLinkSettingsViewModel SearchLinkSettings { get; }
    public CslLocalizationSettingsViewModel CslLocalizationSettings { get; }
    public CslStyleManagerViewModel CslStyleManager { get; }
    public CslOnlineStylesSettingsViewModel CslOnlineStyles { get; }
    public CaywSettingsViewModel CaywSettings { get; }
    public SyncSettingsViewModel SyncSettings { get; }
    public LocalFileManagementSettingsViewModel LocalFileManagement { get; }
    public NetworkProxySettingsViewModel NetworkProxySettings { get; }
    public SystemIntegrationSettingsViewModel SystemIntegrationSettings { get; }
    public ImportSettingsViewModel ImportSettings { get; }
    public UpdateSettingsViewModel UpdateSettings { get; }

    public ObservableCollection<NavCategoryViewModel> Categories { get; }
    public IReadOnlyList<SettingsSectionEntryViewModel> SectionEntries { get; }

    public NavCategoryViewModel ActiveCategory
    {
        get => _activeCategory;
        set
        {
            if (value is not null && !ReferenceEquals(value, _activeCategory))
            {
                RequestNavigation(value, null);
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    public SettingsSectionGroupViewModel? ActiveTabbedGroup =>
        ActiveCategory?.Content as SettingsSectionGroupViewModel;

    [ExcludeFromDerivedGeneration] public bool HasSectionTabs => ActiveTabbedGroup is not null;

    private void SetActiveCategory(NavCategoryViewModel value)
    {
        _activeCategory = value;
        Raise(nameof(ActiveCategory));
        Raise(nameof(ActiveTabbedGroup));
        Raise(nameof(HasSectionTabs));
        StartActiveSectionLoad();
    }

    private void RequestNavigation(NavCategoryViewModel category, SettingsSectionEntryViewModel? entry)
    {
        _navigationTask = NavigateAsync(category, entry);
        _navigationTask.Observe(nameof(SettingsViewModel), nameof(NavigateAsync));
    }

    private async Task NavigateAsync(NavCategoryViewModel category, SettingsSectionEntryViewModel? entry)
    {
        bool leavesMcp = ActiveSections().Contains(McpSettings) &&
                         (!ReferenceEquals(category, ActiveCategory) ||
                          (entry is not null && entry.Content is not
                              (McpSettingsViewModel or McpPermissionsSettingsViewModel)));
        if (leavesMcp && !await ConfirmLeaveMcpAsync())
        {
            Raise(nameof(ActiveCategory));
            ActiveTabbedGroup?.RefreshSelection();
            return;
        }

        bool subpageChanged = false;
        if (entry is not null && category.Content is SettingsSectionGroupViewModel group)
        {
            subpageChanged = !ReferenceEquals(group.ActiveEntry, entry);
            if (subpageChanged)
            {
                group.SetActiveEntry(entry);
            }
        }

        if (!ReferenceEquals(ActiveCategory, category))
        {
            SetActiveCategory(category);
        }
        else if (!subpageChanged)
        {
            StartActiveSectionLoad();
        }
    }

    public Task<bool> ConfirmLeaveMcpAsync()
    {
        return McpSettings.ConfirmLeaveAsync();
    }

    [ObservableProperty] public partial string GlobalStatus { get; set; } = "";

    partial void OnGlobalStatusChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (_sections.Any(section => section.ValidationState == SettingsValidationState.Invalid ||
                                         section.SaveState == SettingsSaveState.Failed))
            {
                _main.ReportError(value);
                _reportedErrorStatus = value;
            }
            else
            {
                _main.Report(value);
                _reportedErrorStatus = null;
            }
        }
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }

    [ExcludeFromDerivedGeneration] public bool HasDirtySections => _sections.Any(section => section.IsDirty);

    [ExcludeFromDerivedGeneration]
    public string ValidationSummary => string.Join(Environment.NewLine,
        _sections.Where(section => !string.IsNullOrWhiteSpace(section.LastError) &&
                                   (section.ValidationState == SettingsValidationState.Invalid ||
                                    section.SaveState == SettingsSaveState.Failed))
            .Select(section => $"{_sectionTitles[section]}：{section.LastError}"));

    [ExcludeFromDerivedGeneration]
    public bool ShowSaveControls => ActiveSections().Any(section => section.SupportsEditing);

    /// <summary>The unified save command commits every dirty section in one action.</summary>
    [ExcludeFromDerivedGeneration]
    public bool CanSaveAll => HasDirtySections &&
                              _sections
                                  .Where(section => section.IsDirty)
                                  .All(section => section.SupportsEditing && section.CanSave) &&
                              !_isRoutingCommand;

    /// <summary>The unified discard command reverts every dirty section in one action.</summary>
    [ExcludeFromDerivedGeneration]
    public bool CanDiscardAll => HasDirtySections && !_isRoutingCommand;

    /// <summary>
    /// Starts the debounced auto-save pipeline (idempotent). The settings page view activates it
    /// when it attaches. MCP drafts require explicit discard or save-and-restart on departure;
    /// other editable sections persist automatically. Hosts without the view (tests)
    /// keep the manual save/discard commands and nothing is persisted implicitly.
    /// </summary>
    public void EnableAutoSave()
    {
        if (_autoSaveSubscription is not null)
        {
            return;
        }

        _autoSaveSubscription = ReactiveUiFlow.SubscribeLatest(
            _autoSaveTriggers,
            AutoSaveThrottle,
            _timingScheduler,
            _uiScheduler,
            AutoSaveDirtySectionsAsync,
            ex => UnexpectedExceptions.Sink.Report(ex, nameof(SettingsViewModel),
                nameof(AutoSaveDirtySectionsAsync)));
        Register(_autoSaveSubscription);

        if (HasAutoSaveChanges)
        {
            _autoSaveTriggers.OnNext(Unit.Default);
        }
    }

    public async Task WaitForActiveSectionLoadAsync()
    {
        await _navigationTask;
        await _activeSectionLoad;
    }

    internal async Task WaitForAutoSaveAsync()
    {
        await _autoSaveGate.WaitAsync();
        _autoSaveGate.Release();
    }

    public async Task ReloadCleanSectionsAsync()
    {
        await _activeSectionLoad;
        foreach (ISettingsSection section in _sections
                     .Where(section => !section.IsDirty))
        {
            await section.LoadAsync();
        }

        RaiseActiveSectionState();
    }

    public void NotifyRuntimeDatabasePathChanged()
    {
        LibrarySettings.NotifyRuntimeDatabasePathChanged();
        StartupSettings.NotifyRuntimeDatabasePathChanged();
        SyncSettings.NotifyLibraryContextChanged();
        NetworkProxySettings.NotifyLibraryContextChanged();
    }

    public void NotifyLibraryContextChanged()
    {
        SyncSettings.NotifyLibraryContextChanged();
        NetworkProxySettings.NotifyLibraryContextChanged();
    }

    /// <summary>Saves every dirty section in one pass. On success the aggregated status is reported
    /// once; a section that requires a service reload (MCP) surfaces a single restart hint.</summary>
    public Task<bool> SaveAllDirtySectionsAsync()
    {
        return SaveDirtySectionsAsync(new Dictionary<ISettingsSection, string>());
    }

    private async Task<bool> SaveDirtySectionsAsync(Dictionary<ISettingsSection, string> failures,
        bool includeMcp = true)
    {
        List<string> savedTitles = [];
        List<string> warnings = [];
        bool requiresReload = false;
        foreach (ISettingsSection section in _sections)
        {
            if (!section.SupportsEditing || !section.IsDirty || failures.ContainsKey(section) ||
                (!includeMcp && ReferenceEquals(section, McpSettings)))
            {
                continue;
            }

            try
            {
                await section.SaveAsync();
            }
            catch (Exception exception)
            {
                ((SettingsSectionViewModelBase)section).ReportSaveFailure(exception.Message);
                failures[section] = $"「{_sectionTitles[section]}」保存失败：{exception.Message}";
                continue;
            }

            if (section.SaveState == SettingsSaveState.Failed || section.IsSaving)
            {
                failures[section] = $"「{_sectionTitles[section]}」保存失败：{section.LastError ?? section.SaveStateText}";
                continue;
            }

            savedTitles.Add(_sectionTitles[section]);
            requiresReload |= section.RequiresReload;
            if (section.ValidationState == SettingsValidationState.Invalid)
            {
                warnings.Add($"「{_sectionTitles[section]}」{section.SaveStateText}");
            }
        }

        if (savedTitles.Count > 0)
        {
            GlobalStatus = $"已保存：{string.Join("、", savedTitles)}。" +
                           (requiresReload ? "MCP 服务需重启后生效，可在「AI 集成 → MCP 服务」中保存并重启。" : "");
        }

        if (failures.Count > 0 || warnings.Count > 0)
        {
            GlobalStatus = (savedTitles.Count > 0 ? GlobalStatus : "") +
                           string.Join("；", warnings.Concat(failures.Values));
        }

        Raise(nameof(HasDirtySections));
        RaiseActiveSectionState();
        return failures.Count == 0;
    }

    /// <summary>Feeds the debounced auto-save trigger from section changes. Events raised by the
    /// save pass itself are suppressed so a failed save is not immediately retried; the next user
    /// edit re-arms the pipeline.</summary>
    private void RequestAutoSave()
    {
        if (_autoSaveSubscription is not null && !_isAutoSaving && HasAutoSaveChanges)
        {
            _autoSaveTriggers.OnNext(Unit.Default);
        }
    }

    /// <summary>Drains all dirty sections after the throttle window. Edits made while a pass is
    /// running are picked up by the next pass; a failed pass reports through
    /// <see cref="GlobalStatus"/> and stops without retrying.</summary>
    private async Task AutoSaveDirtySectionsAsync(CancellationToken cancellationToken)
    {
        await _autoSaveGate.WaitAsync(CancellationToken.None);
        bool completed = true;
        Dictionary<ISettingsSection, string> failures = new();
        try
        {
            _isAutoSaving = true;
            for (int pass = 0;
                 pass < MaxAutoSavePasses && !cancellationToken.IsCancellationRequested &&
                 _sections.Any(section =>
                     section != McpSettings && section.SupportsEditing && section.IsDirty &&
                     !failures.ContainsKey(section));
                 pass++)
            {
                try
                {
                    await SaveDirtySectionsAsync(failures, false);
                }
                catch (Exception exception)
                {
                    GlobalStatus = $"自动保存失败：{exception.Message}";
                    _main.ReportError(GlobalStatus);
                    _reportedErrorStatus = GlobalStatus;
                    completed = false;
                    break;
                }
            }
        }
        finally
        {
            _isAutoSaving = false;
            _autoSaveGate.Release();
            if (completed && !cancellationToken.IsCancellationRequested &&
                _sections.Any(section =>
                    section != McpSettings && section.SupportsEditing && section.IsDirty &&
                    !failures.ContainsKey(section)))
            {
                RequestAutoSave();
            }
        }
    }

    [ExcludeFromDerivedGeneration]
    private bool HasAutoSaveChanges =>
        _sections.Any(section => section != McpSettings && section.SupportsEditing && section.IsDirty);

    private async Task SaveAllSectionsAsync()
    {
        if (!HasDirtySections)
        {
            return;
        }

        _isRoutingCommand = true;
        RaiseActiveSectionState();
        try
        {
            await SaveAllDirtySectionsAsync();
        }
        finally
        {
            _isRoutingCommand = false;
            RaiseActiveSectionState();
        }
    }

    private async Task DiscardAllSectionsAsync()
    {
        if (!HasDirtySections)
        {
            return;
        }

        _isRoutingCommand = true;
        RaiseActiveSectionState();
        try
        {
            foreach (ISettingsSection section in _sections
                         .Where(section => section.SupportsEditing && section.IsDirty))
            {
                await section.DiscardAsync();
            }

            GlobalStatus = "已放弃所有未保存的更改。";
        }
        finally
        {
            _isRoutingCommand = false;
            RaiseActiveSectionState();
        }
    }

    private void RaiseActiveSectionState()
    {
        // Construction can emit section changes before their display titles are registered.
        if (_sectionTitlesReady)
        {
            string summary = ValidationSummary;
            if (summary != _reportedErrorSummary)
            {
                if (summary.Length > 0)
                {
                    _main.ReportError(summary);
                    _reportedErrorStatus = summary;
                }
                else if (_main.StatusIsError && _main.Status == _reportedErrorStatus)
                {
                    // Clear only the message owned by settings, preserving later host notifications.
                    _main.Report("设置校验已通过。");
                    _reportedErrorStatus = null;
                }

                _reportedErrorSummary = summary;
            }
        }

        Raise(nameof(ValidationSummary));
        Raise(nameof(ShowSaveControls));
        Raise(nameof(CanSaveAll));
        Raise(nameof(CanDiscardAll));
        Raise(nameof(HasDirtySections));
    }

    /// <summary>Raised after a settings-search result navigated to its section. The view brings
    /// the card into view and highlights it briefly; no result tab is created.</summary>
    public event EventHandler<SettingsCardNavigationEventArgs>? CardNavigationRequested;

    public async Task NavigateToSearchEntryAsync(SettingsSearchEntry entry)
    {
        await SelectSectionAsync(entry.SectionId);
        if (!_sectionCategories.TryGetValue(entry.SectionId, out NavCategoryViewModel? category) ||
            !ReferenceEquals(ActiveCategory, category) ||
            (category.Content is SettingsSectionGroupViewModel group && group.ActiveEntry.Id != entry.SectionId))
        {
            // A pending MCP draft can cancel navigation. Do not highlight a retained, hidden page.
            return;
        }

        CardNavigationRequested?.Invoke(this, new SettingsCardNavigationEventArgs(entry.CardAnchorId));
    }

    public async Task SelectSectionAsync(string id)
    {
        if (!_sectionCategories.TryGetValue(id, out NavCategoryViewModel? category))
        {
            throw new ArgumentException($"未知设置入口：{id}", nameof(id));
        }

        SettingsSectionEntryViewModel? selectedEntry = null;
        if (category.Content is SettingsSectionGroupViewModel group)
        {
            selectedEntry =
                group.Entries.Single(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        RequestNavigation(category, selectedEntry);
        await WaitForActiveSectionLoadAsync();
    }

    private IEnumerable<ISettingsSection> ActiveSections()
    {
        if (ActiveCategory?.Content is SettingsSectionGroupViewModel group)
        {
            return group.ActiveEntry.Content switch
            {
                TranslationDefaultsViewModel translation => [translation.Settings],
                OcrComponentsSettingsViewModel components => components.Catalogs,
                McpPermissionsSettingsViewModel permissions => [permissions.Mcp],
                SystemIntegrationSettingsViewModel integration => [integration.VirtualLinks, integration.NetworkProxy],
                SearchLinkSettingsViewModel links => [links.VirtualLinks],
                ISettingsSection selectedSection => [selectedSection],
                _ => []
            };
        }

        return ActiveCategory?.Content is ISettingsSection section ? [section] : [];
    }

    private void StartActiveSectionLoad()
    {
        RaiseActiveSectionState();
        _activeSectionLoad = LoadActiveSectionsAsync();
        _activeSectionLoad.Observe(nameof(SettingsViewModel), nameof(ISettingsSection.LoadAsync));
    }

    private async Task LoadActiveSectionsAsync()
    {
        bool loadTranslationEngines = ActiveCategory.Content is SettingsSectionGroupViewModel
        {
            ActiveEntry.Content: TranslationDefaultsViewModel
        };
        bool refreshCli = ActiveCategory.Content is SettingsSectionGroupViewModel
        {
            ActiveEntry.Content: SystemIntegrationSettingsViewModel
        };
        foreach (ISettingsSection section in ActiveSections().ToArray())
        {
            await section.LoadAsync();
        }

        if (loadTranslationEngines)
        {
            await LlmSettings.RefreshTranslationEnginesCommand.ExecuteAsync();
        }

        if (refreshCli)
        {
            await McpSettings.RefreshCliStatusAsync();
        }
    }
}

public sealed record SettingsCardNavigationEventArgs(string AnchorId);
