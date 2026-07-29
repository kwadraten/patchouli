using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Patchouli.Core.Files;
using Patchouli.Core.Import;
using Patchouli.Core.Ids;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Core.Mcp;
using Patchouli.Core.Settings;
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

public sealed record DeviceRootBindingAppSettings(
    string LibraryId,
    string RootKind,
    string LogicalRootId,
    string DeviceId,
    string LocalPath,
    string ProviderIdentity,
    bool IsAvailable,
    string? AuthorizationKind = null,
    byte[]? AuthorizationPayload = null,
    int? AuthorizationPayloadVersion = null,
    string? AuthorizationUpdatedAt = null,
    string? UpdatedAt = null,
    SnapshotSyncLocalState? SnapshotState = null,
    IReadOnlyList<string>? SyncedSettingKeys = null)
{
    public bool Matches(string libraryId, string rootKind, string logicalRootId, string deviceId)
    {
        return string.Equals(LibraryId, libraryId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(RootKind, rootKind, StringComparison.Ordinal) &&
               string.Equals(LogicalRootId, logicalRootId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(DeviceId, deviceId, StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(string? libraryId = null, string? rootKind = null, string? deviceId = null)
    {
        return (string.IsNullOrWhiteSpace(libraryId) ||
                string.Equals(LibraryId, libraryId, StringComparison.OrdinalIgnoreCase)) &&
               (string.IsNullOrWhiteSpace(rootKind) || string.Equals(RootKind, rootKind, StringComparison.Ordinal)) &&
               (string.IsNullOrWhiteSpace(deviceId) ||
                string.Equals(DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    public DeviceRootBinding ToDeviceRootBinding()
    {
        return new DeviceRootBinding(
            Patchouli.Core.Ids.LibraryId.Parse(LibraryId),
            RootKind,
            LogicalRootId,
            DeviceId,
            LocalPath,
            ProviderIdentity,
            IsAvailable,
            AuthorizationKind,
            AuthorizationPayload,
            AuthorizationPayloadVersion,
            DateTimeOffset.TryParse(AuthorizationUpdatedAt, out DateTimeOffset authorizationUpdatedAt)
                ? authorizationUpdatedAt.ToUniversalTime()
                : null,
            DateTimeOffset.TryParse(UpdatedAt, out DateTimeOffset updatedAt)
                ? updatedAt.ToUniversalTime()
                : DateTimeOffset.UnixEpoch);
    }

    public static DeviceRootBindingAppSettings FromDeviceRootBinding(DeviceRootBinding binding)
    {
        return new DeviceRootBindingAppSettings(
            binding.LibraryId.ToString(),
            binding.RootKind,
            binding.LogicalRootId,
            binding.DeviceId,
            binding.LocalPath,
            binding.ProviderIdentity,
            binding.IsAvailable,
            binding.AuthorizationKind,
            binding.AuthorizationPayload,
            binding.AuthorizationPayloadVersion,
            binding.AuthorizationUpdatedAt?.ToUniversalTime().ToString("O"),
            binding.UpdatedAt.ToUniversalTime().ToString("O"));
    }
}

public sealed record SyncAppSettings(
    string DeviceId,
    string DeviceName,
    string SyncRoot,
    bool SyncMetadataLookup,
    string SyncRootId = "",
    SnapshotSyncLocalState? SnapshotState = null,
    IReadOnlyList<string>? SyncedSettingKeys = null,
    IReadOnlyList<DeviceRootBindingAppSettings>? DeviceBindings = null)
{
    public static SyncAppSettings Default(AppRuntimeOptions runtime)
    {
        return new SyncAppSettings("", Environment.MachineName, runtime.DefaultSyncRoot, false);
    }

    [JsonIgnore]
    public IReadOnlyList<string> EnabledSettingKeys =>
        LibrarySettingCatalog.NormalizeSnapshotKeys(SyncedSettingKeys ??
                                                    (SyncMetadataLookup ? [LibrarySettingKeys.MetadataLookup] : []));

    [JsonIgnore]
    public IReadOnlyList<DeviceRootBindingAppSettings> Bindings =>
        NormalizeBindings(DeviceBindings ?? []);

    public bool IsSettingEnabled(string settingKey)
    {
        return EnabledSettingKeys.Contains(settingKey, StringComparer.Ordinal);
    }

    public bool IsSettingEnabled(string settingKey, LibraryId libraryId)
    {
        return EnabledSettingKeysForLibrary(libraryId).Contains(settingKey, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> EnabledSettingKeysForLibrary(LibraryId libraryId)
    {
        DeviceRootBindingAppSettings? binding = CurrentSyncRootBinding(libraryId);
        return LibrarySettingCatalog.NormalizeSnapshotKeys(binding?.SyncedSettingKeys ?? []);
    }

    public SyncAppSettings WithSettingEnabled(string settingKey, bool enabled)
    {
        HashSet<string> keys = EnabledSettingKeys.ToHashSet(StringComparer.Ordinal);
        if (enabled)
        {
            keys.Add(settingKey);
        }
        else
        {
            keys.Remove(settingKey);
        }

        IReadOnlyList<string> normalized = LibrarySettingCatalog.NormalizeSnapshotKeys(keys);
        return this with
        {
            SyncMetadataLookup = normalized.Contains(LibrarySettingKeys.MetadataLookup, StringComparer.Ordinal),
            SyncedSettingKeys = normalized
        };
    }

    public SyncAppSettings WithSettingEnabled(LibraryId libraryId, string settingKey, bool enabled)
    {
        DeviceRootBindingAppSettings binding = EnsureCurrentSyncRootBinding(libraryId);
        HashSet<string> keys =
            LibrarySettingCatalog.NormalizeSnapshotKeys(binding.SyncedSettingKeys ?? EnabledSettingKeys)
                .ToHashSet(StringComparer.Ordinal);
        if (enabled)
        {
            keys.Add(settingKey);
        }
        else
        {
            keys.Remove(settingKey);
        }

        IReadOnlyList<string> normalized = LibrarySettingCatalog.NormalizeSnapshotKeys(keys);
        return WithDeviceBinding(binding with
            {
                SyncedSettingKeys = normalized,
                SnapshotState = binding.SnapshotState ?? SnapshotSyncLocalState.NotConfigured,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            }) with
            {
                SyncMetadataLookup = normalized.Contains(LibrarySettingKeys.MetadataLookup, StringComparer.Ordinal),
                SyncedSettingKeys = normalized
            };
    }

    public DeviceRootBindingAppSettings? CurrentSyncRootBinding(LibraryId libraryId)
    {
        string deviceId = DeviceId.Trim();
        return Bindings.FirstOrDefault(binding =>
            binding.Matches(libraryId.ToString(), LogicalRootKinds.SyncRoot, deviceId));
    }

    public DeviceRootBindingAppSettings EnsureCurrentSyncRootBinding(LibraryId libraryId)
    {
        DeviceRootBindingAppSettings? existing = CurrentSyncRootBinding(libraryId);
        if (existing is not null)
        {
            return existing;
        }

        string logicalRootId = string.IsNullOrWhiteSpace(SyncRootId) ? Guid.NewGuid().ToString("D") : SyncRootId;
        return new DeviceRootBindingAppSettings(
            libraryId.ToString(),
            LogicalRootKinds.SyncRoot,
            logicalRootId,
            DeviceId,
            SyncRoot,
            "settings_json",
            !string.IsNullOrWhiteSpace(SyncRoot) && Directory.Exists(SyncRoot),
            FileSearchRootAuthorizationKinds.None,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            SnapshotState ?? SnapshotSyncLocalState.NotConfigured,
            EnabledSettingKeys);
    }

    public SyncAppSettings WithDeviceBinding(DeviceRootBindingAppSettings binding)
    {
        List<DeviceRootBindingAppSettings> bindings = Bindings
            .Where(candidate => !candidate.Matches(binding.LibraryId, binding.RootKind, binding.LogicalRootId,
                binding.DeviceId))
            .ToList();
        bindings.Add(binding);
        return this with { DeviceBindings = NormalizeBindings(bindings) };
    }

    public SyncAppSettings WithoutDeviceBinding(
        LibraryId libraryId,
        string rootKind,
        string logicalRootId,
        string deviceId)
    {
        return this with
        {
            DeviceBindings = Bindings
                .Where(binding => !binding.Matches(libraryId.ToString(), rootKind, logicalRootId, deviceId))
                .ToArray()
        };
    }

    private static IReadOnlyList<DeviceRootBindingAppSettings> NormalizeBindings(
        IEnumerable<DeviceRootBindingAppSettings> bindings)
    {
        Dictionary<(string LibraryId, string RootKind, string LogicalRootId, string DeviceId),
            DeviceRootBindingAppSettings> normalized = new();
        foreach (DeviceRootBindingAppSettings binding in bindings)
        {
            string libraryId = binding.LibraryId.Trim();
            string rootKind = binding.RootKind.Trim();
            string logicalRootId = binding.LogicalRootId.Trim();
            string deviceId = binding.DeviceId.Trim();
            if (string.IsNullOrWhiteSpace(libraryId) ||
                string.IsNullOrWhiteSpace(rootKind) ||
                string.IsNullOrWhiteSpace(logicalRootId) ||
                string.IsNullOrWhiteSpace(deviceId) ||
                string.IsNullOrWhiteSpace(binding.LocalPath) ||
                !LogicalRootKinds.IsKnown(rootKind))
            {
                continue;
            }

            normalized[(libraryId, rootKind, logicalRootId, deviceId)] = binding with
            {
                LibraryId = libraryId,
                RootKind = rootKind,
                LogicalRootId = logicalRootId,
                DeviceId = deviceId,
                LocalPath = Path.GetFullPath(binding.LocalPath),
                ProviderIdentity = string.IsNullOrWhiteSpace(binding.ProviderIdentity)
                    ? "settings_json"
                    : binding.ProviderIdentity.Trim(),
                SyncedSettingKeys = rootKind == LogicalRootKinds.SyncRoot
                    ? LibrarySettingCatalog.NormalizeSnapshotKeys(binding.SyncedSettingKeys)
                    : null
            };
        }

        return normalized.Values
            .OrderBy(binding => binding.LibraryId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binding => binding.RootKind, StringComparer.Ordinal)
            .ThenBy(binding => binding.LogicalRootId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binding => binding.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

public sealed record SettingsDiagnostic(string Code, string Message, string PathCategory);

public sealed record SettingsLoadResult<T>(T Settings, IReadOnlyList<SettingsDiagnostic> Diagnostics)
{
    public bool IsSuccess => Diagnostics.Count == 0;
}

public sealed record PatchouliAppSettings(
    AppRuntimeOptions Runtime,
    MinerUAppSettings MinerU,
    McpServerSettings Mcp,
    UiPreferences Ui)
{
    public CredentialsAppSettings Credentials { get; init; } = CredentialsAppSettings.Default();
    public SyncAppSettings Sync { get; init; } = SyncAppSettings.Default(Runtime);
    public MetadataLookupAppSettings MetadataLookup { get; init; } = MetadataLookupAppSettings.Default();
    public FileScanningAppSettings FileScanning { get; init; } = FileScanningAppSettings.Default();

    public static PatchouliAppSettings Default(IAppPaths? appPaths = null)
    {
        return new PatchouliAppSettings(AppRuntimeOptions.Default(appPaths), MinerUAppSettings.Default(),
            new McpServerSettings(4536, "127.0.0.1", false, [], false, null, [], DateTimeOffset.UnixEpoch),
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

    public static SettingsLoadResult<PatchouliAppSettings> LoadWithResult(string? settingsPath = null)
    {
        List<SettingsDiagnostic> diagnostics = new();
        PatchouliAppSettings settings = Load(settingsPath, failure =>
            diagnostics.Add(new SettingsDiagnostic(failure.ErrorCode, failure.ErrorMessage, failure.PathCategory)));
        return new SettingsLoadResult<PatchouliAppSettings>(settings, diagnostics);
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
            JsonElement? sync = GetSection(root, "Sync");

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
                ReadMcpSettings(mcp, defaults.Mcp),
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
                Credentials = ReadCredentials(credentials, defaults.Credentials),
                Sync = new SyncAppSettings(
                    ReadString(sync, "DeviceId", defaults.Sync.DeviceId),
                    ReadString(sync, "DeviceName", defaults.Sync.DeviceName),
                    ExpandPath(ReadString(sync, "SyncRoot", defaults.Sync.SyncRoot)),
                    ReadBool(sync, "SyncMetadataLookup", defaults.Sync.SyncMetadataLookup),
                    ReadString(sync, "SyncRootId", defaults.Sync.SyncRootId),
                    ReadSnapshotSyncState(sync, defaults.Sync.SnapshotState ?? SnapshotSyncLocalState.NotConfigured),
                    ReadStringList(sync, "SyncedSettingKeys", defaults.Sync.SyncedSettingKeys ??
                                                              (ReadBool(sync, "SyncMetadataLookup",
                                                                  defaults.Sync.SyncMetadataLookup)
                                                                  ? [LibrarySettingKeys.MetadataLookup]
                                                                  : [])),
                    ReadDeviceBindings(sync, defaults.Sync.DeviceBindings ?? []))
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
        SemaphoreSlim? writeGate = null;
        try
        {
            string path = ResolvePath(settingsPath);
            writeGate = SettingsFileWriteCoordinator.ForPath(path);
            writeGate.Wait();
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
            SyncAppSettings syncToWrite = Sync;
            try
            {
                JsonNode? persistedSync = root["Sync"];
                SnapshotSyncLocalState? existingSnapshotState =
                    persistedSync?["SnapshotState"]?.Deserialize<SnapshotSyncLocalState>();
                if (existingSnapshotState is not null &&
                    (syncToWrite.SnapshotState is null ||
                     existingSnapshotState.UpdatedAt > syncToWrite.SnapshotState.UpdatedAt))
                {
                    syncToWrite = syncToWrite with { SnapshotState = existingSnapshotState };
                }

                DeviceRootBindingAppSettings[] existingBindings = persistedSync?["DeviceBindings"] is JsonArray array
                    ? ReadDeviceBindings(array).ToArray()
                    : [];
                if (existingBindings.Length > 0)
                {
                    foreach (DeviceRootBindingAppSettings existing in existingBindings)
                    {
                        DeviceRootBindingAppSettings? incoming = syncToWrite.Bindings.FirstOrDefault(binding =>
                            binding.Matches(existing.LibraryId, existing.RootKind, existing.LogicalRootId,
                                existing.DeviceId));
                        if (incoming is null)
                        {
                            syncToWrite = syncToWrite.WithDeviceBinding(existing);
                            continue;
                        }

                        if (existing.SnapshotState is not null &&
                            (incoming.SnapshotState is null ||
                             existing.SnapshotState.UpdatedAt > incoming.SnapshotState.UpdatedAt))
                        {
                            syncToWrite =
                                syncToWrite.WithDeviceBinding(incoming with { SnapshotState = existing.SnapshotState });
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // The caller's valid sync bindings replace a malformed persisted state.
            }

            root["Sync"] = JsonSerializer.SerializeToNode(syncToWrite);
            long existingMcpRevision = 0;
            if (root["Mcp"]?["Revision"] is JsonValue revisionNode)
            {
                _ = revisionNode.TryGetValue(out existingMcpRevision);
            }

            if (Mcp.Revision >= existingMcpRevision)
            {
                root["Mcp"] = JsonSerializer.SerializeToNode(Mcp);
            }

            root["Ui"] = JsonSerializer.SerializeToNode(new
            {
                Ui.LibraryGridVisibleColumns,
                Ui.LibraryGridColumnWidths,
                Ui.LibraryGridColumnOrder,
                Ui.ShowLibraryLeftSidebar,
                Ui.ShowLibraryRightSidebar
            });
            if (Sync.IsSettingEnabled(LibrarySettingKeys.MetadataLookup))
            {
                root.Remove("MetadataLookup");
            }
            else
            {
                root["MetadataLookup"] = JsonSerializer.SerializeToNode(new { MetadataLookup.Sources });
            }

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

            writeGate?.Release();
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

    private static SnapshotSyncLocalState ReadSnapshotSyncState(JsonElement? section, SnapshotSyncLocalState fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("SnapshotState", out JsonElement state) ||
            state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return fallback;
        }

        try
        {
            return state.Deserialize<SnapshotSyncLocalState>() ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static IReadOnlyList<DeviceRootBindingAppSettings> ReadDeviceBindings(
        JsonElement? section,
        IReadOnlyList<DeviceRootBindingAppSettings> fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty("DeviceBindings", out JsonElement bindings) ||
            bindings.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        return ReadDeviceBindings(bindings);
    }

    private static IReadOnlyList<DeviceRootBindingAppSettings> ReadDeviceBindings(JsonArray bindings)
    {
        List<DeviceRootBindingAppSettings> values = new();
        foreach (JsonNode? node in bindings)
        {
            if (node is null)
            {
                continue;
            }

            try
            {
                DeviceRootBindingAppSettings? binding = node.Deserialize<DeviceRootBindingAppSettings>();
                if (binding is not null)
                {
                    values.Add(binding);
                }
            }
            catch (JsonException)
            {
                // Ignore one malformed binding row; the rest of the settings file can still be recovered.
            }
        }

        return values;
    }

    private static IReadOnlyList<DeviceRootBindingAppSettings> ReadDeviceBindings(JsonElement bindings)
    {
        List<DeviceRootBindingAppSettings> values = new();
        foreach (JsonElement binding in bindings.EnumerateArray())
        {
            if (binding.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string rootKind = ReadString(binding, "RootKind", "").Trim();
            string localPath = ReadString(binding, "LocalPath", "").Trim();
            if (string.IsNullOrWhiteSpace(rootKind) || string.IsNullOrWhiteSpace(localPath))
            {
                continue;
            }

            SnapshotSyncLocalState? state = null;
            try
            {
                if (binding.TryGetProperty("SnapshotState", out JsonElement snapshotState) &&
                    snapshotState.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    state = snapshotState.Deserialize<SnapshotSyncLocalState>();
                }
            }
            catch (JsonException)
            {
                state = null;
            }

            values.Add(new DeviceRootBindingAppSettings(
                ReadString(binding, "LibraryId", ""),
                rootKind,
                ReadString(binding, "LogicalRootId", ""),
                ReadString(binding, "DeviceId", ""),
                ExpandPath(localPath),
                ReadString(binding, "ProviderIdentity", "settings_json"),
                ReadBool(binding, "IsAvailable", Directory.Exists(ExpandPath(localPath))),
                ReadString(binding, "AuthorizationKind", FileSearchRootAuthorizationKinds.None),
                ReadBytes(binding, "AuthorizationPayload"),
                ReadNullableInt(binding, "AuthorizationPayloadVersion"),
                ReadString(binding, "AuthorizationUpdatedAt", ""),
                ReadString(binding, "UpdatedAt", ""),
                state,
                ReadStringList(binding, "SyncedSettingKeys", [])));
        }

        return values;
    }

    private static McpServerSettings ReadMcpSettings(JsonElement? section, McpServerSettings fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        string bind = ReadString(section, "BindAddress", fallback.BindAddress).Trim();
        string? token = ReadString(section, "Token", fallback.Token ?? "").Trim();
        IReadOnlyList<string> origins =
            element.TryGetProperty("AllowedOrigins", out JsonElement originsElement) &&
            originsElement.ValueKind == JsonValueKind.Array
                ? originsElement.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!).ToArray()
                : fallback.AllowedOrigins;
        IReadOnlyList<McpToolOverride> overrides =
            element.TryGetProperty("ToolOverrides", out JsonElement overridesElement) &&
            overridesElement.ValueKind == JsonValueKind.Array
                ? overridesElement.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object)
                    .Select(value => new McpToolOverride(ReadString(value, "ToolName", ""),
                        ReadBool(value, "Enabled", true), ReadString(value, "DisabledReason", "")))
                    .Where(value => !string.IsNullOrWhiteSpace(value.ToolName)).ToArray()
                : fallback.ToolOverrides;
        return new McpServerSettings(ReadInt(section, "Port", fallback.Port), bind,
            ReadBool(section, "CorsEnabled", fallback.CorsEnabled), origins,
            ReadBool(section, "AuthRequired", fallback.AuthRequired), string.IsNullOrWhiteSpace(token) ? null : token,
            overrides,
            DateTimeOffset.TryParse(ReadString(section, "UpdatedAt", fallback.UpdatedAt.ToString("O")),
                out DateTimeOffset updatedAt)
                ? updatedAt.ToUniversalTime()
                : fallback.UpdatedAt,
            ReadLong(section, "Revision", fallback.Revision));
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

    private static long ReadLong(JsonElement? section, string name, long fallback)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return fallback;
        }

        return element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long parsed)
            ? parsed
            : fallback;
    }

    private static int? ReadNullableInt(JsonElement? section, string name)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        return element.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int parsed)
            ? parsed
            : null;
    }

    private static byte[]? ReadBytes(JsonElement? section, string name)
    {
        if (section is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                return value.GetBytesFromBase64();
            }
            catch (FormatException)
            {
                return null;
            }
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<byte> bytes = new();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.TryGetByte(out byte parsed))
            {
                bytes.Add(parsed);
            }
        }

        return bytes.ToArray();
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
