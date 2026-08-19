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
    /// Permanently deletes the payload for the specified items. Evidence references are preserved
    /// but marked as <see cref="Evidence.EvidenceRecordStatus.Purged"/>. A single library revision
    /// is published after the transaction commits.
    /// </summary>
    Task<Result> PurgeItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default);
}
