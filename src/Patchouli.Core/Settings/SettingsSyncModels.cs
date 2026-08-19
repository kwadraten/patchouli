using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Core.Settings;

/// <summary>
/// A non-secret setting explicitly owned by a Library snapshot. Device-local bindings, overrides and secrets
/// deliberately stay out of this record.
/// </summary>
public sealed record SettingRecord(
    string SettingKey,
    int SchemaVersion,
    string Value,
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId,
    string MergePolicy);

public sealed record DeviceOverride(string SettingKey, string Value, long Revision);

public sealed record EffectiveSetting(string SettingKey, string Value, string Source, long Revision);

public static class SettingsMergePolicies
{
    public const string ScalarReplace = "scalar_replace";
    public const string MapByKey = "map_by_key";
    public const string DeviceOverride = "device_override";
    public const string RuntimeDiscard = "runtime_discard";
}

public static class LibrarySettingKeys
{
    public const string MetadataLookup = "metadata_lookup";
    public const string PinnedTags = "library.tags.pinned";
}

public enum SettingStorageScope
{
    LibrarySnapshot,
    DeviceLocal,
    RuntimeOnly
}

public sealed record SettingCatalogEntry(
    string SettingKey,
    int SchemaVersion,
    SettingStorageScope Scope,
    bool IsSecret,
    string MergePolicy)
{
    public bool IsSnapshotEligible => Scope == SettingStorageScope.LibrarySnapshot && !IsSecret;
}

/// <summary>
/// The explicit allow-list for settings ownership. Adding a new snapshot setting requires a catalog entry and tests;
/// absence from this catalog never implies snapshot eligibility.
/// </summary>
public static class LibrarySettingCatalog
{
    private static readonly IReadOnlyDictionary<string, SettingCatalogEntry> Entries =
        new SettingCatalogEntry[]
        {
            new(LibrarySettingKeys.MetadataLookup, 1, SettingStorageScope.LibrarySnapshot, false,
                SettingsMergePolicies.ScalarReplace),
            new(LibrarySettingKeys.PinnedTags, 1, SettingStorageScope.LibrarySnapshot, false,
                SettingsMergePolicies.ScalarReplace),
            new("runtime", 1, SettingStorageScope.DeviceLocal, false, SettingsMergePolicies.DeviceOverride),
            new("mineru", 1, SettingStorageScope.DeviceLocal, false, SettingsMergePolicies.DeviceOverride),
            new("ui", 1, SettingStorageScope.DeviceLocal, false, SettingsMergePolicies.DeviceOverride),
            new("file_scanning", 1, SettingStorageScope.DeviceLocal, false, SettingsMergePolicies.DeviceOverride),
            new("sync_binding", 1, SettingStorageScope.DeviceLocal, false, SettingsMergePolicies.DeviceOverride),
            new("mcp", 1, SettingStorageScope.DeviceLocal, true, SettingsMergePolicies.DeviceOverride),
            new("credentials", 1, SettingStorageScope.DeviceLocal, true, SettingsMergePolicies.DeviceOverride),
            new("device_bindings", 1, SettingStorageScope.DeviceLocal, false, SettingsMergePolicies.DeviceOverride),
            new("snapshot_runtime_state", 1, SettingStorageScope.RuntimeOnly, false,
                SettingsMergePolicies.RuntimeDiscard)
        }.ToDictionary(entry => entry.SettingKey, StringComparer.Ordinal);

    public static IReadOnlyList<SettingCatalogEntry> All => Entries.Values.OrderBy(entry => entry.SettingKey).ToArray();

    public static bool TryGet(string settingKey, out SettingCatalogEntry entry)
    {
        return Entries.TryGetValue(settingKey, out entry!);
    }

    public static SettingCatalogEntry GetRequired(string settingKey)
    {
        return TryGet(settingKey, out SettingCatalogEntry? entry)
            ? entry
            : throw new ArgumentOutOfRangeException(nameof(settingKey), settingKey, "Setting is not catalogued.");
    }

