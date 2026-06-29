using LiteratureApp.Core.Ids;

namespace LiteratureApp.Core.Bibliography;

public sealed record ItemIdentifier(
    IdentifierId IdentifierId,
    ItemId ItemId,
    string Scheme,
    string Value,
    string? Note,
    DateTimeOffset CreatedAt);
