using Patchouli.Core.Conflicts;
using Patchouli.Core.Results;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Services;

public sealed class ConflictResolutionCoordinator : IConflictCoordinator
{
    private readonly IDialogService _dialogs;
    private readonly ConflictActionExecutorRegistry _executors;

    public ConflictResolutionCoordinator(IDialogService dialogs, ConflictActionExecutorRegistry executors)
    {
        _dialogs = dialogs;
        _executors = executors;
    }

    public Task<Result<ConflictResolutionResult>> ResolveAsync(
        ConflictDescriptor conflict,
        CancellationToken cancellationToken = default)
    {
        return ResolveAsync(conflict, null, cancellationToken);
    }

    public async Task<Result<ConflictResolutionResult>> ResolveAsync(
        ConflictDescriptor conflict,
        IConflictActionExecutor? executor,
        CancellationToken cancellationToken = default)
    {
        ConflictResolutionDialogViewModel dialog = new(conflict);
        ConflictDialogResult? choice = await _dialogs.ShowDialogAsync<ConflictDialogResult>(dialog);
        if (choice is null)
        {
            return Result<ConflictResolutionResult>.Success(new ConflictResolutionResult(
                conflict,
                ConflictExecutionDisposition.Deferred,
                false));
        }

        if (choice.ActionId == "leave_unresolved")
        {
            return Result<ConflictResolutionResult>.Success(new ConflictResolutionResult(
                ConflictResolutionTransitions.LeaveUnresolved(conflict),
                ConflictExecutionDisposition.Deferred,
                false));
        }

        ConflictActionSelection selection = new(choice.ActionId, choice.OptionId);
        Result<ConflictExecutionResult> executed = executor is null
            ? await _executors.ExecuteAsync(conflict, selection, cancellationToken)
            : await executor.ExecuteAsync(conflict, selection, cancellationToken);
        return executed.IsFailure
            ? Result<ConflictResolutionResult>.Failure(executed.ErrorCode!, executed.ErrorMessage!, executed.Conflicts)
            : Result<ConflictResolutionResult>.Success(new ConflictResolutionResult(
                executed.Value.Descriptor,
                executed.Value.Disposition,
                true));
    }
}
