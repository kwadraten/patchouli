namespace Patchouli.Host.Downloads;

/// <summary>
/// Sanitized failure categories for download transports. The text never includes the request
/// URL, proxy address or exception message, so it stays safe for public surfaces and logs.
/// </summary>
internal static class DownloadNetworkErrors
{
    public static string Describe(HttpRequestException exception)
    {
        return exception.HttpRequestError switch
        {
            HttpRequestError.ProxyTunnelError => exception.StatusCode is { } status
                ? $"proxy tunnel error (proxy returned HTTP {(int)status})"
                : "proxy tunnel error",
            HttpRequestError.SecureConnectionError => "TLS handshake failed",
            HttpRequestError.NameResolutionError => "name resolution failed",
            HttpRequestError.ConnectionError => "connection failed",
            HttpRequestError.UserAuthenticationError => "authentication failed",
            _ => "network error"
        };
    }
}
