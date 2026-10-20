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
    /// Holds the settings-file gate across the read-modify-write so a
    /// concurrent <see cref="SaveAutoCheckAsync"/> cannot interleave.
    /// </summary>
    Task<Result<UpdateCheckSettings>> RecordCheckAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the auto-check flag, preserving the last-check timestamp.
    /// Holds the settings-file gate across the read-modify-write so a
    /// concurrent <see cref="RecordCheckAsync"/> cannot clobber the flag
    /// (last-writer-wins). Used by the settings UI's immediate save.
    /// </summary>
    Task<Result<UpdateCheckSettings>> SaveAutoCheckAsync(
        bool autoCheckEnabled,
        CancellationToken cancellationToken = default);
}
