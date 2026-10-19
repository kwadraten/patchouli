namespace Patchouli.Core.Mcp;

public sealed record McpDomainPermission(string Domain, string Verb, bool Enabled);

/// <summary>Device-local capabilities for the VFS roots and the root discovery resource.</summary>
public static class McpPermissionPolicy
{
    public static IReadOnlyList<string> Domains { get; } =
        ["/", "items", "texts", "translations", "csl-styles", "runs", "actions", "library.yml"];

    public static IReadOnlyList<string> Verbs { get; } =
    [
        "find", "fetch", "put", "cite", "add", "view", "ocr.start", "ocr.cancel", "ocr.retry",
        "translation.start", "translation.cancel"
    ];

    /// <summary>Resource capabilities, independent of the user's authorization choices.</summary>
    public static bool Supports(string domain, string verb)
    {
        return domain switch
        {
            "/" => verb == "find",
            "items" => verb is "find" or "fetch" or "put" or "cite" or "add",
            "texts" => verb is "find" or "fetch" or "cite" or "put" or "add" or "view" or "ocr.start"
                or "translation.start",
            "translations" => verb is "find" or "fetch" or "put" or "view",
            "csl-styles" => verb is "find" or "fetch" or "put" or "add",
            "runs" => verb is "find" or "fetch" or "ocr.cancel" or "ocr.retry" or "translation.cancel",
            "actions" => verb is "find" or "fetch",
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

        if (verb is "ocr.start" or "ocr.cancel" or "ocr.retry" or "translation.start" or "translation.cancel")
        {
            if (!Supports(domain, verb))
            {
                return false;
            }

            bool sendAllowed =
                !settings.ToolOverrides.Any(value => value.ToolName == "patchouli.send" && !value.Enabled);
            if (!sendAllowed)
            {
                return false;
            }

            if (settings.DomainPermissions.Any(value => value.Domain == domain && value.Verb == verb && !value.Enabled))
            {
                return false;
            }

            bool domainAllowed = settings.DomainPermissions.Any(value =>
                (value.Domain == domain || (domain == "library.yml" && value.Domain == "library.toon")) &&
                value.Verb == verb && value.Enabled);
            return domainAllowed;
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
