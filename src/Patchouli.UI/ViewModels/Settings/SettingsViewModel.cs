using Patchouli.UI.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private SettingsCategoryViewModel _activeCategory = null!;
    private string _globalStatus = "";
    private bool _isRoutingCommand;

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

        Categories = new ObservableCollection<SettingsCategoryViewModel>
        {
            new("库与本机路径", "Database", LibrarySettings),
            new("同步与快照", "Cloud", SyncSettings),
            new("MCP 服务与安全", "Server", McpSettings),
            new("OCR 引擎", "ScanText", OcrProviderSettings),
            new("元数据来源", "Search", MetadataLookupSettings)
        };

        ActiveCategory = Categories.First();

        SaveCommand = new AsyncCommand(SaveActiveSectionAsync);

        DiscardCommand = new AsyncCommand(DiscardActiveSectionAsync);
    }

    public LibrarySettingsViewModel LibrarySettings { get; }
    public McpSettingsViewModel McpSettings { get; }
    public OcrProviderSettingsViewModel OcrProviderSettings { get; }
    public MetadataLookupSettingsViewModel MetadataLookupSettings { get; }
    public SyncSettingsViewModel SyncSettings { get; }

    public ObservableCollection<SettingsCategoryViewModel> Categories { get; }

    public SettingsCategoryViewModel ActiveCategory
    {
        get => _activeCategory;
        set
        {
            if (_activeCategory is not null && _activeCategory.Section?.IsDirty == true &&
                !ReferenceEquals(_activeCategory, value))
            {
                GlobalStatus = "当前设置分组有未保存的更改；请先保存或放弃更改。";
                Raise(nameof(ActiveCategory));
                RaiseActiveSectionState();
                return;
            }

            _activeCategory = value;
            Raise();
            RaiseActiveSectionState();
            value.Section?.LoadAsync().Observe(nameof(SettingsViewModel), nameof(ISettingsSection.LoadAsync));
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
    public bool HasDirtySections => Categories.Any(category => category.Section?.IsDirty == true);

    public bool ShowSaveControls => ActiveCategory.Section?.SupportsEditing == true;

    public bool CanSaveActiveSection => ActiveCategory.Section?.SupportsEditing == true &&
                                        ActiveCategory.Section.CanSave && !_isRoutingCommand;

    public bool CanDiscardActiveSection => ActiveCategory.Section?.SupportsEditing == true &&
                                           ActiveCategory.Section.IsDirty && !_isRoutingCommand;

    public bool IsActiveSectionDirty => ActiveCategory.Section?.IsDirty == true;
    public string ActiveSaveStateText => ActiveCategory.Section?.SaveStateText ?? "无需保存";
    public string ActiveLastError => ActiveCategory.Section?.LastError ?? "";
    public string ActiveValidationStateText => ActiveCategory.Section?.ValidationState.ToString() ?? "Unknown";
    public bool ActiveRequiresReload => ActiveCategory.Section?.RequiresReload == true;
    public string ActiveScopeText => ActiveCategory.Section?.ScopeText ?? "";
    public string ActiveEffectiveSourceText => ActiveCategory.Section?.EffectiveSourceText ?? "";
    public bool ActiveHasLastError => !string.IsNullOrWhiteSpace(ActiveCategory.Section?.LastError);
    public bool ActiveSaveFailed => ActiveCategory.Section?.SaveState == SettingsSaveState.Failed;
    public bool ActiveSaved => ActiveCategory.Section?.SaveState == SettingsSaveState.Saved;

    public async Task<bool> SaveAllDirtySectionsAsync()
    {
        foreach (SettingsCategoryViewModel category in Categories)
        {
            ISettingsSection? section = category.Section;
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
        }

        Raise(nameof(HasDirtySections));
        RaiseActiveSectionState();
        return true;
    }

    private async Task SaveActiveSectionAsync()
    {
        ISettingsSection? section = ActiveCategory.Section;
        if (section is null || !section.SupportsEditing || !section.CanSave)
        {
            return;
        }

        _isRoutingCommand = true;
        RaiseActiveSectionState();
        try
        {
            await section.SaveAsync();
            GlobalStatus = section.SaveStateText;
        }
        finally
        {
            _isRoutingCommand = false;
            RaiseActiveSectionState();
        }
    }

    private async Task DiscardActiveSectionAsync()
    {
        ISettingsSection? section = ActiveCategory.Section;
        if (section is null || !section.SupportsEditing || !section.IsDirty)
        {
            return;
        }

        _isRoutingCommand = true;
        RaiseActiveSectionState();
        try
        {
            await section.DiscardAsync();
            GlobalStatus = section.SaveStateText;
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
        Raise(nameof(CanSaveActiveSection));
        Raise(nameof(CanDiscardActiveSection));
        Raise(nameof(IsActiveSectionDirty));
        Raise(nameof(ActiveSaveStateText));
        Raise(nameof(ActiveLastError));
        Raise(nameof(ActiveValidationStateText));
        Raise(nameof(ActiveRequiresReload));
        Raise(nameof(ActiveScopeText));
        Raise(nameof(ActiveEffectiveSourceText));
        Raise(nameof(ActiveHasLastError));
        Raise(nameof(ActiveSaveFailed));
        Raise(nameof(ActiveSaved));
        Raise(nameof(HasDirtySections));
    }

    private void SectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveCategory.Section))
        {
            RaiseActiveSectionState();
        }
    }
}
