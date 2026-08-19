using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// Describes a tag aggregated across active library items.
/// </summary>
public sealed record TagInfo(string Name, int Count);

/// <summary>
/// Bulk tag operations against <c>items.tags_json</c>. All write operations skip items in the
/// trash and emit a single library revision / change notification for the batch.
/// </summary>
public interface IItemTagService
{
    /// <summary>
    /// Lists every tag that appears on at least one active item, ordered by name using
    /// ordinal comparison. Counts exclude trashed and merged items.
    /// </summary>
    Task<Result<IReadOnlyList<TagInfo>>> ListTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the given tags to every active item in <paramref name="itemIds"/>. Items already
    /// carrying a tag are left unchanged for that tag. Empty tags are ignored. Trashed items
    /// are silently skipped.
    /// </summary>
    Task<Result> AddTagsToItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="tag"/> from every active item in <paramref name="itemIds"/>.
    /// Trashed items are silently skipped.
    /// </summary>
    Task<Result> RemoveTagFromItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        string tag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="tag"/> from every active item in the library. Trashed items are
    /// silently skipped.
    /// </summary>
    Task<Result> RemoveTagAsync(
        string tag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the tags of every active item in <paramref name="itemIds"/> with
    /// <paramref name="tags"/>. Trashed items are silently skipped.
    /// </summary>
    Task<Result> SetTagsAsync(
        IReadOnlyList<ItemId> itemIds,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames <paramref name="oldTag"/> to <paramref name="newTag"/> on all active items.
    /// If <paramref name="newTag"/> already exists on an item, the old tag is merged into the
    /// new tag instead. Trashed items are skipped.
    /// </summary>
    Task<Result> RenameTagAsync(
        string oldTag,
        string newTag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces every occurrence of <paramref name="sourceTag"/> with
    /// <paramref name="targetTag"/> on active items. Items that already carry the target tag
    /// simply lose the source tag. Trashed items are skipped.
    /// </summary>
    Task<Result> MergeTagsAsync(
        string sourceTag,
        string targetTag,
        CancellationToken cancellationToken = default);
}
