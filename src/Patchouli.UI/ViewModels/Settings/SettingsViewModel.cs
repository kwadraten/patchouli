using Patchouli.UI.ViewModels;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private SettingsCategoryViewModel? _activeCategory;
    private string _globalStatus = "";

    public SettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        LibrarySettings = new LibrarySettingsViewModel(main);
        McpSettings = new McpSettingsViewModel(main);
        OcrProviderSettings = new OcrProviderSettingsViewModel(main);
        MetadataLookupSettings = new MetadataLookupSettingsViewModel(main);
        CslSettings = new CslSettingsViewModel(main);

        Categories = new ObservableCollection<SettingsCategoryViewModel>
        {
            new("库信息与路径", "Database", LibrarySettings),
            new("MCP 服务与安全", "Server", McpSettings),
            new("OCR 引擎", "ScanText", OcrProviderSettings),
            new("元数据来源", "Search", MetadataLookupSettings),
            new("CSL 样式", "Quote", CslSettings)
        };

        ActiveCategory = Categories.First();

        SaveCommand = new AsyncCommand(async () =>
        {
            if (ReferenceEquals(ActiveCategory?.Content, MetadataLookupSettings))
            {
                await MetadataLookupSettings.SaveAsync();
                GlobalStatus = MetadataLookupSettings.Status;
                return;
            }
            GlobalStatus = "所有设置已保存";
        });

        DiscardCommand = new AsyncCommand(async () =>
        {
            if (ReferenceEquals(ActiveCategory?.Content, MetadataLookupSettings))
                await MetadataLookupSettings.DiscardAsync();
            GlobalStatus = "已放弃当前更改";
        });
    }

    public LibrarySettingsViewModel LibrarySettings { get; }
    public McpSettingsViewModel McpSettings { get; }
    public OcrProviderSettingsViewModel OcrProviderSettings { get; }
    public MetadataLookupSettingsViewModel MetadataLookupSettings { get; }
    public CslSettingsViewModel CslSettings { get; }

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

    public SettingsCategoryViewModel? ActiveCategory
    {
        get => _activeCategory;
        set
        {
            _activeCategory = value;
            Raise();
            if (ReferenceEquals(value?.Content, McpSettings))
            {
                _ = McpSettings.LoadAsync();
            }
            if (ReferenceEquals(value?.Content, LibrarySettings))
            {
                _ = LibrarySettings.LoadFileSearchRootsAsync();
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
            if (!string.IsNullOrWhiteSpace(value)) _main.Report(value);
        }
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
}
