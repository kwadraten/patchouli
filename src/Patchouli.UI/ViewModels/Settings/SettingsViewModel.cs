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
        MetadataLookupSettings = new MetadataLookupSettingsViewModel(main);
        SearchRewriteSettings = new SearchRewriteSettingsViewModel(main);
        SyncSettings = new SyncSettingsViewModel(main);
        LocalFileManagement = new LocalFileManagementSettingsViewModel(main);
        ImportSettings = new ImportSettingsViewModel(main);

        ISettingsSection[] sections =
        [
            AppearanceSettings, LibrarySettings, SyncSettings, McpSettings, OcrProviderSettings,
            MetadataLookupSettings, SearchRewriteSettings, LocalFileManagement, ImportSettings
        ];

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

        Categories = new ObservableCollection<NavCategoryViewModel>
        {
            new("外观与显示", "Palette", AppearanceSettings),
            new("库与本机路径", "Database", LibrarySettings),
            new("本地文件", "FolderOpen", LocalFileManagement),
            new("导入", "BookOpen", ImportSettings),
            new("OCR 引擎", "ScanText", OcrProviderSettings),
            new("元数据来源", "Search", MetadataLookupSettings),
            new("搜索重写", "Filter", SearchRewriteSettings),
            new("MCP 服务与安全", "Server", McpSettings),
            new("同步与快照", "Cloud", SyncSettings)
        };

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
    public MetadataLookupSettingsViewModel MetadataLookupSettings { get; }
    public SearchRewriteSettingsViewModel SearchRewriteSettings { get; }
    public SyncSettingsViewModel SyncSettings { get; }
    public LocalFileManagementSettingsViewModel LocalFileManagement { get; }
    public ImportSettingsViewModel ImportSettings { get; }

    public ObservableCollection<NavCategoryViewModel> Categories { get; }

    [ObservableProperty] public partial NavCategoryViewModel ActiveCategory { get; set; } = null!;

    partial void OnActiveCategoryChanged(NavCategoryViewModel value)
    {
        // Drafts stay in memory while the debounced auto-save drains them; switching sections
        // never blocks and never discards.
        RaiseActiveSectionState();
        _activeSectionLoad = SectionOf(value)?.LoadAsync() ?? Task.CompletedTask;
        _activeSectionLoad.Observe(nameof(SettingsViewModel), nameof(ISettingsSection.LoadAsync));
    }

    [ObservableProperty] public partial string GlobalStatus { get; set; } = "";

    partial void OnGlobalStatusChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _main.Report(value);
        }
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }

    [ExcludeFromDerivedGeneration]
    public bool HasDirtySections => Categories.Any(category => SectionOf(category)?.IsDirty == true);

    [ExcludeFromDerivedGeneration] public bool ShowSaveControls => SectionOf(ActiveCategory)?.SupportsEditing == true;

    /// <summary>The unified save command commits every dirty section in one action.</summary>
    [ExcludeFromDerivedGeneration]
    public bool CanSaveAll => HasDirtySections &&
                              Categories.Select(SectionOf)
                                  .OfType<ISettingsSection>()
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
        foreach (ISettingsSection section in Categories
                     .Select(SectionOf)
                     .OfType<ISettingsSection>()
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
    public async Task<bool> SaveAllDirtySectionsAsync()
    {
        List<string> savedTitles = [];
        bool requiresReload = false;
        foreach (NavCategoryViewModel category in Categories)
        {
            ISettingsSection? section = SectionOf(category);
            if (section?.SupportsEditing != true || !section.IsDirty)
            {
                continue;
            }

            await section.SaveAsync();
            if (section.SaveState == SettingsSaveState.Failed)
            {
                GlobalStatus = $"「{category.Title}」保存失败：{section.LastError ?? section.SaveStateText}";
                return false;
            }

            savedTitles.Add(category.Title);
            requiresReload |= section.RequiresReload;
        }

        if (savedTitles.Count > 0)
        {
            GlobalStatus = $"已保存：{string.Join("、", savedTitles)}。" +
                           (requiresReload ? "MCP 服务需重启后生效，可在「MCP 服务与安全」中保存并重启。" : "");
        }

        Raise(nameof(HasDirtySections));
        RaiseActiveSectionState();
        return true;
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
        try
        {
            _isAutoSaving = true;
            for (int pass = 0;
                 pass < MaxAutoSavePasses && !cancellationToken.IsCancellationRequested && HasDirtySections;
                 pass++)
            {
                try
                {
                    if (!await SaveAllDirtySectionsAsync())
                    {
                        break;
                    }
                }
                catch (Exception exception)
                {
                    GlobalStatus = $"自动保存失败：{exception.Message}";
                    break;
                }
            }
        }
        finally
        {
            _isAutoSaving = false;
            _autoSaveGate.Release();
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
            foreach (ISettingsSection section in Categories
                         .Select(SectionOf)
                         .OfType<ISettingsSection>()
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
        Raise(nameof(ShowSaveControls));
        Raise(nameof(CanSaveAll));
        Raise(nameof(CanDiscardAll));
        Raise(nameof(HasDirtySections));
    }

    private static ISettingsSection? SectionOf(NavCategoryViewModel category)
    {
        return category.Content as ISettingsSection;
    }
}
