namespace Patchouli.Core.Csl;

public sealed record CslStyle(
    string StyleId,
    string DisplayName,
    string? DefaultLocale,
    string? SourceUrl,
    string SourceKind,
    string ContentHash,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt,
    bool Enabled,
    bool Deleted);
