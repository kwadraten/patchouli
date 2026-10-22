using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Patchouli.Core.CitationInterop;

namespace Patchouli.Host.CitationInterop;

/// <summary>
/// Loopback-only HTTP transport for the citation interop surface (issue #14
/// §5/§6): binds 127.0.0.1 exclusively, validates Host and Origin headers,
/// sends no CORS headers, enforces request-size limits, and maps every
/// unsupported route/method/parameter combination to a stable error instead
/// of silently degrading. The port is never silently changed: a bind failure
/// (for example Zotero/BBT already listening on 23119) surfaces to the host.
/// </summary>
internal sealed class CitationInteropHttpServer : IAsyncDisposable
{
    private readonly CaywRequestHandler _cayw;
    private readonly JsonRpcHandler _rpc;
    private readonly Action<Exception, string>? _reportUnexpected;
    private readonly CancellationToken _shutdownToken;
    private WebApplication? _app;

    public CitationInteropHttpServer(
        CaywRequestHandler cayw,
        JsonRpcHandler rpc,
        Action<Exception, string>? reportUnexpected = null,
        CancellationToken shutdownToken = default)
    {
        _cayw = cayw;
        _rpc = rpc;
        _reportUnexpected = reportUnexpected;
        _shutdownToken = shutdownToken;
    }

    public bool IsRunning => _app is not null;

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = CaywCompatibility.MaxRequestBodyBytes;
        });
        builder.Services.AddRouting();

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsHostAllowed(context.Request) || !IsOriginAllowed(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "forbidden: this endpoint only accepts loopback requests with a loopback Host/Origin.",
                    context.RequestAborted);
                return;
            }

            await next(context);
        });

        app.MapGet(CaywCompatibility.CaywPath, HandleCaywAsync);
        app.MapPost(CaywCompatibility.CaywPath, static context =>
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return context.Response.WriteAsync(
                "unsupported: the stateful POST citation picker is not supported; use GET.",
                context.RequestAborted);
        });
        app.MapPost(CaywCompatibility.JsonRpcPath, HandleJsonRpcAsync);
        app.Map(CaywCompatibility.JsonRpcPath, static context =>
        {
            if (context.Request.Method != HttpMethods.Post)
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return context.Response.WriteAsync(
                    "unsupported: the JSON-RPC endpoint only accepts POST.", context.RequestAborted);
            }

            return Task.CompletedTask;
        });

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            // A failed start must not leak the WebApplication: the instance
            // is only handed to _app (and thereby to StopAsync's disposal)
            // after a successful start, so dispose the local one here —
            // otherwise every port-conflict retry accumulates an undisposed
            // host.
            await app.DisposeAsync();
            throw;
        }

        _app = app;
    }

    public async Task StopAsync()
    {
        WebApplication? app = _app;
        _app = null;
        if (app is null)
        {
            return;
        }

        try
        {
            await app.StopAsync();
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task HandleCaywAsync(HttpContext context)
    {
        if (context.Request.QueryString.Value is { Length: > CaywCompatibility.MaxQueryStringLength })
        {
            context.Response.StatusCode = StatusCodes.Status414UriTooLong;
            await context.Response.WriteAsync("request too long: the query string exceeds the supported limit.",
                context.RequestAborted);
            return;
        }

        Dictionary<string, string> query = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in context.Request.Query)
        {
            query[pair.Key] = pair.Value.ToString();
        }

        CitationHttpResponse response;
        try
        {
            // The host's per-generation shutdown token joins the request
            // token: stopping the host (library switch, shutdown, settings
            // restart) cancels in-flight picks immediately instead of
            // leaving them to Kestrel's connection drain, during which a
            // late confirmation could still land clipboard/selection side
            // effects on the old library. The handler links this token with
            // its interactive deadline and converges cancellation to the
            // usual empty result.
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _shutdownToken);
            response = await _cayw.HandleAsync(query, linked.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested ||
                                                  _shutdownToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _reportUnexpected?.Invoke(exception, "cayw-request");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("error: the citation request failed unexpectedly.",
                context.RequestAborted);
            return;
        }

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        await context.Response.WriteAsync(response.Body, context.RequestAborted);
    }

    private async Task HandleJsonRpcAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > CaywCompatibility.MaxRequestBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsync("request too large.", context.RequestAborted);
            return;
        }

        // As on the CAYW path, the host's per-generation shutdown token
        // joins the request token: stopping the host cancels an in-flight
        // RPC immediately instead of leaving it to Kestrel's drain.
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _shutdownToken);
        string body;
        try
        {
            body = await ReadBodyWithLimitAsync(context, linked.Token);
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsync("request too large.", context.RequestAborted);
            return;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested ||
                                                  _shutdownToken.IsCancellationRequested)
        {
            return;
        }

        string responseBody;
        try
        {
            responseBody = await _rpc.HandleAsync(body, linked.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested ||
                                                  _shutdownToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _reportUnexpected?.Invoke(exception, "json-rpc-request");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(
                "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Internal error.\"},\"id\":null}",
                context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(responseBody, context.RequestAborted);
    }

    private static async Task<string> ReadBodyWithLimitAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > CaywCompatibility.MaxRequestBodyBytes)
                {
                    throw new InvalidOperationException("Request body exceeds the interop limit.");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsHostAllowed(HttpRequest request)
    {
        string? host = request.Host.Host;
        return IsLoopbackHostName(host);
    }

    private static bool IsOriginAllowed(HttpRequest request)
    {
        string origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            // Non-browser local clients (editors, curl, TexPile) send no Origin.
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHostName(uri.Host);
    }

    private static bool IsLoopbackHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }
}
