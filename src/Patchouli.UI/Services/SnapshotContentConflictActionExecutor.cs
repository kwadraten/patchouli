using Patchouli.Core.Conflicts;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.UI.Services;

/// <summary>
/// Connects the shared conflict dialog to the snapshot coordinator without exposing plan mutation to the view.
/// Each execution returns a replacement immutable plan; it never applies content to the runtime library.
/// </summary>
public sealed class SnapshotContentConflictActionExecutor : IConflictActionExecutor
{
    private readonly ISnapshotSyncCoordinator _snapshotSync;

    public SnapshotContentConflictActionExecutor(
        ISnapshotSyncCoordinator snapshotSync,
        SnapshotContentResolutionPlan plan,
        string conflictCode)
    {
        _snapshotSync = snapshotSync;
        Plan = plan;
        ConflictCode = conflictCode;
    }

    public string ConflictCode { get; }
    public SnapshotContentResolutionPlan Plan { get; private set; }

    public async Task<Result<ConflictExecutionResult>> ExecuteAsync(
        ConflictDescriptor conflict,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conflict.ConflictId))
        {
            return Result<ConflictExecutionResult>.Failure(
                "conflict_not_actionable",
                "The snapshot conflict has no stable plan identifier.",
                [conflict]);
        }

        Result<SnapshotContentResolutionPlan> updated = await _snapshotSync.ResolveContentConflictAsync(
            Plan,
            conflict.ConflictId,
            selection,
            cancellationToken);
        if (updated.IsFailure)
        {
            return Result<ConflictExecutionResult>.Failure(updated.ErrorCode!, updated.ErrorMessage!,
                updated.Conflicts);
        }

        ConflictDescriptor? resolved = updated.Value.BranchImportPlan.Conflicts.SingleOrDefault(candidate =>
            string.Equals(candidate.ConflictId, conflict.ConflictId, StringComparison.Ordinal));
        if (resolved is null)
        {
            return Result<ConflictExecutionResult>.Failure(
                "conflict_not_found",
                "The resolved conflict was not present in the updated snapshot plan.",
                [conflict]);
        }

        Plan = updated.Value;
        return Result<ConflictExecutionResult>.Success(new ConflictExecutionResult(resolved));
    }
}
