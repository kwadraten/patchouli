using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

public sealed record ItemIdentifier(
    IdentifierId IdentifierId,
    ItemId ItemId,
    string Scheme,
    string Value,
    string? Note,
    DateTimeOffset CreatedAt);
