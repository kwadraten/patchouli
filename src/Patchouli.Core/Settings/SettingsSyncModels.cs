namespace Patchouli.Core.Settings;

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
