using System.Text.Json;
using Patchouli.Core.Import;

namespace Patchouli.UI;

public sealed record AppRuntimeOptions(string RuntimeDatabasePath, string DefaultSyncRoot, string DefaultStagingRoot, string LogDirectory, bool UseMockOcrOnly = true)
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
            Path.Combine(root, "logs"));
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

public sealed record PatchouliAppSettings(AppRuntimeOptions Runtime, MinerUAppSettings MinerU)
{
    public static PatchouliAppSettings Default() =>
        new(AppRuntimeOptions.Default(), MinerUAppSettings.Default());

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

        return new PatchouliAppSettings(
            new AppRuntimeOptions(
                ExpandPath(ReadString(patchouli, "RuntimeDatabasePath", defaults.Runtime.RuntimeDatabasePath)),
                ExpandPath(ReadString(patchouli, "DefaultSyncRoot", defaults.Runtime.DefaultSyncRoot)),
                ExpandPath(ReadString(patchouli, "DefaultStagingRoot", defaults.Runtime.DefaultStagingRoot)),
                ExpandPath(ReadString(patchouli, "LogDirectory", defaults.Runtime.LogDirectory)),
                ReadBool(patchouli, "UseMockOcrOnly", defaults.Runtime.UseMockOcrOnly)),
            new MinerUAppSettings(
                ReadString(minerU, "BaseUrl", defaults.MinerU.BaseUrl),
                ReadString(minerU, "ModelVersion", defaults.MinerU.ModelVersion),
                ReadBool(minerU, "IsOcr", defaults.MinerU.IsOcr),
                ReadBool(minerU, "EnableTable", defaults.MinerU.EnableTable),
                ReadBool(minerU, "EnableFormula", defaults.MinerU.EnableFormula),
                ReadString(minerU, "Token", defaults.MinerU.Token).Trim()));
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
