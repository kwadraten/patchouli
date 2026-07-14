using Patchouli.Core.Conflicts;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Conflicts;

public sealed class DocumentBoxConflictActionExecutor : IConflictActionExecutor
{
    private readonly Func<CancellationToken, Task<Result>> _adjust;
    private readonly Func<string, CancellationToken, Task<Result>> _changeType;
    private readonly Func<CancellationToken, Task<Result>> _skip;

    public DocumentBoxConflictActionExecutor(
        Func<CancellationToken, Task<Result>> adjust,
        Func<string, CancellationToken, Task<Result>> changeType,
        Func<CancellationToken, Task<Result>> skip)
    {
        _adjust = adjust;
        _changeType = changeType;
        _skip = skip;
    }

    public string ConflictCode => Patchouli.Core.Conflicts.ConflictCode.LayoutBBoxOrdinaryOverlap;

    public async Task<Result<ConflictExecutionResult>> ExecuteAsync(ConflictDescriptor conflict,
        ConflictActionSelection selection, CancellationToken cancellationToken = default)
    {
        if (conflict.ConflictCode != ConflictCode)
        {
            return Result<ConflictExecutionResult>.Failure("conflict_code_mismatch",
                "The conflict was sent to the wrong executor.", [conflict]);
        }

        Result<ConflictDescriptor> valid = ConflictResolutionTransitions.ValidateSelection(conflict, selection);
        if (valid.IsFailure)
        {
            return Result<ConflictExecutionResult>.Failure(valid.ErrorCode!, valid.ErrorMessage!, valid.Conflicts);
        }

        Result operation = selection.ActionId switch
        {
            "adjust_bbox" => await _adjust(cancellationToken),
            "change_to_allowed_type" => await _changeType(selection.OptionId!, cancellationToken),
            "skip_candidate" => await _skip(cancellationToken),
            _ => Result.Failure("conflict_action_unknown", "The selected action is not executable.")
        };
        return operation.IsFailure
            ? Result<ConflictExecutionResult>.Failure(operation.ErrorCode!, operation.ErrorMessage!, [conflict])
            : Result<ConflictExecutionResult>.Success(new ConflictExecutionResult(
                ConflictResolutionTransitions.Resolve(conflict, selection.ActionId)));
    }
}
