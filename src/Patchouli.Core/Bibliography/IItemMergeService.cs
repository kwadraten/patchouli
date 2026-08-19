using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// Merges one library item into another. The source becomes a redirect tombstone; its documents,
/// tags and selected fields move to the target.
/// </summary>
public interface IItemMergeService
{
    /// <summary>
    /// Builds a preview of merging <paramref name="sourceId"/> into <paramref name="targetId"/>.
    /// </summary>
    Task<Result<ItemMergePreview>> BuildMergePreviewAsync(
        ItemId sourceId,
        ItemId targetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the merge in a single transaction and emits a single library revision.
    /// <paramref name="hasUnsavedEdits"/> is supplied by the caller (usually the UI) because the
    /// service layer must not depend on editor state.
    /// </summary>
    Task<Result> MergeAsync(
        ItemId sourceId,
        ItemId targetId,
        IReadOnlyList<MergeFieldChoice> choices,
        Func<ItemId, bool> hasUnsavedEdits,
        CancellationToken cancellationToken = default);
}
