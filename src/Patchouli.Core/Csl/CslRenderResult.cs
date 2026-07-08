using Patchouli.Core.Ids;

namespace Patchouli.Core.Csl;

public sealed record CslRenderResult(
    string StyleId,
    string StyleDisplayName,
    string? Locale,
    IReadOnlyList<ItemId> ItemIds,
    string RenderedText,
    string RenderedHtml,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);
