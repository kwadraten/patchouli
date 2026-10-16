namespace Patchouli.Core.Mcp;

/// <summary>Root-relative VFS addressing shared by permissions and resource dispatch.</summary>
public static class McpVfsAddress
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
        {
            return false;
        }

        string path = value.Trim();
        if (path.Length == 0 || path.Contains('\\') || path.Contains('#') || path.Contains('%') ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        const string prefix = "patchouli://";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[prefix.Length..];
        }
        else
        {
            if (path.Contains(':'))
            {
                return false;
            }

            if (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path[2..];
            }

            if (path.StartsWith('/'))
            {
                path = path[1..];
            }
        }

        int domainEnd = path.IndexOfAny(['/', '?']);
        string domain = domainEnd < 0 ? path : path[..domainEnd];
        path = domain.ToLowerInvariant() + (domainEnd < 0 ? string.Empty : path[domainEnd..]);
        string resourcePath = path.Split('?')[0];
        if (resourcePath.Split('/').Any(part => part is "." or "..") ||
            resourcePath.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = resourcePath.Split('/');
        if (!path.Contains('?') && !path.EndsWith('/') &&
            ((parts.Length == 1 &&
              parts[0] is "items" or "texts" or "translations" or "csl-styles" or "runs" or "workflows") ||
             (parts.Length == 2 && parts[0] is "texts" or "translations" && Guid.TryParseExact(parts[1], "D", out _))))
        {
            path += "/";
        }

        normalized = prefix + path;
        return true;
    }
}
