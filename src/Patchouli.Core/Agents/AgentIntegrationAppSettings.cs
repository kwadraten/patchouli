namespace Patchouli.Core.Agents;

/// <summary>
/// Device-local Agent integration settings. This is the JSON <c>Agents</c> section of the application
/// settings file. The settings never carry credentials: each agent keeps its own native authentication.
/// </summary>
/// <param name="Enabled">When false the integration is temporarily disabled; detection stays available.</param>
/// <param name="SelectedAgentId">Catalog id the UI should start; unknown ids normalize to the catalog default.</param>
/// <param name="CustomPaths">Per-agent executable override, keyed by catalog id.</param>
public sealed record AgentIntegrationAppSettings(
    bool Enabled = true,
    string SelectedAgentId = AgentIntegrationAppSettings.DefaultAgentId,
    IReadOnlyDictionary<string, string>? CustomPaths = null)
{
    /// <summary>OpenCode is the first supported integration and the catalog default.</summary>
    public const string DefaultAgentId = "opencode";

    /// <summary>Immutable empty path map used when no override is configured.</summary>
    public static IReadOnlyDictionary<string, string> EmptyCustomPaths { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Fresh settings: integration available, OpenCode selected, no path overrides.</summary>
    public static AgentIntegrationAppSettings Default()
    {
        return new AgentIntegrationAppSettings();
    }

    /// <summary>Returns the configured override for an agent, or null when there is none.</summary>
    public string? FindCustomPath(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || CustomPaths is null)
        {
            return null;
        }

        string normalized = agentId.Trim();
        foreach ((string key, string value) in CustomPaths)
        {
            if (string.Equals(key.Trim(), normalized, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Returns a copy with one agent path override set, or removed when the value is blank.</summary>
    public AgentIntegrationAppSettings WithCustomPath(string agentId, string? path)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return this;
        }

        SortedDictionary<string, string> paths = new(StringComparer.Ordinal);
        if (CustomPaths is not null)
        {
            foreach ((string key, string value) in CustomPaths)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    paths[key.Trim()] = value.Trim();
                }
            }
        }

        string normalizedId = agentId.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            foreach (string key in paths.Keys
                         .Where(key => string.Equals(key, normalizedId, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                paths.Remove(key);
            }
        }
        else
        {
            foreach (string key in paths.Keys
                         .Where(key => string.Equals(key, normalizedId, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                paths.Remove(key);
            }

            paths[normalizedId] = path.Trim();
        }

        return this with { CustomPaths = paths };
    }

    /// <summary>
    /// Normalizes a persisted instance: the selected id falls back to the catalog default and path
    /// overrides are trimmed, de-duplicated case-insensitively, and emitted in a stable order.
    /// </summary>
    public AgentIntegrationAppSettings Normalize()
    {
        AgentCatalogEntry? selected = AgentCatalog.Find(SelectedAgentId);
        string selectedId = selected?.Id ?? AgentCatalog.Default?.Id ?? DefaultAgentId;
        SortedDictionary<string, string> paths = new(StringComparer.Ordinal);
        if (CustomPaths is not null)
        {
            foreach ((string key, string value) in CustomPaths)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                string normalizedKey = key.Trim();
                if (paths.Keys.Any(existing =>
                        string.Equals(existing, normalizedKey, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                paths[normalizedKey] = value.Trim();
            }
        }

        return this with
        {
            SelectedAgentId = selectedId,
            CustomPaths = paths
        };
    }
}
