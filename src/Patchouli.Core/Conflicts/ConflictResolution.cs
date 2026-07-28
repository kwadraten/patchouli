using Patchouli.Core.Results;

namespace Patchouli.Core.Conflicts;

public sealed record ConflictActionSelection(
    string ActionId,
    string? OptionId = null,
    IReadOnlyDictionary<string, string>? Choices = null);

public enum ConflictExecutionDisposition
{
    Applied,
    RetainStagingCandidate,
    Discarded,
    Deferred
}

public sealed record ConflictExecutionResult(
    ConflictDescriptor Descriptor,
    ConflictExecutionDisposition Disposition = ConflictExecutionDisposition.Applied);

public sealed record ConflictResolutionResult(
    ConflictDescriptor Descriptor,
    ConflictExecutionDisposition Disposition,
    bool WasExecuted);

public interface IConflictCoordinator
{
    Task<Result<ConflictResolutionResult>> ResolveAsync(
        ConflictDescriptor conflict,
        CancellationToken cancellationToken = default);
}

public interface IConflictActionExecutor
{
    string ConflictCode { get; }

    Task<Result<ConflictExecutionResult>> ExecuteAsync(
        ConflictDescriptor conflict,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default);
}

public sealed class ConflictActionExecutorRegistry
{
    private readonly IReadOnlyDictionary<string, IConflictActionExecutor> _executors;

    public ConflictActionExecutorRegistry(IEnumerable<IConflictActionExecutor> executors)
    {
        IConflictActionExecutor[] values = executors.ToArray();
        string? duplicate = values.GroupBy(executor => executor.ConflictCode, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).FirstOrDefault();
        if (duplicate is not null)
        {
            throw new ArgumentException($"Multiple conflict executors are registered for {duplicate}.",
                nameof(executors));
        }

        _executors = values.ToDictionary(executor => executor.ConflictCode, StringComparer.Ordinal);
    }

    public Task<Result<ConflictExecutionResult>> ExecuteAsync(
        ConflictDescriptor conflict,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default)
    {
        return !_executors.TryGetValue(conflict.ConflictCode, out IConflictActionExecutor? executor)
            ? Task.FromResult(Result<ConflictExecutionResult>.Failure(
                "conflict_executor_unavailable",
                $"No executor is registered for {conflict.ConflictCode}.",
                [conflict]))
            : executor.ExecuteAsync(conflict, selection, cancellationToken);
    }
}

public static class ConflictResolutionTransitions
{
    public static Result<ConflictDescriptor> ValidateSelection(
        ConflictDescriptor conflict,
        ConflictActionSelection selection)
    {
        if (conflict.ResolutionStatus != ConflictResolutionStatus.Unresolved)
        {
            return Result<ConflictDescriptor>.Failure(
                "conflict_not_actionable",
                "Only unresolved conflicts can be acted on.",
                [conflict]);
        }

        ConflictAction? action = conflict.RecommendedActions.SingleOrDefault(candidate =>
            string.Equals(candidate.ActionId, selection.ActionId, StringComparison.Ordinal));
        if (action is null)
        {
            return Result<ConflictDescriptor>.Failure(
                "conflict_action_unknown",
                "The selected action is not offered by this conflict.",
                [conflict]);
        }

        if (action.RequiresOption)
        {
            bool hasMultiChoice = selection.Choices is { Count: > 0 };
            bool validOption = !string.IsNullOrWhiteSpace(selection.OptionId) &&
                               conflict.AvailableOptions.Any(option =>
                                   string.Equals(option.OptionId, selection.OptionId, StringComparison.Ordinal));
            if (!hasMultiChoice && !validOption)
            {
                return Result<ConflictDescriptor>.Failure(
                    "conflict_option_required",
                    "This action requires one of the offered options.",
                    [conflict]);
            }
        }

        return Result<ConflictDescriptor>.Success(conflict);
    }

    public static ConflictDescriptor Resolve(ConflictDescriptor conflict, string actionId)
    {
        return conflict with
        {
            SelectedAction = actionId,
            ResolutionStatus = ConflictResolutionStatus.Resolved
        };
    }

    public static ConflictDescriptor Ignore(ConflictDescriptor conflict, string actionId)
    {
        if (conflict.Severity == ConflictSeverity.Blocking)
        {
            throw new InvalidOperationException("Blocking conflicts cannot be ignored.");
        }

        return conflict with
        {
            SelectedAction = actionId,
            ResolutionStatus = ConflictResolutionStatus.Ignored
        };
    }

    public static ConflictDescriptor LeaveUnresolved(ConflictDescriptor conflict)
    {
        return conflict with
        {
            SelectedAction = "leave_unresolved",
            ResolutionStatus = ConflictResolutionStatus.Unresolved
        };
    }
}
