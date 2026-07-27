using Patchouli.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class OcrProviderSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _token = "";
    private string _persistedToken = "";
    private string _modelVersion;
    private string _persistedModelVersion;
    private bool _isDirty;

    public OcrProviderSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        RemoveMinerUCredentialCommand = new AsyncCommand(RemoveMinerUCredentialAsync);
        _token = "";
        _persistedToken = _token;
        _modelVersion = NormalizeModelVersion(main.AppOptions.MinerU.ModelVersion);
        _persistedModelVersion = _modelVersion;
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
                MarkDirty("有未保存的更改");
            }
        }
    }

    public string MinerUCredentialStatus => string.IsNullOrWhiteSpace(MinerUTokenInput)
        ? "未配置 ProviderCredential"
        : "已配置 ProviderCredential";

    public bool HasPersistedCredential => !string.IsNullOrWhiteSpace(_persistedToken);

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
            MarkDirty("有未保存的更改");
        }
    }

    public string OcrConcurrencySummary { get; } = "OCR 队列第一版使用本机单任务 tick 执行。";

    public string PreferredOcrProviderName => "MinerU";
    public string PreferredOcrProviderType => "云端 OCR/版面解析";

    public AsyncCommand RemoveMinerUCredentialCommand { get; }
    public override bool SupportsEditing => true;
    public override bool IsDirty => _isDirty;
    public override bool CanSave => _isDirty;

    public override Task DiscardAsync()
    {
        _token = _persistedToken;
        _modelVersion = _persistedModelVersion;
        _isDirty = false;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(MinerUModelVersion));
        Raise(nameof(MinerUCredentialStatus));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Clean;
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
        Raise(nameof(HasPersistedCredential));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Saved;
        Status = "已保存";
    }

    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        bool saved = await _main.SaveMinerUSettingsAsync(_token, _modelVersion);
        if (saved)
        {
            _persistedToken = _token;
            _persistedModelVersion = _modelVersion;
            _isDirty = false;
            LastError = null;
            SaveState = SettingsSaveState.Saved;
            ValidationState = SettingsValidationState.Valid;
            Status = "已保存";
        }
        else
        {
            LastError = "无法保存 MinerU 凭据或模型设置。";
            SaveState = SettingsSaveState.Failed;
            Status = "保存失败";
        }

        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private async Task RemoveMinerUCredentialAsync()
    {
        if (!HasPersistedCredential)
        {
            return;
        }

        bool removed = await _main.RemoveMinerUCredentialAsync();
        if (!removed)
        {
            SaveState = SettingsSaveState.Failed;
            Status = "移除凭据失败";
        }
    }

    private void UpdateDirtyState()
    {
        _isDirty = _token != _persistedToken || _modelVersion != _persistedModelVersion;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void MarkDirty(string message)
    {
        SaveState = SettingsSaveState.Dirty;
        Status = message;
    }

    private static string NormalizeModelVersion(string? value)
    {
        return string.Equals(value, "pipeline", StringComparison.OrdinalIgnoreCase) ? "pipeline" : "vlm";
    }
}
