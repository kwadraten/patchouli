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
using Patchouli.UI.Diagnostics;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Core;

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
    private string _reportedErrorSummary = "";
    private string? _reportedErrorStatus;
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
        LibrarySettings = new LibrarySettingsViewModel(main);
        McpSettings = new McpSettingsViewModel(main);
        OcrProviderSettings = new OcrProviderSettingsViewModel(main);
        LlmSettings = new LlmSettingsViewModel(main);
        WorkflowSettings = new WorkflowSettingsViewModel(main);
        MetadataLookupSettings = new MetadataLookupSettingsViewModel(main);
        SearchRewriteSettings = new SearchRewriteSettingsViewModel(main);
        SyncSettings = new SyncSettingsViewModel(main);
        LocalFileManagement = new LocalFileManagementSettingsViewModel(main);
        ImportSettings = new ImportSettingsViewModel(main);

        ISettingsSection[] sections =
        [
            AppearanceSettings, LibrarySettings, SyncSettings, McpSettings, OcrProviderSettings,
            LlmSettings, WorkflowSettings, MetadataLookupSettings, SearchRewriteSettings, LocalFileManagement,
            ImportSettings
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
            new SettingsSectionEntryViewModel("appearance", "外观与阅读", AppearanceSettings),
            new SettingsSectionEntryViewModel("library", "书库与源文件", LibrarySettings),
            new SettingsSectionEntryViewModel("local_files", "模型与临时文件", LocalFileManagement),
            new SettingsSectionEntryViewModel("import", "导入规则", ImportSettings),
            new SettingsSectionEntryViewModel("ocr", "OCR 引擎", OcrProviderSettings),
            new SettingsSectionEntryViewModel("llm", "模型连接与聊天", LlmSettings),
            new SettingsSectionEntryViewModel("workflows", "工作流", WorkflowSettings),
            new SettingsSectionEntryViewModel("metadata", "元数据来源", MetadataLookupSettings),
            new SettingsSectionEntryViewModel("search_rewrite", "搜索", SearchRewriteSettings),
            new SettingsSectionEntryViewModel("mcp", "MCP 服务与权限", McpSettings),
            new SettingsSectionEntryViewModel("sync", "同步与快照", SyncSettings)
        ];
        foreach (SettingsSectionEntryViewModel entry in SectionEntries)
        {
            _sectionTitles[(ISettingsSection)entry.Content] = entry.Title;
        }

        SettingsSectionEntryViewModel Entry(string id)
        {
            return SectionEntries.Single(entry => entry.Id == id);
        }

        SettingsSectionGroupViewModel library = new(false, Entry("library"), Entry("local_files"));
        SettingsSectionGroupViewModel recognition = new(false, Entry("import"), Entry("ocr"),
            new SettingsSectionEntryViewModel("ocr_model", "多模态 OCR 模型", new LlmOcrDefaultsViewModel(LlmSettings)));
        SettingsSectionGroupViewModel ai = new(true, Entry("llm"), Entry("workflows"), Entry("mcp"));
        Categories =
        [
            new NavCategoryViewModel("外观与阅读", "Palette", AppearanceSettings),
            new NavCategoryViewModel("书库与本机文件", "Database", library),
            new NavCategoryViewModel("导入与识别", "ScanText", recognition),
            new NavCategoryViewModel("AI集成", "Sparkles", ai),
            new NavCategoryViewModel("元数据来源", "Search", MetadataLookupSettings),
            new NavCategoryViewModel("搜索", "Filter", SearchRewriteSettings),
            new NavCategoryViewModel("同步与快照", "Cloud", SyncSettings)
        ];
        foreach (NavCategoryViewModel category in Categories)
        {
            if (category.Content is SettingsSectionGroupViewModel group)
            {
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
    public LibrarySettingsViewModel LibrarySettings { get; }
    public McpSettingsViewModel McpSettings { get; }
    public OcrProviderSettingsViewModel OcrProviderSettings { get; }
    public LlmSettingsViewModel LlmSettings { get; }
    public WorkflowSettingsViewModel WorkflowSettings { get; }
    public MetadataLookupSettingsViewModel MetadataLookupSettings { get; }
    public SearchRewriteSettingsViewModel SearchRewriteSettings { get; }
    public SyncSettingsViewModel SyncSettings { get; }
    public LocalFileManagementSettingsViewModel LocalFileManagement { get; }
    public ImportSettingsViewModel ImportSettings { get; }

    public ObservableCollection<NavCategoryViewModel> Categories { get; }
    public IReadOnlyList<SettingsSectionEntryViewModel> SectionEntries { get; }

    [ObservableProperty] public partial NavCategoryViewModel ActiveCategory { get; set; } = null!;

    partial void OnActiveCategoryChanged(NavCategoryViewModel value)
    {
        // Drafts stay in memory while the debounced auto-save drains them; switching sections
        // never blocks and never discards.
        StartActiveSectionLoad();
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
    /// when it attaches, so every edit persists automatically; hosts without the view (tests)
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

        if (HasDirtySections)
        {
            _autoSaveTriggers.OnNext(Unit.Default);
        }
    }

    public Task WaitForActiveSectionLoadAsync()
    {
        return _activeSectionLoad;
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
        SyncSettings.NotifyLibraryContextChanged();
    }

    public void NotifyLibraryContextChanged()
    {
        SyncSettings.NotifyLibraryContextChanged();
    }

    /// <summary>Saves every dirty section in one pass. On success the aggregated status is reported
    /// once; a section that requires a service reload (MCP) surfaces a single restart hint.</summary>
    public Task<bool> SaveAllDirtySectionsAsync()
    {
        return SaveDirtySectionsAsync(new Dictionary<ISettingsSection, string>());
    }

    private async Task<bool> SaveDirtySectionsAsync(Dictionary<ISettingsSection, string> failures)
    {
        List<string> savedTitles = [];
        List<string> warnings = [];
        bool requiresReload = false;
        foreach (ISettingsSection section in _sections)
        {
            if (!section.SupportsEditing || !section.IsDirty || failures.ContainsKey(section))
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
                           (requiresReload ? "MCP 服务需重启后生效，可在「AI集成 → MCP 服务与权限」中保存并重启。" : "");
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
        if (_autoSaveSubscription is not null && !_isAutoSaving && HasDirtySections)
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
                 _sections.Any(section => section.SupportsEditing && section.IsDirty && !failures.ContainsKey(section));
                 pass++)
            {
                try
                {
                    await SaveDirtySectionsAsync(failures);
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
                _sections.Any(section => section.SupportsEditing && section.IsDirty && !failures.ContainsKey(section)))
            {
                RequestAutoSave();
            }
        }
    }

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
        if (_sectionTitles.Count == _sections.Count)
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

    public async Task SelectSectionAsync(string id)
    {
        if (id.Equals("mineru", StringComparison.OrdinalIgnoreCase))
        {
            id = "ocr";
        }

        if (!_sectionCategories.TryGetValue(id, out NavCategoryViewModel? category))
        {
            throw new ArgumentException($"未知设置入口：{id}", nameof(id));
        }

        bool subpageChanged = false;
        if (category.Content is SettingsSectionGroupViewModel group)
        {
            SettingsSectionEntryViewModel entry =
                group.Entries.Single(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            subpageChanged = !ReferenceEquals(group.ActiveEntry, entry);
            group.ActiveEntry = entry;
        }

        if (ReferenceEquals(ActiveCategory, category))
        {
            if (!subpageChanged)
            {
                StartActiveSectionLoad();
            }
        }
        else
        {
            ActiveCategory = category;
        }

        await WaitForActiveSectionLoadAsync();
    }

    private IEnumerable<ISettingsSection> ActiveSections()
    {
        if (ActiveCategory?.Content is SettingsSectionGroupViewModel group)
        {
            IEnumerable<object> contents = group.UseTabs
                ? [group.ActiveEntry.Content]
                : group.Entries.Select(entry => entry.Content);
            return contents.Select(content => content is LlmOcrDefaultsViewModel ocr ? ocr.Settings : content)
                .OfType<ISettingsSection>().Distinct();
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
        foreach (ISettingsSection section in ActiveSections().ToArray())
        {
            await section.LoadAsync();
        }
    }
}
