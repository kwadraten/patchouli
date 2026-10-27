using Patchouli.Core.Ids;
using Patchouli.Core.Mcp;
using Patchouli.Core.Operations;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Mcp;
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
/// Owns MCP settings validation and listener lifecycle, publishing status through events.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly McpServerHostDependencies _dependencies;
    private readonly Action<Exception, string, string?> _reportUnexpectedException;
    private IMcpServerInstance? _server;
    private Task? _backgroundStartTask;
    private McpServerHostStatusSnapshot _status = McpServerHostStatusSnapshot.Initial;

    public McpServerHost(HostServices services, Action<Exception, string, string?> reportUnexpectedException)
        : this(McpServerHostDependencies.FromHostServices(services), reportUnexpectedException)
    {
    }

    public McpServerHost(McpServerHostDependencies dependencies,
        Action<Exception, string, string?> reportUnexpectedException)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _dependencies = dependencies;
        _reportUnexpectedException = reportUnexpectedException;
    }

    public McpHttpServer? Server => _server?.HttpServer;
    public bool IsRunning => _server?.IsRunning == true;
    public long? RunningSettingsRevision => Volatile.Read(ref _status).RunningSettingsRevision;
    public McpServerSettings? RunningSettings => Volatile.Read(ref _status).RunningSettings;
    public string Endpoint => Volatile.Read(ref _status).Endpoint;
    public McpServerHostStatus Status => Volatile.Read(ref _status).Status;
    public string StatusDetail => Volatile.Read(ref _status).Detail;

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
            Result<BlockingOperation> started = await _dependencies.BlockingOperations.StartAsync(
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
            serverSettings = McpServerSettingsService.NormalizeLegacySettings(explicitSettings);
            Result explicitValidation =
                await _dependencies.Settings.ValidateSettingsAsync(serverSettings, cancellationToken);
            if (explicitValidation.IsFailure)
            {
                string message =
                    McpServerHostStatusProjection.ErrorDetail(explicitValidation.ErrorMessage, "MCP 设置无效。");
                SetStatus(McpServerHostStatus.Error, message);
                return;
            }
        }
        else
        {
            Result<McpServerSettings> settingsResult =
                await _dependencies.Settings.GetSettingsAsync(cancellationToken);
            if (settingsResult.IsFailure)
            {
                string message =
                    McpServerHostStatusProjection.ErrorDetail(settingsResult.ErrorMessage, "无法读取 MCP 设置。");
                if (operationId is not null)
                {
                    await _dependencies.BlockingOperations.FailAsync(operationId.Value, settingsResult.ErrorCode!,
                        message, cancellationToken: CancellationToken.None);
                }

                SetStatus(McpServerHostStatus.Error, message);
                return;
            }

            serverSettings = settingsResult.Value;
            Result validation = await _dependencies.Settings.ValidateSettingsAsync(serverSettings, cancellationToken);
            if (validation.IsFailure)
            {
                string message =
                    McpServerHostStatusProjection.ErrorDetail(validation.ErrorMessage, "MCP 设置无效。");
                if (operationId is not null)
                {
                    await _dependencies.BlockingOperations.FailAsync(operationId.Value, validation.ErrorCode!,
                        message, cancellationToken: CancellationToken.None);
                }

                SetStatus(McpServerHostStatus.Error, message);
                return;
            }
        }

        SetStatus(McpServerHostStatus.Starting, McpServerHostStatusProjection.StartingDetail(serverSettings));

        IMcpServerInstance server = _dependencies.ServerFactory.Create(serverSettings, ReportMcpException);
        server.ConnectionCountsChanged += OnServerConnectionCountsChanged;
        try
        {
            await server.StartAsync(cancellationToken);
            _server = server;
            UpdateStatus(snapshot => snapshot with
            {
                RunningSettingsRevision = serverSettings.Revision,
                RunningSettings = serverSettings,
                Endpoint = server.Endpoint
            });
            SetStatus(McpServerHostStatus.Running, BuildConnectionDetail());
            if (operationId is not null)
            {
                await _dependencies.BlockingOperations.CompleteAsync(operationId.Value, "MCP HTTP listener 已启动。",
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
                await _dependencies.BlockingOperations.FailAsync(operationId.Value, AppErrorCodes.InvalidState, message,
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

        UpdateStatus(static snapshot => snapshot with
        {
            RunningSettingsRevision = null,
            RunningSettings = null
        });
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
        return McpServerHostStatusProjection.ConnectionDetail(active, total);
    }

    private void SetStatus(McpServerHostStatus status, string detail)
    {
        UpdateStatus(snapshot => snapshot with { Status = status, Detail = detail });
        StatusChanged?.Invoke(this, new McpServerHostStatusChangedEventArgs(status, detail));
    }

    private void UpdateStatus(Func<McpServerHostStatusSnapshot, McpServerHostStatusSnapshot> update)
    {
        McpServerHostStatusSnapshot current;
        McpServerHostStatusSnapshot next;
        do
        {
            current = Volatile.Read(ref _status);
            next = update(current);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _status, next, current), current));
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
