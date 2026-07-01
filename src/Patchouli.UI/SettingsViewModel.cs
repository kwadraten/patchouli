namespace Patchouli.UI;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;

    public SettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        SaveMinerUSettingsCommand = new AsyncCommand(SaveMinerUSettingsAsync);
    }

    public string SelectedSection { get; private set; } = "mineru";
    public string MinerUTokenInput { get; set; } = "";
    public string Status { get; private set; } = "在这里管理 MinerU OCR 凭据。";
    public string SettingsFilePath => _main.SettingsFilePath;
    public bool ShowMinerUSection => SelectedSection == "mineru";
    public AsyncCommand SaveMinerUSettingsCommand { get; }

    public void FocusMinerU(string? status = null)
    {
        SelectedSection = "mineru";
        if (!string.IsNullOrWhiteSpace(status))
            Status = status;
        Raise(nameof(SelectedSection));
        Raise(nameof(ShowMinerUSection));
        Raise(nameof(Status));
    }

    public void SyncFromCurrentSettings(string token)
    {
        MinerUTokenInput = token;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(SettingsFilePath));
    }

    private async Task SaveMinerUSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(MinerUTokenInput))
        {
            Status = "请先填写 MinerU API token。";
            Raise(nameof(Status));
            _main.Report(Status);
            return;
        }

        var saved = await _main.SaveMinerUTokenSettingsAsync(MinerUTokenInput);
        Status = saved
            ? "MinerU 凭据已保存到 ProviderCredential 和 appsettings。"
            : "MinerU 凭据保存失败。";
        Raise(nameof(Status));
    }
}
