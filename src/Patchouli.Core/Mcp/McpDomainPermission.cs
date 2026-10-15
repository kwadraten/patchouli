namespace Patchouli.Core.Mcp;

public sealed record McpDomainPermission(string Domain, string Verb, bool Enabled);

/// <summary>Device-local capabilities for the VFS roots and the root discovery resource.</summary>
public static class McpPermissionPolicy
{
    public static IReadOnlyList<string> Domains { get; } =
        ["/", "items", "texts", "translations", "csl-styles", "runs", "workflows", "library.yml"];

    public static IReadOnlyList<string> Verbs { get; } = ["find", "fetch", "put", "cite", "send"];

    /// <summary>Resource capabilities, independent of the user's authorization choices.</summary>
    public static bool Supports(string domain, string verb)
    {
        return domain switch
        {
            "/" => verb == "find",
            "items" => verb is "find" or "fetch" or "put" or "cite",
            "texts" => verb is "find" or "fetch" or "cite" or "put",
            "translations" or "csl-styles" => verb is "find" or "fetch" or "put",
            "runs" or "workflows" => verb is "find" or "fetch" or "send",
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
        if (settings.ToolOverrides.Any(value => value.ToolName == "patchouli." + verb && !value.Enabled))
        {
            return false;
        }

        string domain = DomainOf(uri);
        return !settings.DomainPermissions.Any(value =>
            (value.Domain == domain || (domain == "library.yml" && value.Domain == "library.toon")) &&
            value.Verb == verb && !value.Enabled);
    }
}
