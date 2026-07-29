using Patchouli.UI.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private NavCategoryViewModel _activeCategory = null!;
    private string _globalStatus = "";
    private bool _isRoutingCommand;
    private Task _activeSectionLoad = Task.CompletedTask;

    public SettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        LibrarySettings = new LibrarySettingsViewModel(main);
        McpSettings = new McpSettingsViewModel(main);
        OcrProviderSettings = new OcrProviderSettingsViewModel(main);
        MetadataLookupSettings = new MetadataLookupSettingsViewModel(main);
        SyncSettings = new SyncSettingsViewModel(main);

        foreach (ISettingsSection section in new ISettingsSection[]
                     { LibrarySettings, SyncSettings, McpSettings, OcrProviderSettings, MetadataLookupSettings })
        {
            ((INotifyPropertyChanged)section).PropertyChanged += SectionPropertyChanged;
        }

        Categories = new ObservableCollection<NavCategoryViewModel>
        {
            new("库与本机路径", "Database", LibrarySettings),
            new("同步与快照", "Cloud", SyncSettings),
            new("MCP 服务与安全", "Server", McpSettings),
            new("OCR 引擎", "ScanText", OcrProviderSettings),
            new("元数据来源", "Search", MetadataLookupSettings)
        };

        ActiveCategory = Categories.First();

        SaveCommand = new AsyncCommand(SaveAllSectionsAsync);

        DiscardCommand = new AsyncCommand(DiscardAllSectionsAsync);
    }

    public LibrarySettingsViewModel LibrarySettings { get; }
    public McpSettingsViewModel McpSettings { get; }
    public OcrProviderSettingsViewModel OcrProviderSettings { get; }
    public MetadataLookupSettingsViewModel MetadataLookupSettings { get; }
    public SyncSettingsViewModel SyncSettings { get; }

    public ObservableCollection<NavCategoryViewModel> Categories { get; }

    public NavCategoryViewModel ActiveCategory
    {
        get => _activeCategory;
        set
        {
            if (ReferenceEquals(_activeCategory, value))
            {
                return;
            }

            // Unsaved drafts stay in memory when switching sections; the header save/discard
            // acts on all dirty sections at once.
            _activeCategory = value;
            Raise();
            RaiseActiveSectionState();
            _activeSectionLoad = SectionOf(value)?.LoadAsync() ?? Task.CompletedTask;
            _activeSectionLoad.Observe(nameof(SettingsViewModel), nameof(ISettingsSection.LoadAsync));
        }
    }

    public string GlobalStatus
    {
        get => _globalStatus;
        set
        {
            _globalStatus = value;
            Raise();
            if (!string.IsNullOrWhiteSpace(value))
            {
                _main.Report(value);
            }
        }
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
    public bool HasDirtySections => Categories.Any(category => SectionOf(category)?.IsDirty == true);

    public bool ShowSaveControls => SectionOf(ActiveCategory)?.SupportsEditing == true;

    /// <summary>The header save commits every dirty section in one action.</summary>
    public bool CanSaveAll => HasDirtySections &&
                              Categories.Select(SectionOf)
                                  .OfType<ISettingsSection>()
                                  .Where(section => section.IsDirty)
                                  .All(section => section.SupportsEditing && section.CanSave) &&
                              !_isRoutingCommand;

    /// <summary>The header discard reverts every dirty section in one action.</summary>
    public bool CanDiscardAll => HasDirtySections && !_isRoutingCommand;

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

    private void SectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Header save/discard targets all dirty sections, so any section's state change matters.
        RaiseActiveSectionState();
    }

    private static ISettingsSection? SectionOf(NavCategoryViewModel category)
    {
        return category.Content as ISettingsSection;
    }
}
