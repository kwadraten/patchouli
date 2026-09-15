using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class OcrProviderSettingsViewModel : SettingsSectionViewModelBase
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
    private bool _isConstructing;
    private bool _isSyncing;

    public OcrProviderSettingsViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
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
        SyncState(_persistedToken, _persistedModelVersion, _persistedPollingTimeoutSeconds,
            _persistedDocumentOcrEngine, _persistedPageOcrEngine, _persistedRegionOcrEngine);
        _isConstructing = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinerUCredentialStatus))]
    public partial string MinerUTokenInput { get; set; } = "";

    partial void OnMinerUTokenInputChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        _token = value;
        UpdateDirtyState();
        MarkDirty("有未保存的更改");
    }

    [ExcludeFromDerivedGeneration]
    public string MinerUCredentialStatus => string.IsNullOrWhiteSpace(MinerUTokenInput)
        ? "未配置 ProviderCredential"
        : "已配置 ProviderCredential";

    [ExcludeFromDerivedGeneration] public bool HasPersistedCredential => !string.IsNullOrWhiteSpace(_persistedToken);

    public ReadOnlyCollection<string> MinerUModelVersionOptions { get; } =
        Array.AsReadOnly(["vlm", "pipeline"]);

    [ObservableProperty] public partial string MinerUModelVersion { get; set; } = "vlm";

    partial void OnMinerUModelVersionChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string normalized = NormalizeModelVersion(value);
        if (_modelVersion != normalized)
        {
            _modelVersion = normalized;
            UpdateDirtyState();
            MarkDirty("有未保存的更改");
        }
    }

    [ObservableProperty] public partial int MinerUPollingTimeoutSeconds { get; set; }

    partial void OnMinerUPollingTimeoutSecondsChanged(int value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        int clamped = Math.Max(30, Math.Min(value, 3600));
        if (_pollingTimeoutSeconds != clamped)
        {
            _pollingTimeoutSeconds = clamped;
            UpdateDirtyState();
            MarkDirty("有未保存的更改");
        }
    }

    public string OcrConcurrencySummary { get; } = "OCR 队列第一版使用本机单任务 tick 执行。";

    public string PreferredOcrProviderName => "MinerU";
    public string PreferredOcrProviderType => "云端 OCR/版面解析";

    [ExcludeFromDerivedGeneration] public ObservableCollection<OcrEngineOption> AvailableEngines { get; } = new();

    [ObservableProperty] public partial string SelectedDocumentEngine { get; set; } = "";

    partial void OnSelectedDocumentEngineChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string normalized = NormalizeEngineId(value);
        if (_documentOcrEngine != normalized)
        {
            _documentOcrEngine = normalized;
            UpdateDirtyState();
            MarkDirty("有未保存的更改");
        }
    }

    [ObservableProperty] public partial string SelectedPageEngine { get; set; } = "";

    partial void OnSelectedPageEngineChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string normalized = NormalizeEngineId(value);
        if (_pageOcrEngine != normalized)
        {
            _pageOcrEngine = normalized;
            UpdateDirtyState();
            MarkDirty("有未保存的更改");
        }
    }

    [ObservableProperty] public partial string SelectedRegionEngine { get; set; } = "";

    partial void OnSelectedRegionEngineChanged(string value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        string normalized = NormalizeEngineId(value);
        if (_regionOcrEngine != normalized)
        {
            _regionOcrEngine = normalized;
            UpdateDirtyState();
            MarkDirty("有未保存的更改");
        }
    }

    public AsyncCommand RemoveMinerUCredentialCommand { get; }
    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            HostServices services = await _main.ServicesAsync();
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
        SyncState(_persistedToken, _persistedModelVersion, _persistedPollingTimeoutSeconds,
            _persistedDocumentOcrEngine, _persistedPageOcrEngine, _persistedRegionOcrEngine);
        _isDirty = false;
        SaveState = SettingsSaveState.Clean;
        Status = "已放弃更改";
        return Task.CompletedTask;
    }

    internal void LoadPersistedToken(string token)
    {
        _persistedToken = token;
        _persistedModelVersion = NormalizeModelVersion(_main.AppOptions.MinerU.ModelVersion);
        _persistedPollingTimeoutSeconds = _main.AppOptions.MinerU.PollingTimeoutSeconds;
        LoadEnginesFromSettings(_main.AppOptions.OcrEngines);
        _persistedDocumentOcrEngine = _documentOcrEngine;
        _persistedPageOcrEngine = _pageOcrEngine;
        _persistedRegionOcrEngine = _regionOcrEngine;
        SyncState(_persistedToken, _persistedModelVersion, _persistedPollingTimeoutSeconds,
            _persistedDocumentOcrEngine, _persistedPageOcrEngine, _persistedRegionOcrEngine);
        _isDirty = false;
        LastError = null;
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
        Raise(nameof(HasPersistedCredential));
        UpdateDirtyState();
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
            SelectedDocumentEngine = _documentOcrEngine;
        }

        if (!AvailableEngines.Any(option => option.EngineId == _pageOcrEngine))
        {
            _pageOcrEngine = AvailableEngines[0].EngineId;
            SelectedPageEngine = _pageOcrEngine;
        }

        if (!AvailableEngines.Any(option => option.EngineId == _regionOcrEngine))
        {
            _regionOcrEngine = AvailableEngines[0].EngineId;
            SelectedRegionEngine = _regionOcrEngine;
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

    private void SyncState(string token, string modelVersion, int pollingTimeout, string docEngine, string pageEngine,
        string regionEngine)
    {
        _isSyncing = true;
        _token = token;
        MinerUTokenInput = token;
        _modelVersion = modelVersion;
        MinerUModelVersion = modelVersion;
        _pollingTimeoutSeconds = pollingTimeout;
        MinerUPollingTimeoutSeconds = pollingTimeout;
        _documentOcrEngine = docEngine;
        SelectedDocumentEngine = docEngine;
        _pageOcrEngine = pageEngine;
        SelectedPageEngine = pageEngine;
        _regionOcrEngine = regionEngine;
        SelectedRegionEngine = regionEngine;
        _isSyncing = false;
        UpdateDirtyState();
        Raise(nameof(HasPersistedCredential));
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