    public static IReadOnlyList<string> NormalizeSnapshotKeys(IEnumerable<string>? settingKeys)
    {
        return (settingKeys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key) &&
                          TryGet(key, out SettingCatalogEntry? entry) &&
                          entry.IsSnapshotEligible)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    public static Result ValidateRecord(SettingRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SettingKey) || string.IsNullOrWhiteSpace(record.Value) ||
            record.SchemaVersion < 1 || record.Revision < 1 || string.IsNullOrWhiteSpace(record.UpdatedByDeviceId) ||
            string.IsNullOrWhiteSpace(record.MergePolicy))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Library setting record is missing a required value.");
        }

        if (!TryGet(record.SettingKey.Trim(), out SettingCatalogEntry? entry) || !entry.IsSnapshotEligible)
        {
            return Result.Failure(AppErrorCodes.UnsupportedOperation,
                "Setting is not eligible for library snapshot storage.");
        }

        if (record.SchemaVersion != entry.SchemaVersion ||
            !string.Equals(record.MergePolicy, entry.MergePolicy, StringComparison.Ordinal))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                "Library setting schema or merge policy does not match the catalog.");
        }

        return ValidateJsonValue(entry, record.Value);
    }

    public static Result ValidateJsonValue(SettingCatalogEntry entry, string valueJson)
    {
        return entry.SettingKey switch
        {
            LibrarySettingKeys.MetadataLookup => ValidateMetadataLookupJson(valueJson),
            LibrarySettingKeys.PinnedTags => ValidatePinnedTagsJson(valueJson),
            _ => Result.Failure(AppErrorCodes.UnsupportedOperation,
                $"Setting '{entry.SettingKey}' does not define a JSON validator.")
        };
    }

    public static Result ValidateClrValue(SettingCatalogEntry entry, Type type)
    {
        if (entry.SettingKey == LibrarySettingKeys.MetadataLookup && type.Name == "MetadataLookupAppSettings")
        {
            return Result.Success();
        }

        if (entry.SettingKey == LibrarySettingKeys.PinnedTags && type.Name == "PinnedTagsAppSettings")
        {
            return Result.Success();
        }

        return Result.Failure(AppErrorCodes.UnsupportedOperation,
            $"Setting '{entry.SettingKey}' does not define a serializer for CLR value '{type.Name}'.");
    }

    private static Result ValidateMetadataLookupJson(string valueJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(valueJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "metadata_lookup setting value must be a JSON object.");
            }

            if (!TryGetProperty(document.RootElement, "Sources", out JsonElement sources) &&
                !TryGetProperty(document.RootElement, "sources", out sources))
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "metadata_lookup setting value must include a sources array.");
            }

            if (sources.ValueKind != JsonValueKind.Array)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "metadata_lookup sources must be a JSON array.");
            }

            foreach (JsonElement source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object)
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "metadata_lookup sources must contain objects.");
                }

                bool hasId = (TryGetProperty(source, "SourceId", out JsonElement sourceId) ||
                              TryGetProperty(source, "sourceId", out sourceId) ||
                              TryGetProperty(source, "source_id", out sourceId)) &&
                             sourceId.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(sourceId.GetString());
                bool hasEnabled = (TryGetProperty(source, "Enabled", out JsonElement enabled) ||
                                   TryGetProperty(source, "enabled", out enabled)) &&
                                  enabled.ValueKind is JsonValueKind.True or JsonValueKind.False;
                if (!hasId || !hasEnabled)
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "metadata_lookup source entries require SourceId and Enabled values.");
                }
            }

            return Result.Success();
        }
        catch (JsonException exception)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                $"metadata_lookup setting value is invalid JSON: {exception.Message}");
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        return element.TryGetProperty(name, out value);
    }

    private static Result ValidatePinnedTagsJson(string valueJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(valueJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Result.Failure(AppErrorCodes.ValidationFailed,
                    "library.tags.pinned setting value must be a JSON array.");
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
                {
                    return Result.Failure(AppErrorCodes.ValidationFailed,
                        "library.tags.pinned must contain non-empty strings.");
                }
            }

            return Result.Success();
        }
        catch (JsonException exception)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed,
                $"library.tags.pinned setting value is invalid JSON: {exception.Message}");
        }
    }
}

public interface ILibrarySettingStore
{
    Task<Result<SettingRecord?>> GetAsync(string settingKey,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<SettingRecord>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Result> SaveAsync(SettingRecord record, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string settingKey, CancellationToken cancellationToken = default);
}
