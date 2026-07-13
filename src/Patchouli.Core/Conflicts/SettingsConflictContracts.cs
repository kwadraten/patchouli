using Patchouli.Core.Results;

namespace Patchouli.Core.Conflicts;

public enum SettingsConflictAction
{
    KeepLocal,
    UseIncoming,
    MergeEntries,
    MapOnThisDevice,
    LeaveUnresolved
}

public sealed record SettingsConflictResolution(
    string SettingKey,
    long ExpectedRevision,
    SettingsConflictAction Action,
    string? Value);

public interface ISettingsConflictActionExecutor
{
    Task<Result> ExecuteAsync(SettingsConflictResolution resolution, CancellationToken cancellationToken = default);
}
