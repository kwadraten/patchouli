using Patchouli.UI.ViewModels;
using Patchouli.Core.Mcp;
using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Patchouli.Core.Results;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class McpSettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _status = "";
    private McpServerSettings _settings = new(4536, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow);

    public McpSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        GenerateTokenCommand = new AsyncCommand(GenerateTokenAsync);
        StartMcpCommand = new AsyncCommand(StartMcpAsync);
        StopMcpCommand = new AsyncCommand(StopMcpAsync);
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
                if (value.Contains("失败", StringComparison.Ordinal) || value.Contains("无法", StringComparison.Ordinal))
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

    public int Port
    {
        get => _settings.Port;
        set
        {
            if (_settings.Port != value)
            {
                _settings = _settings with { Port = value };
                Raise();
                ObserveSave();
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
            ObserveSave();
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
            ObserveSave();
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
            ObserveSave();
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
            ObserveSave();
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
            ObserveSave();
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
                ObserveSave();
            }
        }
    }

    public ObservableCollection<McpToolOverrideViewModel> ToolOverrides { get; } = new();

    public string McpEndpoint => _main.McpEndpoint;
    public string McpStatusText => _main.McpStatusText;

    public AsyncCommand GenerateTokenCommand { get; }
    public AsyncCommand StartMcpCommand { get; }
    public AsyncCommand StopMcpCommand { get; }

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

    public async Task LoadAsync()
    {
        Result<McpServerSettings> result = await (await _main.ServicesAsync()).McpSettings.GetSettingsAsync();
        if (result.IsFailure)
        {
            Status = result.ErrorMessage ?? "无法读取 MCP 设置。";
            return;
        }

        _settings = result.Value;
        ReloadToolOverrides();
        RaiseAllSettings();
        Status = "已加载数据库 MCP 设置。";
    }

    private async Task SaveAsync()
    {
        Result<McpServerSettings> result = await (await _main.ServicesAsync()).McpSettings.SaveSettingsAsync(_settings);
        if (result.IsFailure)
        {
            Status = result.ErrorMessage ?? "MCP 设置保存失败。";
            return;
        }

        _settings = result.Value;
        Status = "已保存 (重启服务生效)";
    }

    private void ObserveSave()
    {
        SaveAsync().Observe(nameof(McpSettingsViewModel), nameof(SaveAsync));
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
        ObserveSave();
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
        "search_library", "get_item_metadata", "get_document_status", "get_page_text", "get_page_blocks",
        "get_search_result_context", "list_csl_styles", "get_csl_style", "render_item_bibliography",
        "render_items_bibliography"
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
