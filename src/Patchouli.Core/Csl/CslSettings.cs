namespace Patchouli.Core.Csl;

public sealed record CslSettings(
    string? DefaultStyleId,
    string? Locale,
    DateTimeOffset UpdatedAt);
