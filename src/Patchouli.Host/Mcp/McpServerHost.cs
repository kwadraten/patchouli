using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Mcp;
using Patchouli.McpServer;

namespace Patchouli.Host.Mcp;

public enum McpServerHostStatus
{
    Stopped,
    Starting,
    Running,
    Error
}

public sealed class McpServerHostStatusChangedEventArgs(McpServerHostStatus status, string detail) : EventArgs
{
    public McpServerHostStatus Status { get; } = status;
    public string Detail { get; } = detail;
}

public sealed class McpServerHostExceptionEventArgs(Exception exception, string operation) : EventArgs
{
    public Exception Exception { get; } = exception;
    public string Operation { get; } = operation;
}

/// <summary>
/// Owns the MCP HTTP server lifecycle that used to live inline in <c>MainWindowViewModel</c>:
/// settings load/validation, handler/server construction, background auto-start, stop/restart,
/// and shutdown. UI concerns are surfaced through events instead of direct ViewModel/Dispatcher use.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly HostServices _services;
    private readonly Action<Exception, string, string?> _reportUnexpectedException;
    private McpHttpServer? _server;
    private Task? _backgroundStartTask;

    public McpServerHost(HostServices services, Action<Exception, string, string?> reportUnexpectedException)
    {
        _services = services;
        _reportUnexpectedException = reportUnexpectedException;
    }

    public McpHttpServer? Server => _server;
    public bool IsRunning => _server?.IsRunning == true;
    public long? RunningSettingsRevision { get; private set; }
    public string Endpoint { get; private set; } = $"http://localhost:{McpHttpServer.DefaultPort}/mcp";
    public McpServerHostStatus Status { get; private set; } = McpServerHostStatus.Stopped;
    public string StatusDetail { get; private set; } = "MCP HTTP 服务未启动。";

    public event EventHandler<McpServerHostStatusChangedEventArgs>? StatusChanged;
    public event EventHandler? ConnectionCountsChanged;
    public event EventHandler<McpServerHostExceptionEventArgs>? ExceptionReported;

    /// <summary>
    /// Starts the server on the current context unless a background start is already in flight.
    /// </summary>
    public void StartInBackground()
    {
        if (_backgroundStartTask is { IsCompleted: false })
        {
            return;
        }

        _backgroundStartTask = StartInBackgroundAsync();
    }

    /// <summary>
    /// Desktop path: loads the persisted MCP settings, validates them, and starts the listener,
    /// wrapping the work in a blocking-operation progress record.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return StartCoreAsync(null, true, cancellationToken);
    }

    /// <summary>
    /// Standalone-server path: starts the listener from caller-supplied effective settings (for
    /// example app settings with a command-line port override) without a blocking-operation record.
    /// </summary>
    public Task StartAsync(McpServerSettings explicitSettings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(explicitSettings);
        return StartCoreAsync(explicitSettings, false, cancellationToken);
    }

    /// <summary>
    /// Awaits any in-flight background start, then stops the server and marks the host stopped.
    /// </summary>
    public async Task StopAsync(string detail = "MCP HTTP 服务已停止。", CancellationToken cancellationToken = default)
    {
        Task? backgroundStart = _backgroundStartTask;
        _backgroundStartTask = null;
        if (backgroundStart is not null)
        {
            try
            {
                await backgroundStart;
            }
            catch (Exception exception)
            {
                ReportException(exception, "await-background-start");
            }
        }

        await StopCoreAsync(detail);
    }

    /// <summary>
    /// Settings-apply flow: stop with an explanatory detail, then start again from current settings.
    /// </summary>
    public async Task RestartAsync(string detail = "应用新设置", CancellationToken cancellationToken = default)
    {
        await StopAsync(detail, cancellationToken);
        await StartAsync(cancellationToken);
    }

    /// <summary>
    /// Application shutdown hook: awaits any background start and stops the server.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// Blocks until the running server shuts down (Ctrl+C / process signal).
    /// </summary>
    public async Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            await _server.RunAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
    }

    private async Task StartInBackgroundAsync()
    {
        try
        {
            await StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportException(exception, "start-background");
            SetStatus(McpServerHostStatus.Error, McpOutputSanitizer.Sanitize(exception.Message));
        }
    }

    private async Task StartCoreAsync(McpServerSettings? explicitSettings, bool recordProgress,
        CancellationToken cancellationToken)
    {
        if (_server?.IsRunning == true)
        {
            SetStatus(McpServerHostStatus.Running, BuildConnectionDetail());
            return;
        }

        BlockingOperationId? operationId = null;
        if (recordProgress)
        {
            Result<BlockingOperation> started = await _services.BlockingOperations.StartAsync(
                BlockingOperationTypes.McpStartValidation,
                BlockingOperationScopeTypes.McpServerSettings,
                "default",
                progressLabel: "正在验证 MCP 设置并启动 listener。",
                nextActions: ["检查 MCP bind、端口和鉴权 token"],
                cancellationToken: CancellationToken.None);
            if (started.IsSuccess)
            {
                operationId = started.Value.OperationId;
            }
        }

        await StopCoreAsync("MCP HTTP 服务正在启动。");

        McpServerSettings serverSettings;
        if (explicitSettings is not null)
        {
            serverSettings = explicitSettings;
            Result explicitValidation =
                await _services.McpSettings.ValidateSettingsAsync(serverSettings, cancellationToken);
            if (explicitValidation.IsFailure)
            {
                string message = McpOutputSanitizer.Sanitize(explicitValidation.ErrorMessage ?? "MCP 设置无效。");
                SetStatus(McpServerHostStatus.Error, message);
                return;
            }
        }
        else
        {
            Result<McpServerSettings> settingsResult =
                await _services.McpSettings.GetSettingsAsync(cancellationToken);
            if (settingsResult.IsFailure)
            {
                string message = McpOutputSanitizer.Sanitize(settingsResult.ErrorMessage ?? "无法读取 MCP 设置。");
                if (operationId is not null)
                {
                    await _services.BlockingOperations.FailAsync(operationId.Value, settingsResult.ErrorCode!, message,
                        cancellationToken: CancellationToken.None);
                }

                SetStatus(McpServerHostStatus.Error, message);
                return;
            }

            serverSettings = settingsResult.Value;
            Result validation = await _services.McpSettings.ValidateSettingsAsync(serverSettings, cancellationToken);
            if (validation.IsFailure)
            {
                string message = McpOutputSanitizer.Sanitize(validation.ErrorMessage ?? "MCP 设置无效。");
                if (operationId is not null)
                {
                    await _services.BlockingOperations.FailAsync(operationId.Value, validation.ErrorCode!, message,
                        cancellationToken: CancellationToken.None);
                }

                SetStatus(McpServerHostStatus.Error, message);
                return;
            }
        }

        SetStatus(McpServerHostStatus.Starting,
            $"正在监听 http://{serverSettings.BindAddress}:{serverSettings.Port}/mcp");

        McpProtocolHandler handler = new(_services.Mcp, _services.McpWrites, _services.BiblatexImport,
            _services.Items, _services.VersionedEvidenceReader, _services.ConnectionFactory, serverSettings,
            ReportMcpException);
        McpHttpServer server = new(handler, serverSettings, ReportMcpException);
        server.ConnectionCountsChanged += OnServerConnectionCountsChanged;
        try
        {
            await server.StartAsync(cancellationToken);
            _server = server;
            RunningSettingsRevision = serverSettings.Revision;
            Endpoint = server.Endpoint;
            SetStatus(McpServerHostStatus.Running, BuildConnectionDetail());
            if (operationId is not null)
            {
                await _services.BlockingOperations.CompleteAsync(operationId.Value, "MCP HTTP listener 已启动。",
                    cancellationToken: CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportException(ex, "start-listener");
            server.ConnectionCountsChanged -= OnServerConnectionCountsChanged;
            try
            {
                await server.DisposeAsync();
            }
            catch (Exception disposeException)
            {
                ReportException(disposeException, "dispose-after-start-failure");
            }

            string message = McpOutputSanitizer.Sanitize(ex.Message);
            if (operationId is not null)
            {
                await _services.BlockingOperations.FailAsync(operationId.Value, AppErrorCodes.InvalidState, message,
                    "MCP HTTP listener 启动失败。", ["检查端口占用", "检查 bind 和鉴权设置"], CancellationToken.None);
            }

            SetStatus(McpServerHostStatus.Error, message);
        }
    }

    private async Task StopCoreAsync(string detail)
    {
        if (_server is not null)
        {
            _server.ConnectionCountsChanged -= OnServerConnectionCountsChanged;
            await _server.DisposeAsync();
            _server = null;
        }

        RunningSettingsRevision = null;
        SetStatus(McpServerHostStatus.Stopped, detail);
    }

    private void OnServerConnectionCountsChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _server))
        {
            return;
        }

        ConnectionCountsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string BuildConnectionDetail()
    {
        long active = _server?.ActiveConnectionCount ?? 0;
        long total = _server?.TotalConnectionCount ?? 0;
        return $"连接数: {active} / {total}";
    }

    private void SetStatus(McpServerHostStatus status, string detail)
    {
        Status = status;
        StatusDetail = detail;
        StatusChanged?.Invoke(this, new McpServerHostStatusChangedEventArgs(status, detail));
    }

    private void ReportMcpException(Exception exception, string operation)
    {
        ReportException(exception, operation);
    }

    private void ReportException(Exception exception, string operation)
    {
        try
        {
            _reportUnexpectedException(exception, "mcp-server", operation);
        }
        catch
        {
            // Reporting failure must never break the server lifecycle path.
        }

        try
        {
            ExceptionReported?.Invoke(this, new McpServerHostExceptionEventArgs(exception, operation));
        }
        catch
        {
            // Event subscribers must never break the server lifecycle path.
        }
    }
}
