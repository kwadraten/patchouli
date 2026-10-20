namespace Patchouli.Core.Settings;

/// <summary>
/// Issue #15: device-local auto-update check settings (not synced with Library
/// snapshots); desktop and headless hosts read them by the same rules.
/// </summary>
public sealed record UpdateCheckSettings(
    bool AutoCheckEnabled,
    DateTimeOffset? LastCheckUtc)
{
    public static UpdateCheckSettings Default()
    {
        // Auto-check defaults to ON: security and stability fixes reach users
        // without manual polling; checks are throttled to once per 24 hours and
        // stay silent when the app is already up to date. See ADR 0048.
        return new UpdateCheckSettings(AutoCheckEnabled: true, LastCheckUtc: null);
    }
}
