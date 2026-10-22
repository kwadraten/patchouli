using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Patchouli.Core.CitationInterop;
using Patchouli.Host.Composition;
using Patchouli.UI;

namespace Patchouli.Host.CitationInterop;

/// <summary>Lifecycle state of the loopback citation interop service.</summary>
public enum CitationInteropHostStatus
{
    Stopped,
    Starting,
    Running,
    Error
}

/// <summary>Observable status snapshot for the settings UI.</summary>
public sealed record CitationInteropHostStatusSnapshot(
    CitationInteropHostStatus Status,
    string Detail,
    string? Endpoint)
{
    public bool IsRunning => Status == CitationInteropHostStatus.Running;
}

/// <summary>
/// Owns the citation interop service for one library session (issue #14 §4/§5,
/// ADR 0024 single-Host rule): created from the current
/// <see cref="HostServices"/>, stopped on library switch and shutdown, which
/// cancels in-flight picks (Kestrel aborts the connections) and frees the
/// port. Disabled by default; a failed bind never takes down MCP or the
/// library — it lands in <see cref="CitationInteropHostStatus.Error"/> with a
/// diagnosable detail the settings page shows verbatim.
/// </summary>
public sealed class CitationInteropHost : IAsyncDisposable
{
    private readonly HostServices _services;
    private readonly ICitationDesktopBridge _bridge;
    private readonly Action<Exception, string, string?> _reportUnexpectedException;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CitationInteropHttpServer? _server;
    private bool _disposed;

    public CitationInteropHost(HostServices services, ICitationDesktopBridge bridge,
        Action<Exception, string, string?> reportUnexpectedException)
    {
        _services = services;
        _bridge = bridge;
        _reportUnexpectedException = reportUnexpectedException;
    }

    public CitationInteropHostStatusSnapshot Status { get; private set; } =
        new(CitationInteropHostStatus.Stopped, "兼容服务未启动。", null);

    public bool IsRunning => Status.IsRunning;

    /// <summary>The configured endpoint (also shown while stopped/errored so the
    /// settings page can explain what would be contacted).</summary>
    public string? Endpoint => Status.Endpoint;

    public event EventHandler? StatusChanged;

    /// <summary>Builds the domain service bound to this host's library.</summary>
    public CitationInteropService CreateService()
    {
        return new CitationInteropService(
            _services.LibraryItems,
            _services.Items,
            _services.CslItemMapper,
            _services.CslRenderer,
            _services.BiblatexImport);
    }

    public async Task StartAsync(CitationInteropAppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_server is not null)
            {
                return;
            }

            if (!settings.Enabled)
            {
                SetStatus(CitationInteropHostStatus.Stopped,
                    "CAYW / TexPile 兼容服务已在设置中关闭。", null);
                return;
            }

            int port = CitationInteropAppSettings.NormalizePort(settings.Port);
            SetStatus(CitationInteropHostStatus.Starting, "正在启动兼容服务…", settings.Endpoint);

            CitationInteropService service = CreateService();
            Action<Exception, string> report = (exception, context) =>
                _reportUnexpectedException(exception, "citation-interop", context);
            CaywRequestHandler cayw = new(service, _bridge, reportUnexpected: report);
            JsonRpcHandler rpc = new(service);
            CitationInteropHttpServer server = new(cayw, rpc, report);
            try
            {
                await server.StartAsync(port, cancellationToken);
            }
            catch (Exception exception)
            {
                await server.DisposeAsync();
                SetStatus(CitationInteropHostStatus.Error, DescribeStartFailure(exception, port), settings.Endpoint);
                return;
            }

            _server = server;
            SetStatus(CitationInteropHostStatus.Running,
                _bridge.IsInteractiveAvailable
                    ? "兼容服务运行中：CAYW 选择器与 TexPile JSON-RPC 均可用。"
                    : "兼容服务运行中（headless）：TexPile JSON-RPC 可用，交互式 CAYW 返回 ui_unavailable。",
                settings.Endpoint);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(string detail = "兼容服务已停止。")
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            CitationInteropHttpServer? server = _server;
            _server = null;
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            SetStatus(CitationInteropHostStatus.Stopped, detail, null);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CitationInteropAppSettings settings,
        CancellationToken cancellationToken = default)
    {
        await StopAsync("正在重启兼容服务…");
        await StartAsync(settings, cancellationToken);
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync("应用正在退出，兼容服务已停止。");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ShutdownAsync();
        _lifecycleGate.Dispose();
    }

    private static string DescribeStartFailure(Exception exception, int port)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } or
                AddressInUseException)
            {
                return $"端口 {port} 已被占用，兼容服务无法启动。常见原因是 Zotero（Better BibTeX）正在使用同一端口：" +
                       "请关闭 Zotero，或在设置中为 Patchouli 改用其他端口（同时更新编辑器/TexPile 的连接配置）。";
            }
        }

        return $"兼容服务启动失败（{exception.GetType().Name}）。请检查端口 {port} 是否可用；详细原因已写入应用诊断日志。";
    }

    private void SetStatus(CitationInteropHostStatus status, string detail, string? endpoint)
    {
        Status = new CitationInteropHostStatusSnapshot(status, detail, endpoint);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
