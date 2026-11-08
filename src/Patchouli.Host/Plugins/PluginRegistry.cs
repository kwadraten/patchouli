using System.Text.Json;

namespace Patchouli.Host.Plugins;

/// <summary>One validated record of the device plugin registry.</summary>
public sealed record PluginRegistryEntry(
    string ManifestPath,
    bool Enabled,
    IReadOnlyList<string> AllowedVerbs,
    string Source = "dev",
    string? SourceRepository = null,
    string? SourceRevision = null);

/// <summary>The parsed registry file plus per-record rejection diagnostics (never a grant).</summary>
public sealed record PluginRegistrySnapshot(
    IReadOnlyList<PluginRegistryEntry> Entries,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Reads &lt;DataDirectory&gt;/plugins/dev-plugins.json — the device-wide explicit
/// install-and-approval record. The file is a JSON array of
/// { manifest_path, enabled, allowed_verbs }; every record is validated independently and an
/// invalid record never produces a grant. A missing file means no registered plugins.
/// </summary>
public static class PluginRegistry
{
    public const string FileName = "dev-plugins.json";
    private static readonly HashSet<string> ValidVerbs = new(StringComparer.Ordinal)
        { "find", "fetch", "send", "view" };
    private const int MaxRegistryBytes = 64 * 1024;

    public static string RegistryPath(string dataDirectory)
    {
        return Path.Combine(dataDirectory, "plugins", FileName);
    }

    public static PluginRegistrySnapshot Load(string registryPath, bool includeDisabled = false)
    {
        List<PluginRegistryEntry> entries = [];
        List<string> diagnostics = [];
        if (!File.Exists(registryPath))
        {
            return new PluginRegistrySnapshot(entries, diagnostics);
        }

        JsonElement root;
        try
        {
            if (new FileInfo(registryPath).Length > MaxRegistryBytes)
            {
                return new PluginRegistrySnapshot(entries,
                    [$"Registry file exceeds {MaxRegistryBytes} bytes; no plugins were registered."]);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(registryPath));
            root = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PluginRegistrySnapshot(entries,
                [$"Registry file is unreadable ({exception.Message}); no plugins were registered."]);
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return new PluginRegistrySnapshot(entries,
                ["Registry root must be a JSON array; no plugins were registered."]);
        }

        HashSet<string> manifestPaths = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement record in root.EnumerateArray())
        {
            index++;
            PluginRegistryEntry? entry = ParseRecord(record, index, diagnostics);
            if (entry is null || (!includeDisabled && !entry.Enabled))
            {
                continue;
            }

            if (!manifestPaths.Add(entry.ManifestPath))
            {
                diagnostics.Add($"Record {index}: duplicate manifest_path '{entry.ManifestPath}' was ignored.");
                continue;
            }

            entries.Add(entry);
        }

        return new PluginRegistrySnapshot(entries, diagnostics);
    }

    public static void Save(string registryPath, IEnumerable<PluginRegistryEntry> entries)
    {
        string fullPath = Path.GetFullPath(registryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(entries.Select(entry => new
            {
                manifest_path = Path.GetFullPath(entry.ManifestPath),
                enabled = entry.Enabled,
                allowed_verbs = entry.AllowedVerbs.Distinct(StringComparer.Ordinal).ToArray(),
                source = entry.Source,
                source_repository = entry.SourceRepository,
                source_revision = entry.SourceRevision
            }), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json + Environment.NewLine);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static PluginRegistryEntry? ParseRecord(JsonElement record, int index, List<string> diagnostics)
    {
        string Reject(string reason)
        {
            diagnostics.Add($"Record {index}: {reason}");
            return null!;
        }

        if (record.ValueKind != JsonValueKind.Object)
        {
            Reject("entry must be an object.");
            return null;
        }

        string? manifestPath = null;
        bool enabled = false;
        IReadOnlyList<string> verbs = [];
        if (record.TryGetProperty("manifest_path", out JsonElement path) &&
            path.ValueKind == JsonValueKind.String)
        {
            manifestPath = path.GetString();
        }

        if (record.TryGetProperty("enabled", out JsonElement enabledValue) &&
            enabledValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            enabled = enabledValue.GetBoolean();
        }

        if (record.TryGetProperty("allowed_verbs", out JsonElement verbsValue) &&
            verbsValue.ValueKind == JsonValueKind.Array)
        {
            List<string> parsed = [];
            foreach (JsonElement verb in verbsValue.EnumerateArray())
            {
                if (verb.ValueKind == JsonValueKind.String && ValidVerbs.Contains(verb.GetString()!))
                {
                    parsed.Add(verb.GetString()!);
                }
                else
                {
                    Reject("allowed_verbs may only contain find/fetch/send/view.");
                    return null;
                }
            }

            verbs = parsed.Distinct(StringComparer.Ordinal).ToArray();
        }

        string source = OptionalString(record, "source") ?? "dev";
        if (source is not ("dev" or "community"))
        {
            Reject("source must be dev or community.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifestPath) || !Path.IsPathFullyQualified(manifestPath))
        {
            Reject("manifest_path must be an absolute path.");
            return null;
        }

        return new PluginRegistryEntry(Path.GetFullPath(manifestPath), enabled, verbs, source,
            OptionalString(record, "source_repository"), OptionalString(record, "source_revision"));
    }

    private static string? OptionalString(JsonElement record, string name)
    {
        return record.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
