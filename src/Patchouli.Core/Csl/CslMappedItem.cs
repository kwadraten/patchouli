using Patchouli.Core.Ids;

namespace Patchouli.Core.Csl;

public sealed record CslMappedItem(
    ItemId ItemId,
    string ItemType,
    IReadOnlyDictionary<string, object?> Variables);
