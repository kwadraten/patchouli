using Patchouli.UI.ViewModels;
using System;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class McpSettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private string _status = "";

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
        private set { _status = value; Raise(); }
    }

    public int Port
    {
        get => _main.AppOptions.Mcp.Port;
        set
        {
            if (_main.AppOptions.Mcp.Port != value)
            {
                var options = _main.AppOptions;
                _main.UpdateAppOptions(options with { Mcp = options.Mcp with { Port = value } });
                Raise();
                Status = "已保存 (重启服务生效)";
            }
        }
    }

    public bool AllowExternalAccess
    {
        get => !_main.AppOptions.Mcp.BlockExternalAccess;
        set
        {
            if (_main.AppOptions.Mcp.BlockExternalAccess == value)
            {
                var options = _main.AppOptions;
                _main.UpdateAppOptions(options with { Mcp = options.Mcp with { BlockExternalAccess = !value } });
                Raise();
                Raise(nameof(IsAllowExternalAccessWarningVisible));
                Status = "已保存 (重启服务生效)";
            }
        }
    }

    public bool IsAllowExternalAccessWarningVisible => AllowExternalAccess && string.IsNullOrWhiteSpace(ServerToken);

    public string ServerToken
    {
        get => _main.AppOptions.Mcp.ServerToken;
        set
        {
            if (_main.AppOptions.Mcp.ServerToken != value)
            {
                var options = _main.AppOptions;
                _main.UpdateAppOptions(options with { Mcp = options.Mcp with { ServerToken = value } });
                Raise();
                Raise(nameof(IsAllowExternalAccessWarningVisible));
                Status = "已保存 (重启服务生效)";
            }
        }
    }

    public string McpEndpoint => _main.McpEndpoint;
    public string McpStatusText => _main.McpStatusText;

    public AsyncCommand GenerateTokenCommand { get; }
    public AsyncCommand StartMcpCommand { get; }
    public AsyncCommand StopMcpCommand { get; }

    private Task GenerateTokenAsync()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace("+", "").Replace("/", "").Replace("=", "");
        ServerToken = token;
        return Task.CompletedTask;
    }

    private Task StartMcpAsync() => _main.StartMcpServerAsync();
    private Task StopMcpAsync() => _main.StopMcpServerAsync("用户手动停止");
}
