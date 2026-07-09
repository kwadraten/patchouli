using Patchouli.UI.ViewModels;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class SettingsViewModel : ViewModelBase
{
    private SettingsCategoryViewModel? _activeCategory;
    private string _globalStatus = "";

    public SettingsViewModel(MainWindowViewModel main)
    {
        LibrarySettings = new LibrarySettingsViewModel(main);
        McpSettings = new McpSettingsViewModel(main);
        OcrProviderSettings = new OcrProviderSettingsViewModel(main);
        CslSettings = new CslSettingsViewModel(main);

        Categories = new ObservableCollection<SettingsCategoryViewModel>
        {
            new("库信息与路径", "Database", LibrarySettings),
            new("MCP 服务与安全", "Server", McpSettings),
            new("OCR 引擎", "ScanText", OcrProviderSettings),
            new("CSL 样式", "Quote", CslSettings)
        };

        ActiveCategory = Categories.First();

        SaveCommand = new AsyncCommand(async () =>
        {
            GlobalStatus = "正在保存设置...";
            await Task.Delay(300); // Simulate some work
            GlobalStatus = "所有设置已保存";
        });

        DiscardCommand = new AsyncCommand(async () =>
        {
            GlobalStatus = "已放弃当前更改";
            await Task.CompletedTask;
        });
    }

    public LibrarySettingsViewModel LibrarySettings { get; }
    public McpSettingsViewModel McpSettings { get; }
    public OcrProviderSettingsViewModel OcrProviderSettings { get; }
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
        set { _activeCategory = value; Raise(); }
    }

    public string GlobalStatus
    {
        get => _globalStatus;
        set { _globalStatus = value; Raise(); }
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
}
