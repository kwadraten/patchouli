using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Conflicts;

public sealed record LayoutStagingCandidate(
    LayoutRevisionId RevisionId,
    PageId PageId,
    LayoutNodeId? ParentNodeId,
    string NodeType,
    NormalizedBBox BBox,
    string? OwnText,
    string TextPolicy,
    int ReadingOrder,
    string Source);

public sealed class LayoutStagingConflictActionExecutor : IConflictActionExecutor
{
    private readonly ILayoutTreeService _layout;
    private readonly LayoutStagingCandidate _candidate;

    public LayoutStagingConflictActionExecutor(ILayoutTreeService layout, LayoutStagingCandidate candidate)
    {
        _layout = layout;
        _candidate = candidate;
    }

    public string ConflictCode => Patchouli.Core.Conflicts.ConflictCode.LayoutBBoxOrdinaryOverlap;

    public async Task<Result<ConflictExecutionResult>> ExecuteAsync(
        ConflictDescriptor conflict,
        ConflictActionSelection selection,
        CancellationToken cancellationToken = default)
    {
        Result<ConflictDescriptor> valid = ConflictResolutionTransitions.ValidateSelection(conflict, selection);
        if (valid.IsFailure)
        {
            return Result<ConflictExecutionResult>.Failure(valid.ErrorCode!, valid.ErrorMessage!, valid.Conflicts);
        }

        switch (selection.ActionId)
        {
            case "adjust_bbox":
                return Result<ConflictExecutionResult>.Success(new ConflictExecutionResult(
                    conflict with { SelectedAction = selection.ActionId },
                    ConflictExecutionDisposition.RetainStagingCandidate));

            case "skip_candidate":
                return Result<ConflictExecutionResult>.Success(new ConflictExecutionResult(
                    ConflictResolutionTransitions.Resolve(conflict, selection.ActionId),
                    ConflictExecutionDisposition.Discarded));

            case "change_to_allowed_type":
                Result<LayoutNode> added = await _layout.AddNodeAsync(
                    _candidate.RevisionId,
                    _candidate.PageId,
                    _candidate.ParentNodeId,
                    selection.OptionId!,
                    _candidate.BBox,
                    _candidate.OwnText,
                    _candidate.TextPolicy,
                    _candidate.ReadingOrder,
                    _candidate.Source,
                    cancellationToken: cancellationToken);
                if (added.IsFailure)
                {
                    return Result<ConflictExecutionResult>.Failure(
                        added.ErrorCode!, added.ErrorMessage!, added.Conflicts);
                }

                return Result<ConflictExecutionResult>.Success(new ConflictExecutionResult(
                    ConflictResolutionTransitions.Resolve(conflict, selection.ActionId)));

            default:
                return Result<ConflictExecutionResult>.Failure(
                    "conflict_action_unknown",
                    "The selected action is not executable for this layout conflict.",
                    [conflict]);
        }
    }
}
