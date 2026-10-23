using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// 「引用与题录 → Cite As You Write」设置子页（issue #14 §5；用户指示：
/// 全部 CAYW 设置集中于此页）。设备本地设置，修改后立即持久化并重启兼容
/// 服务；端口冲突等启动失败显示在「服务状态」区，不静默换端口。设置合并
/// 只触及 CitationInterop 一节，不覆盖 MCP 等其他节。
/// </summary>
public sealed partial class CaywSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isConstructing = true;
    private bool _isSyncing;

    public CaywSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        RestartServiceCommand = new AsyncCommand(RestartServiceAsync);
        Register(System.Reactive.Linq.Observable
            .FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                handler => _main.PropertyChanged += handler,
                handler => _main.PropertyChanged -= handler)
            .Subscribe(_ => RaiseServiceStatus()));
        SyncFromSettings(_main.SettingsStore.Current.CitationInterop);
        _isConstructing = false;
    }

    [ObservableProperty] public partial bool Enabled { get; set; }

    [ObservableProperty] public partial int Port { get; set; } = CitationInteropAppSettings.DefaultPort;

    public AsyncCommand RestartServiceCommand { get; }

    /// <summary>The endpoint implied by the current port input.</summary>
    [ExcludeFromDerivedGeneration]
    public string Endpoint => new CitationInteropAppSettings(Enabled, Port).Endpoint;

    [ExcludeFromDerivedGeneration] public string ServiceStatusText => _main.CitationInteropStatusText;

    [ExcludeFromDerivedGeneration] public string ServiceStatusDetail => _main.CitationInteropStatusDetail;

    [ExcludeFromDerivedGeneration] public string RunningEndpoint => _main.CitationInteropEndpoint;

    [ExcludeFromDerivedGeneration] public bool ServiceRunning => _main.CitationInteropRunning;

    public static string SupportedText =>
        "支持：GET /better-bibtex/cayw（probe、natbib/latex/cite/citep/citet/biblatex、pandoc、typst、json、" +
        "formatted-citation、formatted-bibliography、selected、clipboard、select、minimize）；" +
        "POST /better-bibtex/json-rpc（item.search、item.export，含无 id 请求）。";

    public static string UnsupportedText =>
        "不支持（返回明确错误，不静默降级）：POST 有状态选择器、Eta、scannable-cite、TeXstudio push (texstudio)、" +
        "mmd、jupyter、asciidoctor-bibtex、完整 translate 集合。这是一个常用 CAYW 兼容子集，并非完整的 Better BibTeX 兼容。";

    partial void OnEnabledChanged(bool value)
    {
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        SaveNowAsync().Observe("settings", "save-cayw-settings");
    }

    partial void OnPortChanged(int value)
    {
        Raise(nameof(Endpoint));
        if (_isConstructing || _isSyncing)
        {
            return;
        }

        SaveNowAsync().Observe("settings", "save-cayw-settings");
    }

    public override bool SupportsEditing => true;

    public override bool IsDirty => false;

    public override bool CanSave => false;

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        SyncFromSettings(_main.SettingsStore.Current.CitationInterop);
        RaiseServiceStatus();
        SetStatus(string.Empty);
        return Task.CompletedTask;
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return LoadAsync();
    }

    private void SyncFromSettings(CitationInteropAppSettings settings)
    {
        _isSyncing = true;
        try
        {
            Enabled = settings.Enabled;
            Port = CitationInteropAppSettings.NormalizePort(settings.Port);
        }
        finally
        {
            _isSyncing = false;
        }

        Raise(nameof(Endpoint));
    }

    private void RaiseServiceStatus()
    {
        Raise(nameof(ServiceStatusText));
        Raise(nameof(ServiceStatusDetail));
        Raise(nameof(RunningEndpoint));
        Raise(nameof(ServiceRunning));
    }

    private async Task RestartServiceAsync()
    {
        await _main.RestartCitationInteropAsync();
        RaiseServiceStatus();
    }

    private async Task SaveNowAsync()
    {
        if (Port is < 1 or > 65535)
        {
            ValidationState = SettingsValidationState.Invalid;
            LastError = "端口必须在 1–65535 之间。";
            SetStatus($"保存失败：{LastError}");
            return;
        }

        ValidationState = SettingsValidationState.Valid;
        SaveState = SettingsSaveState.Saving;
        SetStatus("正在保存…");
        try
        {
            CitationInteropAppSettings updated = new(Enabled, Port);
            SettingsSaveResult saved = await _main.UpdateAppOptionsAsync(
                _main.SettingsStore.Current with { CitationInterop = updated }, "CitationInterop");
            if (!saved.IsSuccess)
            {
                LastError = saved.ErrorMessage ?? "保存失败。";
                SaveState = SettingsSaveState.Failed;
                SetStatus($"保存失败：{LastError}");
                return;
            }

            LastError = null;
            SaveState = SettingsSaveState.Saved;
            SetStatus("已保存，正在按新设置重启兼容服务…");
            await _main.RestartCitationInteropAsync();
            RaiseServiceStatus();
            SetStatus(_main.CitationInteropRunning || !Enabled
                ? "已保存。"
                : $"已保存，但服务未运行：{_main.CitationInteropStatusDetail}");
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            SaveState = SettingsSaveState.Failed;
            SetStatus($"保存失败：{exception.Message}");
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
    }
}
