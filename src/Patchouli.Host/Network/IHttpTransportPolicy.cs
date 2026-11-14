using System.Net;
using Patchouli.Core.Settings;

namespace Patchouli.Host.Network;

/// <summary>
/// The single host policy for every external HTTP transport (issue #56). All outbound
/// <see cref="SocketsHttpHandler"/> instances for downloads, catalogs, OCR providers, LLM calls,
/// agent installs and updates are configured through <see cref="ConfigureHandler"/> so proxy mode,
/// proxy credentials and certificate validation stay consistent. Loopback transports
/// (RuntimeHostCoordinator, the CLI MCP client) never go through this policy.
/// </summary>
public interface IHttpTransportPolicy
{
    /// <summary>The live network settings snapshot; <see cref="Update"/> swaps it atomically.</summary>
    NetworkAppSettings Settings { get; }

    /// <summary>
    /// Applies the current proxy configuration to <paramref name="handler"/>: <c>UseProxy</c>,
    /// <c>Proxy</c> and <c>DefaultProxyCredentials</c>. The injected proxy object resolves the
    /// mode per request, so later <see cref="Update"/> calls take effect without rebuilding the
    /// handler. Certificate validation is never relaxed — an intercepting proxy must put its CA
    /// into the operating-system trust store.
    /// </summary>
    void ConfigureHandler(SocketsHttpHandler handler);

    /// <summary>Swaps the live settings snapshot and drops any cached proxy credentials.</summary>
    void Update(NetworkAppSettings settings);

    /// <summary>Reloads the stored proxy password after a credential-store write.</summary>
    void InvalidateCredentials();
}
