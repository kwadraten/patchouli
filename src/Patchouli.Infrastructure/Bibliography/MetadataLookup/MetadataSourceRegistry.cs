using Patchouli.Core.Bibliography.MetadataLookup;

namespace Patchouli.Infrastructure.Bibliography.MetadataLookup;

public sealed class MetadataSourceRegistry : IMetadataSourceRegistry
{
    private readonly IReadOnlyList<IMetadataSource> _sources;

    public MetadataSourceRegistry(IEnumerable<IMetadataSource> sources)
    {
        _sources = sources
            .GroupBy(source => source.Definition.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<MetadataSourceDefinition> Sources => _sources.Select(source => source.Definition).ToArray();

    public IReadOnlyList<IMetadataSource> Resolve(
        string identifierScheme,
        IReadOnlyList<MetadataSourcePreference>? preferences = null)
    {
        Dictionary<string, MetadataSourcePreference> configured =
            (preferences ?? Array.Empty<MetadataSourcePreference>())
            .GroupBy(preference => preference.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return _sources
            .Where(source => source.Definition.SupportedSchemes.Contains(identifierScheme))
            .Where(source => configured.TryGetValue(source.Definition.Id, out MetadataSourcePreference? preference)
                ? preference.Enabled
                : source.Definition.DefaultEnabled)
            .OrderBy(source => configured.TryGetValue(source.Definition.Id, out MetadataSourcePreference? preference)
                ? preference.Priority
                : source.Definition.DefaultPriority)
            .ThenBy(source => source.Definition.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static MetadataSourceRegistry CreateDefault(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        return new MetadataSourceRegistry(PublicMetadataSources.Create(httpClient, requestTimeout));
    }
}
