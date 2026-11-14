using System.Net;
using Patchouli.Core.Settings;

namespace Patchouli.Host.Network;

/// <summary>Machine-readable failure categories for <see cref="NetworkConnectionTestService"/>.</summary>
public static class NetworkConnectionTestKinds
{
    public const string Ok = "ok";
    public const string ProxyTunnel = "proxy_tunnel";
    public const string Tls = "tls";
    public const string Dns = "dns";
    public const string Connection = "connection";
    public const string Authentication = "authentication";
    public const string Timeout = "timeout";
    public const string Http = "http";
    public const string InvalidConfiguration = "invalid_configuration";
    public const string Canceled = "canceled";
    public const string Error = "error";
}

/// <summary>
/// Outcome of one connectivity probe. <see cref="Summary"/> is always a sanitized, fixed-shape
/// English message: it never carries the target URL, proxy address, proxy credentials or raw
/// exception text (which could embed the proxy hostname).
/// </summary>
public sealed record NetworkConnectionTestResult(
    bool Success,
    string Kind,
    string Summary,
    int? HttpStatus = null)
{
    public static NetworkConnectionTestResult Succeeded()
    {
        return new NetworkConnectionTestResult(true, NetworkConnectionTestKinds.Ok,
            "Connection succeeded.");
    }
}

/// <summary>
/// Non-UI "test connection" probe behind <see cref="IHttpTransportPolicy"/> (issue #56). It sends
/// one HTTPS request through a dedicated handler built by the policy — honoring Manual/System/None
/// modes, the stored proxy credential and ordinary certificate validation — and classifies the
/// outcome so callers can tell a proxy tunnel rejection, TLS failure (e.g. an untrusted MITM CA),
/// DNS failure, connection failure and authentication failure apart.
/// </summary>
public sealed class NetworkConnectionTestService
{
    /// <summary>Default probe target: the same host the updater already needs to reach.</summary>
    public const string DefaultProbeUrl = "https://api.github.com/";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly IHttpTransportPolicy _policy;
    private readonly Func<SocketsHttpHandler>? _handlerFactory;

    public NetworkConnectionTestService(IHttpTransportPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    /// <summary>Test seam: replaces the transport handler while keeping result classification.</summary>
    internal NetworkConnectionTestService(IHttpTransportPolicy policy, Func<SocketsHttpHandler> handlerFactory)
    {
        _policy = policy;
        _handlerFactory = handlerFactory;
    }

    public async Task<NetworkConnectionTestResult> TestAsync(string? target = null,
        CancellationToken cancellationToken = default)
    {
        NetworkAppSettings settings = _policy.Settings;
        if (settings.ProxyMode == NetworkProxyModes.Manual &&
            !string.IsNullOrWhiteSpace(settings.ProxyAddress) &&
            NetworkAppSettings.TryCreateManualProxyUri(settings.ProxyAddress) is null)
        {
            return new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.InvalidConfiguration,
                "The manual proxy configuration is invalid.");
        }

        string probeUrl = string.IsNullOrWhiteSpace(target) ? DefaultProbeUrl : target.Trim();
        if (!Uri.TryCreate(probeUrl, UriKind.Absolute, out Uri? probeUri) ||
            probeUri.Scheme is not ("https" or "http"))
        {
            return new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.InvalidConfiguration,
                "The probe target is not a valid HTTP(S) URL.");
        }

        try
        {
            using SocketsHttpHandler handler = _handlerFactory?.Invoke() ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };
            handler.ConnectTimeout = ConnectTimeout;
            _policy.ConfigureHandler(handler);
            using HttpClient client = new(handler) { Timeout = RequestTimeout };
            using HttpResponseMessage response = await client.GetAsync(probeUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? NetworkConnectionTestResult.Succeeded()
                : new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Http,
                    $"The server returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Canceled,
                "The connection test was canceled.");
        }
        catch (OperationCanceledException)
        {
            return new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Timeout,
                "The connection timed out.");
        }
        catch (HttpRequestException exception)
        {
            return Classify(exception);
        }
        catch (InvalidOperationException)
        {
            return new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.InvalidConfiguration,
                "The manual proxy configuration is invalid.");
        }
        catch (Exception)
        {
            return new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Error,
                "The connection test failed.");
        }
    }

    private static NetworkConnectionTestResult Classify(HttpRequestException exception)
    {
        return exception.HttpRequestError switch
        {
            HttpRequestError.ProxyTunnelError when
                exception.StatusCode == HttpStatusCode.ProxyAuthenticationRequired =>
                new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Authentication,
                    "The proxy requires authentication (HTTP 407). Check the proxy username and password.", 407),
            HttpRequestError.ProxyTunnelError => new NetworkConnectionTestResult(false,
                NetworkConnectionTestKinds.ProxyTunnel,
                exception.StatusCode is { } status
                    ? $"The proxy refused the tunnel (proxy returned HTTP {(int)status})."
                    : "The proxy refused the tunnel.",
                exception.StatusCode is { } code ? (int)code : null),
            HttpRequestError.SecureConnectionError => new NetworkConnectionTestResult(false,
                NetworkConnectionTestKinds.Tls,
                "The TLS handshake failed. If a proxy intercepts TLS, trust its CA certificate in the operating system; certificate validation is never bypassed."),
            HttpRequestError.NameResolutionError => new NetworkConnectionTestResult(false,
                NetworkConnectionTestKinds.Dns,
                "The host name could not be resolved."),
            HttpRequestError.UserAuthenticationError => new NetworkConnectionTestResult(false,
                NetworkConnectionTestKinds.Authentication,
                "Authentication failed."),
            HttpRequestError.ConnectionError => new NetworkConnectionTestResult(false,
                NetworkConnectionTestKinds.Connection,
                "The connection could not be established."),
            _ when exception.StatusCode == HttpStatusCode.ProxyAuthenticationRequired =>
                new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Authentication,
                    "The proxy requires authentication (HTTP 407). Check the proxy username and password.", 407),
            _ when exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Authentication,
                    $"The server rejected authentication (HTTP {(int)exception.StatusCode.Value}).",
                    (int)exception.StatusCode.Value),
            _ => new NetworkConnectionTestResult(false, NetworkConnectionTestKinds.Error,
                "The connection test failed.")
        };
    }
}
