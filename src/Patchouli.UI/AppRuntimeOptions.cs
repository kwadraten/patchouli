using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Import;

namespace Patchouli.UI;

public sealed record AppRuntimeOptions(string RuntimeDatabasePath, string DefaultSyncRoot, string DefaultStagingRoot, string LogDirectory, string FileSearchRoot, bool RememberLastDatabase = true, bool UseMockOcrOnly = false)
{
    public static AppRuntimeOptions FromAppSettings(string? settingsPath = null) =>
        PatchouliAppSettings.Load(settingsPath).Runtime;

    public static AppRuntimeOptions Default(IAppPaths? appPaths = null)
    {
        var locations = (appPaths ?? new PlatformAppPaths()).Resolve();
        var root = locations.DataDirectory;
        return new(
            Path.Combine(root, "patchouli-runtime.sqlite"),
            Path.Combine(root, "sync"),
            Path.Combine(root, "staging"),
            locations.LogDirectory,
            Path.Combine(root, "search"));
    }
}

public sealed record MinerUAppSettings(
    string BaseUrl,
    string ModelVersion,
    bool IsOcr,
    bool EnableTable,
    bool EnableFormula,
    string Token = "")
{
    public static MinerUAppSettings Default() =>
        new("https://mineru.net", "vlm", true, true, true);

    public MinerUConfiguration ToConfiguration(string token) =>
        new(token, BaseUrl, ModelVersion, IsOcr, EnableTable, EnableFormula);
}

public sealed record McpAppSettings(
    int Port,
    bool BlockExternalAccess,
    string ServerToken,
    Dictionary<string, bool> DisabledTools)
{
    public static McpAppSettings Default() =>
        new(4536, true, string.Empty, new Dictionary<string, bool>());
}

public sealed record UiPreferences(
    Dictionary<string, bool> LibraryGridVisibleColumns,
    Dictionary<string, double> LibraryGridColumnWidths,
    Dictionary<string, int> LibraryGridColumnOrder,
    bool ShowLibraryLeftSidebar = true,
    bool ShowLibraryRightSidebar = true)
{
    public static UiPreferences Default() =>
        new(new Dictionary<string, bool>(), new Dictionary<string, double>(), new Dictionary<string, int>(), true, true);
}

public sealed record MetadataSourcePreference(string SourceId, bool Enabled);

public sealed record MetadataLookupAppSettings(IReadOnlyList<MetadataSourcePreference> Sources)
{
    public static MetadataLookupAppSettings Default() => new(
    [
        new("calis", true),
        new("nlc", true),
        new("ndl", true),
        new("cinii", true),
        new("library-of-congress", true),
        new("dnb", true),
        new("bnf", true),
        new("pmc-id-converter", true),
        new("pubmed", true),
        new("arxiv", true),
        new("open-library", true),
        new("datacite", true),
        new("crossref", true),
        new("openalex", true),
        new("semantic-scholar", true),
        new("google-books", false)
    ]);

    public static MetadataLookupAppSettings MergeWithDefaults(IEnumerable<MetadataSourcePreference>? saved)
    {
        var merged = new List<MetadataSourcePreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in saved ?? [])
        {
            var id = source.SourceId?.Trim();
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                merged.Add(new MetadataSourcePreference(id, source.Enabled));
        }

        foreach (var source in Default().Sources)
        {
            if (seen.Add(source.SourceId))
                merged.Add(source);
        }

        return new MetadataLookupAppSettings(merged);
    }
}

public sealed record PatchouliAppSettings(AppRuntimeOptions Runtime, MinerUAppSettings MinerU, McpAppSettings Mcp, UiPreferences Ui)
{
    public MetadataLookupAppSettings MetadataLookup { get; init; } = MetadataLookupAppSettings.Default();

    public static PatchouliAppSettings Default(IAppPaths? appPaths = null) =>
        new(AppRuntimeOptions.Default(appPaths), MinerUAppSettings.Default(), McpAppSettings.Default(), UiPreferences.Default());

    public static string ResolvePath(string? settingsPath = null) =>
        string.IsNullOrWhiteSpace(settingsPath) ? new PlatformAppPaths().Resolve().UserSettingsPath : Path.GetFullPath(settingsPath);

    public static PatchouliAppSettings Load(string? settingsPath = null)
    {
        if (!string.IsNullOrWhiteSpace(settingsPath)) return LoadFile(Default(), Path.GetFullPath(settingsPath));
        return Load(new PlatformAppPaths());
    }

    public static PatchouliAppSettings Load(IAppPaths appPaths)
    {
        var locations = appPaths.Resolve();
        var bundled = LoadFile(Default(appPaths), locations.BundledDefaultsPath);
        return LoadFile(bundled, locations.UserSettingsPath);
    }

