using Patchouli.Core.Ids;

namespace Patchouli.Core.CitationInterop;

/// <summary>
/// One picked reference inside a citation pick (issue #14). Protocol-neutral:
/// the same shape feeds the LaTeX/Pandoc/Typst string formatters, the CSL
/// cluster renderer and the BBT-compatible <c>json</c> pick output. The order
/// of entries inside a <see cref="CitationPick"/> is the user's citation order
/// and is never re-sorted by formatters.
/// </summary>
public sealed record CitationPickEntry(
    ItemId ItemId,
    string CitationKey,
    string? Locator = null,
    string? Label = null,
    string? Prefix = null,
    string? Suffix = null,
    bool SuppressAuthor = false);

/// <summary>An ordered citation pick: the result of one picker interaction or one
/// <c>selected</c> snapshot. An empty pick is the normal representation of a
/// cancelled picker or an empty selection, not an error.</summary>
public sealed record CitationPick(IReadOnlyList<CitationPickEntry> Entries)
{
    public static readonly CitationPick Empty = new([]);
    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>
/// The CSL-JSON projection of one picked/search item, shared by the CAYW
/// <c>json</c> formatter and the TexPile <c>item.search</c> result. Variables
/// come from <see cref="Patchouli.Core.Csl.CslMappedItem"/>; the projection
/// keeps the CSL <c>citation-key</c> variable and additionally exposes it as
/// <c>citekey</c>, matching the field TexPile/BBT consumers read.
/// </summary>
public sealed record CitationItemProjection(
    ItemId ItemId,
    string CitationKey,
    IReadOnlyDictionary<string, object?> CslVariables);
