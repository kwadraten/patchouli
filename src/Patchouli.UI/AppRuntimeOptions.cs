using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.Import;
using System.Security;
using Patchouli.UI.Diagnostics;

namespace Patchouli.UI;

public sealed record AppRuntimeOptions(
    string RuntimeDatabasePath,
    string DefaultSyncRoot,
    string DefaultStagingRoot,
    string LogDirectory,
    string FileSearchRoot,
    bool RememberLastDatabase = true,
    bool UseMockOcrOnly = false)
{
    public static AppRuntimeOptions FromAppSettings(string? settingsPath = null)
    {
        return PatchouliAppSettings.Load(settingsPath).Runtime;
    }

    public static AppRuntimeOptions Default(IAppPaths? appPaths = null)
    {
        AppStorageLocations locations = (appPaths ?? new PlatformAppPaths()).Resolve();
        string root = locations.DataDirectory;
        return new AppRuntimeOptions(
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
    bool EnableFormula)
{
    public static MinerUAppSettings Default()
    {
        return new MinerUAppSettings("https://mineru.net", "vlm", true, true, true);
    }

    public MinerUConfiguration ToConfiguration(string token)
    {
        return new MinerUConfiguration(token, BaseUrl, ModelVersion, IsOcr, EnableTable, EnableFormula);
    }
}

public sealed record ProviderCredentialAppSettings(
    string CredentialId,
    string ProviderId,
    string DisplayName,
    string SecretValue,
    string Status,
    string CreatedAt,
    string UpdatedAt);

public sealed record CredentialsAppSettings(IReadOnlyList<ProviderCredentialAppSettings> Providers)
{
    public static CredentialsAppSettings Default()
    {
        return new CredentialsAppSettings([]);
    }
}

public sealed record McpAppSettings(
    int Port,
    bool BlockExternalAccess,
    string ServerToken,
    Dictionary<string, bool> DisabledTools)
{
    public bool CorsEnabled { get; init; }
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
    public bool AuthRequired { get; init; }

    public static McpAppSettings Default()
    {
        return new McpAppSettings(4536, true, string.Empty, new Dictionary<string, bool>());
    }
}

public sealed record UiPreferences(
    Dictionary<string, bool> LibraryGridVisibleColumns,
    Dictionary<string, double> LibraryGridColumnWidths,
    Dictionary<string, int> LibraryGridColumnOrder,
    bool ShowLibraryLeftSidebar = true,
    bool ShowLibraryRightSidebar = true)
{
    public static UiPreferences Default()
    {
        return new UiPreferences(new Dictionary<string, bool>(), new Dictionary<string, double>(),
            new Dictionary<string, int>(),
            true, true);
    }
}

public sealed record MetadataSourcePreference(string SourceId, bool Enabled);

public sealed record MetadataLookupAppSettings(IReadOnlyList<MetadataSourcePreference> Sources)
{
    public static MetadataLookupAppSettings Default()
    {
        return new MetadataLookupAppSettings(
        [
            new MetadataSourcePreference("calis", true),
            new MetadataSourcePreference("nlc", true),
            new MetadataSourcePreference("ndl", true),
            new MetadataSourcePreference("cinii", true),
            new MetadataSourcePreference("library-of-congress", true),
            new MetadataSourcePreference("dnb", true),
            new MetadataSourcePreference("bnf", true),
            new MetadataSourcePreference("pmc-id-converter", true),
            new MetadataSourcePreference("pubmed", true),
            new MetadataSourcePreference("arxiv", true),
            new MetadataSourcePreference("open-library", true),
            new MetadataSourcePreference("datacite", true),
            new MetadataSourcePreference("crossref", true),
            new MetadataSourcePreference("openalex", true),
            new MetadataSourcePreference("semantic-scholar", true),
            new MetadataSourcePreference("google-books", false)
        ]);
    }

    public static MetadataLookupAppSettings MergeWithDefaults(IEnumerable<MetadataSourcePreference>? saved)
    {
        List<MetadataSourcePreference> merged = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (MetadataSourcePreference source in saved ?? [])
        {
            string? id = source.SourceId?.Trim();
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                merged.Add(new MetadataSourcePreference(id, source.Enabled));
            }
        }

        foreach (MetadataSourcePreference source in Default().Sources)
        {
            if (seen.Add(source.SourceId))
            {
                merged.Add(source);
            }
        }

        return new MetadataLookupAppSettings(merged);
    }
}

public sealed record FileScanningAppSettings(IReadOnlyList<string> ExclusionPatterns)
{
    public static FileScanningAppSettings Default()
    {
        return new FileScanningAppSettings(
        [
            @"(^|/)bin(/|$)", @"(^|/)obj(/|$)", @"(^|/)node_modules(/|$)", @"(^|/)\.git(/|$)",
            @"(^|/)\.svn(/|$)", @"(^|/)\.hg(/|$)", @"(^|/)\.vs(/|$)"
        ]);
    }
}

public sealed record SettingsSaveResult(
    bool IsSuccess,
    string? ErrorCode,
    string? ErrorMessage,
    string PathCategory,
    bool CanRetry)
{
    public static SettingsSaveResult Success { get; } = new(true, null, null, "user_settings", false);
}

public sealed record SettingsLoadFailure(
    string ErrorCode,
    string ErrorMessage,
    string PathCategory);

public sealed record PatchouliAppSettings(
    AppRuntimeOptions Runtime,
    MinerUAppSettings MinerU,
    McpAppSettings Mcp,
    UiPreferences Ui)
{
    public CredentialsAppSettings Credentials { get; init; } = CredentialsAppSettings.Default();
    public MetadataLookupAppSettings MetadataLookup { get; init; } = MetadataLookupAppSettings.Default();
    public FileScanningAppSettings FileScanning { get; init; } = FileScanningAppSettings.Default();

    public static PatchouliAppSettings Default(IAppPaths? appPaths = null)
    {
        return new PatchouliAppSettings(AppRuntimeOptions.Default(appPaths), MinerUAppSettings.Default(),
            McpAppSettings.Default(),
            UiPreferences.Default());
    }

    public static string ResolvePath(string? settingsPath = null)
    {
        return string.IsNullOrWhiteSpace(settingsPath)
            ? new PlatformAppPaths().Resolve().UserSettingsPath
            : Path.GetFullPath(settingsPath);
    }

    public static PatchouliAppSettings Load(string? settingsPath = null,
        Action<SettingsLoadFailure>? onFailure = null)
    {
        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            return LoadFile(Default(), Path.GetFullPath(settingsPath), "user_settings", onFailure);
        }

        return Load(new PlatformAppPaths(), onFailure);
    }

    public static PatchouliAppSettings Load(IAppPaths appPaths, Action<SettingsLoadFailure>? onFailure = null)
    {
        AppStorageLocations locations = appPaths.Resolve();
        PatchouliAppSettings bundled = LoadFile(Default(appPaths), locations.BundledDefaultsPath,
            "bundled_defaults", onFailure);
        return LoadFile(bundled, locations.UserSettingsPath, "user_settings", onFailure);
    }

    private static PatchouliAppSettings LoadFile(PatchouliAppSettings defaults, string path, string pathCategory,
        Action<SettingsLoadFailure>? onFailure)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return defaults;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            JsonElement? patchouli = GetSection(root, "Patchouli");
            JsonElement? minerU = GetSection(root, "MinerU");
            JsonElement? mcp = GetSection(root, "Mcp");
            JsonElement? ui = GetSection(root, "Ui");
            JsonElement? metadataLookup = GetSection(root, "MetadataLookup");
            JsonElement? fileScanning = GetSection(root, "FileScanning");
            JsonElement? credentials = GetSection(root, "Credentials");

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
                    ReadBool(minerU, "EnableFormula", defaults.MinerU.EnableFormula)),
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
                MetadataLookup = MetadataLookupAppSettings.MergeWithDefaults(ReadMetadataSources(metadataLookup)),
                FileScanning = new FileScanningAppSettings(
                    ReadStringList(fileScanning, "ExclusionPatterns", defaults.FileScanning.ExclusionPatterns)),
                Credentials = ReadCredentials(credentials, defaults.Credentials)
            };
        }
        catch (JsonException exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "settings-load", "parse-settings");
            onFailure?.Invoke(new SettingsLoadFailure("settings_json_invalid",
                $"设置文件格式无效，已使用默认值：{exception.Message}", pathCategory));
            return defaults;
        }
        catch (IOException exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "settings-load", "read-settings");
            onFailure?.Invoke(new SettingsLoadFailure("settings_io_failed",
                $"无法读取设置文件，已使用默认值：{exception.Message}", pathCategory));
            return defaults;
        }
        catch (UnauthorizedAccessException exception)
        {
            UnexpectedExceptions.Sink.Report(exception, "settings-load", "read-settings");
            onFailure?.Invoke(new SettingsLoadFailure("settings_access_denied",
                $"没有权限读取设置文件，已使用默认值：{exception.Message}", pathCategory));
            return defaults;
        }
    }

    public SettingsSaveResult Save(string? settingsPath = null)
    {
        string? temporaryPath = null;
        try
        {
            string path = ResolvePath(settingsPath);
            AppPathGuard.ValidateMutablePath(path);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

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
                MinerU.EnableFormula
            });
            root["Credentials"] = JsonSerializer.SerializeToNode(new
            {
                SchemaVersion = 1,
                Providers = Credentials.Providers
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
            root["FileScanning"] = JsonSerializer.SerializeToNode(new { FileScanning.ExclusionPatterns });

            temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, path, true);
            temporaryPath = null;
            return SettingsSaveResult.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
                                              or InvalidOperationException)
        {
            string code = exception switch
            {
                UnauthorizedAccessException or SecurityException => "settings_access_denied",
                InvalidOperationException => "settings_path_rejected",
                _ => "settings_io_failed"
            };
            return new SettingsSaveResult(false, code, exception.Message, "user_settings", exception is IOException);
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = exception;
                }
            }
        }
    }

    private static JsonElement? GetSection(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement section)
            ? section
            : null;
    }

    private static CredentialsAppSettings ReadCredentials(JsonElement? section, CredentialsAppSettings fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("Providers", out JsonElement providers) ||
            providers.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        List<ProviderCredentialAppSettings> values = new();
        foreach (JsonElement provider in providers.EnumerateArray())
        {
            string providerId = ReadString(provider, "ProviderId", "").Trim().ToLowerInvariant();
            string secret = ReadString(provider, "SecretValue", "").Trim();
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(secret))
            {
                continue;
            }

            values.RemoveAll(value => string.Equals(value.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            values.Add(new ProviderCredentialAppSettings(
                ReadString(provider, "CredentialId", Guid.NewGuid().ToString("D")),
                providerId,
                ReadString(provider, "DisplayName", providerId),
                secret,
                ReadString(provider, "Status", "active"),
                ReadString(provider, "CreatedAt", now.ToString("O")),
                ReadString(provider, "UpdatedAt", now.ToString("O"))));
        }

        return new CredentialsAppSettings(values);
    }

    private static string ReadString(JsonElement? section, string name, string fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static bool ReadBool(JsonElement? section, string name, bool fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        return element.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    private static int ReadInt(JsonElement? section, string name, int fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int result)
            ? result
            : fallback;
    }

    private static Dictionary<string, bool> ReadStringBoolDict(JsonElement? section, string name,
        Dictionary<string, bool> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        Dictionary<string, bool> dict = new();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                dict[property.Name] = property.Value.GetBoolean();
            }
        }

        return dict;
    }

    private static Dictionary<string, double> ReadStringDoubleDict(JsonElement? section, string name,
        Dictionary<string, double> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        Dictionary<string, double> dict = new();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out double number))
            {
                dict[property.Name] = number;
            }
        }

        return dict;
    }

    private static Dictionary<string, int> ReadStringIntDict(JsonElement? section, string name,
        Dictionary<string, int> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        Dictionary<string, int> dict = new();
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int number))
            {
                dict[property.Name] = number;
            }
        }

        return dict;
    }

    private static IReadOnlyList<MetadataSourcePreference> ReadMetadataSources(JsonElement? section)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("Sources", out JsonElement sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<MetadataSourcePreference> result = new();
        foreach (JsonElement source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string id = ReadString(source, "SourceId", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(new MetadataSourcePreference(id, ReadBool(source, "Enabled", true)));
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement? section, string name,
        IReadOnlyList<string> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(value => value.GetString()!.Trim())
            .ToArray();
    }

    private static string ExpandPath(string value)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return value
            .Replace("{LocalAppData}", localAppData, StringComparison.OrdinalIgnoreCase)
            .Replace('/', Path.DirectorySeparatorChar);
    }
}
