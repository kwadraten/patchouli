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

    public string MinerUTokenInput
    {
        get => OcrProviderSettings.MinerUTokenInput;
        set
        {
            OcrProviderSettings.MinerUTokenInput = value;
            Raise();
            Raise(nameof(MinerUCredentialStatus));
        }
    }

    public string MinerUCredentialStatus => OcrProviderSettings.MinerUCredentialStatus;
    public AsyncCommand SaveMinerUSettingsCommand => OcrProviderSettings.SaveMinerUSettingsCommand;

    public ObservableCollection<SettingsCategoryViewModel> Categories { get; }

    public SettingsCategoryViewModel ActiveCategory
    {
        get => _activeCategory;
        set
        {
            if (_activeCategory is not null && _activeCategory.Section?.IsDirty == true && !ReferenceEquals(_activeCategory, value))
            {
                _activeCategory.Section.DiscardAsync().Observe(nameof(SettingsViewModel), nameof(ISettingsSection.DiscardAsync));
            }
            _activeCategory = value;
            Raise();
            RaiseActiveSectionState();
            if (ReferenceEquals(value.Content, McpSettings))
            {
                McpSettings.LoadAsync().Observe(nameof(SettingsViewModel), nameof(McpSettings.LoadAsync));
            }

            if (ReferenceEquals(value.Content, LibrarySettings))
            {
                LibrarySettings.LoadFileSearchRootsAsync().Observe(nameof(SettingsViewModel),
                    nameof(LibrarySettings.LoadFileSearchRootsAsync));
            }
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
