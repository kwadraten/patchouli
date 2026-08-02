namespace Patchouli.Mcp;

/// <summary>
/// Defense-in-depth text sanitizer for diagnostics. It redacts host paths, file URLs, cache
/// and model paths, and secret-like tokens while preserving canonical <c>patchouli://</c>
/// resource URIs. The v3 structured surface never places free-text exception details in
/// responses; this only guards diagnostic output (for example server logs).
/// </summary>
public static class McpOutputSanitizer
{
    private const string FileUrlPattern = @"file://\S+";
    private const string DrivePathPattern = @"(?<![A-Za-z0-9_:/])[A-Za-z]:[\\/][^\s""']+";
    private const string UncPathPattern = @"\\\\[^\s""']+";
    private const string PosixPathPattern = @"(?<![A-Za-z0-9_:/])/[^\s""']+";

    private const string SecretPattern =
        @"(?i)(?:api[_-]?key|provider[_-]?secret|secret|token|sk-[A-Za-z0-9_-]+)\s*[:=_-]\s*[A-Za-z0-9_-]+";

    private const string SensitiveTokenPattern =
        @"(?i)(?:cache[/\\]|page-renders[/\\]|manifest\.json|model_path|[/\\]models[/\\]|staging[/\\])[^\s""']*";

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string sanitized = value;
        foreach (string pattern in new[]
                 {
                     FileUrlPattern, DrivePathPattern, UncPathPattern, PosixPathPattern, SecretPattern,
                     SensitiveTokenPattern
                 })
        {
            sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, pattern, "[redacted]");
        }

        return sanitized;
    }

    public static bool IsSafe(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        return !System.Text.RegularExpressions.Regex.IsMatch(value, DrivePathPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, UncPathPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, PosixPathPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, SecretPattern)
               && !System.Text.RegularExpressions.Regex.IsMatch(value, SensitiveTokenPattern);
    }
}
