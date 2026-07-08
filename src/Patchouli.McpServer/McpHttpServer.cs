using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Patchouli.Core.Mcp;
using Patchouli.Mcp;

namespace Patchouli.McpServer;

public sealed class McpHttpServer : IAsyncDisposable
{
    private readonly McpProtocolHandler _handler;
    private readonly McpServerSettings _settings;
    private long _activeConnectionCount;
    private long _totalConnectionCount;
    private WebApplication? _app;

    public McpHttpServer(McpProtocolHandler handler, int port = McpServerOptions.DefaultPort)
        : this(handler, new McpServerSettings(port, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UtcNow))
    {
    }

    public McpHttpServer(McpProtocolHandler handler, McpServerSettings settings)
    {
        _handler = handler;
        _settings = settings;
        Endpoint = $"http://{DisplayHost(settings.BindAddress)}:{settings.Port}";
    }

    public string Endpoint { get; }
    public bool IsRunning => _app is not null;
    public long ActiveConnectionCount => Interlocked.Read(ref _activeConnectionCount);
    public long TotalConnectionCount => Interlocked.Read(ref _totalConnectionCount);
    public event EventHandler? ConnectionCountsChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var app = CreateApplication();
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        await _app!.WaitForShutdownAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null) return;

        var app = _app;
        _app = null;
        await app.StopAsync(cancellationToken);
        await app.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
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
                    ConnectionCountsChanged?.Invoke(this, EventArgs.Empty);
                    try
                    {
                        await next(connection);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeConnectionCount);
                        ConnectionCountsChanged?.Invoke(this, EventArgs.Empty);
                    }
                });
            });
        });

        var app = builder.Build();
        if (_settings.CorsEnabled)
        {
            var allowedOrigins = _settings.AllowedOrigins.Count == 0 ? ["*"] : _settings.AllowedOrigins.ToArray();
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
        app.MapPost("/", (HttpContext context, CancellationToken ct) => HandleMcpRequestAsync(context, _handler, _settings, ct));
        app.MapPost("/mcp", (HttpContext context, CancellationToken ct) => HandleMcpRequestAsync(context, _handler, _settings, ct));
        return app;
    }

    private void ConfigureListen(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options, Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> configure)
    {
        var bindAddress = _settings.BindAddress.Trim();
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

        if (System.Net.IPAddress.TryParse(bindAddress, out var ipAddress))
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
            return Results.Json(new { error = McpErrorCodes.Unauthorized }, statusCode: StatusCodes.Status401Unauthorized);
        }

        using var reader = new StreamReader(context.Request.Body);
        var request = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request))
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        var response = await handler.HandleAsync(request, cancellationToken);
        return Results.Text(response, "application/json");
    }

    private static bool RequiresAuthentication(McpServerSettings settings)
        => settings.AuthRequired || !IsLoopback(settings.BindAddress);

    private static bool IsLoopback(string bindAddress)
        => string.Equals(bindAddress, "127.0.0.1", StringComparison.Ordinal)
           || string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase);

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

        var header = context.Request.Headers.Authorization.ToString();
        return string.Equals(header, $"Bearer {token}", StringComparison.Ordinal);
    }

    private static string DisplayHost(string bindAddress)
        => IsLoopback(bindAddress) ? "localhost" : bindAddress;
}
