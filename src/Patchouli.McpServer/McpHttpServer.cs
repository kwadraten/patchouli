using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Patchouli.McpServer;

public sealed class McpHttpServer : IAsyncDisposable
{
    private readonly McpProtocolHandler _handler;
    private readonly int _port;
    private long _activeConnectionCount;
    private long _totalConnectionCount;
    private WebApplication? _app;

    public McpHttpServer(McpProtocolHandler handler, int port = McpServerOptions.DefaultPort)
    {
        _handler = handler;
        _port = port;
        Endpoint = $"http://localhost:{port}";
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
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(_port, listenOptions =>
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
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapPost("/", (HttpContext context, CancellationToken ct) => HandleMcpRequestAsync(context, _handler, ct));
        app.MapPost("/mcp", (HttpContext context, CancellationToken ct) => HandleMcpRequestAsync(context, _handler, ct));
        return app;
    }

    private static async Task<IResult> HandleMcpRequestAsync(
        HttpContext context,
        McpProtocolHandler handler,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body);
        var request = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request))
        {
            return Results.BadRequest(new { error = "Request body is required." });
        }

        var response = await handler.HandleAsync(request, cancellationToken);
        return Results.Text(response, "application/json");
    }
}
