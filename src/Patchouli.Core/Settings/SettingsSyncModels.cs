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

public interface ILibrarySettingStore
{
    Task<Results.Result<SettingRecord?>> GetAsync(string settingKey,
        CancellationToken cancellationToken = default);

    Task<Results.Result<IReadOnlyList<SettingRecord>>> ListAsync(CancellationToken cancellationToken = default);

    Task<Results.Result> SaveAsync(SettingRecord record, CancellationToken cancellationToken = default);

    Task<Results.Result> DeleteAsync(string settingKey, CancellationToken cancellationToken = default);
}
