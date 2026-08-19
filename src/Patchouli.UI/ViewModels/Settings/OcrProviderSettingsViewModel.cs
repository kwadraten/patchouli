using System.Collections.ObjectModel;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class OcrProviderSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _token = "";
    private string _persistedToken = "";
    private string _modelVersion;
    private string _persistedModelVersion;
    private int _pollingTimeoutSeconds;
    private int _persistedPollingTimeoutSeconds;
    private string _documentOcrEngine = "";
    private string _persistedDocumentOcrEngine = "";
    private string _pageOcrEngine = "";
    private string _persistedPageOcrEngine = "";
    private string _regionOcrEngine = "";
    private string _persistedRegionOcrEngine = "";
    private bool _isDirty;

    public OcrProviderSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        RemoveMinerUCredentialCommand = new AsyncCommand(RemoveMinerUCredentialAsync);
        _token = "";
        _persistedToken = _token;
        _modelVersion = NormalizeModelVersion(main.AppOptions.MinerU.ModelVersion);
        _persistedModelVersion = _modelVersion;
        _pollingTimeoutSeconds = main.AppOptions.MinerU.PollingTimeoutSeconds;
        _persistedPollingTimeoutSeconds = _pollingTimeoutSeconds;
        LoadEnginesFromSettings(main.AppOptions.OcrEngines);
        _persistedDocumentOcrEngine = _documentOcrEngine;
        _persistedPageOcrEngine = _pageOcrEngine;
        _persistedRegionOcrEngine = _regionOcrEngine;
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

    public int MinerUPollingTimeoutSeconds
    {
        get => _pollingTimeoutSeconds;
        set
        {
            int clamped = Math.Max(30, Math.Min(value, 3600));
            if (_pollingTimeoutSeconds == clamped)
            {
                return;
            }

            _pollingTimeoutSeconds = clamped;
            UpdateDirtyState();
            Raise();
            MarkDirty("有未保存的更改");
        }
    }

    public string OcrConcurrencySummary { get; } = "OCR 队列第一版使用本机单任务 tick 执行。";

    public string PreferredOcrProviderName => "MinerU";
    public string PreferredOcrProviderType => "云端 OCR/版面解析";

    public ObservableCollection<OcrEngineOption> AvailableEngines { get; } = new();

    public string SelectedDocumentEngine
    {
        get => _documentOcrEngine;
        set
        {
            string normalized = NormalizeEngineId(value);
            if (_documentOcrEngine == normalized)
            {
                return;
            }

            _documentOcrEngine = normalized;
            UpdateDirtyState();
            Raise();
            MarkDirty("有未保存的更改");
        }
    }

    public string SelectedPageEngine
    {
        get => _pageOcrEngine;
        set
        {
            string normalized = NormalizeEngineId(value);
            if (_pageOcrEngine == normalized)
            {
                return;
            }

            _pageOcrEngine = normalized;
            UpdateDirtyState();
            Raise();
            MarkDirty("有未保存的更改");
        }
    }

    public string SelectedRegionEngine
    {
        get => _regionOcrEngine;
        set
        {
            string normalized = NormalizeEngineId(value);
            if (_regionOcrEngine == normalized)
            {
                return;
            }

            _regionOcrEngine = normalized;
            UpdateDirtyState();
            Raise();
            MarkDirty("有未保存的更改");
        }
    }

    public AsyncCommand RemoveMinerUCredentialCommand { get; }
    public override bool SupportsEditing => true;
    public override bool IsDirty => _isDirty;
    public override bool CanSave => _isDirty;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AppServices services = await _main.ServicesAsync();
            IReadOnlyList<OcrEngineCapability> capabilities = services.OcrAdapters.ListCapabilities();
            AvailableEngines.Clear();
            foreach (OcrEngineCapability capability in capabilities)
            {
                AvailableEngines.Add(new OcrEngineOption(capability.EngineId, capability.DisplayName));
            }

            if (AvailableEngines.Count == 0)
            {
                Status = "未注册任何 OCR 引擎。";
                return;
            }

            EnsureSelectionInAvailableEngines();
            Status = "OCR 引擎列表已加载。";
        }
        catch (Exception exception)
        {
            Status = $"加载 OCR 引擎列表失败：{exception.Message}";
        }
    }

    public override Task DiscardAsync()
    {
        _token = _persistedToken;
        _modelVersion = _persistedModelVersion;
        _pollingTimeoutSeconds = _persistedPollingTimeoutSeconds;
        _documentOcrEngine = _persistedDocumentOcrEngine;
        _pageOcrEngine = _persistedPageOcrEngine;
        _regionOcrEngine = _persistedRegionOcrEngine;
        _isDirty = false;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(MinerUModelVersion));
        Raise(nameof(MinerUPollingTimeoutSeconds));
        Raise(nameof(MinerUCredentialStatus));
        Raise(nameof(SelectedDocumentEngine));
        Raise(nameof(SelectedPageEngine));
        Raise(nameof(SelectedRegionEngine));
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
        _pollingTimeoutSeconds = _main.AppOptions.MinerU.PollingTimeoutSeconds;
        _persistedPollingTimeoutSeconds = _pollingTimeoutSeconds;
        LoadEnginesFromSettings(_main.AppOptions.OcrEngines);
        _persistedDocumentOcrEngine = _documentOcrEngine;
        _persistedPageOcrEngine = _pageOcrEngine;
        _persistedRegionOcrEngine = _regionOcrEngine;
        _isDirty = false;
        LastError = null;
        Raise(nameof(MinerUTokenInput));
        Raise(nameof(MinerUModelVersion));
        Raise(nameof(MinerUPollingTimeoutSeconds));
        Raise(nameof(MinerUCredentialStatus));
        Raise(nameof(HasPersistedCredential));
        Raise(nameof(SelectedDocumentEngine));
        Raise(nameof(SelectedPageEngine));
        Raise(nameof(SelectedRegionEngine));
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Saved;
        Status = "已保存";
    }

    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        string pendingDocumentEngine = _documentOcrEngine;
        string pendingPageEngine = _pageOcrEngine;
        string pendingRegionEngine = _regionOcrEngine;

        bool minerUSaved = string.IsNullOrWhiteSpace(_token)
            ? await _main.SaveMinerUModelSettingsAsync(_modelVersion, _pollingTimeoutSeconds)
            : await _main.SaveMinerUSettingsAsync(_token, _modelVersion, _pollingTimeoutSeconds);
        if (!minerUSaved)
        {
            LastError = "无法保存 MinerU 模型设置。";
            SaveState = SettingsSaveState.Failed;
            Status = "保存失败";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            return;
        }

        _documentOcrEngine = pendingDocumentEngine;
        _pageOcrEngine = pendingPageEngine;
        _regionOcrEngine = pendingRegionEngine;

        OcrEnginesAppSettings engines = new(
            NormalizeEngineId(SelectedDocumentEngine),
            NormalizeEngineId(SelectedPageEngine),
            NormalizeEngineId(SelectedRegionEngine));
        bool enginesSaved = await _main.SaveOcrEngineSettingsAsync(engines);
        if (!enginesSaved)
        {
            LastError = "无法保存 OCR 引擎选择。";
            SaveState = SettingsSaveState.Failed;
            Status = "保存失败";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            return;
        }

        _persistedToken = _token;
        _persistedModelVersion = _modelVersion;
        _persistedPollingTimeoutSeconds = _pollingTimeoutSeconds;
        _persistedDocumentOcrEngine = _documentOcrEngine;
        _persistedPageOcrEngine = _pageOcrEngine;
        _persistedRegionOcrEngine = _regionOcrEngine;
        _isDirty = false;
        LastError = null;
        SaveState = SettingsSaveState.Saved;
        ValidationState = SettingsValidationState.Valid;
        Status = "已保存";
        Raise(nameof(SelectedDocumentEngine));
        Raise(nameof(SelectedPageEngine));
        Raise(nameof(SelectedRegionEngine));
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

    private void LoadEnginesFromSettings(OcrEnginesAppSettings engines)
    {
        _documentOcrEngine = NormalizeEngineId(engines.DocumentOcrEngine);
        _pageOcrEngine = NormalizeEngineId(engines.PageOcrEngine);
        _regionOcrEngine = NormalizeEngineId(engines.RegionOcrEngine);
    }

    private void EnsureSelectionInAvailableEngines()
    {
        if (AvailableEngines.Count == 0)
        {
            return;
        }

        if (!AvailableEngines.Any(option => option.EngineId == _documentOcrEngine))
        {
            _documentOcrEngine = AvailableEngines[0].EngineId;
        }

        if (!AvailableEngines.Any(option => option.EngineId == _pageOcrEngine))
        {
            _pageOcrEngine = AvailableEngines[0].EngineId;
        }

        if (!AvailableEngines.Any(option => option.EngineId == _regionOcrEngine))
        {
            _regionOcrEngine = AvailableEngines[0].EngineId;
        }
    }

    private void UpdateDirtyState()
    {
        _isDirty = _token != _persistedToken || _modelVersion != _persistedModelVersion
                                             || _pollingTimeoutSeconds != _persistedPollingTimeoutSeconds
                                             || _documentOcrEngine != _persistedDocumentOcrEngine
                                             || _pageOcrEngine != _persistedPageOcrEngine
                                             || _regionOcrEngine != _persistedRegionOcrEngine;
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

    private static string NormalizeEngineId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
    }
}

public sealed class OcrEngineOption
{
    public OcrEngineOption(string engineId, string displayName)
    {
        EngineId = engineId;
        DisplayName = displayName;
    }

    public string EngineId { get; }
    public string DisplayName { get; }
}
