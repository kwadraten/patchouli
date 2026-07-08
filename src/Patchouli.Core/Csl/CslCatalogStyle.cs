namespace Patchouli.Core.Csl;

public sealed record CslCatalogStyle(
    string StyleId,
    string DisplayName,
    string? SourceUrl,
    string SourceKind,
    string? DefaultLocale = null);
