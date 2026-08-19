using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// Lifecycle metadata for an item, independent of its bibliographic metadata.
/// </summary>
public sealed record ItemLifecycleInfo(
    ItemId ItemId,
    ItemLifecycleState State,
    ItemId? MergedIntoItemId,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? PurgedAt);
