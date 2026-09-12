using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// CRUD and membership operations for one-level Library Collections. Names are unique per
/// Library (ordinal comparison); membership targets active, non-merged Items and is preserved
/// across trash/restore. Dissolving a Collection removes only its membership rows and never
/// deletes Items. Every mutation publishes a single Library revision/change notification.
/// </summary>
public interface ICollectionService
{
    /// <summary>Lists the Library's Collections ordered by name (ordinal), including empty
    /// Collections. Item counts exclude trashed and merged Items.</summary>
    Task<Result<IReadOnlyList<Collection>>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a Collection with a trimmed, non-empty, Library-unique name.</summary>
    Task<Result<Collection>> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Renames a Collection to a trimmed, non-empty, Library-unique name.</summary>
    Task<Result<Collection>> RenameCollectionAsync(CollectionId collectionId, string name,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a Collection and its membership rows. Items are never deleted.</summary>
    Task<Result> DissolveCollectionAsync(CollectionId collectionId, CancellationToken cancellationToken = default);

    /// <summary>Adds the active Items to a Collection, ignoring existing memberships.</summary>
    Task<Result> AddItemsAsync(CollectionId collectionId, IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the Items from a Collection. Items without membership are ignored.</summary>
    Task<Result> RemoveItemsAsync(CollectionId collectionId, IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the full Collection membership of one Item with <paramref name="collectionIds"/>.</summary>
    Task<Result> SetItemCollectionsAsync(ItemId itemId, IReadOnlyList<CollectionId> collectionIds,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the Collection ids that contain the Item, ordered by name.</summary>
    Task<Result<IReadOnlyList<CollectionId>>> GetItemCollectionIdsAsync(ItemId itemId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the active Item ids in a Collection.</summary>
    Task<Result<IReadOnlyList<ItemId>>> GetCollectionItemIdsAsync(CollectionId collectionId,
        CancellationToken cancellationToken = default);
}
