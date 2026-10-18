using Patchouli.Core.Results;

namespace Patchouli.Core.Settings;

/// <summary>
/// Issue #11: device-local file handling behavior settings. These are system settings
/// (not synced with Library snapshots); desktop and headless hosts read them by the
/// same rules. MCP callers cannot override the behavior per request.
/// </summary>
public interface IFileHandlingBehaviorSettingsService
{
    Task<Result<FileHandlingBehaviorSettings>> GetSettingsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<FileHandlingBehaviorSettings>> SaveSettingsAsync(
        FileHandlingBehaviorSettings settings,
        CancellationToken cancellationToken = default);
}
