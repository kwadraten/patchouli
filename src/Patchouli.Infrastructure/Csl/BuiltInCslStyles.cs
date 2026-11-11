using System.Reflection;
using Patchouli.Core.Csl;

namespace Patchouli.Infrastructure.Csl;

public static class BuiltInCslStyles
{
    public const string HistoricalResearchId = "historical-research";

    private const string HistoricalResearchResource =
        "Patchouli.Infrastructure.Csl.Styles.historical-research.csl";

    public static IReadOnlyList<CslCatalogStyle> Definitions { get; } =
    [
        new(HistoricalResearchId, "历史研究杂志社规定样式", null, "built-in")
    ];

    public static string GetContent(string styleId)
    {
        string resourceName = styleId switch
        {
            HistoricalResearchId => HistoricalResearchResource,
            _ => throw new ArgumentException($"Unknown built-in CSL style: {styleId}.", nameof(styleId))
        };
        Assembly assembly = typeof(BuiltInCslStyles).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName) ??
                              throw new InvalidDataException(
                                  $"Built-in CSL style resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
