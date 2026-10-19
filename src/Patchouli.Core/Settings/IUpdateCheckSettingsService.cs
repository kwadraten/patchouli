using Patchouli.Core.Results;

namespace Patchouli.Core.Settings;

/// <summary>
/// Issue #15: device-local persistence for <see cref="UpdateCheckSettings"/>.
/// Stored in the shared device settings file (same file as MCP settings,
/// credentials and file-handling behavior), guarded by the settings-file write
/// coordinator; never synced with Library snapshots.
/// </summary>
public interface IUpdateCheckSettingsService
{
    Task<Result<UpdateCheckSettings>> GetSettingsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<UpdateCheckSettings>> SaveSettingsAsync(
        UpdateCheckSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that an update check was performed now (success or failure),
    /// so automatic checks stay throttled to the configured interval.
    /// </summary>
    Task<Result<UpdateCheckSettings>> RecordCheckAsync(
        CancellationToken cancellationToken = default);
}
