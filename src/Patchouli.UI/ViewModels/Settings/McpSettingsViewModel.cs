using Patchouli.UI.ViewModels;
using Patchouli.Core.Mcp;
using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Patchouli.Core.Results;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class McpSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private McpServerSettings _settings = new(4536, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);

    private McpServerSettings _persistedSettings =
        new(4536, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);

    private bool _isDirty;
    private long _editRevision;
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    public McpSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.McpStatusText)
                or nameof(MainWindowViewModel.McpEndpoint)
                or nameof(MainWindowViewModel.McpServerRunning)
                or nameof(MainWindowViewModel.McpRunningSettingsRevision)
                or nameof(MainWindowViewModel.ShellSandboxStatusText))
            {
                Raise(args.PropertyName switch
                {
                    nameof(MainWindowViewModel.McpStatusText) => nameof(McpStatusText),
                    nameof(MainWindowViewModel.McpEndpoint) => nameof(McpEndpoint),
                    nameof(MainWindowViewModel.McpServerRunning) => nameof(McpServerRunning),
                    nameof(MainWindowViewModel.McpRunningSettingsRevision) => nameof(RequiresReload),
                    nameof(MainWindowViewModel.ShellSandboxStatusText) => nameof(ShellSandboxStatusText),
                    _ => args.PropertyName ?? string.Empty
                });
                RefreshRequiresReload();
            }
        };
        GenerateTokenCommand = new AsyncCommand(GenerateTokenAsync);
        StartMcpCommand = new AsyncCommand(StartMcpAsync);
        StopMcpCommand = new AsyncCommand(StopMcpAsync);
        SaveAndRestartCommand = new AsyncCommand(SaveAndRestartAsync);
        ForceRestartShellSandboxCommand = new AsyncCommand(ForceRestartShellSandboxAsync);
    }

    public int Port
    {
        get => _settings.Port;
        set
        {
            if (_settings.Port != value)
            {
                _settings = _settings with { Port = value };
                Raise();
                MarkDirty();
            }
        }
    }

    public string BindAddress
    {
        get => _settings.BindAddress;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            if (_settings.BindAddress == next)
            {
                return;
            }

            _settings = _settings with { BindAddress = next };
            Raise();
            Raise(nameof(AllowExternalAccess));
            Raise(nameof(IsAllowExternalAccessWarningVisible));
            MarkDirty();
        }
    }

    public bool AllowExternalAccess
    {
        get => string.Equals(_settings.BindAddress, "0.0.0.0", StringComparison.Ordinal);
        set
        {
            string next = value ? "0.0.0.0" : "127.0.0.1";
            if (_settings.BindAddress == next)
            {
                return;
            }

            _settings = _settings with { BindAddress = next };
            Raise();
            Raise(nameof(BindAddress));
            Raise(nameof(IsAllowExternalAccessWarningVisible));
            MarkDirty();
        }
    }

    public bool IsAllowExternalAccessWarningVisible => AllowExternalAccess && string.IsNullOrWhiteSpace(ServerToken);

    public bool CorsEnabled
    {
        get => _settings.CorsEnabled;
        set
        {
            if (_settings.CorsEnabled == value)
            {
                return;
            }

            _settings = _settings with { CorsEnabled = value };
            Raise();
            MarkDirty();
        }
    }

    public string TransportDescription => "Streamable HTTP：使用同一个 /mcp 地址，POST 发送 JSON-RPC，GET 建立 SSE。";

    public string AllowedOriginsText
    {
        get => string.Join("\n", _settings.AllowedOrigins);
        set
        {
            string[] origins = value.Split(new[] { '\r', '\n', ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (_settings.AllowedOrigins.SequenceEqual(origins, StringComparer.Ordinal))
            {
                return;
            }

            _settings = _settings with { AllowedOrigins = origins };
            Raise();
            MarkDirty();
        }
    }

    public bool AuthRequired
    {
        get => _settings.AuthRequired;
        set
        {
            if (_settings.AuthRequired == value)
            {
                return;
            }

            _settings = _settings with { AuthRequired = value };
            Raise();
            MarkDirty();
        }
    }

    public string ServerToken
    {
        get => _settings.Token ?? "";
        set
        {
            string? next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_settings.Token != next)
            {
                _settings = _settings with { Token = next };
                Raise();
                Raise(nameof(IsAllowExternalAccessWarningVisible));
                MarkDirty();
            }
        }
    }

    public ObservableCollection<McpToolOverrideViewModel> ToolOverrides { get; } = new();

    public string McpEndpoint => _main.McpEndpoint;
    public string McpStatusText => _main.McpStatusText;
    public bool McpServerRunning => _main.McpServerRunning;
    public string ShellSandboxStatusText => _main.ShellSandboxStatusText;

    public AsyncCommand GenerateTokenCommand { get; }
    public AsyncCommand StartMcpCommand { get; }
    public AsyncCommand StopMcpCommand { get; }
    public AsyncCommand SaveAndRestartCommand { get; }
    public AsyncCommand ForceRestartShellSandboxCommand { get; }
    public override bool SupportsEditing => true;
    public override bool IsDirty => _isDirty;
    public override bool CanSave => _isDirty;

    public override async Task DiscardAsync()
    {
        await _commitGate.WaitAsync();
        try
        {
            _editRevision++;
            Result<McpServerSettings> persisted =
                await (await _main.ServicesAsync()).McpSettings.GetSettingsAsync();
            if (persisted.IsSuccess)
            {
                _persistedSettings = persisted.Value;
            }

            _settings = _persistedSettings;
            _isDirty = false;
            ReloadToolOverrides();
            RaiseAllSettings();
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            SaveState = SettingsSaveState.Clean;
            LastError = null;
            RefreshRequiresReload();
            SetStatus("已放弃更改");
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private Task GenerateTokenAsync()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "").Replace("/", "")
            .Replace("=", "");
        ServerToken = token;
        return Task.CompletedTask;
    }

    private Task StartMcpAsync()
    {
        return _main.StartMcpServerAsync();
    }

    private Task StopMcpAsync()
    {
        return _main.StopMcpServerAsync("用户手动停止");
    }

    private async Task SaveAndRestartAsync()
    {
        if (IsDirty)
        {
            await SaveAsync();
        }

        if (IsDirty || SaveState == SettingsSaveState.Failed)
        {
            return;
        }

        await _main.StopMcpServerAsync("应用新设置");
        await _main.StartMcpServerAsync();
        RequiresReload = false;
    }

    private async Task ForceRestartShellSandboxAsync()
    {
        try
        {
            SetStatus("正在强制重启 Shell 沙箱…");
            await _main.ForceRestartShellSandboxAsync();
            Raise(nameof(ShellSandboxStatusText));
            Raise(nameof(McpStatusText));
            SetStatus($"Shell 沙箱状态：{ShellSandboxStatusText}");
        }
        catch (Exception ex)
        {
            SetStatus($"强制重启 Shell 沙箱失败：{ex.Message}");
        }
    }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsDirty)
        {
            SetStatus("MCP 设置有未保存的更改，已保留当前草稿。");
            return;
        }

        Result<McpServerSettings> result = await (await _main.ServicesAsync()).McpSettings.GetSettingsAsync();
        if (result.IsFailure)
        {
            LastError = result.ErrorMessage;
            SetStatus(result.ErrorMessage ?? "无法读取 MCP 设置。");
            return;
        }

        _settings = result.Value;
        _persistedSettings = result.Value;
        _isDirty = false;
        ReloadToolOverrides();
        RaiseAllSettings();
        SaveState = SettingsSaveState.Clean;
        LastError = null;
        RefreshRequiresReload();
        SetStatus("已加载数据库 MCP 设置。");
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    public override async Task SaveAsync()
    {
        await _commitGate.WaitAsync();
        try
        {
            SaveState = SettingsSaveState.Saving;
            Status = "正在保存...";
            long revision = _editRevision;
            McpServerSettings draft = _settings with
            {
                AllowedOrigins = _settings.AllowedOrigins.ToArray(),
                ToolOverrides = _settings.ToolOverrides.ToArray()
            };
            Result<McpServerSettings> result = await (await _main.ServicesAsync()).McpSettings.SaveSettingsAsync(
                draft,
                _persistedSettings.Revision);
            if (result.IsFailure)
            {
                SaveState = SettingsSaveState.Failed;
                LastError = result.ErrorMessage;
                SetStatus(result.ErrorMessage ?? "MCP 设置保存失败。");
                return;
            }

            _persistedSettings = result.Value;
            if (revision == _editRevision)
            {
                _settings = result.Value;
                _isDirty = false;
                SaveState = SettingsSaveState.Saved;
                SetStatus("已保存");
            }
            else
            {
                SaveState = SettingsSaveState.Dirty;
                SetStatus("已保存旧版本，仍有新的未保存更改");
            }

            LastError = null;
            RefreshRequiresReload();
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
        }
        finally
        {
            _commitGate.Release();
        }
    }

    internal void UpdateToolOverride(string toolName, bool enabled)
    {
        List<McpToolOverride> overrides = _settings.ToolOverrides.Where(value => value.ToolName != toolName).ToList();
        if (!enabled)
        {
            overrides.Add(new McpToolOverride(toolName, false, "Disabled in Patchouli settings."));
        }

        _settings = _settings with
        {
            ToolOverrides = overrides.OrderBy(value => value.ToolName, StringComparer.Ordinal).ToArray()
        };
        MarkDirty();
    }

    private void SetStatus(string text)
    {
        Status = text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Contains("失败", StringComparison.Ordinal) || text.Contains("无法", StringComparison.Ordinal))
        {
            _main.ReportError(text);
        }
        else
        {
            _main.Report(text);
        }
    }

    private void MarkDirty()
    {
        _editRevision++;
        _isDirty = true;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
    }

    private void RefreshRequiresReload()
    {
        RequiresReload = _main.McpRunningSettingsRevision is long runningRevision &&
                         runningRevision != _persistedSettings.Revision;
    }

    private void ReloadToolOverrides()
    {
        HashSet<string> disabled = _settings.ToolOverrides.Where(value => !value.Enabled)
            .Select(value => value.ToolName).ToHashSet(StringComparer.Ordinal);
        ToolOverrides.Clear();
        foreach (string tool in KnownTools)
        {
            ToolOverrides.Add(new McpToolOverrideViewModel(this, tool, !disabled.Contains(tool)));
        }
    }

    private void RaiseAllSettings()
    {
        foreach (string property in new[]
                 {
                     nameof(Port), nameof(BindAddress), nameof(AllowExternalAccess), nameof(CorsEnabled),
                     nameof(TransportDescription), nameof(AllowedOriginsText), nameof(AuthRequired),
                     nameof(ServerToken), nameof(IsAllowExternalAccessWarningVisible), nameof(ToolOverrides)
                 })
        {
            Raise(property);
        }
    }

    private static readonly string[] KnownTools =
    [
        "patchouli_shell"
    ];
}

public sealed class McpToolOverrideViewModel : ViewModelBase
{
    private readonly McpSettingsViewModel _parent;
    private bool _enabled;

    public McpToolOverrideViewModel(McpSettingsViewModel parent, string toolName, bool enabled)
    {
        _parent = parent;
        ToolName = toolName;
        _enabled = enabled;
    }

    public string ToolName { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Raise();
            _parent.UpdateToolOverride(ToolName, value);
        }
    }
}
