namespace Patchouli.Core.Bibliography;

public sealed record CslItemTypeProfile(
    string ItemType,
    string DisplayName,
    string Description,
    IReadOnlyList<string> PrimaryFields,
    IReadOnlyList<string> RecommendedFields,
    IReadOnlyList<string> AdvancedFields,
    IReadOnlyList<string> CreatorRoles,
    IReadOnlyList<string> DateRoles,
    IReadOnlyList<string> IdentifierSchemes,
    IReadOnlyDictionary<string, string> FieldLabels,
    IReadOnlyList<string> HiddenByDefaultFields,
    bool IsRenderableInCsl);
