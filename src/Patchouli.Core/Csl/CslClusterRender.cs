using Patchouli.Core.Ids;

namespace Patchouli.Core.Csl;

/// <summary>
/// One item inside a single CSL citation cluster, carrying the per-item citation
/// modifiers the Cite As You Write picker collects (issue #14). Mirrors the
/// citeproc citation-item vocabulary: <c>locator</c>, <c>label</c>, <c>prefix</c>,
/// <c>suffix</c> and <c>suppress-author</c>.
/// </summary>
public sealed record CslCitationItem(
    ItemId ItemId,
    string? Locator = null,
    string? Label = null,
    string? Prefix = null,
    string? Suffix = null,
    bool SuppressAuthor = false);

/// <summary>
/// Renders one citation cluster (the in-text citation for a single pick) rather
/// than a bibliography. Kept separate from <see cref="CslRenderRequest"/> so the
/// existing bibliography callers keep their contract: a cluster render may
/// legitimately succeed for citation-only styles that have no bibliography.
/// </summary>
public sealed record CslClusterRenderRequest(
    IReadOnlyList<CslCitationItem> Items,
    string? StyleId = null,
    string? Locale = null,
    string OutputFormat = CslRenderFormats.Text);

/// <summary>The rendered in-text citation for one cluster, in text and HTML form.</summary>
public sealed record CslClusterRenderResult(
    string StyleId,
    string StyleDisplayName,
    string? Locale,
    IReadOnlyList<ItemId> ItemIds,
    string CitationText,
    string CitationHtml,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
