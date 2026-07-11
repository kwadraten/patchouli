using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Patchouli.Core.Mcp;
using Patchouli.Mcp;
using System.Text;
using System.Text.Json;

namespace Patchouli.McpServer;

public sealed class McpHttpServer : IAsyncDisposable
{
    private readonly McpProtocolHandler _handler;
    private readonly McpServerSettings _settings;
    private readonly Action<Exception, string>? _unexpectedException;
    private long _activeConnectionCount;
    private long _totalConnectionCount;
    private WebApplication? _app;

    public McpHttpServer(McpProtocolHandler handler, int port = McpServerOptions.DefaultPort,
        Action<Exception, string>? unexpectedException = null)
        : this(handler, new McpServerSettings(port, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow),
            unexpectedException)
    {
    }

    public McpHttpServer(McpProtocolHandler handler, McpServerSettings settings,
        Action<Exception, string>? unexpectedException = null)
    {
        _handler = handler;
        _settings = settings;
        _unexpectedException = unexpectedException;
        Endpoint = $"http://{DisplayHost(settings.BindAddress)}:{settings.Port}/mcp";
    }

    public string Endpoint { get; }
    public bool IsRunning => _app is not null;
    public long ActiveConnectionCount => Interlocked.Read(ref _activeConnectionCount);
    public long TotalConnectionCount => Interlocked.Read(ref _totalConnectionCount);
    public event EventHandler? ConnectionCountsChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        WebApplication app = CreateApplication();
        try
        {
            await app.StartAsync(cancellationToken);
            _app = app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        await _app!.WaitForShutdownAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        WebApplication? app = _app;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private WebApplication CreateApplication()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        if (_settings.CorsEnabled)
        {
            builder.Services.AddCors();
        }

        builder.WebHost.ConfigureKestrel(options =>
        {
            ConfigureListen(options, listenOptions =>
            {
                listenOptions.Use(next => async connection =>
                {
                    Interlocked.Increment(ref _activeConnectionCount);
                    Interlocked.Increment(ref _totalConnectionCount);
                    NotifyConnectionCountsChanged();
                    try
                    {
                        await next(connection);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeConnectionCount);
                        NotifyConnectionCountsChanged();
                    }
                });
            });
        });

        WebApplication app = builder.Build();
        if (_settings.CorsEnabled)
        {
            string[] allowedOrigins = _settings.AllowedOrigins.Count == 0 ? ["*"] : _settings.AllowedOrigins.ToArray();
            app.UseCors(policy =>
            {
                if (allowedOrigins.Length == 1 && allowedOrigins[0] == "*")
                {
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                }
                else
                {
                    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
                }
            });
        }

        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapGet("/mcp", (HttpContext context, CancellationToken ct) => HandleMcpSseAsync(context, _settings, ct));
        app.MapPost("/mcp",
            (HttpContext context, CancellationToken ct) => HandleMcpRequestAsync(context, _handler, _settings, ct));
        app.MapMethods("/mcp", ["OPTIONS"], (HttpContext context) => HandleMcpOptions(context, _settings));
        return app;
    }

    private void NotifyConnectionCountsChanged()
    {
        foreach (EventHandler subscriber in ConnectionCountsChanged?.GetInvocationList() ?? [])
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                try
                {
                    _unexpectedException?.Invoke(exception, "mcp-connection-count-subscriber");
                }
                // The external diagnostic callback must not break connection handling or later subscribers.
                // ReSharper disable once EmptyGeneralCatchClause
                catch
                {
                }
            }
        }
    }

    private void ConfigureListen(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options,
        Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> configure)
    {
        string bindAddress = _settings.BindAddress.Trim();
        if (string.Equals(bindAddress, "0.0.0.0", StringComparison.Ordinal))
        {
            options.ListenAnyIP(_settings.Port, configure);
            return;
        }

        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bindAddress, "127.0.0.1", StringComparison.Ordinal))
        {
            options.ListenLocalhost(_settings.Port, configure);
            return;
        }

        if (IPAddress.TryParse(bindAddress, out IPAddress? ipAddress))
        {
            options.Listen(ipAddress, _settings.Port, configure);
            return;
        }

        options.ListenLocalhost(_settings.Port, configure);
    }

    private static async Task<IResult> HandleMcpRequestAsync(
        HttpContext context,
        McpProtocolHandler handler,
        McpServerSettings settings,
        CancellationToken cancellationToken)
    {
        if (RequiresAuthentication(settings) && !IsAuthorized(context, settings.Token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Results.Json(new { error = McpErrorCodes.Unauthorized },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!IsJsonRequest(context))
        {
            return Results.Text("Unsupported media type. Use application/json.", "text/plain",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        using StreamReader reader = new(context.Request.Body);
        string request = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request))
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        if (IsJsonRpcNotification(request))
        {
            await handler.HandleAsync(request, cancellationToken);
            return Results.Accepted();
        }

        string response = await handler.HandleAsync(request, cancellationToken);
        return Results.Text(response, "application/json");
    }

    private static async Task HandleMcpSseAsync(
        HttpContext context,
        McpServerSettings settings,
        CancellationToken cancellationToken)
    {
        if (RequiresAuthentication(settings) && !IsAuthorized(context, settings.Token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = McpErrorCodes.Unauthorized }, cancellationToken);
            return;
        }

        if (!AcceptsEventStream(context))
        {
            context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
            await context.Response.WriteAsJsonAsync(new
            {
                name = "Patchouli.Net MCP",
                transport = "streamable_http",
                endpoint = "/mcp"
            }, cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        try
        {
            await WriteSseCommentAsync(context, "connected", cancellationToken);
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await WriteSseCommentAsync(context, "keepalive", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static IResult HandleMcpOptions(HttpContext context, McpServerSettings settings)
    {
        if (settings.CorsEnabled)
        {
            string origin = settings.AllowedOrigins.Count == 0 ? "*" : string.Join(", ", settings.AllowedOrigins);
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Authorization, Accept";
        }

        return Results.NoContent();
    }

    private static async Task WriteSseCommentAsync(HttpContext context, string comment,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        builder.Append(": ").Append(comment).Append('\n');
        builder.Append('\n');
        await context.Response.WriteAsync(builder.ToString(), cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private static bool AcceptsEventStream(HttpContext context)
    {
        return context.Request.Headers.Accept.Any(value =>
            value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsJsonRequest(HttpContext context)
    {
        return context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsJsonRpcNotification(string request)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(request);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("method", out _)
                   && !document.RootElement.TryGetProperty("id", out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool RequiresAuthentication(McpServerSettings settings)
    {
        return settings.AuthRequired || !IsLoopback(settings.BindAddress);
    }

    private static bool IsLoopback(string bindAddress)
    {
        return string.Equals(bindAddress, "127.0.0.1", StringComparison.Ordinal)
               || string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthorized(HttpContext context, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (!context.Request.Headers.Authorization.Any())
        {
            return false;
        }

        string header = context.Request.Headers.Authorization.ToString();
        return string.Equals(header, $"Bearer {token}", StringComparison.Ordinal);
    }

    private static string DisplayHost(string bindAddress)
    {
        return IsLoopback(bindAddress) ? "localhost" : bindAddress;
    }
}
