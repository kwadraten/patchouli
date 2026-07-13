using System.Text.Json;

namespace Patchouli.Core.Settings;

public sealed class SettingsResolver
{
    public EffectiveSetting Resolve(
        SettingRecord? syncedBase,
        DeviceOverride? deviceOverride,
        string? runtimeValue = null)
    {
        if (runtimeValue is not null)
        {
            return new EffectiveSetting(syncedBase?.SettingKey ?? deviceOverride?.SettingKey ?? "runtime",
                runtimeValue, "runtime", syncedBase?.Revision ?? deviceOverride?.Revision ?? 0);
        }

        if (deviceOverride is not null)
        {
            return new EffectiveSetting(deviceOverride.SettingKey, deviceOverride.Value, "device_override",
                deviceOverride.Revision);
        }

        return new EffectiveSetting(syncedBase?.SettingKey ?? "", syncedBase?.Value ?? "", "synced_base",
            syncedBase?.Revision ?? 0);
    }
}
