using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// A one-level user playlist inside a Library. A Collection is a named many-to-many
/// relationship over active Items; it never owns or deletes Items, and it cannot nest.
/// </summary>
public sealed record Collection(
    CollectionId CollectionId,
    LibraryId LibraryId,
    string Name,
    int ItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One membership edge between an Item and a Collection.
/// </summary>
public sealed record ItemCollectionMembership(
    CollectionId CollectionId,
    ItemId ItemId);
