namespace Patchouli.Core.Bibliography;

/// <summary>
/// User-visible lifecycle state of an <see cref="ItemMetadata"/> in a library.
/// </summary>
public enum ItemLifecycleState
{
    /// <summary>Normal, visible item.</summary>
    Active,

    /// <summary>Soft-deleted item that appears in the trash and can be restored.</summary>
    /// <remarks>This is a tombstone at the item level.</remarks>
    Trash,

    /// <summary>Item that has been merged into another item and is no longer independently editable.</summary>
    /// <remarks>The row remains as a redirect tombstone.</remarks>
    Merged,

    /// <summary>Item whose payload has been permanently deleted.</summary>
    /// <remarks>Purged items are not represented by rows in <c>items</c>; their tombstone lives in <c>item_purge_records</c>.</remarks>
    Purged
}
