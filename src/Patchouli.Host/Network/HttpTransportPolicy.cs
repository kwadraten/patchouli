using System.Net;
using System.Text.RegularExpressions;
using Patchouli.Core.Settings;

namespace Patchouli.Host.Network;

/// <summary>
/// Default <see cref="IHttpTransportPolicy"/>: resolves the persisted <see cref="NetworkAppSettings"/>
/// per request through one injected <see cref="IWebProxy"/>. Manual mode accepts
/// http/https/socks5/socks5h addresses and a host bypass list (wildcards <c>*</c>/<c>?</c>, optional
/// <c>:port</c> suffix, loopback always bypassed). Mode <c>none</c> connects directly; mode
/// <c>system</c> delegates to .NET's own system/PAC/environment resolution unchanged. The proxy
/// password is resolved lazily from the injected secret loader (the credential store) and never
/// surfaces in logs or diagnostics.
/// </summary>
public sealed class HttpTransportPolicy : IHttpTransportPolicy
{
    private readonly Func<CancellationToken, Task<string?>> _secretLoader;
    private readonly object _credentialGate = new();
    private volatile NetworkAppSettings _settings;
    private volatile Lazy<NetworkCredential?>? _credential;

    /// <summary>
    /// <paramref name="secretLoader"/> returns the stored proxy password (or null when absent).
    /// It is invoked at most once per credential lifetime, on the first request that actually
    /// authenticates against a proxy.
    /// </summary>
    public HttpTransportPolicy(NetworkAppSettings settings, Func<CancellationToken, Task<string?>> secretLoader)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secretLoader);
        _settings = settings.Normalize();
        _secretLoader = secretLoader;
    }

    public NetworkAppSettings Settings => _settings;

    public void ConfigureHandler(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler.UseProxy = true;
        handler.Proxy = new HostWebProxy(this);
        handler.DefaultProxyCredentials = new ProxyCredentialSource(this);
    }

    public void Update(NetworkAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalize();
        InvalidateCredentials();
    }

    public void InvalidateCredentials()
    {
        lock (_credentialGate)
        {
            _credential = null;
        }
    }

    private NetworkCredential? ResolveCredential()
    {
        Lazy<NetworkCredential?>? lazy = _credential;
        if (lazy is null)
        {
            lock (_credentialGate)
            {
                _credential ??= new Lazy<NetworkCredential?>(LoadCredential);
                lazy = _credential;
            }
        }

        return lazy!.Value;
    }

    private NetworkCredential? LoadCredential()
    {
        NetworkAppSettings settings = _settings;
        if (string.IsNullOrWhiteSpace(settings.ProxyUsername) || !settings.HasProxyPassword)
        {
            return null;
        }

        try
        {
            // IWebProxy.Credentials is queried synchronously by the handler when it answers a 407;
            // there is no async seam, so the (small) credential-store read is blocked on once.
            string? password = _secretLoader(CancellationToken.None).ConfigureAwait(false)
                .GetAwaiter().GetResult();
            return string.IsNullOrEmpty(password)
                ? null
                : new NetworkCredential(settings.ProxyUsername, password);
        }
        catch (Exception)
        {
            // A credential-store failure degrades to an anonymous proxy attempt; the resulting 407
            // is surfaced by the connection test / download classification instead of crashing here.
            return null;
        }
    }

    // The return type is Uri? because IWebProxy.GetProxy may signal "connect directly" with null.
    private Uri? ResolveProxy(Uri destination)
    {
        NetworkAppSettings settings = _settings;
        switch (settings.ProxyMode)
        {
            case NetworkProxyModes.None:
                return destination;
            case NetworkProxyModes.Manual:
                if (IsManualBypassed(destination, settings))
                {
                    return destination;
                }

                if (string.IsNullOrWhiteSpace(settings.ProxyAddress))
                {
                    // Incomplete manual configuration (autosaved draft) connects directly; the
                    // settings validation reports proxy_address_required to the user.
                    return destination;
                }

                return NetworkAppSettings.TryCreateManualProxyUri(settings.ProxyAddress)
                       ?? InvalidManualProxyUri;
            default:
                return SystemProxy().GetProxy(destination);
        }
    }

    private bool ResolveIsBypassed(Uri destination)
    {
        NetworkAppSettings settings = _settings;
        return settings.ProxyMode switch
        {
            NetworkProxyModes.None => true,
            NetworkProxyModes.Manual => IsManualBypassed(destination, settings),
            _ => SystemProxy().IsBypassed(destination)
        };
    }

    // SocketsHttpHandler checks the returned IWebProxy.Credentials for proxy auth; it delegates to
    // the same lazily resolved credential as DefaultProxyCredentials.
    private NetworkCredential? GetCredentialsFor(Uri? uri, string? authType)
    {
        NetworkAppSettings settings = _settings;
        return string.IsNullOrWhiteSpace(settings.ProxyUsername) ? null : ResolveCredential();
    }

    private static IWebProxy SystemProxy()
    {
        return HttpClient.DefaultProxy;
    }

    // SocketsHttpHandler swallows exceptions thrown from IWebProxy.GetProxy/IsBypassed and then
    // connects directly — throwing here would silently bypass the user's proxy. An invalid manual
    // address therefore routes to an unroutable sentinel (loopback port 9, discard): every request
    // fails visibly with a connection error instead of going direct, while the connection test and
    // NetworkAppSettings.Validate() report the configuration problem explicitly.
    private static readonly Uri InvalidManualProxyUri = new("http://127.0.0.1:9/");

    internal static bool IsManualBypassed(Uri destination, NetworkAppSettings settings)
    {
        if (destination.IsLoopback)
        {
            return true;
        }

        string host = destination.Host;
        foreach (string entry in settings.BypassList)
        {
            if (MatchesBypassEntry(entry, host, destination.Port))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesBypassEntry(string entry, string host, int port)
    {
        string pattern = entry;
        int? entryPort = null;
        int separator = entry.LastIndexOf(':');
        // A trailing ":digits" is a port qualifier; IPv6 literals keep their brackets.
        if (separator > 0 && !entry.EndsWith(']') &&
            int.TryParse(entry[(separator + 1)..], out int parsedPort))
        {
            pattern = entry[..separator];
            entryPort = parsedPort;
        }

        if (entryPort is not null && entryPort != port)
        {
            return false;
        }

        pattern = pattern.TrimStart('.').Trim();
        if (pattern.Length == 0)
        {
            return false;
        }

        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        string regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(host, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Per-request proxy resolution driven by the policy's live settings. Returning the
    /// destination URI itself signals "no proxy" to <see cref="SocketsHttpHandler"/>.
    /// </summary>
    private sealed class HostWebProxy(HttpTransportPolicy owner) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = new ProxyCredentialSource(owner);

        public Uri? GetProxy(Uri destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            return owner.ResolveProxy(destination);
        }

        public bool IsBypassed(Uri host)
        {
            ArgumentNullException.ThrowIfNull(host);
            return owner.ResolveIsBypassed(host);
        }
    }

    /// <summary>Credentials facade queried by SocketsHttpHandler for proxy authentication (407).</summary>
    private sealed class ProxyCredentialSource(HttpTransportPolicy owner) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri? uri, string? authType)
        {
            return owner.GetCredentialsFor(uri, authType);
        }
    }
}
