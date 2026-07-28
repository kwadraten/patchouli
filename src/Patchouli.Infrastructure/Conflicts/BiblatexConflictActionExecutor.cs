using Patchouli.Core.Conflicts;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Conflicts;

/// <summary>
/// Marks CF-06/CF-07 as resolved after the UI collects multi-choice selections.
/// Persistence is performed by <c>IBiblatexImportService</c> using those choices.
/// </summary>
public sealed class BiblatexConflictActionExecutor : IConflictActionExecutor
{
    public BiblatexConflictActionExecutor(string conflictCode)
    {
        ConflictCode = conflictCode;
    }

    public string ConflictCode { get; }

    public Task<Result<ConflictExecutionResult>> ExecuteAsync(
        ConflictDescriptor conflict,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default)
    {
        Result<ConflictDescriptor> validated = ConflictResolutionTransitions.ValidateSelection(conflict, selection);
        if (validated.IsFailure)
        {
            return Task.FromResult(Result<ConflictExecutionResult>.Failure(
                validated.ErrorCode!,
                validated.ErrorMessage!,
                validated.Conflicts));
        }

        if (selection.Choices is null || selection.Choices.Count == 0)
        {
            return Task.FromResult(Result<ConflictExecutionResult>.Failure(
                "conflict_option_required",
                "This bibliography import action requires per-row choices.",
                [conflict]));
        }

        ConflictDescriptor resolved = ConflictResolutionTransitions.Resolve(conflict, selection.ActionId);
        return Task.FromResult(Result<ConflictExecutionResult>.Success(
            new ConflictExecutionResult(resolved)));
    }
}
