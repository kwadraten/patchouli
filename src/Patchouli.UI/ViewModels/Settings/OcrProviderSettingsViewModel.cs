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
    private string _modelVersion;
    private string _persistedModelVersion;
    private bool _isDirty;

    public OcrProviderSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        SaveMinerUSettingsCommand = new AsyncCommand(SaveAsync);
        _token = "";
        _persistedToken = _token;
        _modelVersion = NormalizeModelVersion(main.AppOptions.MinerU.ModelVersion);
        _persistedModelVersion = _modelVersion;
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
                UpdateDirtyState();
                Raise();
                Raise(nameof(MinerUCredentialStatus));
                Status = "有未保存的更改";
            }
        }
    }

    public string MinerUCredentialStatus => string.IsNullOrWhiteSpace(MinerUTokenInput)
        ? "未配置 ProviderCredential"
        : "已配置 ProviderCredential";

    public ReadOnlyCollection<string> MinerUModelVersionOptions { get; } =
        Array.AsReadOnly(["vlm", "pipeline"]);

    public string MinerUModelVersion
    {
        get => _modelVersion;
        set
        {
            string normalized = NormalizeModelVersion(value);
            if (_modelVersion == normalized)
            {
                return;
            }

            _modelVersion = normalized;
            UpdateDirtyState();
            Raise();
            Status = "有未保存的更改";
        }
    }

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
        _modelVersion = _persistedModelVersion;
        _isDirty = false;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(MinerUModelVersion));
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
        _modelVersion = NormalizeModelVersion(_main.AppOptions.MinerU.ModelVersion);
        _persistedModelVersion = _modelVersion;
        _isDirty = false;
        LastError = null;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(MinerUModelVersion));
        Raise(nameof(MinerUCredentialStatus));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Raise(nameof(LastError));
        Status = "已保存";
    }

    public async Task SaveAsync()
    {
        Status = "正在保存...";
        bool saved = await _main.SaveMinerUSettingsAsync(_token, _modelVersion);
        Status = saved ? "已保存" : "保存失败";
        if (saved)
        {
            _persistedToken = _token;
            _persistedModelVersion = _modelVersion;
            _isDirty = false;
            LastError = null;
        }
        else
        {
            LastError = "无法保存 MinerU 凭据或模型设置。";
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        Raise(nameof(LastError));
    }

    private void UpdateDirtyState()
    {
        _isDirty = _token != _persistedToken || _modelVersion != _persistedModelVersion;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private static string NormalizeModelVersion(string? value)
    {
        return string.Equals(value, "pipeline", StringComparison.OrdinalIgnoreCase) ? "pipeline" : "vlm";
    }
}
