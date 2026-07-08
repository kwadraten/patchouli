using System.Collections.Generic;
using System.Text.Json;
using Patchouli.Core.Import;

namespace Patchouli.UI;

public sealed record AppRuntimeOptions(string RuntimeDatabasePath, string DefaultSyncRoot, string DefaultStagingRoot, string LogDirectory, string FileSearchRoot, bool RememberLastDatabase = true, bool UseMockOcrOnly = false)
{
    public static AppRuntimeOptions FromAppSettings(string? settingsPath = null) =>
        PatchouliAppSettings.Load(settingsPath).Runtime;

    public static AppRuntimeOptions Default()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Patchouli");
        return new(
            Path.Combine(root, "patchouli-runtime.sqlite"),
            Path.Combine(root, "sync"),
            Path.Combine(root, "staging"),
            Path.Combine(root, "logs"),
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
        new(31337, true, string.Empty, new Dictionary<string, bool>());
}

public sealed record UiPreferences(
    Dictionary<string, bool> LibraryGridVisibleColumns)
{
    public static UiPreferences Default() =>
        new(new Dictionary<string, bool>());
}

public sealed record PatchouliAppSettings(AppRuntimeOptions Runtime, MinerUAppSettings MinerU, McpAppSettings Mcp, UiPreferences Ui)
{
    public static PatchouliAppSettings Default() =>
        new(AppRuntimeOptions.Default(), MinerUAppSettings.Default(), McpAppSettings.Default(), UiPreferences.Default());

    public static string ResolvePath(string? settingsPath = null) =>
        string.IsNullOrWhiteSpace(settingsPath)
            ? AppSettingsLocator.FindNearest("appsettings.json") ?? Path.Combine(Environment.CurrentDirectory, "appsettings.json")
            : settingsPath;

    public static PatchouliAppSettings Load(string? settingsPath = null)
    {
        var defaults = Default();
        var path = ResolvePath(settingsPath);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return defaults;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var patchouli = GetSection(root, "Patchouli");
        var minerU = GetSection(root, "MinerU");
        var mcp = GetSection(root, "Mcp");
        var ui = GetSection(root, "Ui");

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
                ReadStringBoolDict(ui, "LibraryGridVisibleColumns", defaults.Ui.LibraryGridVisibleColumns)));
    }

    public void Save(string? settingsPath = null)
    {
        var path = ResolvePath(settingsPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var payload = new
        {
            Patchouli = new
            {
                Runtime.RuntimeDatabasePath,
                Runtime.DefaultSyncRoot,
                Runtime.DefaultStagingRoot,
                Runtime.LogDirectory,
                Runtime.FileSearchRoot,
                Runtime.RememberLastDatabase,
                Runtime.UseMockOcrOnly
            },
            MinerU = new
            {
                MinerU.BaseUrl,
                MinerU.ModelVersion,
                MinerU.IsOcr,
                MinerU.EnableTable,
                MinerU.EnableFormula,
                MinerU.Token
            },
            Mcp = new
            {
                Mcp.Port,
                Mcp.BlockExternalAccess,
                Mcp.ServerToken,
                Mcp.DisabledTools
            },
            Ui = new
            {
                Ui.LibraryGridVisibleColumns
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
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

    private static string ExpandPath(string value)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return value
            .Replace("{LocalAppData}", localAppData, StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);
    }
}

internal static class AppSettingsLocator
{
    public static string? FindNearest(string fileName)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }
        }

        return null;
    }
}
