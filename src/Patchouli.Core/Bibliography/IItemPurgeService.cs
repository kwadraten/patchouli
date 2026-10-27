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
        CancellationToken cancellationToken = default,
        IProgress<ItemPurgeProgress>? progress = null);

    /// <summary>
    /// Builds dependency reports for several items. Implementations can share snapshot inspection
    /// across the batch; the default preserves compatibility for existing implementations.
    /// </summary>
    async Task<Result<IReadOnlyList<ItemPurgeDependencyReport>>> BuildPurgeReportsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default,
        IProgress<ItemPurgeProgress>? progress = null)
    {
        List<ItemPurgeDependencyReport> reports = new(itemIds.Count);
        foreach (ItemId itemId in itemIds)
        {
            Result<ItemPurgeDependencyReport> report = await BuildPurgeReportAsync(itemId, cancellationToken, progress);
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
    /// Permanently deletes the specified items and their document payload. Versioned evidence then
    /// resolves as not found; original source files remain untouched. A single library revision
    /// is published after the whole batch commits.
    /// </summary>
    Task<Result> PurgeItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default,
        IProgress<ItemPurgeProgress>? progress = null);
}
