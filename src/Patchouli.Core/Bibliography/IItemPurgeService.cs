using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

public interface IItemPurgeService
{
    /// <summary>
    /// Builds a dependency report for the requested item.
    /// </summary>
    Task<Result<ItemPurgeDependencyReport>> BuildPurgeReportAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds dependency reports for several items. Implementations can share snapshot inspection
    /// across the batch; the default preserves compatibility for existing implementations.
    /// </summary>
    async Task<Result<IReadOnlyList<ItemPurgeDependencyReport>>> BuildPurgeReportsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default)
    {
        List<ItemPurgeDependencyReport> reports = new(itemIds.Count);
        foreach (ItemId itemId in itemIds)
        {
            Result<ItemPurgeDependencyReport> report = await BuildPurgeReportAsync(itemId, cancellationToken);
            if (report.IsFailure)
            {
                return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Failure(
                    report.ErrorCode!, report.ErrorMessage!, report.Conflicts, report.Details);
            }

            reports.Add(report.Value);
        }

        return Result<IReadOnlyList<ItemPurgeDependencyReport>>.Success(reports);
    }

    /// <summary>
    /// Permanently deletes the payload for the specified items. Evidence references are preserved
    /// but marked as <see cref="Evidence.EvidenceRecordStatus.Purged"/>. A single library revision
    /// is published after the transaction commits.
    /// </summary>
    Task<Result> PurgeItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default);
}
