namespace Patchouli.Core.Settings;

/// <summary>Persisted values of <see cref="NetworkAppSettings.ProxyMode"/>.</summary>
public static class NetworkProxyModes
{
    /// <summary>Follow the operating-system proxy (and PAC) resolution that .NET already performs.</summary>
    public const string System = "system";

    /// <summary>Use the explicit proxy address in <see cref="NetworkAppSettings.ProxyAddress"/>.</summary>
    public const string Manual = "manual";

    /// <summary>Always connect directly; the system proxy is not consulted.</summary>
    public const string None = "none";

    public static string Normalize(string? value)
    {
        return string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase)
            ? Manual
            : string.Equals(value, None, StringComparison.OrdinalIgnoreCase)
                ? None
                : System;
    }
}

/// <summary>
/// Device-local network/proxy preferences persisted in the settings JSON "Network" section
/// (issue #56). No credential material is stored here: the proxy password lives in
/// <see cref="Patchouli.Core.Credentials.ICredentialStore"/> under
/// <c>ProviderIds.NetworkProxy</c>, and the UI should mark <see cref="HasProxyPassword"/> instead
/// of ever displaying it. <see cref="ProxyUsername"/> is metadata, not a secret, but diagnostics
/// never print it.
/// </summary>
public sealed record NetworkAppSettings(
    string ProxyMode,
    string ProxyAddress,
    string ProxyUsername,
    bool HasProxyPassword,
    IReadOnlyList<string> BypassList)
{
    public static NetworkAppSettings Default()
    {
        return new NetworkAppSettings(NetworkProxyModes.System, "", "", false, []);
    }

    /// <summary>Supported manual proxy schemes. <c>socks5h</c> maps onto .NET socks5, which already resolves names on the proxy.</summary>
    public static bool IsSupportedProxyScheme(string scheme)
    {
        return scheme is "http" or "https" or "socks5" or "socks5h";
    }

    /// <summary>
    /// Normalizes the persisted values: the mode falls back to <see cref="NetworkProxyModes.System"/>
    /// for unknown values and blank bypass entries are dropped (order preserved).
    /// </summary>
    public NetworkAppSettings Normalize()
    {
        return this with
        {
            ProxyMode = NetworkProxyModes.Normalize(ProxyMode),
            ProxyAddress = ProxyAddress?.Trim() ?? "",
            ProxyUsername = ProxyUsername?.Trim() ?? "",
            BypassList = (BypassList ?? [])
            .Select(entry => entry?.Trim() ?? "")
            .Where(entry => entry.Length > 0)
            .ToArray()
        };
    }

    /// <summary>
    /// Parses the manual proxy address. Returns null for empty input and for anything that is not
    /// an absolute http/https/socks5/socks5h URI. Embedded credentials are rejected — credentials
    /// always travel through the credential store, never the URL, so they can never leak into logs.
    /// </summary>
    public static Uri? TryCreateManualProxyUri(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) ||
            !Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri) ||
            !IsSupportedProxyScheme(uri.Scheme))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        // socks5h (remote DNS, the curl name) is what .NET socks5 already does.
        return string.Equals(uri.Scheme, "socks5h", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Scheme = "socks5" }.Uri
            : uri;
    }

    /// <summary>
    /// Field-level validation feedback for the settings UI (issue #56). Returns machine-readable
    /// codes rather than throwing; the runtime treats an absent manual address as "not configured
    /// yet" while a malformed one is a hard error.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        NetworkAppSettings normalized = Normalize();
        if (!string.Equals(normalized.ProxyMode, NetworkProxyModes.Manual, StringComparison.Ordinal))
        {
            return [];
        }

        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(normalized.ProxyAddress))
        {
            errors.Add("proxy_address_required");
        }
        else
        {
            if (!Uri.TryCreate(normalized.ProxyAddress, UriKind.Absolute, out Uri? uri))
            {
                errors.Add("proxy_address_invalid");
            }
            else
            {
                if (!IsSupportedProxyScheme(uri.Scheme))
                {
                    errors.Add("proxy_scheme_unsupported");
                }

                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    errors.Add("proxy_credentials_in_url");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Log-safe description of the effective proxy configuration. Never contains the proxy URL,
    /// username or password; the host and port are intentionally dropped too because in an
    /// enterprise environment they identify internal infrastructure.
    /// </summary>
    public string DescribeForDiagnostics()
    {
        NetworkAppSettings normalized = Normalize();
        return normalized.ProxyMode switch
        {
            NetworkProxyModes.Manual => string.IsNullOrWhiteSpace(normalized.ProxyAddress)
                ? "manual (no address)"
                : "manual (address set)",
            NetworkProxyModes.None => "none",
            _ => "system"
        };
    }
}
