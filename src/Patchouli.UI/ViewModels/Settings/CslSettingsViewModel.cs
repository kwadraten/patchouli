using Patchouli.UI.ViewModels;
namespace Patchouli.UI.ViewModels.Settings;

public sealed class CslSettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _status = "";

    public CslSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        OpenManagerCommand = new AsyncCommand(OpenManagerAsync);
    }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
            if (!string.IsNullOrWhiteSpace(value)) _main.Report(value);
        }
    }
    
    public string Description => "管理 CSL 样式索引、安装本地样式、设置默认样式，并用于题录复制和预览。";
    public AsyncCommand OpenManagerCommand { get; }

    private async Task OpenManagerAsync()
    {
        Status = "正在打开 CSL 样式管理器...";
        await _main.OpenCslStyleManagerCommand.ExecuteAsync();
        Status = "已打开 CSL 样式管理器。";
    }
}
