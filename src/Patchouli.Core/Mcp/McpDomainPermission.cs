namespace Patchouli.Core.Mcp;

public sealed record McpDomainPermission(string Domain, string Verb, bool Enabled);

/// <summary>Device-local capabilities for the VFS roots and the root discovery resource.</summary>
public static class McpPermissionPolicy
{
    public static IReadOnlyList<string> RunSendDomains { get; } =
        Actions.RunEndpointCatalog.Domains.Select(domain => "runs/" + domain).ToArray();

    public static IReadOnlyList<string> Domains { get; } =
        new[] { "/", "items", "texts", "images", "translations", "csl-styles", "runs", "library.yml" }
            .Concat(RunSendDomains)
            .ToArray();

    public static IReadOnlyList<string> Verbs { get; } = ["find", "fetch", "put", "cite", "send", "add"];

    /// <summary>Resource capabilities, independent of the user's authorization choices.</summary>
    public static bool Supports(string domain, string verb)
    {
        if (verb == "send")
        {
            return RunSendDomains.Contains(domain) || domain == "plugin" ||
                   domain.StartsWith("plugin/", StringComparison.Ordinal);
        }

        return domain switch
        {
            "/" => verb == "find",
            "items" => verb is "find" or "fetch" or "put" or "cite" or "add",
            "texts" => verb is "find" or "fetch" or "cite" or "put" or "add",
            "images" => verb is "find" or "fetch",
            "translations" => verb is "find" or "fetch" or "put",
            "csl-styles" => verb is "find" or "fetch" or "put" or "add",
            "runs" => verb is "find" or "fetch",
            "library.yml" => verb is "find" or "fetch" or "put",
            _ => domain == "plugin" || domain.StartsWith("plugin/", StringComparison.Ordinal)
                ? verb is "find" or "fetch" or "view"
                : false
        };
    }

    public static string DomainOf(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri == "patchouli://")
        {
            return "/";
        }

        const string prefix = "patchouli://";
        if (!McpVfsAddress.TryNormalize(uri, out string normalized))
        {
            return string.Empty;
        }

        string domain = normalized[prefix.Length..].Split('/', '?', '#')[0];
        return domain == "library.toon" ? "library.yml" : domain;
    }

    /// <summary>
    /// Whether a normalized plugin URI is governed by the dev-plugin registry rather than
    /// the built-in domain table. Plugin approval is device-explicit (the registry entry);
    /// the policy layer only enforces tool switches and explicit domain denials, so an
    /// unregistered plugin is never granted by the legacy unknown-domain default.
    /// </summary>
    public static bool IsPluginAddress(string normalizedUri)
    {
        return normalizedUri.StartsWith("patchouli://plugin/", StringComparison.Ordinal) ||
               string.Equals(normalizedUri, "patchouli://plugin", StringComparison.Ordinal);
    }

    private static string PluginDomainOf(string normalizedAddress)
    {
        string[] parts = normalizedAddress["patchouli://".Length..].Split('/', '?', '#');
        return parts.Length >= 2 && parts[0] == "plugin" && parts[1].Length > 0
            ? "plugin/" + parts[1]
            : "plugin";
    }

    public static bool Allows(McpServerSettings settings, string verb, string? uri)
    {
        string domain = DomainOf(uri);

        if (verb == "send")
        {
            if (!McpVfsAddress.TryNormalize(uri, out string address))
            {
                return false;
            }

            string[] parts = address["patchouli://".Length..].Split('/');
            if (parts[0] == "plugin")
            {
                // Plugin send requires the send switch plus no explicit denial; the dev-plugin
                // registry allowed_verbs entry is the actual grant and is enforced by the
                // plugin mount, not by this settings-only check.
                string pluginSendDomain = PluginDomainOf(address);
                return !settings.ToolOverrides.Any(value => value.ToolName == "patchouli.send" && !value.Enabled) &&
                       !settings.DomainPermissions.Any(value =>
                           (value.Domain == "plugin" || value.Domain == pluginSendDomain) &&
                           value.Verb == "send" && !value.Enabled);
            }

            string child = parts.Length >= 2 && parts[0] == "runs" ? "runs/" + parts[1] : string.Empty;
            return RunSendDomains.Contains(child) &&
                   !settings.ToolOverrides.Any(value => value.ToolName == "patchouli.send" && !value.Enabled) &&
                   settings.DomainPermissions.Any(value =>
                       value.Domain == child && value.Verb == "send" && value.Enabled) &&
                   !settings.DomainPermissions.Any(value =>
                       value.Domain == child && value.Verb == "send" && !value.Enabled);
        }

        if (!Verbs.Contains(verb))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri) && verb is not "find" and not "fetch" &&
            McpVfsAddress.TryNormalize(uri, out string normalized) && IsPluginAddress(normalized))
        {
            // Plugin mounts support find/fetch only; put/cite/add never apply.
            return false;
        }

        if (settings.ToolOverrides.Any(value => value.ToolName == "patchouli." + verb && !value.Enabled))
        {
            return false;
        }

        string? pluginDomain = domain == "plugin" && uri is not null &&
                               McpVfsAddress.TryNormalize(uri, out string pluginAddress)
            ? PluginDomainOf(pluginAddress)
            : null;
        if (settings.DomainPermissions.Any(value =>
                (value.Domain == domain || (domain == "library.yml" && value.Domain == "library.toon") ||
                 (pluginDomain is not null && value.Domain == pluginDomain)) &&
                value.Verb == verb && !value.Enabled))
        {
            return false;
        }

        return true;
    }
}
