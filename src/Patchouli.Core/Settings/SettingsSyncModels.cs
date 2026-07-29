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
}

public interface ILibrarySettingStore
{
    Task<Results.Result<SettingRecord?>> GetAsync(string settingKey,
        CancellationToken cancellationToken = default);

    Task<Results.Result<IReadOnlyList<SettingRecord>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Results.Result> SaveAsync(SettingRecord record, CancellationToken cancellationToken = default);

    Task<Results.Result> DeleteAsync(string settingKey, CancellationToken cancellationToken = default);
}
