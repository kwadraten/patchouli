namespace Patchouli.Core.Mcp;

public sealed record McpDomainPermission(string Domain, string Verb, bool Enabled);

/// <summary>Device-local capabilities for the VFS roots and the root discovery resource.</summary>
public static class McpPermissionPolicy
{
    public static IReadOnlyList<string> RunSendDomains { get; } =
        Actions.RunEndpointCatalog.Domains.Select(domain => "runs/" + domain).ToArray();

    public static IReadOnlyList<string> Domains { get; } =
        new[] { "/", "items", "texts", "translations", "csl-styles", "runs", "library.yml" }.Concat(RunSendDomains)
            .ToArray();

    public static IReadOnlyList<string> Verbs { get; } = ["find", "fetch", "put", "cite", "send", "add", "view"];

    /// <summary>Resource capabilities, independent of the user's authorization choices.</summary>
    public static bool Supports(string domain, string verb)
    {
        if (verb == "send")
        {
            return RunSendDomains.Contains(domain);
        }

        return domain switch
        {
            "/" => verb == "find",
            "items" => verb is "find" or "fetch" or "put" or "cite" or "add",
            "texts" => verb is "find" or "fetch" or "cite" or "put" or "add" or "view",
            "translations" => verb is "find" or "fetch" or "put" or "view",
            "csl-styles" => verb is "find" or "fetch" or "put" or "add",
            "runs" => verb is "find" or "fetch",
            "library.yml" => verb is "find" or "fetch",
            _ => false
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

        if (settings.ToolOverrides.Any(value => value.ToolName == "patchouli." + verb && !value.Enabled))
        {
            return false;
        }

        if (settings.DomainPermissions.Any(value =>
                (value.Domain == domain || (domain == "library.yml" && value.Domain == "library.toon")) &&
                value.Verb == verb && !value.Enabled))
        {
            return false;
        }

        return true;
    }
}
