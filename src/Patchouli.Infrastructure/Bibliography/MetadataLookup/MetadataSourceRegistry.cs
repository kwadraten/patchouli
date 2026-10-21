using Patchouli.Core.Bibliography.MetadataLookup;

namespace Patchouli.Infrastructure.Bibliography.MetadataLookup;

public sealed class MetadataSourceRegistry : IMetadataSourceRegistry
{
    private readonly IReadOnlyList<IMetadataSource> _sources;
    private readonly IMetadataSourceRegistry? _existing;
    private readonly IReadOnlyList<IMetadataSource> _extensionSources;

    public MetadataSourceRegistry(IEnumerable<IMetadataSource> sources)
    {
        _sources = sources
            .GroupBy(source => source.Definition.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        _extensionSources = Array.Empty<IMetadataSource>();
    }

    private MetadataSourceRegistry(IMetadataSourceRegistry existing, IEnumerable<IMetadataSource> extensionSources)
    {
        _existing = existing;
        _extensionSources = extensionSources
            .GroupBy(source => source.Definition.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        _sources = Array.Empty<IMetadataSource>();
    }

    public IReadOnlyList<MetadataSourceDefinition> Sources
    {
        get
        {
            IEnumerable<MetadataSourceDefinition> definitions = _existing?.Sources ??
                                                                _sources.Select(source => source.Definition).ToArray();
            return definitions.Concat(_extensionSources.Select(source => source.Definition))
                .GroupBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => _extensionSources.FirstOrDefault(source =>
                                         string.Equals(source.Definition.Id, group.Key,
                                             StringComparison.OrdinalIgnoreCase))
                                     ?.Definition ??
                                 group.First())
                .ToArray();
        }
    }

    public IReadOnlyList<IMetadataSource> Resolve(
        string identifierScheme,
        IReadOnlyList<MetadataSourcePreference>? preferences = null)
    {
        if (_existing is not null)
        {
            Dictionary<string, MetadataSourcePreference> extensionPreferences =
                (preferences ?? Array.Empty<MetadataSourcePreference>())
                .GroupBy(preference => preference.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MetadataSourcePreference> priorities = extensionPreferences;
            IMetadataSource[] matchingExtensions = _extensionSources
                .Where(source => source.Definition.SupportedSchemes.Contains(identifierScheme))
                .ToArray();
            IMetadataSource[] extensions = matchingExtensions
                .Where(source =>
                    extensionPreferences.TryGetValue(source.Definition.Id, out MetadataSourcePreference? preference)
                        ? preference.Enabled
                        : source.Definition.DefaultEnabled)
                .ToArray();
            HashSet<string> overridden = matchingExtensions.Select(source => source.Definition.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _existing.Resolve(identifierScheme, preferences)
                .Where(source => !overridden.Contains(source.Definition.Id))
                .Concat(extensions)
                .OrderBy(source =>
                    priorities.TryGetValue(source.Definition.Id, out MetadataSourcePreference? preference)
                        ? preference.Priority
                        : source.Definition.DefaultPriority)
                .ThenBy(source => source.Definition.Id, StringComparer.Ordinal)
                .ToArray();
        }

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

    public static MetadataSourceRegistry Combine(
        IMetadataSourceRegistry existing,
        IEnumerable<IMetadataSource> extensionSources)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(extensionSources);
        return new MetadataSourceRegistry(existing, extensionSources);
    }
}
