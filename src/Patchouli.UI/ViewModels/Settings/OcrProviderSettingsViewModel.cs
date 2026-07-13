using Patchouli.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class OcrProviderSettingsViewModel : ViewModelBase, ISettingsSection
{
    private readonly MainWindowViewModel _main;
    private string _status = "";
    private string _token = "";
    private string _persistedToken = "";
    private bool _isDirty;

    public OcrProviderSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        SaveMinerUSettingsCommand = new AsyncCommand(SaveAsync);
        _token = "";
        _persistedToken = _token;
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
        get => _token;
        set
        {
            if (_token != value)
            {
                _token = value;
                _isDirty = true;
                Raise();
                Raise(nameof(MinerUCredentialStatus));
                Raise(nameof(IsDirty));
                Raise(nameof(CanSave));
                Status = "有未保存的更改";
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
    public bool SupportsEditing => true;
    public bool IsDirty => _isDirty;
    public bool CanSave => _isDirty;
    public string SaveStateText => Status;
    public string? LastError { get; private set; }

    public Task DiscardAsync()
    {
        _token = _persistedToken;
        _isDirty = false;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(MinerUCredentialStatus));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Status = "已放弃更改";
        return Task.CompletedTask;
    }

    internal void LoadPersistedToken(string token)
    {
        _token = token;
        _persistedToken = token;
        _isDirty = false;
        LastError = null;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(MinerUCredentialStatus));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Raise(nameof(LastError));
        Status = "已保存";
    }

    public async Task SaveAsync()
    {
        Status = "正在保存...";
        bool saved = await _main.SaveMinerUTokenSettingsAsync(_token);
        Status = saved ? "已保存" : "保存失败";
        if (saved)
        {
            _persistedToken = _token;
            _isDirty = false;
            LastError = null;
        }
        else
        {
            LastError = "无法保存 MinerU 凭据。";
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Raise(nameof(LastError));
    }
}
