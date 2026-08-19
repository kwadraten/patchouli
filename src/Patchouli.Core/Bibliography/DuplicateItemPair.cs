using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// A pair of library items that are suspected duplicates, along with the rules that matched.
/// </summary>
public sealed record DuplicateItemPair(
    ItemId ItemIdA,
    ItemId ItemIdB,
    IReadOnlyList<DuplicateItemReason> Reasons,
    ItemId DefaultTargetItemId);
