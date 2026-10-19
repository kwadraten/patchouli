namespace Patchouli.Host.Agents;

/// <summary>
/// Resolves the runtime copy of the ACP registry that supplies install offers. The registry is
/// discovered next to the application binary, with a developer fallback to the repository
/// <c>.tmp/acp-registry</c> cache. No configuration or environment override is consulted.
/// </summary>
public static class AgentRegistryLocations
{
    public static string Resolve(string? baseDirectory = null)
    {
        string root = TryFullPath(baseDirectory ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        foreach (string candidate in EnumerateCandidates(root))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(root, "acp-registry", "registry.json");
    }

    private static IEnumerable<string> EnumerateCandidates(string root)
    {
        yield return Path.Combine(root, "acp-registry", "registry.json");
        yield return Path.Combine(root, "registry", "acp-registry.json");

        DirectoryInfo? current = new(root);
        for (int depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
        {
            yield return Path.Combine(current.FullName, ".tmp", "acp-registry", "registry.json");
            yield return Path.Combine(current.FullName, "acp-registry", "registry.json");
        }
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                              PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
