using Patchouli.Core.Ids;

namespace Patchouli.Core.Csl;

public static class CslRenderFormats
{
    public const string Text = "text";
    public const string Html = "html";
}

public sealed record CslRenderRequest(
    IReadOnlyList<ItemId> ItemIds,
    string? StyleId = null,
    string? Locale = null,
    string OutputFormat = CslRenderFormats.Text);