    private static PatchouliAppSettings LoadFile(PatchouliAppSettings defaults, string path)
    {

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return defaults;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var patchouli = GetSection(root, "Patchouli");
            var minerU = GetSection(root, "MinerU");
            var mcp = GetSection(root, "Mcp");
            var ui = GetSection(root, "Ui");
            var metadataLookup = GetSection(root, "MetadataLookup");

            return new PatchouliAppSettings(
                new AppRuntimeOptions(
                    ExpandPath(ReadString(patchouli, "RuntimeDatabasePath", defaults.Runtime.RuntimeDatabasePath)),
                    ExpandPath(ReadString(patchouli, "DefaultSyncRoot", defaults.Runtime.DefaultSyncRoot)),
                    ExpandPath(ReadString(patchouli, "DefaultStagingRoot", defaults.Runtime.DefaultStagingRoot)),
                    ExpandPath(ReadString(patchouli, "LogDirectory", defaults.Runtime.LogDirectory)),
                    ExpandPath(ReadString(patchouli, "FileSearchRoot", defaults.Runtime.FileSearchRoot)),
                    ReadBool(patchouli, "RememberLastDatabase", defaults.Runtime.RememberLastDatabase),
                    ReadBool(patchouli, "UseMockOcrOnly", defaults.Runtime.UseMockOcrOnly)),
                new MinerUAppSettings(
                    ReadString(minerU, "BaseUrl", defaults.MinerU.BaseUrl),
                    ReadString(minerU, "ModelVersion", defaults.MinerU.ModelVersion),
                    ReadBool(minerU, "IsOcr", defaults.MinerU.IsOcr),
                    ReadBool(minerU, "EnableTable", defaults.MinerU.EnableTable),
                    ReadBool(minerU, "EnableFormula", defaults.MinerU.EnableFormula),
                    ReadString(minerU, "Token", defaults.MinerU.Token).Trim()),
                new McpAppSettings(
                    ReadInt(mcp, "Port", defaults.Mcp.Port),
                    ReadBool(mcp, "BlockExternalAccess", defaults.Mcp.BlockExternalAccess),
                    ReadString(mcp, "ServerToken", defaults.Mcp.ServerToken).Trim(),
                    ReadStringBoolDict(mcp, "DisabledTools", defaults.Mcp.DisabledTools)),
                new UiPreferences(
                    ReadStringBoolDict(ui, "LibraryGridVisibleColumns", defaults.Ui.LibraryGridVisibleColumns),
                    ReadStringDoubleDict(ui, "LibraryGridColumnWidths", defaults.Ui.LibraryGridColumnWidths),
                    ReadStringIntDict(ui, "LibraryGridColumnOrder", defaults.Ui.LibraryGridColumnOrder),
                    ReadBool(ui, "ShowLibraryLeftSidebar", defaults.Ui.ShowLibraryLeftSidebar),
                    ReadBool(ui, "ShowLibraryRightSidebar", defaults.Ui.ShowLibraryRightSidebar)))
            {
                MetadataLookup = MetadataLookupAppSettings.MergeWithDefaults(ReadMetadataSources(metadataLookup))
            };
        }
        catch (JsonException)
        {
            return defaults;
        }
        catch (IOException)
        {
            return defaults;
        }
        catch (UnauthorizedAccessException)
        {
            return defaults;
        }
    }

    public void Save(string? settingsPath = null)
    {
        var path = ResolvePath(settingsPath);
        AppPathGuard.ValidateMutablePath(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        JsonObject root;
        try
        {
            root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
                ? existing
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        root["Patchouli"] = JsonSerializer.SerializeToNode(new
            {
                Runtime.RuntimeDatabasePath,
                Runtime.DefaultSyncRoot,
                Runtime.DefaultStagingRoot,
                Runtime.LogDirectory,
                Runtime.FileSearchRoot,
                Runtime.RememberLastDatabase,
                Runtime.UseMockOcrOnly
            });
        root["MinerU"] = JsonSerializer.SerializeToNode(new
            {
                MinerU.BaseUrl,
                MinerU.ModelVersion,
                MinerU.IsOcr,
                MinerU.EnableTable,
                MinerU.EnableFormula,
                MinerU.Token
            });
        root["Mcp"] = JsonSerializer.SerializeToNode(new
            {
                Mcp.Port,
                Mcp.BlockExternalAccess,
                Mcp.ServerToken,
                Mcp.DisabledTools
            });
        root["Ui"] = JsonSerializer.SerializeToNode(new
            {
                Ui.LibraryGridVisibleColumns,
                Ui.LibraryGridColumnWidths,
                Ui.LibraryGridColumnOrder,
                Ui.ShowLibraryLeftSidebar,
                Ui.ShowLibraryRightSidebar
            });
        root["MetadataLookup"] = JsonSerializer.SerializeToNode(new { MetadataLookup.Sources });

        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static JsonElement? GetSection(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var section)
            ? section
            : null;

    private static string ReadString(JsonElement? section, string name, string fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
            return fallback;

        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static bool ReadBool(JsonElement? section, string name, bool fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
            return fallback;

        return element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    private static int ReadInt(JsonElement? section, string name, int fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
            return fallback;

        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    private static Dictionary<string, bool> ReadStringBoolDict(JsonElement? section, string name, Dictionary<string, bool> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
            return fallback;

        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return fallback;

        var dict = new Dictionary<string, bool>();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                dict[property.Name] = property.Value.GetBoolean();
            }
        }
        return dict;
    }

    private static Dictionary<string, double> ReadStringDoubleDict(JsonElement? section, string name, Dictionary<string, double> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
            return fallback;

        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return fallback;

        var dict = new Dictionary<string, double>();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
            {
                dict[property.Name] = number;
            }
        }
        return dict;
    }

    private static Dictionary<string, int> ReadStringIntDict(JsonElement? section, string name, Dictionary<string, int> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
            return fallback;

        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return fallback;

        var dict = new Dictionary<string, int>();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number))
            {
                dict[property.Name] = number;
            }
        }
        return dict;
    }

    private static IReadOnlyList<MetadataSourcePreference> ReadMetadataSources(JsonElement? section)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("Sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<MetadataSourcePreference>();
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(source, "SourceId", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(id))
                result.Add(new MetadataSourcePreference(id, ReadBool(source, "Enabled", true)));
        }
        return result;
    }

    private static string ExpandPath(string value)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return value
            .Replace("{LocalAppData}", localAppData, StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);
    }
}
