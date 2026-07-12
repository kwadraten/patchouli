using Patchouli.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class OcrProviderSettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _status = "";

    public OcrProviderSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        SaveMinerUSettingsCommand = new AsyncCommand(SaveMinerUSettingsAsync);
    }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (value.Contains("失败", StringComparison.Ordinal))
                {
                    _main.ReportError(value);
                }
                else
                {
                    _main.Report(value);
                }
            }
        }
    }

    public string MinerUTokenInput
    {
        get => _main.AppOptions.MinerU.Token;
        set
        {
            if (_main.AppOptions.MinerU.Token != value)
            {
                PatchouliAppSettings options = _main.AppOptions;
                SettingsSaveResult saved =
                    _main.UpdateAppOptions(options with { MinerU = options.MinerU with { Token = value } });
                if (saved.IsSuccess)
                {
                    Raise();
                    Raise(nameof(MinerUCredentialStatus));
                }
                else
                {
                    Status = $"保存失败：{saved.ErrorMessage}";
                }
            }
        }
    }

    public string MinerUCredentialStatus => string.IsNullOrWhiteSpace(MinerUTokenInput)
        ? "未配置 ProviderCredential"
        : "已配置 ProviderCredential";

    public string OcrConcurrencySummary { get; } = "OCR 队列第一版使用本机单任务 tick 执行。";

    public string PreferredOcrProviderName => "MinerU";
    public string PreferredOcrProviderType => "云端 OCR/版面解析";

    public AsyncCommand SaveMinerUSettingsCommand { get; }

    private async Task SaveMinerUSettingsAsync()
    {
        bool saved = await _main.SaveMinerUTokenSettingsAsync(MinerUTokenInput);
        Status = saved ? "已保存" : "保存失败";
    }
}
